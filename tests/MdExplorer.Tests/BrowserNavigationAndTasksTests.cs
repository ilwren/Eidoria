using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.7 导航效率：地址栏、最近访问、复制路径/定位与增强标签页。</summary>
public sealed class BrowserNavigationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eidoria-navigation-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;
    private readonly string _docsDir;
    private readonly string _otherDir;
    private readonly string _filePath;

    public BrowserNavigationTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        _docsDir = Directory.CreateDirectory(Path.Combine(_startDir, "docs")).FullName;
        _otherDir = Directory.CreateDirectory(Path.Combine(_startDir, "other")).FullName;
        _filePath = Path.Combine(_docsDir, "note.txt");
        File.WriteAllText(_filePath, "hello");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeSystemClipboardService : ISystemClipboardService
    {
        public IReadOnlyList<string> ExternalPaths { get; private set; } = [];
        public string? Text { get; private set; }

        public Task SetFilesAsync(IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
        {
            ExternalPaths = localPaths.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> TryGetFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ExternalPaths);

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            ExternalPaths = [];
            return Task.CompletedTask;
        }
    }

    private ShellViewModel CreateShell(FakeSystemClipboardService? clipboard = null)
    {
        var shell = AppHost.CreateShell(AppHost.Build(
            dataDirectory: _dataDir,
            startFolder: _startDir,
            systemClipboard: clipboard));
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    [Fact]
    public async Task AddressBarResolvesRelativeFolderSelectsFileAndKeepsErrorsEditable()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_startDir, pushHistory: false);

        browser.BeginEditAddressCommand.Execute(null);
        Assert.True(browser.IsAddressEditing);
        Assert.Equal(_startDir, browser.AddressText);

        Assert.True(await browser.NavigateToAddressAsync("docs"));
        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.False(browser.IsAddressEditing);

        Assert.True(await browser.NavigateToAddressAsync(_filePath));
        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.Equal(_filePath, Assert.Single(browser.SelectedItems).FullPath);

        Assert.True(await browser.NavigateToAddressAsync("../other"));
        Assert.Equal(_otherDir, browser.CurrentPath);

        Assert.False(await browser.NavigateToAddressAsync(Path.Combine(_startDir, "missing")));
        Assert.True(browser.IsAddressEditing);
        Assert.True(browser.HasAddressError);

        browser.CancelEditAddressCommand.Execute(null);
        Assert.False(browser.IsAddressEditing);
        Assert.Equal(_otherDir, browser.AddressText);
    }

    [Fact]
    public async Task RecentFoldersAndFilesAreDedupedSurfacedAndClearable()
    {
        var shell = CreateShell();
        var browser = shell.Browser;

        await browser.NavigateToAsync(_docsDir, pushHistory: false);
        await browser.NavigateToAsync(_otherDir);
        await browser.NavigateToAsync(_docsDir);
        await browser.RecordRecentFileAsync(_filePath);

        var settings = shell.SettingsStore.Settings;
        Assert.Equal(_docsDir, settings.RecentFolderPaths[0]);
        Assert.Equal([_filePath], settings.RecentFilePaths);

        // RecentsChanged 由 Shell 以事件方式异步刷新主页集合；等它落定后断言（不在无字体服务的单元进程走完整 RefreshHome）。
        for (var i = 0; i < 50 && (shell.RecentFolders.Count == 0 || shell.RecentFiles.Count == 0); i++)
            await Task.Delay(20);
        Assert.Equal(_docsDir, shell.RecentFolders[0].Path);
        Assert.Equal(_filePath, Assert.Single(shell.RecentFiles).FullPath);

        await browser.NavigateToAsync(_otherDir);
        await shell.OpenRecentFileAsync(shell.RecentFiles[0]);
        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.Equal(_filePath, Assert.Single(browser.SelectedItems).FullPath);

        await shell.ClearRecentsCommand.ExecuteAsync(null);
        Assert.Empty(settings.RecentFolderPaths);
        Assert.Empty(settings.RecentFilePaths);
        Assert.Empty(shell.RecentFolders);
        Assert.Empty(shell.RecentFiles);
    }

    [Fact]
    public async Task CopyPathWritesTextAndClearsPendingFileClipboard()
    {
        var clipboard = new FakeSystemClipboardService();
        var browser = CreateShell(clipboard).Browser;

        await browser.NavigateToAsync(_docsDir, pushHistory: false);
        browser.SelectedItems.Add(Assert.Single(browser.Items));
        await browser.CopySelectionCommand.ExecuteAsync(null);
        Assert.True(browser.HasClipboard);

        await browser.CopyPathCommand.ExecuteAsync(null);
        Assert.Equal(_filePath, clipboard.Text);
        Assert.Empty(clipboard.ExternalPaths);
        Assert.False(browser.HasClipboard);

        await browser.CopyCurrentPathCommand.ExecuteAsync(null);
        Assert.Equal(_docsDir, clipboard.Text);
    }

    [Fact]
    public async Task OpenContainingFolderSelectsItemFromElsewhere()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_docsDir, pushHistory: false);
        var file = Assert.Single(browser.Items);

        await browser.NavigateToAsync(_otherDir);
        await browser.OpenContainingFolderCommand.ExecuteAsync(file);

        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.Equal(_filePath, Assert.Single(browser.SelectedItems).FullPath);
    }

    [Fact]
    public async Task DuplicatePreservesHistoryCloseOthersAndReopenRestoreSession()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_startDir, pushHistory: false);
        await browser.NavigateToAsync(_docsDir);

        await browser.DuplicateTabCommand.ExecuteAsync(browser.ActiveTab);
        Assert.Equal(2, browser.Tabs.Count);
        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.True(browser.CanGoBack);
        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(_startDir, browser.CurrentPath);

        await browser.OpenInNewTabAsync(_otherDir);
        Assert.Equal(3, browser.Tabs.Count);
        await browser.CloseOtherTabsCommand.ExecuteAsync(browser.Tabs.Single(tab => tab.Path == _docsDir));
        Assert.Single(browser.Tabs);
        Assert.Equal(_docsDir, browser.CurrentPath);
        Assert.True(browser.HasClosedTabs);

        await browser.ReopenClosedTabCommand.ExecuteAsync(null);
        Assert.Equal(2, browser.Tabs.Count);
        Assert.Equal(_startDir, browser.CurrentPath); // 原左起第一个被关标签先恢复
    }
}

/// <summary>任务中心：成功/失败留档、串行排队与逐项取消。</summary>
public sealed class OperationTaskCenterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eidoria-tasks-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private ShellViewModel CreateShell()
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "data")).FullName;
        var startDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "start")).FullName;
        var shell = AppHost.CreateShell(AppHost.Build(dataDir, startDir));
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    [Fact]
    public async Task CompletedAndFailedOperationsAreRecordedWithoutModalProgress()
    {
        var shell = CreateShell();

        var success = await shell.RunOperationAsync("复制演示", (progress, _) =>
        {
            progress.Report(new OperationProgress(0.5, "a.txt", 1, 2));
            return Task.FromResult(new OperationOutcome(2, [], []));
        });
        Assert.True(success.Success);
        var completed = Assert.Single(shell.OperationTasks);
        Assert.Equal(OperationTaskStatus.Completed, completed.Status);
        Assert.True(completed.IsCompleted);
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Equal("100%", completed.ProgressPercentText);
        Assert.Equal("0", shell.ActiveOperationTaskCountText);
        Assert.False(shell.IsDialogOpen);

        var failed = await shell.RunOperationAsync("删除演示", (_, _) =>
            Task.FromResult(new OperationOutcome(1, [new OperationError("b.txt", "拒绝访问")], [])));
        Assert.False(failed.Success);
        Assert.Equal(OperationTaskStatus.Failed, shell.OperationTasks[0].Status);
        Assert.True(shell.OperationTasks[0].IsFailed);
        Assert.Contains("b.txt", shell.OperationTasks[0].ErrorText);
        Assert.True(shell.IsTaskCenterOpen);
    }

    [Fact]
    public async Task OperationsAreQueuedSeriallyAndCanBeCancelledIndividually()
    {
        var shell = CreateShell();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        var first = shell.RunOperationAsync("第一个", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
            return new OperationOutcome(1, [], []);
        });
        await firstStarted.Task;

        var second = shell.RunOperationAsync("第二个", (_, _) =>
        {
            secondStarted = true;
            return Task.FromResult(new OperationOutcome(1, [], []));
        });
        var queued = shell.OperationTasks.Single(task => task.Title == "第二个");
        Assert.Equal(OperationTaskStatus.Queued, queued.Status);
        Assert.False(secondStarted);

        queued.CancelCommand.Execute(null);
        var secondOutcome = await second;
        Assert.True(secondOutcome.Cancelled);
        Assert.Equal(OperationTaskStatus.Cancelled, queued.Status);
        Assert.False(secondStarted);

        releaseFirst.TrySetResult();
        Assert.True((await first).Success);
    }
}
