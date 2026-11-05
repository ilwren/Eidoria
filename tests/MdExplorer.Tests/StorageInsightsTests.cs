using System.Security.Cryptography;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>只读存储洞察：容量聚合、一级文件夹桶、大文件榜与精确重复查找。</summary>
public sealed class StorageInsightsTests : IDisposable
{
    private readonly string _root;
    private readonly StorageInsightsService _service = new();

    public StorageInsightsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mdexplorer-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string FileIn(string relative, byte fill, int length)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        return path;
    }

    private void CreateSampleTree()
    {
        FileIn(Path.Combine("a", "big.bin"), 1, 100);
        FileIn(Path.Combine("a", "photo.jpg"), 7, 40);
        FileIn(Path.Combine("b", "photo-copy.jpg"), 7, 40);
        FileIn(Path.Combine("b", "other.jpg"), 9, 40);
        FileIn(Path.Combine("b", "note.txt"), 3, 10);
        FileIn("root.txt", 4, 5);
        FileIn(Path.Combine(".hidden", "secret.txt"), 8, 100);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
    }

    [Fact]
    public async Task Analyze_AggregatesKindsTopFoldersAndLargestFiles()
    {
        CreateSampleTree();

        var result = await _service.AnalyzeAsync(
            _root, new StorageAnalysisOptions(), null, TestContext.Current.CancellationToken);

        Assert.Equal(235, result.TotalBytes);
        Assert.Equal(6, result.FileCount);
        Assert.Equal(3, result.FolderCount);
        Assert.False(result.IsPartial);
        Assert.False(result.WasTruncated);

        Assert.Equal(120, result.KindUsages.Single(usage => usage.Kind == FileKind.Image).TotalBytes);
        Assert.Equal(100, result.KindUsages.Single(usage => usage.Kind == FileKind.Executable).TotalBytes);
        Assert.Equal(15, result.KindUsages.Single(usage => usage.Kind == FileKind.Text).TotalBytes);

        var folderA = result.TopFolders.Single(folder => folder.Name == "a");
        Assert.Equal(140, folderA.TotalBytes);
        Assert.Equal(2, folderA.FileCount);
        Assert.Equal(1, folderA.FolderCount);
        var folderB = result.TopFolders.Single(folder => folder.Name == "b");
        Assert.Equal(90, folderB.TotalBytes);
        Assert.Equal(3, folderB.FileCount);
        var rootFiles = result.TopFolders.Single(folder => folder.IsRootFiles);
        Assert.Equal(5, rootFiles.TotalBytes);
        Assert.Equal(_root, rootFiles.Path);

        Assert.Equal("big.bin", result.LargestFiles[0].Name);
        Assert.All(result.LargestFiles.Skip(1).Take(3), file => Assert.Equal(40, file.Length));
    }

    [Fact]
    public async Task Analyze_IncludeHiddenAndEntryCapAreExplicit()
    {
        CreateSampleTree();

        var withHidden = await _service.AnalyzeAsync(
            _root,
            new StorageAnalysisOptions(IncludeHidden: true),
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(335, withHidden.TotalBytes);
        Assert.Equal(7, withHidden.FileCount);
        Assert.Equal(4, withHidden.FolderCount);

        var truncated = await _service.AnalyzeAsync(
            _root,
            new StorageAnalysisOptions(MaxEntries: 2),
            null,
            TestContext.Current.CancellationToken);
        Assert.True(truncated.WasTruncated);
        Assert.True(truncated.FileCount + truncated.FolderCount <= 2);
    }

    [Fact]
    public async Task FindDuplicates_UsesExactHashAndIgnoresSameSizeDifferentContent()
    {
        CreateSampleTree();

        var result = await _service.FindDuplicatesAsync(
            _root, new StorageAnalysisOptions(), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.CandidateFileCount);
        Assert.Equal(3, result.FilesHashed);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(40, result.ReclaimableBytes);
        var group = Assert.Single(result.Groups);
        Assert.Equal(40, group.FileSize);
        Assert.Equal(2, group.Count);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Enumerable.Repeat((byte)7, 40).ToArray())),
            group.Sha256);
        Assert.Contains(group.Files, file => file.Name == "photo.jpg");
        Assert.Contains(group.Files, file => file.Name == "photo-copy.jpg");
        Assert.DoesNotContain(group.Files, file => file.Name == "other.jpg");
    }

    [Fact]
    public async Task FindDuplicates_CandidateCapIsReportedAsTruncated()
    {
        FileIn("one.bin", 5, 20);
        FileIn("two.bin", 5, 20);

        var result = await _service.FindDuplicatesAsync(
            _root,
            new StorageAnalysisOptions(MaxDuplicateCandidateFiles: 1),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.WasTruncated);
        Assert.Equal(1, result.CandidateFileCount);
        Assert.Equal(1, result.FilesHashed);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Analyze_MissingRootThrows()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _service.AnalyzeAsync(
            Path.Combine(_root, "missing"),
            new StorageAnalysisOptions(),
            null,
            TestContext.Current.CancellationToken));
    }
}
