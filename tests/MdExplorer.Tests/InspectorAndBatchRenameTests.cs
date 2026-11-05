using MdExplorer.Application;
using MdExplorer.Infrastructure;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.2：文件检视（校验和/文件夹大小/监视）与批量重命名规划器的真实 IO 测试。</summary>
public sealed class InspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mdexplorer-inspector-" + Guid.NewGuid().ToString("N")[..8]);

    public InspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Hash_KnownVectors()
    {
        var file = Path.Combine(_root, "abc.txt");
        await File.WriteAllTextAsync(file, "abc", TestContext.Current.CancellationToken);
        var inspector = new FileInspectorService();
        var sha = await inspector.ComputeHashAsync(file, "SHA256", null, TestContext.Current.CancellationToken);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sha);
        var md5 = await inspector.ComputeHashAsync(file, "MD5", null, TestContext.Current.CancellationToken);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", md5);
        await Assert.ThrowsAsync<ArgumentException>(
            () => inspector.ComputeHashAsync(file, "SHA-9999", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FolderSize_NestedSums()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "a", "f1.bin"), new byte[100], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "a", "b", "f2.bin"), new byte[250], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "top.txt"), new byte[7], TestContext.Current.CancellationToken);
        var size = await new FileInspectorService().GetFolderSizeAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(357, size.TotalBytes);
        Assert.Equal(3, size.FileCount);
        Assert.Equal(2, size.FolderCount);
        Assert.False(size.IsPartial);
    }

    [Fact]
    public async Task Watcher_FiresOnCreate()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new FolderWatchService().Watch(_root, () => fired.TrySetResult());
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "x", TestContext.Current.CancellationToken);
        var done = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        Assert.Same(fired.Task, done);
    }
}

public sealed class BatchRenamePlannerTests
{
    private static BatchRenameOptions Opts(string pattern = "{name}_{n}", string find = "", string replace = "",
        int start = 1, int step = 1, string caseMode = "Keep") => new(pattern, find, replace, start, step, caseMode);

    [Fact]
    public void Pattern_CounterAndExtensionKept()
    {
        var rows = BatchRenamePlanner.Plan(["photo.jpg", "song.mp3"], Opts(), []);
        Assert.Equal("photo_1.jpg", rows[0].Proposed);
        Assert.Equal("song_2.mp3", rows[1].Proposed);
        Assert.All(rows, r => Assert.Null(r.Error));
    }

    [Fact]
    public void Pattern_PaddedCounterAndExtToken()
    {
        var rows = BatchRenamePlanner.Plan(["a.txt", "b.txt"], Opts("IMG_{n:3}.{ext}", start: 7), []);
        Assert.Equal("IMG_007.txt", rows[0].Proposed);
        Assert.Equal("IMG_008.txt", rows[1].Proposed);
    }

    [Fact]
    public void FindReplace_And_CaseMode()
    {
        var rows = BatchRenamePlanner.Plan(
            ["Draft Report.PDF"], Opts("{name}", find: "Draft", replace: "Final", caseMode: "Lower"), []);
        Assert.Equal("final report.pdf", rows[0].Proposed);
    }

    [Fact]
    public void Errors_DuplicateWithinBatch_AndSiblingCollision_AndInvalidChars()
    {
        var dup = BatchRenamePlanner.Plan(["a.txt", "b.txt"], Opts("same"), []);
        Assert.All(dup, r => Assert.NotNull(r.Error));

        var collide = BatchRenamePlanner.Plan(["a.txt"], Opts("taken"), ["taken.txt"]);
        Assert.NotNull(collide[0].Error);

        var invalid = BatchRenamePlanner.Plan(["a.txt"], Opts("bad|name"), []);
        Assert.NotNull(invalid[0].Error);
    }

    [Fact]
    public void Step_And_UnchangedAllowed()
    {
        var rows = BatchRenamePlanner.Plan(["f_5.txt"], Opts("{name}", start: 5, step: 5), ["other.txt"]);
        Assert.Equal("f_5.txt", rows[0].Proposed);
        Assert.Null(rows[0].Error);
        Assert.False(rows[0].IsChanged);
    }
}
