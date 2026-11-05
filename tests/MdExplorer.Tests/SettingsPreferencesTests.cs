using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.26：设置页新增的可调项必须真实生效、落盘并夹在支持区间内。</summary>
public sealed class SettingsPreferencesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-settings-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;
    private readonly string _manyDir;

    public SettingsPreferencesTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        _manyDir = Directory.CreateDirectory(Path.Combine(_startDir, "Many")).FullName;
        for (var i = 0; i < 150; i++)
            File.WriteAllText(Path.Combine(_manyDir, $"item-{i:000}.txt"), "settings");
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
    public void SettingsExposeDocumentedDefaults()
    {
        var shell = CreateShell();

        Assert.Equal(120, shell.ItemPageSize);
        Assert.Equal("120 项/页", shell.ItemPageSizeText);
        Assert.True(shell.AutoLoadMore);
        Assert.True(shell.EnableThumbnails);
        Assert.Equal("Comfortable", shell.Density);
        Assert.True(shell.IsDensityComfortable);
        Assert.True(shell.ConfirmBeforeTrash);
        Assert.True(shell.RestoreTabsOnLaunch);
        Assert.Equal(12, shell.RecentItemLimit);
        Assert.True(shell.IsRecentLimit12);
        Assert.Equal(72d, shell.PullRefreshTriggerDistance);
        Assert.True(shell.IsPullTrigger72);
        Assert.Equal("72 DIP", shell.PullTriggerText);
    }

    [Fact]
    public async Task LoadingSettingsClampsUnsupportedValues()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dataDir, "settings.json"),
            """
            {
              "SchemaVersion": 1,
              "ItemPageSize": 7,
              "AutoLoadMore": false,
              "EnableThumbnails": false,
              "Density": "dense",
              "RecentItemLimit": 5000,
              "PullRefreshTriggerDistance": 999,
              "RestoreTabsOnLaunch": false
            }
            """,
            TestContext.Current.CancellationToken);

        var store = await SettingsStore.LoadAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(60, store.Settings.ItemPageSize);
        Assert.Equal("Comfortable", store.Settings.Density);
        Assert.Equal(60, store.Settings.RecentItemLimit);
        Assert.Equal(120d, store.Settings.PullRefreshTriggerDistance);
        Assert.False(store.Settings.AutoLoadMore);
        Assert.False(store.Settings.EnableThumbnails);
        Assert.False(store.Settings.RestoreTabsOnLaunch);
    }

    [Fact]
    public async Task PageSizeSettingControlsFirstPageAndImmediateFill()
    {
        var shell = CreateShell();
        shell.ItemPageSize = 60;
        Assert.Equal(60, shell.SettingsStore.Settings.ItemPageSize);
        Assert.Equal(60, shell.Browser.PageSize);

        await shell.Browser.NavigateToAsync(_manyDir, pushHistory: false);
        Assert.Equal(60, shell.Browser.Items.Count);
        Assert.Equal(150, shell.Browser.TotalItemCount);
        Assert.Equal("已显示 60 / 150 项", shell.Browser.PagingStatusText);

        // 页数调大后应就地补足到新的一页，而不是等下一次加载。
        shell.ItemPageSize = 120;
        Assert.Equal(120, shell.Browser.Items.Count);
        Assert.True(shell.Browser.HasMoreItems);

        await shell.Browser.LoadMoreItemsCommand.ExecuteAsync(null);
        Assert.Equal(150, shell.Browser.Items.Count);
        Assert.False(shell.Browser.HasMoreItems);
    }

    [Fact]
    public void AutoLoadMoreGateNeedsSettingAndScrollProximity()
    {
        var shell = CreateShell();

        Assert.True(shell.ShouldAutoLoadMore(200));
        Assert.False(shell.ShouldAutoLoadMore(400));

        shell.AutoLoadMore = false;
        Assert.False(shell.SettingsStore.Settings.AutoLoadMore);
        Assert.False(shell.ShouldAutoLoadMore(0));
        Assert.False(shell.ShouldAutoLoadMore(200));
    }

    /// <summary>v0.29：浏览器/搜索新增的三项设置必须落盘、夹取并真正作用到运行时判定。</summary>
    [Fact]
    public void BrowsingDistancePullRefreshAndSearchLimitStayInRange()
    {
        var shell = CreateShell();

        Assert.True(shell.EnablePullRefresh);
        Assert.Equal(320d, shell.AutoLoadMoreDistance);
        Assert.Equal("320 DIP", shell.AutoLoadMoreDistanceText);
        Assert.True(shell.IsAutoLoadDistance320);
        Assert.Equal(2000, shell.SearchResultLimit);
        Assert.Equal("2000 条", shell.SearchResultLimitText);
        Assert.True(shell.IsSearchLimit2000);

        // 分段选择走命令，超范围走属性都由夹取兜住
        shell.SetAutoLoadMoreDistanceCommand.Execute("480");
        Assert.Equal(480d, shell.SettingsStore.Settings.AutoLoadMoreDistance);
        Assert.True(shell.IsAutoLoadDistance480);
        Assert.True(shell.ShouldAutoLoadMore(470));
        Assert.False(shell.ShouldAutoLoadMore(500));

        shell.AutoLoadMoreDistance = 99999;
        Assert.Equal(600d, shell.SettingsStore.Settings.AutoLoadMoreDistance);
        Assert.Equal(600d, shell.AutoLoadMoreDistanceClamped);
        Assert.True(shell.ShouldAutoLoadMore(590));

        shell.SetSearchResultLimitCommand.Execute("1000");
        Assert.Equal(1000, shell.SettingsStore.Settings.SearchResultLimit);
        Assert.True(shell.IsSearchLimit1000);
        shell.SearchResultLimit = 10;
        Assert.Equal(200, shell.SettingsStore.Settings.SearchResultLimit);

        shell.EnablePullRefresh = false;
        Assert.False(shell.SettingsStore.Settings.EnablePullRefresh);
        shell.EnablePullRefresh = true;
        Assert.True(shell.SettingsStore.Settings.EnablePullRefresh);
    }

    /// <summary>v0.29：关掉下拉刷新手势后手势入口立刻收手，工具栏刷新仍然可用。</summary>
    [Fact]
    public async Task DisabledPullRefreshOnlyBlocksTheGesture()
    {
        var shell = CreateShell();
        await shell.Browser.NavigateToAsync(_manyDir, pushHistory: false);
        Assert.Equal(150, shell.Browser.TotalItemCount);

        shell.EnablePullRefresh = false;
        await shell.Browser.PullRefreshCommand.ExecuteAsync(null);
        Assert.False(shell.Browser.IsRefreshing);
        Assert.Equal(150, shell.Browser.TotalItemCount);

        await shell.Browser.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(150, shell.Browser.TotalItemCount);

        shell.EnablePullRefresh = true;
        await shell.Browser.PullRefreshCommand.ExecuteAsync(null);
        Assert.False(shell.Browser.IsRefreshing);
        Assert.Equal(150, shell.Browser.TotalItemCount);
    }

    /// <summary>v0.29：应用内回收站的配额与保留期也是可调项，夹取后落盘并给出可读文案。</summary>
    [Fact]
    public void AppTrashQuotaAndRetentionSettingsAreClampedAndDescribed()
    {
        var shell = CreateShell();

        // 桌面宿主用系统回收站，设置项不显示，但值仍然可读、可夹取
        Assert.False(shell.UsesAppTrash);
        Assert.Equal(1_000_000_000, shell.AppTrashQuotaBytes);
        Assert.True(shell.IsAppTrashQuota1Gb);
        Assert.Equal(30, shell.AppTrashRetentionDays);
        Assert.True(shell.IsAppTrashRetention30);
        Assert.Equal("30 天", shell.AppTrashRetentionText);

        shell.SetAppTrashQuotaCommand.Execute("500");
        Assert.Equal(500_000_000, shell.SettingsStore.Settings.AppTrashQuotaBytes);
        Assert.True(shell.IsAppTrashQuota500Mb);
        Assert.Contains("MB", shell.AppTrashQuotaText);

        shell.AppTrashQuotaBytes = 9_999_999_999_999;
        Assert.Equal(10_000_000_000, shell.SettingsStore.Settings.AppTrashQuotaBytes);

        shell.SetAppTrashRetentionDaysCommand.Execute("7");
        Assert.Equal(7, shell.SettingsStore.Settings.AppTrashRetentionDays);
        shell.AppTrashRetentionDays = 0;
        Assert.Equal(1, shell.SettingsStore.Settings.AppTrashRetentionDays);
        Assert.Equal("1 天", shell.AppTrashRetentionText);
    }

    /// <summary>v0.29：新字段手改越界也会在加载时被夹回文档区间。</summary>
    [Fact]
    public async Task LoadingClampsNewBrowsingAndSearchSettings()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dataDir, "settings.json"),
            """
            {
              "SchemaVersion": 1,
              "AutoLoadMoreDistance": 5000,
              "SearchResultLimit": 1,
              "EnablePullRefresh": false,
              "AppTrashQuotaBytes": 1,
              "AppTrashRetentionDays": 9999
            }
            """,
            TestContext.Current.CancellationToken);

        var store = await SettingsStore.LoadAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(600d, store.Settings.AutoLoadMoreDistance);
        Assert.Equal(200, store.Settings.SearchResultLimit);
        Assert.False(store.Settings.EnablePullRefresh);
        Assert.Equal(200_000_000, store.Settings.AppTrashQuotaBytes);
        Assert.Equal(365, store.Settings.AppTrashRetentionDays);
    }

    [Fact]
    public void ThumbnailSettingReachesBothPanes()
    {
        var shell = CreateShell();
        Assert.True(shell.Browser.ThumbnailDecodingEnabled);

        shell.EnableThumbnails = false;
        Assert.False(shell.SettingsStore.Settings.EnableThumbnails);
        Assert.False(shell.Browser.ThumbnailDecodingEnabled);
        Assert.False(shell.SecondaryBrowser.ThumbnailDecodingEnabled);

        shell.EnableThumbnails = true;
        Assert.True(shell.Browser.ThumbnailDecodingEnabled);
        Assert.True(shell.SecondaryBrowser.ThumbnailDecodingEnabled);
    }

    [Fact]
    public void TrashConfirmationToggleNeverWeakensPermanentDelete()
    {
        var shell = CreateShell();
        var browser = shell.Browser;
        Assert.True(browser.RequiresTrashConfirmation);

        shell.ConfirmBeforeTrash = false;
        Assert.False(shell.SettingsStore.Settings.ConfirmBeforeTrash);
        Assert.False(browser.RequiresTrashConfirmation);
        Assert.False(shell.SecondaryBrowser.RequiresTrashConfirmation);

        shell.ConfirmBeforeTrash = true;
        Assert.True(browser.RequiresTrashConfirmation);
    }

    [Fact]
    public async Task RestoreTabsSettingSkipsSavedSession()
    {
        var otherDir = Directory.CreateDirectory(Path.Combine(_startDir, "Other")).FullName;
        var shell = CreateShell();
        shell.SettingsStore.Settings.OpenTabPaths = [_manyDir, otherDir];
        shell.SettingsStore.Settings.ActiveTabIndex = 1;
        shell.RestoreTabsOnLaunch = false;
        Assert.False(shell.SettingsStore.Settings.RestoreTabsOnLaunch);

        await shell.Browser.RestoreTabsAsync(_startDir);

        Assert.Single(shell.Browser.Tabs);
        Assert.Equal(_startDir, shell.Browser.CurrentPath);
    }

    [Fact]
    public async Task RecentLimitTrimsExistingEntriesAndIsPersisted()
    {
        var shell = CreateShell();
        var settings = shell.SettingsStore.Settings;
        for (var i = 0; i < 40; i++)
            settings.RecentFolderPaths.Add(Path.Combine(_startDir, $"folder-{i}"));
        for (var i = 0; i < 40; i++)
            settings.RecentFilePaths.Add(Path.Combine(_startDir, $"file-{i}.txt"));

        shell.RecentItemLimit = 24;

        Assert.Equal(24, shell.SettingsStore.Settings.RecentItemLimit);
        Assert.Equal(24, settings.RecentFolderPaths.Count);
        Assert.Equal(24, settings.RecentFilePaths.Count);
        Assert.Equal("24 条", shell.RecentLimitText);
        Assert.True(shell.IsRecentLimit24);
        Assert.False(shell.IsRecentLimit12);
        Assert.Equal(Path.Combine(_startDir, "folder-0"), settings.RecentFolderPaths[0]);

        // 主页最近访问的刷新是即发即忘；等它读完文件再结束用例，避免临时目录被提前删除。
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void DensityAndPullDistanceCommandsAcceptOnlySupportedValues()
    {
        var shell = CreateShell();

        shell.SetDensityCommand.Execute("Compact");
        Assert.Equal("Compact", shell.SettingsStore.Settings.Density);
        Assert.True(shell.IsCompactDensity);
        Assert.True(shell.IsDensityCompact);
        Assert.False(shell.IsDensityComfortable);
        Assert.Equal("紧凑", shell.DensityText);

        shell.SetDensityCommand.Execute("unknown");
        Assert.Equal("Comfortable", shell.Density);

        shell.SetPullRefreshTriggerDistanceCommand.Execute("96");
        Assert.Equal(96d, shell.PullRefreshTriggerDistance);
        Assert.True(shell.IsPullTrigger96);
        Assert.False(shell.IsPullTrigger72);

        shell.SetPullRefreshTriggerDistanceCommand.Execute("999");
        Assert.Equal(120d, shell.PullRefreshTriggerDistance);
        shell.SetPullRefreshTriggerDistanceCommand.Execute("not-a-number");
        Assert.Equal(120d, shell.PullRefreshTriggerDistance);
    }

    [Fact]
    public void SecondaryPaneKeepsItsOwnViewAndSort()
    {
        var shell = CreateShell();
        var settings = shell.SettingsStore.Settings;

        shell.Browser.SetViewModeCommand.Execute("Details");
        shell.Browser.SetSort(SortField.LastWriteTime, SortDirection.Descending);
        Assert.Equal("Details", settings.ViewMode);
        Assert.Equal("LastWriteTime", settings.SortField);

        shell.SecondaryBrowser.SetViewModeCommand.Execute("Grid");
        shell.SecondaryBrowser.SetSort(SortField.Size, SortDirection.Ascending);

        Assert.True(shell.SecondaryBrowser.IsGridView);
        Assert.Equal(SortField.Size, shell.SecondaryBrowser.CurrentSortField);
        Assert.Equal("Grid", settings.DualPaneViewMode);
        Assert.Equal("Size", settings.DualPaneSortField);
        Assert.Equal("Ascending", settings.DualPaneSortDirection);
        // 全局默认（A 的）不被 B 覆盖
        Assert.Equal("Details", settings.ViewMode);
        Assert.Equal("LastWriteTime", settings.SortField);
        Assert.Equal("Descending", settings.SortDirection);
    }

    [Fact]
    public async Task SecondaryPaneViewPreferencesPersistIndependently()
    {
        var first = CreateShell();
        first.Browser.SetViewModeCommand.Execute("List");
        first.SecondaryBrowser.SetViewModeCommand.Execute("Details");
        first.SecondaryBrowser.SetSort(SortField.Kind, SortDirection.Descending);
        await first.SettingsStore.SaveSettingsAsync(TestContext.Current.CancellationToken);

        var second = CreateShell();
        Assert.True(second.Browser.IsListView);
        Assert.True(second.SecondaryBrowser.IsDetailsView);
        Assert.Equal(SortField.Kind, second.SecondaryBrowser.CurrentSortField);
        Assert.Equal(SortDirection.Descending, second.SecondaryBrowser.CurrentSortDirection);
    }

    [Fact]
    public async Task InvalidSecondaryPanePreferencesFallBackToGlobalDefaults()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dataDir, "settings.json"),
            """
            { "SchemaVersion": 1, "ViewMode": "Grid", "SortField": "Size",
              "DualPaneViewMode": "Thumbnails", "DualPaneSortField": "Colour", "DualPaneSortDirection": "sideways" }
            """,
            TestContext.Current.CancellationToken);

        var shell = CreateShell();
        Assert.Null(shell.SettingsStore.Settings.DualPaneViewMode);
        Assert.Null(shell.SettingsStore.Settings.DualPaneSortField);
        Assert.Null(shell.SettingsStore.Settings.DualPaneSortDirection);
        Assert.True(shell.Browser.IsGridView);
        Assert.True(shell.SecondaryBrowser.IsGridView);
        Assert.Equal(SortField.Size, shell.SecondaryBrowser.CurrentSortField);
    }

    [Fact]
    public async Task SettingsSurviveReload()
    {
        var first = CreateShell();
        first.ItemPageSize = 240;
        first.AutoLoadMore = false;
        first.EnableThumbnails = false;
        first.SetDensityCommand.Execute("Compact");
        first.ConfirmBeforeTrash = false;
        first.RestoreTabsOnLaunch = false;
        first.RecentItemLimit = 48;
        first.SetPullRefreshTriggerDistanceCommand.Execute("96");
        await first.SettingsStore.SaveSettingsAsync(TestContext.Current.CancellationToken);

        var second = CreateShell();
        Assert.Equal(240, second.ItemPageSize);
        Assert.False(second.AutoLoadMore);
        Assert.False(second.EnableThumbnails);
        Assert.True(second.IsCompactDensity);
        Assert.False(second.ConfirmBeforeTrash);
        Assert.False(second.RestoreTabsOnLaunch);
        Assert.Equal(48, second.RecentItemLimit);
        Assert.Equal(96d, second.PullRefreshTriggerDistance);
    }
}
