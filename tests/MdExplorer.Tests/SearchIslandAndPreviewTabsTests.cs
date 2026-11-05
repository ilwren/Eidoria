using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.11：灵动岛式搜索面板状态、返回优先级、预览双页签与窄屏悬浮预览开关。</summary>
public sealed class SearchIslandAndPreviewTabsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-search-island-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;
    private readonly string _childDir;
    private readonly string _needlePath;
    private readonly string _plainPath;
    private readonly string _notesPath;

    public SearchIslandAndPreviewTabsTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        _childDir = Directory.CreateDirectory(Path.Combine(_startDir, "Child")).FullName;
        _needlePath = Path.Combine(_childDir, "needle.txt");
        _plainPath = Path.Combine(_startDir, "plain.txt");
        File.WriteAllText(_needlePath, "needle");
        File.WriteAllText(_plainPath, "plain");
        _notesPath = Path.Combine(_startDir, "notes.txt");
        File.WriteAllText(_notesPath, "notes");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private ShellViewModel CreateShell()
    {
        var shell = AppHost.CreateShell(AppHost.Build(_dataDir, _startDir));
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    [Fact]
    public async Task SearchPanelFiltersThenRecursesAndBackClosesPanelBeforeExitingSearch()
    {
        var shell = CreateShell();
        var browser = shell.Browser;
        await shell.OpenFolderAsync(_startDir);

        browser.OpenSearchPanelCommand.Execute(null);
        Assert.True(browser.IsSearchPanelOpen);
        Assert.False(browser.HasActiveSearch);
        Assert.Equal("搜索", browser.SearchIslandText);
        Assert.Equal(string.Empty, browser.SearchResultBadgeText);
        Assert.Contains("当前列表筛选", browser.SearchPanelScopeText, StringComparison.Ordinal);

        browser.FilterText = "needle";
        Assert.True(browser.HasActiveSearch);
        Assert.Equal("needle", browser.SearchIslandText);
        Assert.Equal("0", browser.SearchResultBadgeText);
        Assert.Equal("当前列表匹配 0 项", browser.SearchPanelStatusText);
        Assert.Empty(browser.Items); // needle 只在子文件夹里，列表筛选看不到

        await browser.SearchRecursiveFromPanelCommand.ExecuteAsync(null);
        Assert.False(browser.IsSearchPanelOpen);
        Assert.True(browser.IsSearchMode);
        Assert.Equal("搜索到 1 项", browser.SearchIslandText);
        Assert.Equal("1", browser.SearchResultBadgeText);
        Assert.Equal("搜索到 1 项", browser.SearchPanelStatusText);
        Assert.Contains("递归搜索", browser.SearchPanelScopeText, StringComparison.Ordinal);
        Assert.Equal(_needlePath, Assert.Single(browser.Items).FullPath);

        browser.IsSearchPanelOpen = true;
        Assert.True(shell.WouldHandleBack);
        Assert.True(await shell.TryGoBackAsync());
        Assert.False(browser.IsSearchPanelOpen);
        Assert.True(browser.IsSearchMode); // 第一次返回只收面板，不退出搜索

        Assert.True(await shell.TryGoBackAsync());
        Assert.False(browser.IsSearchMode);
        Assert.Equal(string.Empty, browser.FilterText);
        Assert.Equal(_startDir, browser.CurrentPath);
    }

    [Fact]
    public async Task ClosingPreviewKeepsCheapTextPayload()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        var item = shell.Browser.Items.First(entry => entry.Name == "plain.txt");
        shell.Browser.SelectedItems.Add(item);
        for (var attempt = 0; attempt < 100 && shell.Preview.TextContent is null; attempt++)
            await Task.Delay(20);
        Assert.Equal("plain", shell.Preview.TextContent);
        Assert.False(shell.Preview.IsPayloadReleased);

        // 文本只有几百 KB：关闭预览不必跟着丢，重开时还是同一份内容。
        shell.IsPreviewPageOpen = false;
        Assert.Null(shell.Preview.Image);
        Assert.Null(shell.Preview.OfficeDocument);
        Assert.False(shell.Preview.IsPayloadReleased);
        Assert.Equal("plain", shell.Preview.TextContent);

        // 位图 / PDF / 直接控件 / 密码库这些重负载由截图测试与 KeePass 用例覆盖：
        // 普通单测没有 IPlatformRenderInterface，造不出真位图。
        shell.IsPreviewPageOpen = true;
        for (var attempt = 0; attempt < 100 && shell.Preview.TextContent is null; attempt++)
            await Task.Delay(20);
        Assert.Equal("plain.txt", shell.Preview.Title);
        Assert.Equal("plain", shell.Preview.TextContent);
        Assert.False(shell.Preview.IsPayloadReleased);
    }

    [Fact]
    public async Task CompactAddressEditingGivesPathFieldTheWholeRow()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);

        shell.SetViewportSize(390, 844);
        Assert.True(shell.ShowPathRowActions);

        shell.Browser.BeginEditAddressCommand.Execute(null);
        Assert.True(shell.Browser.IsAddressEditing);
        Assert.False(shell.ShowPathRowActions);

        shell.SetViewportSize(1100, 760);
        Assert.True(shell.ShowPathRowActions);

        shell.Browser.CancelEditAddressCommand.Execute(null);
        shell.SetViewportSize(390, 844);
        Assert.True(shell.ShowPathRowActions);
    }

    [Fact]
    public async Task LeavingBrowserPageClosesSearchPanel()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        shell.Browser.IsSearchPanelOpen = true;

        shell.Page = ShellPage.Home;

        Assert.False(shell.Browser.IsSearchPanelOpen);
    }

    [Fact]
    public async Task PreviewTabsDefaultToContentForFilesAndPropertiesForFolders()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        var folder = Assert.Single(shell.Browser.Items, item => item.Name == "Child");
        var file = Assert.Single(shell.Browser.Items, item => item.Name == "plain.txt");

        await shell.Preview.LoadAsync(folder.Model);
        Assert.Equal(1, shell.Preview.SelectedTabIndex);
        Assert.False(shell.Preview.CanOpen);
        Assert.False(shell.Preview.HasQuickFacts);
        Assert.NotEmpty(shell.Preview.MetadataRows);

        await shell.Preview.LoadAsync(file.Model);
        Assert.Equal(0, shell.Preview.SelectedTabIndex);
        Assert.True(shell.Preview.CanOpen);
        Assert.True(shell.Preview.HasQuickFacts);
        Assert.False(string.IsNullOrWhiteSpace(shell.Preview.QuickSizeText));
        Assert.False(string.IsNullOrWhiteSpace(shell.Preview.QuickModifiedText));
        Assert.False(shell.Preview.HasImageDimensions);
        Assert.Equal("plain", shell.Preview.TextContent);
    }

    [Fact]
    public async Task FloatingPreviewLoadsTheTappedFileEvenWhenTheSelectionDidNotFeedPreview()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        var file = Assert.Single(shell.Browser.Items, item => item.Name == "plain.txt");

        // 预览被清空（上一轮返回、切换窗格、页面切换都可能出现），此时点开文件：
        // 浮窗不能展开成一片空白——内容必须由 OnFileOpened 自己核对。
        await shell.Preview.LoadAsync(null);
        Assert.False(shell.Preview.HasItem);

        shell.SetViewportSize(390, 844);
        shell.OnFileOpened(file);
        Assert.True(shell.IsPreviewPageOpen);

        // HasItem 在解码之前就置位了，所以要等内容真正落地再断言。
        for (var i = 0; i < 100 && shell.Preview.TextContent is null; i++)
            await Task.Delay(20);

        Assert.True(shell.Preview.HasItem);
        Assert.Equal(_plainPath, shell.Preview.CurrentItem!.FullPath);
        Assert.Equal("plain.txt", shell.Preview.Title);
        Assert.Equal("plain", shell.Preview.TextContent);
    }

    [Fact]
    public async Task StalePreviewLoadDoesNotClearTheNewerItem()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        var plain = Assert.Single(shell.Browser.Items, item => item.Name == "plain.txt");
        var needle = Assert.Single(shell.Browser.Items, item => item.Name == "notes.txt");

        // 两次 LoadAsync 背靠背发出（选集 Clear + Add 就是这个形状）：
        // 先发起的旧代次不许在 await 之后把新项的状态清掉。
        var stale = shell.Preview.LoadAsync(plain.Model);
        var fresh = shell.Preview.LoadAsync(needle.Model);
        await Task.WhenAll(stale, fresh);

        Assert.Equal(_notesPath, shell.Preview.CurrentItem!.FullPath);
        Assert.True(shell.Preview.HasItem);
        Assert.Equal("notes.txt", shell.Preview.Title);
        Assert.Equal("notes", shell.Preview.TextContent); // 旧项（plain）的内容不许留在新项上
    }

    [Fact]
    public async Task FloatingPreviewOpensOnlyOnCompactOrMediumAndBackOrderIsPanelThenPreview()
    {
        var shell = CreateShell();
        await shell.OpenFolderAsync(_startDir);
        var file = Assert.Single(shell.Browser.Items, item => item.Name == "plain.txt");

        shell.SetViewportSize(1100, 760);
        shell.OnFileOpened(file);
        Assert.False(shell.IsPreviewPageOpen);

        shell.SetViewportSize(390, 844);
        shell.OnFileOpened(file);
        Assert.True(shell.IsPreviewPageOpen);

        shell.Browser.IsSearchPanelOpen = true;
        Assert.True(await shell.TryGoBackAsync());
        Assert.False(shell.Browser.IsSearchPanelOpen);
        Assert.True(shell.IsPreviewPageOpen);

        Assert.True(await shell.TryGoBackAsync());
        Assert.False(shell.IsPreviewPageOpen);
    }
}
