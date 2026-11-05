using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>XL 双窗格的第二会话、跨窗格传输与窄屏会话收纳。</summary>
public sealed class DualPaneBrowserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eidoria-dual-pane-").FullName;

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
        shell.SecondaryBrowser.SetWatchEnabled(false);
        shell.Page = ShellPage.Browser;
        return shell;
    }

    private string CreateFile(string relativeDirectory, string fileName, string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, relativeDirectory)).FullName;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileItemViewModel Select(BrowserViewModel browser, string name)
    {
        var item = Assert.Single(browser.Items, candidate => candidate.Name == name);
        browser.SelectedItems.Clear();
        browser.SelectedItems.Add(item);
        return item;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task DualPaneRequiresExtraLargeAndKeepsIndependentSessions()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("left", "left.txt", "left");
        var rightFile = CreateFile("right", "right.txt", "right");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        await shell.Browser.NavigateToAsync(leftDir);

        shell.SetViewportSize(1440, 900);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        Assert.False(shell.IsDualPaneEnabled);
        Assert.False(shell.ShowDualPane);

        shell.SettingsStore.Settings.DualPanePath = rightDir;
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);

        Assert.True(shell.IsDualPaneEnabled);
        Assert.True(shell.ShowDualPane);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Same(shell.SecondaryBrowser, shell.ActiveBrowser);

        _ = Select(shell.Browser, "left.txt");
        _ = Select(shell.SecondaryBrowser, "right.txt");
        Assert.Equal("left.txt", Assert.Single(shell.Browser.SelectedItems).Name);
        Assert.Equal("right.txt", Assert.Single(shell.SecondaryBrowser.SelectedItems).Name);
        await WaitForAsync(() => shell.Preview.CurrentItem?.FullPath == rightFile);

        shell.ActivatePrimaryPane();
        Assert.Same(shell.Browser, shell.ActiveBrowser);
        await WaitForAsync(() => shell.Preview.CurrentItem?.FullPath == leftFile);
        Assert.Equal(rightDir, shell.SettingsStore.Settings.DualPanePath);
    }

    /// <summary>v0.28：B 自己有可编辑地址栏，提交只移动 B 的会话，A 的路径/编辑态/面包屑不受影响。</summary>
    [Fact]
    public async Task SecondaryAddressBarEditsOnlyItsOwnSession()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("addr-left", "left.txt", "left");
        var rightFile = CreateFile("addr-right", "right.txt", "right");
        var thirdFile = CreateFile("addr-third", "third.txt", "third");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        var thirdDir = Path.GetDirectoryName(thirdFile)!;

        await shell.Browser.NavigateToAsync(leftDir);
        shell.SettingsStore.Settings.DualPanePath = rightDir;
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.True(shell.ShowSecondaryBreadcrumbBar);

        shell.ActivateSecondaryPane();
        var notified = new List<string?>();
        shell.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        shell.SecondaryBrowser.BeginEditAddressCommand.Execute(null);

        Assert.True(shell.SecondaryBrowser.IsAddressEditing);
        Assert.False(shell.Browser.IsAddressEditing);
        Assert.False(shell.ShowSecondaryBreadcrumbBar);
        Assert.True(shell.IsEditingSecondaryAddress);
        Assert.Contains(nameof(ShellViewModel.ShowSecondaryBreadcrumbBar), notified);
        Assert.Contains(nameof(ShellViewModel.IsEditingSecondaryAddress), notified);

        // 提交只作用于 B：A 的路径与选集完全不动。
        _ = Select(shell.Browser, "left.txt");
        Assert.True(await shell.SecondaryBrowser.NavigateToAddressAsync(thirdDir));
        Assert.Equal(thirdDir, shell.SecondaryBrowser.CurrentPath);
        Assert.False(shell.SecondaryBrowser.IsAddressEditing);
        Assert.True(shell.ShowSecondaryBreadcrumbBar);
        Assert.Equal(leftDir, shell.Browser.CurrentPath);
        Assert.False(shell.Browser.IsAddressEditing);
        Assert.Equal("left.txt", Assert.Single(shell.Browser.SelectedItems).Name);
        Assert.Equal(thirdDir, shell.SettingsStore.Settings.DualPanePath);

        // 错误路径留在编辑态并给出提示，Esc（取消）回到当前路径。
        Assert.False(await shell.SecondaryBrowser.NavigateToAddressAsync(Path.Combine(thirdDir, "missing")));
        Assert.True(shell.SecondaryBrowser.IsAddressEditing);
        Assert.True(shell.SecondaryBrowser.HasAddressError);
        Assert.True(shell.IsEditingSecondaryAddress);
        shell.SecondaryBrowser.CancelEditAddressCommand.Execute(null);
        Assert.False(shell.IsEditingSecondaryAddress);
        Assert.Equal(thirdDir, shell.SecondaryBrowser.AddressText);
        Assert.False(shell.SecondaryBrowser.HasAddressError);
        Assert.Equal(thirdDir, shell.SecondaryBrowser.CurrentPath);
    }

    /// <summary>v0.29：B 有自己的搜索面板，筛选只作用于 B；返回键先收面板，再谈后退。</summary>
    [Fact]
    public async Task SecondarySearchFiltersOnlyItsOwnSession()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("search-left", "left-only.txt", "left");
        var rightFile = CreateFile("search-right", "right-only.txt", "right");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;

        await shell.Browser.NavigateToAsync(leftDir);
        shell.SettingsStore.Settings.DualPanePath = rightDir;
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        shell.ActivateSecondaryPane();

        shell.SecondaryBrowser.OpenSearchPanelCommand.Execute(null);
        Assert.True(shell.SecondaryBrowser.IsSearchPanelOpen);
        Assert.False(shell.Browser.IsSearchPanelOpen);
        Assert.True(shell.IsSecondaryNotSearching);

        shell.SecondaryBrowser.FilterText = "right";
        await WaitForAsync(() => shell.SecondaryBrowser.Items.Count == 1);
        Assert.Equal("right-only.txt", Assert.Single(shell.SecondaryBrowser.Items).Name);
        // A 的列表、筛选词与面板状态都保持原样
        Assert.Contains(shell.Browser.Items, item => item.Name == "left-only.txt");
        Assert.Equal(string.Empty, shell.Browser.FilterText);
        Assert.False(shell.Browser.IsSearchPanelOpen);

        // 返回（Esc）先收 B 的搜索面板，不触发 B 的后退
        Assert.True(await shell.TryGoBackAsync());
        Assert.False(shell.SecondaryBrowser.IsSearchPanelOpen);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.True(shell.IsSecondaryNotSearching);
        _ = leftFile;
    }

    /// <summary>v0.30：B 有自己的标签轨，标签互不串台，且不写入全局标签会话；交换目录后两侧各就各位。</summary>
    [Fact]
    public async Task SecondaryPaneTabsAreLocalAndSwapPanesExchangesFolders()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("tab-left", "left.txt", "left");
        var rightFile = CreateFile("tab-right", "right.txt", "right");
        var thirdFile = CreateFile("tab-third", "third.txt", "third");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        var thirdDir = Path.GetDirectoryName(thirdFile)!;

        await shell.Browser.NavigateToAsync(leftDir);
        shell.SettingsStore.Settings.DualPanePath = rightDir;
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        Assert.False(shell.ShowSecondaryTabs);

        var primaryTabCount = shell.Browser.Tabs.Count;
        await shell.SecondaryBrowser.OpenInNewTabAsync(thirdDir);

        Assert.Equal(2, shell.SecondaryBrowser.Tabs.Count);
        Assert.Equal(primaryTabCount, shell.Browser.Tabs.Count);
        Assert.True(shell.ShowSecondaryTabs);
        Assert.Equal(thirdDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Equal(leftDir, shell.Browser.CurrentPath);
        // B 不写全局标签会话，避免与 A 的 OpenTabPaths 互相覆盖
        Assert.DoesNotContain(shell.SettingsStore.Settings.OpenTabPaths, path => path == thirdDir);

        // 切回 B 的第一个标签，A 的活动标签与目录都不动
        var primaryActive = shell.Browser.ActiveTab;
        var firstTab = shell.SecondaryBrowser.Tabs.First(tab => tab.Path is not null && tab.Path == rightDir);
        await shell.SecondaryBrowser.ActivateTabCommand.ExecuteAsync(firstTab);
        Assert.Same(primaryActive, shell.Browser.ActiveTab);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Equal(leftDir, shell.Browser.CurrentPath);

        // 关闭 B 的第二个标签后标签轨收起
        var secondTab = shell.SecondaryBrowser.Tabs.First(tab => ReferenceEquals(tab, firstTab) is false);
        await shell.SecondaryBrowser.CloseTabCommand.ExecuteAsync(secondTab);
        Assert.Single(shell.SecondaryBrowser.Tabs);
        Assert.False(shell.ShowSecondaryTabs);

        // 交换两个窗格的目录
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        await shell.SwapPanesCommand.ExecuteAsync(null);
        Assert.Equal(leftDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Equal(rightDir, shell.Browser.CurrentPath);
        Assert.Equal(leftDir, shell.SettingsStore.Settings.DualPanePath);
    }

    [Fact]
    public async Task CrossPaneCopyAndMoveRefreshBothPanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var copySource = CreateFile("copy-left", "copy.txt", "copy-me");
        var moveSource = CreateFile("move-right", "move.txt", "move-me");
        var leftDir = Path.GetDirectoryName(copySource)!;
        var rightDir = Path.GetDirectoryName(moveSource)!;
        shell.SettingsStore.Settings.DualPanePath = rightDir;
        await shell.Browser.NavigateToAsync(leftDir);
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);

        _ = Select(shell.Browser, "copy.txt");
        Assert.True(shell.CanTransferPrimaryToSecondary);
        await shell.CopyPrimaryToSecondaryCommand.ExecuteAsync(null);

        var copied = Path.Combine(rightDir, "copy.txt");
        Assert.Equal("copy-me", await File.ReadAllTextAsync(copied, ct));
        Assert.Contains(shell.SecondaryBrowser.Items, item => item.Name == "copy.txt");

        _ = Select(shell.SecondaryBrowser, "move.txt");
        Assert.True(shell.CanTransferSecondaryToPrimary);
        await shell.MoveSecondaryToPrimaryCommand.ExecuteAsync(null);

        Assert.False(File.Exists(moveSource));
        Assert.Equal("move-me", await File.ReadAllTextAsync(Path.Combine(leftDir, "move.txt"), ct));
        Assert.Contains(shell.Browser.Items, item => item.Name == "move.txt");
        Assert.DoesNotContain(shell.SecondaryBrowser.Items, item => item.Name == "move.txt");
    }

    [Fact]
    public async Task CutInPrimaryCanPasteInSecondary()
    {
        var shell = CreateShell();
        var source = CreateFile("clip-left", "cut.txt", "cut-me");
        var rightFile = CreateFile("clip-right", "keep.txt", "keep");
        var leftDir = Path.GetDirectoryName(source)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        await shell.Browser.NavigateToAsync(leftDir);
        await shell.SecondaryBrowser.NavigateToAsync(rightDir, pushHistory: false);

        _ = Select(shell.Browser, "cut.txt");
        await WaitForAsync(() => !shell.Preview.IsLoading && shell.Preview.TextContent == "cut-me");
        await shell.Browser.CutSelectionCommand.ExecuteAsync(null);
        Assert.True(shell.Browser.HasClipboard);
        Assert.True(shell.SecondaryBrowser.HasClipboard);

        await shell.SecondaryBrowser.PasteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(source));
        Assert.Equal("cut-me", await File.ReadAllTextAsync(
            Path.Combine(rightDir, "cut.txt"), TestContext.Current.CancellationToken));
        Assert.Contains(shell.SecondaryBrowser.Items, item => item.Name == "cut.txt");
        Assert.False(shell.Browser.HasClipboard);
        Assert.False(shell.SecondaryBrowser.HasClipboard);
    }

    [Fact]
    public async Task LeavingExtraLargeRetainsSecondarySessionAsBackgroundTab()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("fallback-left", "a.txt", "a");
        var rightFile = CreateFile("fallback-right", "b.txt", "b");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        shell.SettingsStore.Settings.DualPanePath = rightDir;
        await shell.Browser.NavigateToAsync(leftDir);
        shell.SetViewportSize(1920, 1080);
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        Assert.DoesNotContain(shell.Browser.Tabs, tab => tab.Path == rightDir);

        shell.SetViewportSize(1440, 900);

        Assert.True(shell.IsDualPaneEnabled);
        Assert.False(shell.ShowDualPane);
        Assert.False(shell.IsSecondaryPaneActive);
        Assert.Same(shell.Browser, shell.ActiveBrowser);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Contains(shell.Browser.Tabs, tab => tab.Path == rightDir && !tab.IsActive);

        shell.SetViewportSize(1920, 1080);
        Assert.True(shell.ShowDualPane);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
    }

    [Fact]
    public async Task SecondaryNavigationDoesNotOverwritePrimaryTabPersistence()
    {
        var shell = CreateShell();
        var leftFile = CreateFile("persist-left", "a.txt", "a");
        var rightFile = CreateFile("persist-right", "b.txt", "b");
        var leftDir = Path.GetDirectoryName(leftFile)!;
        var rightDir = Path.GetDirectoryName(rightFile)!;
        await shell.Browser.NavigateToAsync(leftDir);
        await WaitForAsync(() => shell.SettingsStore.Settings.OpenTabPaths.Contains(leftDir));

        await shell.SecondaryBrowser.NavigateToAsync(rightDir, pushHistory: false);
        await shell.SecondaryBrowser.WhenIdleAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(leftDir, shell.Browser.CurrentPath);
        Assert.Equal(rightDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Contains(leftDir, shell.SettingsStore.Settings.OpenTabPaths);
        Assert.DoesNotContain(rightDir, shell.SettingsStore.Settings.OpenTabPaths);
        Assert.Equal(rightDir, shell.SettingsStore.Settings.DualPanePath);
    }
}
