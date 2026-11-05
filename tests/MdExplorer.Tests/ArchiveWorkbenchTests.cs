using System.IO.Compression;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.15：ZIP 家族单条目有界读取、所选/扁平解压、冲突策略与虚拟目录状态。</summary>
public sealed class ArchiveWorkbenchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-archive-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task ExtractEntriesHonorsFolderSelectionAndSingleFileSelection()
    {
        var archivePath = CreateArchive("selected.zip",
            ("docs/readme.txt", "hello"),
            ("docs/config.json", "{}"),
            ("photos/readme.txt", "world"),
            ("root.txt", "root"));
        var service = new ArchiveService();

        var folderDest = NewDestination("folder");
        var folderOutcome = await service.ExtractEntriesAsync(
            archivePath,
            folderDest,
            new ArchiveExtractOptions(new HashSet<string> { "docs/" }),
            progress: null,
            CancellationToken.None);

        Assert.True(folderOutcome.Success);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(folderDest, "docs", "readme.txt")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folderDest, "docs", "config.json")));
        Assert.False(File.Exists(Path.Combine(folderDest, "photos", "readme.txt")));
        Assert.False(File.Exists(Path.Combine(folderDest, "root.txt")));

        var fileDest = NewDestination("file");
        var fileOutcome = await service.ExtractEntriesAsync(
            archivePath,
            fileDest,
            new ArchiveExtractOptions(
                new HashSet<string> { "photos/readme.txt" },
                PreserveStructure: false),
            progress: null,
            CancellationToken.None);

        Assert.True(fileOutcome.Success);
        Assert.Equal("world", File.ReadAllText(Path.Combine(fileDest, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(fileDest, "photos")));
    }

    [Fact]
    public async Task FlattenedExtractionKeepsBothWhenBasenamesCollide()
    {
        var archivePath = CreateArchive("flatten.zip",
            ("a/readme.txt", "first"),
            ("b/readme.txt", "second"));
        var destination = NewDestination("flattened");

        var outcome = await new ArchiveService().ExtractEntriesAsync(
            archivePath,
            destination,
            new ArchiveExtractOptions(
                new HashSet<string> { "a/readme.txt", "b/readme.txt" },
                PreserveStructure: false,
                ConflictPolicy: ConflictPolicy.KeepBoth),
            progress: null,
            CancellationToken.None);

        Assert.True(outcome.Success);
        var contents = Directory.EnumerateFiles(destination)
            .Select(File.ReadAllText)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["first", "second"], contents);
        Assert.Contains(Path.Combine(destination, "readme.txt"), outcome.ProducedPaths);
        Assert.Contains(Path.Combine(destination, "readme (2).txt"), outcome.ProducedPaths);
    }

    [Fact]
    public async Task ConflictPoliciesSkipOrOverwriteExplicitly()
    {
        var archivePath = CreateArchive("conflict.zip", ("root.txt", "new"));
        var service = new ArchiveService();

        var skipDest = NewDestination("skip");
        File.WriteAllText(Path.Combine(skipDest, "root.txt"), "old");
        var skipOutcome = await service.ExtractEntriesAsync(
            archivePath,
            skipDest,
            new ArchiveExtractOptions(ConflictPolicy: ConflictPolicy.Skip),
            progress: null,
            CancellationToken.None);
        Assert.True(skipOutcome.Success);
        Assert.Equal("old", File.ReadAllText(Path.Combine(skipDest, "root.txt")));

        var overwriteDest = NewDestination("overwrite");
        File.WriteAllText(Path.Combine(overwriteDest, "root.txt"), "old");
        var overwriteOutcome = await service.ExtractEntriesAsync(
            archivePath,
            overwriteDest,
            new ArchiveExtractOptions(ConflictPolicy: ConflictPolicy.Overwrite),
            progress: null,
            CancellationToken.None);
        Assert.True(overwriteOutcome.Success);
        Assert.Equal("new", File.ReadAllText(Path.Combine(overwriteDest, "root.txt")));
    }

    [Fact]
    public async Task ReadEntryBytesIsExactAndBounded()
    {
        var archivePath = CreateArchive("read.zip", ("docs/readme.txt", "hello archive"));
        var service = new ArchiveService();

        var bytes = await service.ReadEntryBytesAsync(
            archivePath, "docs/readme.txt", maxBytes: 64, CancellationToken.None);
        Assert.NotNull(bytes);
        Assert.Equal("hello archive", Encoding.UTF8.GetString(bytes));

        Assert.Null(await service.ReadEntryBytesAsync(
            archivePath, "docs/readme.txt", maxBytes: 4, CancellationToken.None));
        Assert.Null(await service.ReadEntryBytesAsync(
            archivePath, "missing.txt", maxBytes: 64, CancellationToken.None));
    }

    [Fact]
    public async Task SelectedExtractionStillBlocksZipSlip()
    {
        var archivePath = CreateArchive("evil.zip",
            ("../evil.txt", "evil"),
            ("safe.txt", "safe"));
        var destination = NewDestination("safe-root");

        var outcome = await new ArchiveService().ExtractEntriesAsync(
            archivePath,
            destination,
            new ArchiveExtractOptions(),
            progress: null,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Single(outcome.Errors);
        Assert.Equal("safe", File.ReadAllText(Path.Combine(destination, "safe.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task WorkbenchNavigatesVirtualFoldersPreviewsTextAndExtractsSelection()
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        var archivePath = Path.Combine(startDir, "backup.zip");
        WriteArchive(archivePath,
            ("docs/readme.txt", "hello archive"),
            ("docs/config.json", "{\"theme\":\"teal\"}"),
            ("photos/names.txt", "IMG_0001"),
            ("root.txt", "root"));

        var shell = AppHost.CreateShell(AppHost.Build(dataDir, startDir));
        shell.Browser.SetWatchEnabled(false);
        await shell.OpenArchiveWorkbenchAsync(archivePath);

        Assert.Equal(ShellPage.ArchiveWorkbench, shell.Page);
        Assert.True(shell.HasArchive);
        Assert.Equal(4, shell.ArchiveFileCount);
        Assert.Equal(2, shell.ArchiveFolderCount);
        Assert.Equal("backup.zip", shell.ArchiveBreadcrumbs[0].Name);
        Assert.Equal(["docs", "photos", "root.txt"], shell.ArchiveVisibleEntries.Select(row => row.Name).ToArray());

        var docs = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "docs");
        shell.NavigateArchiveFolder(docs.VirtualPath);
        Assert.Equal(["config.json", "readme.txt"], shell.ArchiveVisibleEntries.Select(row => row.Name).ToArray());
        Assert.Equal("docs", shell.ArchiveBreadcrumbs[^1].Name);

        var readme = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "readme.txt");
        await shell.SelectArchiveEntryAsync(readme);
        Assert.Equal("hello archive", shell.ArchivePreviewText);

        var config = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "config.json");
        config.IsChecked = true;
        Assert.Equal(1, shell.ArchiveSelectedCount);

        var destination = Path.Combine(_root, "out");
        shell.ArchiveDestinationPath = destination;
        await shell.ExtractArchiveSelectedCommand.ExecuteAsync(null);

        Assert.Equal("{\"theme\":\"teal\"}", File.ReadAllText(Path.Combine(destination, "docs", "config.json")));
        Assert.False(File.Exists(Path.Combine(destination, "docs", "readme.txt")));

        Assert.True(await shell.TryGoBackAsync());
        Assert.Equal(string.Empty, shell.ArchiveCurrentFolder);
        Assert.True(await shell.TryGoBackAsync());
        Assert.Equal(ShellPage.Browser, shell.Page);
    }

    private string NewDestination(string name) =>
        Directory.CreateDirectory(Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"))).FullName;

    private string CreateArchive(string fileName, params (string EntryName, string Content)[] entries)
    {
        var path = Path.Combine(_root, fileName);
        WriteArchive(path, entries);
        return path;
    }

    private static void WriteArchive(string path, params (string EntryName, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = entry.Open();
            writer.Write(Encoding.UTF8.GetBytes(content));
        }
    }
}
