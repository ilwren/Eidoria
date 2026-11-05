using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.25：文件面渐进分页与下拉刷新状态；全选、定位和搜索计数仍以完整结果集为准。</summary>
public sealed class BrowserPagingAndRefreshTests : IDisposable
{
    private const int DirectoryCount = 260;
    private const int SearchCount = 135;

    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-paging-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;
    private readonly string _manyDir;
    private readonly string _searchDir;

    public BrowserPagingAndRefreshTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        _manyDir = Directory.CreateDirectory(Path.Combine(_startDir, "Many")).FullName;
        _searchDir = Directory.CreateDirectory(Path.Combine(_startDir, "Search")).FullName;

        for (var i = 0; i < DirectoryCount; i++)
            File.WriteAllText(Path.Combine(_manyDir, $"item-{i:000}.txt"), "paging");
        for (var i = 0; i < SearchCount; i++)
            File.WriteAllText(Path.Combine(_searchDir, $"needle-{i:000}.txt"), "search");
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
        shell.SecondaryBrowser.SetWatchEnabled(false);
        return shell;
    }

    [Fact]
    public async Task DirectoryMaterializesFirstPageAndLoadMoreAppendsUntilComplete()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_manyDir, pushHistory: false);

        Assert.Equal(browser.PageSize, browser.Items.Count);
        Assert.Equal(browser.PageSize, browser.VisibleItemCount);
        Assert.Equal(DirectoryCount, browser.TotalItemCount);
        Assert.True(browser.HasMoreItems);
        Assert.True(browser.CanLoadMoreItems);
        Assert.True(browser.ShowLoadMoreFooter);
        Assert.Equal("已显示 120 / 260 项", browser.PagingStatusText);
        Assert.Equal(browser.PagingStatusText, browser.StatusText);
        Assert.Equal("item-000.txt", browser.Items[0].Name);
        Assert.Equal("item-119.txt", browser.Items[^1].Name);

        await browser.LoadMoreItemsCommand.ExecuteAsync(null);
        Assert.Equal(240, browser.Items.Count);
        Assert.Equal("item-239.txt", browser.Items[^1].Name);
        Assert.True(browser.HasMoreItems);
        Assert.False(browser.IsLoadingMore);

        await browser.LoadMoreItemsCommand.ExecuteAsync(null);
        Assert.Equal(DirectoryCount, browser.Items.Count);
        Assert.Equal("item-259.txt", browser.Items[^1].Name);
        Assert.False(browser.HasMoreItems);
        Assert.False(browser.CanLoadMoreItems);
        Assert.False(browser.ShowLoadMoreFooter);
        Assert.Equal("260 个项目", browser.StatusText);
    }

    [Fact]
    public async Task SelectAllAndFileLocationRevealBeyondFirstPage()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        browser.SelectAllCommand.Execute(null);

        Assert.Equal(DirectoryCount, browser.Items.Count);
        Assert.Equal(DirectoryCount, browser.SelectedItems.Count);
        Assert.Equal(DirectoryCount, browser.SelectedCount);

        await browser.NavigateToAsync(_startDir, pushHistory: false);
        var target = Path.Combine(_manyDir, "item-259.txt");
        await browser.NavigateToFileLocationAsync(target);

        Assert.Equal(_manyDir, browser.CurrentPath);
        Assert.Equal(DirectoryCount, browser.Items.Count);
        Assert.Equal(target, Assert.Single(browser.SelectedItems).FullPath);
    }

    [Fact]
    public async Task FilteringReportsTotalMatchesAndResetsPaging()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        await browser.LoadMoreItemsCommand.ExecuteAsync(null);
        Assert.Equal(240, browser.Items.Count);

        browser.FilterText = "item-01";
        Assert.Equal(10, browser.TotalItemCount);
        Assert.Equal(10, browser.Items.Count);
        Assert.Equal("10", browser.SearchResultBadgeText);
        Assert.Equal("当前列表匹配 10 项", browser.SearchPanelStatusText);
        Assert.False(browser.HasMoreItems);

        browser.FilterText = string.Empty;
        Assert.Equal(DirectoryCount, browser.TotalItemCount);
        Assert.Equal(browser.PageSize, browser.Items.Count);
        Assert.True(browser.HasMoreItems);
    }

    [Fact]
    public async Task RecursiveSearchKeepsTotalCountWhileOnlyFirstPageIsMaterialized()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_startDir, pushHistory: false);
        await browser.SearchRecursiveAsync("needle");

        Assert.True(browser.IsSearchMode);
        Assert.Equal(SearchCount, browser.TotalItemCount);
        Assert.Equal(browser.PageSize, browser.Items.Count);
        Assert.Equal("99+", browser.SearchResultBadgeText);
        Assert.Contains("搜索到 135 项", browser.StatusText, StringComparison.Ordinal);
        Assert.Contains("已显示 120 项", browser.StatusText, StringComparison.Ordinal);
        Assert.True(browser.HasMoreItems);

        await browser.LoadMoreItemsCommand.ExecuteAsync(null);
        Assert.Equal(SearchCount, browser.Items.Count);
        Assert.False(browser.HasMoreItems);
    }

    [Fact]
    public async Task ReachingTheBottomFillsTheRestOfTheDirectoryInTheBackground()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        Assert.Equal(browser.PageSize, browser.Items.Count);
        Assert.True(browser.ShowLoadMoreFooter);

        // 滚到底部：不是再要一页，而是把剩余条目在后台分批补齐（v0.33）。
        // 无头单测没有消息泵，补齐按批连续跑完；真实 UI 线程上每批之间会让出一帧。
        browser.BeginBackgroundFill();
        await browser.WhenPagingIdleAsync();

        Assert.False(browser.IsBackgroundPaging);
        Assert.False(browser.HasMoreItems);
        Assert.False(browser.ShowLoadMoreFooter);
        Assert.Equal(DirectoryCount, browser.Items.Count);
        Assert.Equal("item-259.txt", browser.Items[^1].Name);
        Assert.Equal("260 个项目", browser.StatusText);
        // 补齐不许改变顺序：定位/收藏仍按完整结果集算下标。
        Assert.Equal("item-000.txt", browser.Items[0].Name);
    }

    [Fact]
    public async Task BackgroundFillOnlyTouchesTheFilteredResultSet()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        browser.FilterText = "item-1";

        browser.BeginBackgroundFill();
        await browser.WhenPagingIdleAsync();

        Assert.Equal(browser.TotalItemCount, browser.Items.Count);
        Assert.All(browser.Items, item => Assert.True(item.Name.StartsWith("item-1", StringComparison.Ordinal)));
        Assert.False(browser.HasMoreItems);
    }

    [Fact]
    public async Task SecondBackgroundFillIsANoOpOnceEverythingIsLoaded()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        browser.BeginBackgroundFill();
        await browser.WhenPagingIdleAsync();
        var loaded = browser.Items.Count;

        browser.BeginBackgroundFill();
        await browser.WhenPagingIdleAsync();

        Assert.Equal(loaded, browser.Items.Count);
        Assert.False(browser.IsBackgroundPaging);
    }

    [Fact]
    public async Task VisibleRangeRequestOnlyGrowsWhatIsMaterialized()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_manyDir, pushHistory: false);

        // 小屏幕：首屏已经够用，不应缩水。
        browser.EnsureVisibleRange(10);
        Assert.Equal(browser.PageSize, browser.Items.Count);

        // 高屏：先补齐可见范围（含越界缓冲），剩余交给后台补齐。
        browser.EnsureVisibleRange(200);
        Assert.Equal(208, browser.Items.Count);
        Assert.Equal("item-207.txt", browser.Items[^1].Name);
        Assert.True(browser.HasMoreItems);
    }

    [Fact]
    public async Task RevealStillWorksAfterBackgroundFill()
    {
        var browser = CreateShell().Browser;
        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        browser.BeginBackgroundFill();
        await browser.WhenPagingIdleAsync();

        browser.RevealItemsByPaths([Path.Combine(_manyDir, "item-259.txt")]);
        Assert.Equal(DirectoryCount, browser.Items.Count);
        Assert.Equal("item-259.txt", browser.Items[^1].Name);
    }

    [Fact]
    public async Task PullRefreshSoftReloadsAndDoesNotExitRecursiveSearch()
    {
        var browser = CreateShell().Browser;

        await browser.NavigateToAsync(_manyDir, pushHistory: false);
        await browser.LoadMoreItemsCommand.ExecuteAsync(null);
        File.WriteAllText(Path.Combine(_manyDir, "item-999.txt"), "new");

        await browser.PullRefreshCommand.ExecuteAsync(null);
        Assert.False(browser.IsRefreshing);
        Assert.Equal(DirectoryCount + 1, browser.TotalItemCount);
        Assert.Equal(browser.PageSize, browser.Items.Count);

        await browser.NavigateToAsync(_startDir, pushHistory: false);
        await browser.SearchRecursiveAsync("needle");
        Assert.True(browser.IsSearchMode);

        await browser.PullRefreshCommand.ExecuteAsync(null);
        Assert.False(browser.IsRefreshing);
        Assert.True(browser.IsSearchMode);
        Assert.Equal(SearchCount, browser.TotalItemCount);

        await browser.RefreshCommand.ExecuteAsync(null);
        Assert.False(browser.IsRefreshing);
        Assert.False(browser.IsSearchMode);
    }
}
