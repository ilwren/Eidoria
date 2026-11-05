using MdExplorer.Application;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

public sealed class FileTransferRulesTests
{
    [Theory]
    [InlineData(true, false, false, true)]   // 应用内默认移动
    [InlineData(true, true, false, false)]   // Ctrl 复制
    [InlineData(false, false, false, false)] // 外部默认复制
    [InlineData(false, false, true, true)]   // Shift 移动
    [InlineData(false, true, true, false)]   // Ctrl 优先于 Shift
    public void ShouldMoveFollowsFileManagerModifierConvention(
        bool isInternal, bool ctrl, bool shift, bool expected) =>
        Assert.Equal(expected, FileTransferRules.ShouldMove(isInternal, ctrl, shift));

    [Fact]
    public void SameOrDescendantWalksProvidedParentChain()
    {
        var parents = new Dictionary<string, string?>
        {
            ["content://root/a/b"] = "content://root/a",
            ["content://root/a"] = "content://root",
            ["content://root"] = null,
        };
        string? Parent(string path) => parents.GetValueOrDefault(path);
        static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

        Assert.True(FileTransferRules.IsSameOrDescendant("content://root/a/b", "content://root/a", Parent, Equal));
        Assert.True(FileTransferRules.IsSameOrDescendant("content://root/a", "content://root/a", Parent, Equal));
        Assert.False(FileTransferRules.IsSameOrDescendant("content://root/a", "content://root/a/b", Parent, Equal));
    }
}

/// <summary>系统剪贴板与共享传输管线（剪贴板以内存替身注入；真实平台格式由 Avalonia 实现层负责）。</summary>
public sealed class BrowserClipboardAndTransferTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-transfer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeSystemClipboardService : ISystemClipboardService
    {
        public IReadOnlyList<string> ExternalPaths { get; set; } = [];
        public List<IReadOnlyList<string>> SetCalls { get; } = [];

        public Task SetFilesAsync(IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
        {
            var snapshot = localPaths.ToList();
            SetCalls.Add(snapshot);
            ExternalPaths = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> TryGetFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ExternalPaths);

        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            ExternalPaths = [];
            return Task.CompletedTask;
        }
    }

    private ShellViewModel CreateShell(FakeSystemClipboardService clipboard)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        var shell = AppHost.CreateShell(AppHost.Build(
            dataDirectory: dataDir,
            startFolder: startDir,
            systemClipboard: clipboard));
        // 单元测试没有 Avalonia Dispatcher：关闭文件夹监视，避免后台回调碰 UI 线程。
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    private string CreateFile(string relativeDirectory, string fileName, string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, relativeDirectory)).FullName;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task SelectOnlyFileAsync(ShellViewModel shell, string folder)
    {
        await shell.Browser.NavigateToAsync(folder);
        var item = Assert.Single(shell.Browser.Items);
        shell.Browser.SelectedItems.Add(item);
    }

    [Fact]
    public async Task CopySelectionSyncsLocalFilesToSystemClipboard()
    {
        var clipboard = new FakeSystemClipboardService();
        var shell = CreateShell(clipboard);
        var source = CreateFile("source", "a.txt", "alpha");

        await SelectOnlyFileAsync(shell, Path.GetDirectoryName(source)!);
        await shell.Browser.CopySelectionCommand.ExecuteAsync(null);

        var call = Assert.Single(clipboard.SetCalls);
        Assert.Equal([source], call);
        Assert.Equal([source], clipboard.ExternalPaths);
        Assert.True(shell.Browser.HasClipboard);
    }

    [Fact]
    public async Task CutStaysInternalAndBaselineExternalContentCannotOverrideIt()
    {
        var clipboard = new FakeSystemClipboardService();
        var shell = CreateShell(clipboard);
        var sourceDir = Path.Combine(_root, "from");
        var destDir = Directory.CreateDirectory(Path.Combine(_root, "to")).FullName;
        var source = CreateFile("from", "move.txt", "move me");
        var staleExternal = CreateFile("elsewhere", "stale.txt", "old clipboard");
        clipboard.ExternalPaths = [staleExternal];

        await SelectOnlyFileAsync(shell, sourceDir);
        await shell.Browser.CutSelectionCommand.ExecuteAsync(null);
        Assert.Empty(clipboard.SetCalls); // 跨应用剪切效果无统一编码，剪切暂时不冒充系统复制

        await shell.Browser.RefreshExternalClipboardAsync();
        await shell.Browser.NavigateToAsync(destDir);
        await shell.Browser.PasteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(source));
        Assert.Equal("move me", await File.ReadAllTextAsync(Path.Combine(destDir, "move.txt")));
        Assert.False(File.Exists(Path.Combine(destDir, "stale.txt")));
    }

    [Fact]
    public async Task ExternalClipboardFilesCanBePastedAsCopy()
    {
        var clipboard = new FakeSystemClipboardService();
        var shell = CreateShell(clipboard);
        var external = CreateFile("external", "from-explorer.txt", "hello");
        var destDir = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        clipboard.ExternalPaths = [external];

        await shell.Browser.NavigateToAsync(destDir);
        await shell.Browser.RefreshExternalClipboardAsync();
        Assert.True(shell.Browser.HasClipboard);

        await shell.Browser.PasteCommand.ExecuteAsync(null);
        Assert.True(File.Exists(external));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destDir, "from-explorer.txt")));
    }

    [Fact]
    public async Task TransferUsesSharedPipelineForDropCopy()
    {
        var clipboard = new FakeSystemClipboardService();
        var shell = CreateShell(clipboard);
        var source = CreateFile("drop-source", "drop.txt", "dragged");
        var destDir = Directory.CreateDirectory(Path.Combine(_root, "drop-dest")).FullName;

        await shell.Browser.NavigateToAsync(destDir);
        var outcome = await shell.Browser.TransferItemsAsync([source], destDir, isMove: false);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success, string.Join(";", outcome.Errors.Select(e => e.Message)));
        Assert.Equal("dragged", await File.ReadAllTextAsync(Path.Combine(destDir, "drop.txt")));
    }

    [Fact]
    public async Task TransferRejectsDestinationInsideSourceFolder()
    {
        var clipboard = new FakeSystemClipboardService();
        var shell = CreateShell(clipboard);
        var sourceDir = Directory.CreateDirectory(Path.Combine(_root, "parent")).FullName;
        var childDir = Directory.CreateDirectory(Path.Combine(sourceDir, "child")).FullName;

        Assert.False(shell.Browser.CanTransferItems([sourceDir], childDir));
        var outcome = await shell.Browser.TransferItemsAsync([sourceDir], childDir, isMove: false);

        Assert.Null(outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(childDir));
    }
}
