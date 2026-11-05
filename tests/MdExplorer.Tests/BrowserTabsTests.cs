using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>浏览器标签页：独立路径/历史/过滤/选集、关闭切换与启动恢复。</summary>
public sealed class BrowserTabsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-tabs-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;
    private readonly string _alphaDir;
    private readonly string _alphaChildDir;
    private readonly string _betaDir;
    private readonly string _betaChildDir;
    private readonly string _gammaDir;

    public BrowserTabsTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        _alphaDir = Directory.CreateDirectory(Path.Combine(_root, "Alpha")).FullName;
        _alphaChildDir = Directory.CreateDirectory(Path.Combine(_alphaDir, "Child")).FullName;
        _betaDir = Directory.CreateDirectory(Path.Combine(_root, "Beta")).FullName;
        _betaChildDir = Directory.CreateDirectory(Path.Combine(_betaDir, "Child")).FullName;
        _gammaDir = Directory.CreateDirectory(Path.Combine(_root, "Gamma")).FullName;

        File.WriteAllText(Path.Combine(_betaChildDir, "alpha-note.txt"), "alpha");
        File.WriteAllText(Path.Combine(_betaChildDir, "beta-note.txt"), "beta");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private ShellViewModel CreateShell()
    {
        var shell = AppHost.CreateShell(AppHost.Build(_dataDir, _startDir));
        // 单元测试没有 Avalonia Dispatcher：关监视，避免后台回调碰 UI 线程。
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    [Fact]
    public async Task TabsKeepIndependentHistoryFilterAndSelection()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_alphaDir, pushHistory: false);
        await browser.NavigateToAsync(_alphaChildDir);
        await browser.OpenInNewTabAsync(_betaDir);

        Assert.Equal(2, browser.Tabs.Count);
        Assert.Equal("2", browser.TabCountText);
        Assert.Equal(_betaDir, browser.CurrentPath);
        Assert.False(browser.CanGoBack); // 新标签有自己的空历史栈

        await browser.NavigateToAsync(_betaChildDir);
        var alphaNote = Assert.Single(browser.Items, item => item.Name == "alpha-note.txt");
        browser.SelectedItems.Add(alphaNote);
        browser.FilterText = "alpha";
        Assert.Single(browser.Items);

        await browser.ActivateTabCommand.ExecuteAsync(browser.Tabs[0]);
        Assert.Equal(_alphaChildDir, browser.CurrentPath);
        Assert.True(browser.CanGoBack);
        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(_alphaDir, browser.CurrentPath);

        await browser.ActivateTabCommand.ExecuteAsync(browser.Tabs[1]);
        Assert.Equal(_betaChildDir, browser.CurrentPath);
        Assert.Equal("alpha", browser.FilterText);
        Assert.Equal("alpha-note.txt", Assert.Single(browser.SelectedItems).Name);
        Assert.True(browser.CanGoBack);

        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(_betaDir, browser.CurrentPath);
    }

    [Fact]
    public async Task ClosingActiveTabActivatesNeighborAndLastTabStaysOpen()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_alphaDir, pushHistory: false);
        await browser.OpenInNewTabAsync(_betaDir);
        await browser.OpenInNewTabAsync(_gammaDir);
        Assert.Equal([_alphaDir, _betaDir, _gammaDir], browser.Tabs.Select(tab => tab.Path));

        await browser.CloseTabCommand.ExecuteAsync(browser.ActiveTab);
        Assert.Equal(_betaDir, browser.CurrentPath);
        Assert.Equal(_betaDir, browser.ActiveTab!.Path);

        await browser.CloseTabCommand.ExecuteAsync(browser.Tabs[0]); // 关掉非活动 Alpha
        Assert.Single(browser.Tabs);
        Assert.Equal(_betaDir, browser.CurrentPath);
        Assert.False(browser.ActiveTab!.CanClose);

        await browser.CloseActiveTabCommand.ExecuteAsync(null);
        Assert.Single(browser.Tabs);
        Assert.Equal("1", browser.TabCountText);
        Assert.Equal(_betaDir, browser.CurrentPath);
    }

    [Fact]
    public async Task MovingTabsPreservesActiveSessionAndRestoresOrder()
    {
        var firstShell = CreateShell();
        var browser = firstShell.Browser;
        await browser.NavigateToAsync(_alphaDir, pushHistory: false);
        await browser.OpenInNewTabAsync(_betaDir);
        await browser.OpenInNewTabAsync(_gammaDir);
        var alpha = browser.Tabs[0];
        var beta = browser.Tabs[1];
        var gamma = browser.Tabs[2];

        Assert.False(browser.MoveTab(beta, 1));
        Assert.True(browser.MoveTab(beta, 0));
        Assert.Equal([_betaDir, _alphaDir, _gammaDir], browser.Tabs.Select(tab => tab.Path));
        Assert.Same(gamma, browser.ActiveTab);
        Assert.Equal(_gammaDir, browser.CurrentPath);

        Assert.True(browser.MoveTab(gamma, 0));
        Assert.Equal([_gammaDir, _betaDir, _alphaDir], browser.Tabs.Select(tab => tab.Path));
        Assert.Same(gamma, browser.ActiveTab);
        Assert.Equal(_gammaDir, browser.CurrentPath);
        Assert.False(browser.MoveTab(null, 0));
        Assert.False(browser.MoveTab(alpha, 99)); // 已在末尾，钳制后无变化

        await firstShell.SettingsStore.SaveSettingsAsync(CancellationToken.None);
        var second = CreateShell().Browser;
        await second.RestoreTabsAsync(_startDir);
        Assert.Equal([_gammaDir, _betaDir, _alphaDir], second.Tabs.Select(tab => tab.Path));
        Assert.Equal(_gammaDir, second.ActiveTab!.Path);
    }

    [Fact]
    public async Task OpenTabsAreRestoredFromSettings()
    {
        var firstShell = CreateShell();
        var first = firstShell.Browser;
        await first.NavigateToAsync(_alphaDir, pushHistory: false);
        await first.OpenInNewTabAsync(_betaDir);
        await firstShell.SettingsStore.SaveSettingsAsync(CancellationToken.None);

        var second = CreateShell().Browser;
        await second.RestoreTabsAsync(_startDir);

        Assert.Equal([_alphaDir, _betaDir], second.Tabs.Select(tab => tab.Path));
        Assert.Equal(_betaDir, second.ActiveTab!.Path);
        Assert.Equal(_betaDir, second.CurrentPath);
    }
}
