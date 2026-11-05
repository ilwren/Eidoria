using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>以每用例独立临时目录驱动真实 IO 的基础设施测试。</summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly string _root;

    public FileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mdexplorer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string FileIn(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task List_HidesDotFilesByDefault_AndClassifies()
    {
        FileIn("a.txt");
        FileIn(".hidden.txt");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var service = new LocalFileSystemService();
        var items = new List<FileItem>();
        await foreach (var item in service.ListAsync(_root, includeHidden: false, TestContext.Current.CancellationToken))
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i is { Name: "a.txt", Kind: FileKind.Text });
        Assert.Contains(items, i => i is { Name: "sub", Kind: FileKind.Folder });
    }

    [Fact]
    public async Task Copy_KeepBoth_RenamesOnConflict()
    {
        var srcDir = Path.Combine(_root, "src");
        var dstDir = Path.Combine(_root, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        var source = Path.Combine(srcDir, "a.txt");
        File.WriteAllText(source, "new");
        File.WriteAllText(Path.Combine(dstDir, "a.txt"), "old");

        var ops = new FileOperationService();
        var outcome = await ops.CopyAsync([source], dstDir, ConflictPolicy.KeepBoth, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dstDir, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dstDir, "a (2).txt")));
    }

    [Fact]
    public async Task Move_RecursiveFolder()
    {
        var source = Path.Combine(_root, "folder");
        FileIn(Path.Combine("folder", "inner", "deep.txt"), "deep");
        var destParent = Path.Combine(_root, "target");
        Directory.CreateDirectory(destParent);

        var ops = new FileOperationService();
        var outcome = await ops.MoveAsync([source], destParent, ConflictPolicy.KeepBoth, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.False(Directory.Exists(source));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(destParent, "folder", "inner", "deep.txt")));
    }

    [Fact]
    public async Task Move_IntoOwnChild_IsRejected()
    {
        var source = Path.Combine(_root, "folder");
        FileIn(Path.Combine("folder", "child", "a.txt"));
        var ops = new FileOperationService();
        var outcome = await ops.MoveAsync([source], Path.Combine(source, "child"), ConflictPolicy.KeepBoth, null, TestContext.Current.CancellationToken);
        Assert.False(outcome.Success);
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task Search_FindsNestedByName_AndKindFilter()
    {
        FileIn(Path.Combine("a", "report-final.pdf"));
        FileIn(Path.Combine("a", "b", "report-draft.txt"));
        FileIn(Path.Combine("a", "other.bin"));

        var search = new SearchService();
        var hits = new List<FileItem>();
        await foreach (var hit in search.SearchByNameAsync(
                           new SearchSpec("report", _root), TestContext.Current.CancellationToken))
            hits.Add(hit);
        Assert.Equal(2, hits.Count);

        var pdfOnly = new List<FileItem>();
        await foreach (var hit in search.SearchByNameAsync(
                           new SearchSpec(string.Empty, _root, new HashSet<FileKind> { FileKind.Document }),
                           TestContext.Current.CancellationToken))
            pdfOnly.Add(hit);
        Assert.Single(pdfOnly);
    }

    [Fact]
    public async Task Archive_RoundTrip_AndZipSlipBlocked()
    {
        var archive = new ArchiveService();
        var src = FileIn("doc.txt", "hello");
        var zip = Path.Combine(_root, "out.zip");
        var compress = await archive.CompressToZipAsync([src], zip, null, TestContext.Current.CancellationToken);
        Assert.True(compress.Success, string.Join(",", compress.Errors.Select(e => e.Message)));

        var entries = await archive.ListEntriesAsync(zip, 100, TestContext.Current.CancellationToken);
        Assert.Single(entries);

        var dest = Path.Combine(_root, "extracted");
        var extract = await archive.ExtractAsync(zip, dest, null, TestContext.Current.CancellationToken);
        Assert.True(extract.Success, string.Join(",", extract.Errors.Select(e => e.Message)));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "doc.txt")));

        // 手工构造带 ../ 条目的恶意包
        var evilZip = Path.Combine(_root, "evil.zip");
        using (var stream = File.Create(evilZip))
        using (var z = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = z.CreateEntry("../evil.txt");
            await using var writer = entry.Open();
            await writer.WriteAsync("pwn"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        var evilDest = Path.Combine(_root, "evildest");
        var evilOutcome = await archive.ExtractAsync(evilZip, evilDest, null, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")), "Zip Slip 必须被拦截");
        Assert.NotEmpty(evilOutcome.Errors);
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        var dataDir = Path.Combine(_root, "appdata");
        var store = await SettingsStore.LoadAsync(dataDir, TestContext.Current.CancellationToken);
        store.Settings.SeedColor = "#123456";
        await store.SaveSettingsAsync(TestContext.Current.CancellationToken);
        await store.SetFavoriteAsync("/some/where", true, TestContext.Current.CancellationToken);

        var reloaded = await SettingsStore.LoadAsync(dataDir, TestContext.Current.CancellationToken);
        Assert.Equal("#123456", reloaded.Settings.SeedColor);
        Assert.True(reloaded.IsFavorite("/some/where"));
    }
}
