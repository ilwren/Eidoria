using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Md3.Avalonia.Controls;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.Preview;

namespace MdExplorer.UI.ViewModels;

public enum ShellPage { Home, Browser, Favorites, Trash, Settings, SearchCenter, StorageInsights, ArchiveWorkbench, DocumentWorkbench, Automation, ExtensionCenter }
public enum SizeClass { Compact, Medium, Expanded, Large, ExtraLarge }
internal enum DialogKind { None, Prompt, Confirm, Conflict, Message, Progress, BatchRename }

/// <summary>根视图模型：页面切换、断点状态、主页数据、设置、对话框与操作进度（实现 IDialogs）。</summary>
public sealed partial class ShellViewModel : ObservableObject, IDialogs
{
    private readonly AppHost.AppServices _services;
    private readonly SemaphoreSlim _favoritesRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _recentsRefreshGate = new(1, 1);
    private int _favoritesRefreshGeneration;
    private int _recentsRefreshGeneration;
    private CancellationTokenSource? _searchCenterCts;
    private List<ArchiveEntryItem> _archiveEntries = [];
    private readonly Dictionary<string, ArchiveFolderAggregate> _archiveFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ArchiveEntryItem>> _archiveFilesByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _archiveChildFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _archiveDescendantsByFolder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveEntryItem> _archiveExplicitFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedArchiveEntries = new(StringComparer.Ordinal);
    private int _archiveGeneration;
    private int _archivePreviewGeneration;
    private Func<IFileLauncherService> _launcher;
    private IFileLauncherService Launcher => _launcher();
    private readonly BrowserClipboardState _browserClipboard = new();

    public BrowserViewModel Browser { get; }

    /// <summary>XL 对照窗格 B 的完整第二浏览会话；与主 Browser 分开保存路径、历史与选集。</summary>
    public BrowserViewModel SecondaryBrowser { get; }

    [ObservableProperty] private bool _isDualPaneEnabled;
    [ObservableProperty] private bool _isSecondaryPaneActive;
    private bool _secondaryBrowserInitialized;

    public PreviewViewModel Preview { get; }

    /// <summary>独立文档工作台：持有文本编辑与 PDF 阅读状态，页面切换时不随预览选集重建。</summary>
    public DocumentWorkbenchViewModel DocumentWorkbench { get; }

    /// <summary>NodifyM 自动化工作流编辑器；图定义持久化在设置中，运行走本 Shell 的任务中心。</summary>
    public AutomationViewModel Automation { get; }
    public ExtensionCenterViewModel ExtensionCenter { get; }
    public PlatformCapabilities Capabilities => _services.Capabilities;
    public ISettingsStore SettingsStore => _services.SettingsStore;

    private readonly List<AuthorizedRoot> _persistedRoots = [];

    /// <summary>已授权文件夹入口（Android SAF；含会话内与书签恢复）。</summary>
    public ObservableCollection<AuthorizedRootModel> AuthorizedRoots { get; } = [];

    public bool HasAuthorizedRoots => AuthorizedRoots.Count > 0;
    public int AvailableAuthorizedRootCount => AuthorizedRoots.Count(root => root.IsAvailable);
    public int UnavailableAuthorizedRootCount => AuthorizedRoots.Count(root => root.NeedsReauthorization);
    public bool HasUnavailableAuthorizedRoots => UnavailableAuthorizedRootCount > 0;
    public string AuthorizedRootsSummaryText => AuthorizedRoots.Count == 0
        ? "尚未授权文件夹"
        : UnavailableAuthorizedRootCount == 0
            ? $"{AvailableAuthorizedRootCount} 个可用"
            : $"{AvailableAuthorizedRootCount} 个可用 · {UnavailableAuthorizedRootCount} 个需重新授权";

    public ShellViewModel(AppHost.AppServices services)
    {
        _services = services;
        _launcher = () => services.Launcher;
        // 折叠姿态由宿主注入（v0.58）：平铺实现不会触发任何回调，布局照旧。
        _foldablePosture = services.FoldablePosture;
        _foldablePosture.Changed += OnFoldablePostureChanged;
        Preview = new PreviewViewModel(
            MdExplorer.UI.Preview.BuiltInPreviewProviders.Create(services.Archive, services.FileSystem),
            () => Launcher,
            services.MediaPlayback,
            () => services.SettingsStore.Settings);
        DocumentWorkbench = new DocumentWorkbenchViewModel(services.FileSystem, services.TextFiles, this, () => Launcher);
        DocumentWorkbench.CloseRequested += async (_, _) => await CloseDocumentWorkbenchAsync().ConfigureAwait(true);
        DocumentWorkbench.DocumentSaved += async (_, path) => await OnDocumentSavedAsync(path).ConfigureAwait(true);
        DocumentWorkbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentWorkbenchViewModel.Title) && Page == ShellPage.DocumentWorkbench)
                OnPropertyChanged(nameof(PageTitle));
        };
        _persistedRoots.AddRange(services.PersistedRoots);
        AuthorizedRoots.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAuthorizedRoots));
            OnPropertyChanged(nameof(AvailableAuthorizedRootCount));
            OnPropertyChanged(nameof(UnavailableAuthorizedRootCount));
            OnPropertyChanged(nameof(HasUnavailableAuthorizedRoots));
            OnPropertyChanged(nameof(AuthorizedRootsSummaryText));
        };
        Browser = CreateBrowserSession(services);
        SecondaryBrowser = CreateBrowserSession(services);
        SecondaryBrowser.PersistSessionTabs = false;
        SecondaryBrowser.PersistSharedViewPreferences = false;
        SecondaryBrowser.ApplyIndependentViewPreferences(
            services.SettingsStore.Settings.DualPaneViewMode,
            services.SettingsStore.Settings.DualPaneSortField,
            services.SettingsStore.Settings.DualPaneSortDirection);
        SecondaryBrowser.DrivesSharedPreview = false;
        SubscribeBrowserSession(Browser, isPrimary: true);
        SubscribeBrowserSession(SecondaryBrowser, isPrimary: false);
        Automation = new AutomationViewModel(
            services,
            this,
            () => Browser.IsSearchMode ? null : Browser.CurrentPath,
            RefreshBrowsersAfterAutomationAsync,
            Notify);
        ExtensionCenter = new ExtensionCenterViewModel(
            services.Plugins,
            this,
            seed => SetSeedColorCommand.Execute(seed),
            () => SeedColor);
        Preview.ArchiveWorkbenchRequested += async (_, path) => await OpenArchiveWorkbenchAsync(path).ConfigureAwait(true);
        Preview.DocumentWorkbenchRequested += async (_, item) => await OpenDocumentWorkbenchAsync(item).ConfigureAwait(true);
        FavoriteItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFavorites));
        RecentFolders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecentFolders));
            OnPropertyChanged(nameof(HasRecents));
        };
        RecentFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecentFiles));
            OnPropertyChanged(nameof(HasRecents));
        };
        TrashItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowTrashEmpty));
            OnPropertyChanged(nameof(HasTrashItems));
            OnPropertyChanged(nameof(TrashItemCountText));
            OnPropertyChanged(nameof(TrashSummaryText));
        };
        SearchIndexRoots.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSearchIndexRoots));
            OnPropertyChanged(nameof(SearchIndexRootCountText));
            OnPropertyChanged(nameof(SearchIndexStateText));
            OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
            OnPropertyChanged(nameof(CanRebuildContentIndex));
            OnPropertyChanged(nameof(SearchCenterModeHintText));
        };
        SearchCenterResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSearchCenterResults));
            OnPropertyChanged(nameof(ShowSearchCenterEmpty));
        };
        StorageKindUsages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStorageKindUsages));
        StorageTopFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStorageTopFolders));
        StorageLargestFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStorageLargestFiles));
        StorageDuplicateGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStorageDuplicateGroups));
        ArchiveVisibleEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasArchiveVisibleEntries));
            OnPropertyChanged(nameof(ShowArchiveEmpty));
        };
        OperationTasks.CollectionChanged += (_, _) => RefreshTaskCenterSummary();
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreviewViewModel.HasItem))
            {
                OnPropertyChanged(nameof(ShowPreviewPaneNow));
                OnPropertyChanged(nameof(ShowPreviewPaneEffective));
                OnPropertyChanged(nameof(ShowFoldSecondaryHint));
                OnPropertyChanged(nameof(ShowFoldSecondaryPreview));
                OnPropertyChanged(nameof(FoldSecondaryPreviewSource));
            }
        };

        // v0.56：内容索引的后台增量（目录监听驱动）改完库要刷新计数与时间；
        // 监听看不住时（批量解压、平台不支持）如实提示需要重建。
        if (services.ContentIndex is IContentIndexMaintenance contentIndexMaintenance)
        {
            contentIndexMaintenance.Changed += OnContentIndexIncrementallyUpdated;
            contentIndexMaintenance.WatchFailed += OnContentIndexWatchFailed;
        }

        var s = services.SettingsStore.Settings;
        _themeMode = s.ThemeMode;
        _seedColor = s.SeedColor;
        _showHidden = s.ShowHidden;
        _foldersFirst = s.FoldersFirst;
        _showPreviewPane = s.ShowPreviewPane;
        _reduceMotion = s.ReduceMotion;
        _rememberFolderView = s.RememberFolderView;
        _watchFolderChanges = s.WatchFolderChanges;
        _notifyOnTaskCompletion = s.NotifyOnTaskCompletion;
        _playVideoInline = s.PlayVideoInline;
        _mediaAutoPlay = s.MediaAutoPlay;
        _showMediaDiagnostics = s.ShowMediaDiagnostics;
        _enableLocalAi = s.EnableLocalAi;
        _aiModelSourceName = s.AiModelSource;
        _useSearchIndex = s.UseSearchIndex;
        _enableContentIndex = s.EnableContentIndex;
        _isDualPaneEnabled = s.DualPaneEnabled;
        _itemPageSize = Math.Clamp(s.ItemPageSize, 60, 480);
        _autoLoadMore = s.AutoLoadMore;
        _enableThumbnails = s.EnableThumbnails;
        _thumbnailSize = ThumbnailTierNames.Parse(s.ThumbnailTier);
        _density = string.Equals(s.Density, "Compact", StringComparison.OrdinalIgnoreCase) ? "Compact" : "Comfortable";
        _confirmBeforeTrash = s.ConfirmBeforeTrash;
        _restoreTabsOnLaunch = s.RestoreTabsOnLaunch;
        _recentItemLimit = Math.Clamp(s.RecentItemLimit, 6, 60);
        _pullRefreshTriggerDistance = Math.Clamp(
            double.IsFinite(s.PullRefreshTriggerDistance) ? s.PullRefreshTriggerDistance : 72, 48, 120);
        _enablePullRefresh = s.EnablePullRefresh;
        _autoLoadMoreDistance = Math.Clamp(
            double.IsFinite(s.AutoLoadMoreDistance) ? s.AutoLoadMoreDistance : 320, 120, 600);
        _searchResultLimit = Math.Clamp(s.SearchResultLimit, 200, 20000);
        _appTrashQuotaBytes = Math.Clamp(s.AppTrashQuotaBytes, 200_000_000, 10_000_000_000);
        _appTrashRetentionDays = Math.Clamp(s.AppTrashRetentionDays, 1, 365);
        RefreshSearchIndexRoots();
        RefreshRecentSearches(s.RecentSearchQueries);
        RefreshLocalAiState();
        _ = RefreshThumbnailCacheStatsAsync();
        if (_enableContentIndex)
            _ = RefreshContentIndexAsync();
    }

    /// <summary>Android 挂载后将打开方式切换为 Avalonia Launcher 实现。</summary>
    public void UseLauncher(IFileLauncherService launcher) => _launcher = () => launcher;

    /// <summary>视图挂载后把 TopLevel 提供者交给 Avalonia 系统剪贴板实现（测试替身则忽略）。</summary>
    public void UseSystemClipboard(Func<TopLevel?> topLevelProvider)
    {
        if (_services.SystemClipboard is AvaloniaSystemClipboardService clipboard)
            clipboard.TopLevelProvider = topLevelProvider;
    }

    private BrowserViewModel CreateBrowserSession(AppHost.AppServices services)
    {
        var browser = new BrowserViewModel(
            services.FileSystem, services.Operations, services.Trash,
            services.Archive, services.Search, services.SettingsStore,
            services.Inspector, services.FolderWatch, services.SystemClipboard, services.Thumbnails,
            this, Preview, () => Launcher);
        browser.ClipboardState = _browserClipboard;
        return browser;
    }

    private void SubscribeBrowserSession(BrowserViewModel browser, bool isPrimary)
    {
        browser.FavoritesChanged += async (_, _) => await RefreshFavoritesAsync().ConfigureAwait(true);
        browser.RecentsChanged += async (_, _) => await RefreshRecentsAsync().ConfigureAwait(true);
        browser.ArchiveWorkbenchRequested += async (_, path) => await OpenArchiveWorkbenchAsync(path).ConfigureAwait(true);
        browser.DocumentWorkbenchRequested += async (_, item) => await OpenDocumentWorkbenchAsync(item).ConfigureAwait(true);
        browser.PropertyChanged += (sender, e) =>
        {
            if (sender is not BrowserViewModel session)
                return;
            if ((e.PropertyName is nameof(BrowserViewModel.CurrentFolderName) or nameof(BrowserViewModel.IsSearchMode)) &&
                ReferenceEquals(session, ActiveBrowser))
                OnPropertyChanged(nameof(PageTitle));

            if (isPrimary)
            {
                if (e.PropertyName is nameof(BrowserViewModel.HasMultipleTabs))
                    OnPropertyChanged(nameof(ShowBrowserTabs));
                if (e.PropertyName is nameof(BrowserViewModel.IsAddressEditing))
                {
                    OnPropertyChanged(nameof(ShowBreadcrumbBar));
                    OnPropertyChanged(nameof(ShowCompactPath));
                    OnPropertyChanged(nameof(ShowPathRowActions));
                }
            }
            else if (e.PropertyName is nameof(BrowserViewModel.HasMultipleTabs))
            {
                OnPropertyChanged(nameof(ShowSecondaryTabs));
            }
            else if (e.PropertyName is nameof(BrowserViewModel.CurrentPath))
            {
                _services.SettingsStore.Settings.DualPanePath = SecondaryBrowser.CurrentPath;
                _ = SaveAsync();
            }

            // B 的地址编辑状态驱动自己的面包屑/输入框互换，A 的这两项互不影响。
            if (!isPrimary && e.PropertyName is nameof(BrowserViewModel.IsAddressEditing))
            {
                OnPropertyChanged(nameof(ShowSecondaryBreadcrumbBar));
                OnPropertyChanged(nameof(IsEditingSecondaryAddress));
            }

            if (e.PropertyName is nameof(BrowserViewModel.SelectedCount)
                or nameof(BrowserViewModel.HasSelection)
                or nameof(BrowserViewModel.CurrentPath)
                or nameof(BrowserViewModel.IsSearchMode))
                NotifyDualPaneState();
        };
    }

    // ---------- XL 双窗格（对照窗格 B） ----------
    public bool IsExtraLarge => SizeClass == SizeClass.ExtraLarge;
    public bool CanUseDualPane => IsExtraLarge;
    public bool ShowDualPane => IsBrowser && IsDualPaneEnabled && CanUseDualPane;
    public bool IsPrimaryPaneActive => !ShowDualPane || !IsSecondaryPaneActive;
    public BrowserViewModel ActiveBrowser => ShowDualPane && IsSecondaryPaneActive ? SecondaryBrowser : Browser;
    public string DualPaneToggleText => ShowDualPane ? "关闭对照窗格" : "打开对照窗格";
    public string PrimaryPaneBadgeText => IsPrimaryPaneActive ? "A · 活动" : "A";
    public string SecondaryPaneBadgeText => IsSecondaryPaneActive && ShowDualPane ? "B · 活动" : "B";
    public bool CanTransferPrimaryToSecondary =>
        ShowDualPane && Browser.HasSelection && !SecondaryBrowser.IsSearchMode &&
        !string.IsNullOrWhiteSpace(SecondaryBrowser.CurrentPath);
    public bool CanTransferSecondaryToPrimary =>
        ShowDualPane && SecondaryBrowser.HasSelection && !Browser.IsSearchMode &&
        !string.IsNullOrWhiteSpace(Browser.CurrentPath);
    /// <summary>B 的标签轨只在出现第二个标签时展开，单标签时把高度让给文件区。</summary>
    public bool ShowSecondaryTabs => ShowDualPane && SecondaryBrowser.HasMultipleTabs;

    /// <summary>交换两个窗格的目录（双窗格整理时的常见动作），各自保留历史栈。</summary>
    [RelayCommand]
    private async Task SwapPanesAsync()
    {
        if (!ShowDualPane)
            return;
        var primaryPath = Browser.CurrentPath;
        var secondaryPath = SecondaryBrowser.CurrentPath;
        if (string.IsNullOrWhiteSpace(primaryPath) || string.IsNullOrWhiteSpace(secondaryPath))
            return;
        if (string.Equals(primaryPath, secondaryPath, StringComparison.Ordinal))
            return;
        await SecondaryBrowser.NavigateToAsync(primaryPath).ConfigureAwait(true);
        await Browser.NavigateToAsync(secondaryPath).ConfigureAwait(true);
        NotifyDualPaneState();
    }

    /// <summary>B 的路径胶囊在编辑地址时让位给输入框；B 只出现在 ExtraLarge，不需要 Compact 折叠分支。</summary>
    public bool ShowSecondaryBreadcrumbBar => !SecondaryBrowser.IsAddressEditing;
    /// <summary>B 搜索面板里“清除”按钮的可见性；编译绑定的取反只写到一层属性，这里显式给个镜像。</summary>
    public bool IsSecondaryNotSearching => !SecondaryBrowser.IsSearchMode;
    public bool IsEditingSecondaryAddress => SecondaryBrowser.IsAddressEditing;

    partial void OnIsDualPaneEnabledChanged(bool value)
    {
        _services.SettingsStore.Settings.DualPaneEnabled = value;
        _ = SaveAsync();
        if (!value)
            IsSecondaryPaneActive = false;
        NotifyDualPaneState();
        ApplyActivePanePreviewSource();
    }

    partial void OnIsSecondaryPaneActiveChanged(bool value)
    {
        NotifyDualPaneState();
        OnPropertyChanged(nameof(PageTitle));
        ApplyActivePanePreviewSource();
    }

    private void NotifyDualPaneState()
    {
        OnPropertyChanged(nameof(IsExtraLarge));
        OnPropertyChanged(nameof(CanUseDualPane));
        OnPropertyChanged(nameof(ShowDualPane));
        OnPropertyChanged(nameof(IsPrimaryPaneActive));
        OnPropertyChanged(nameof(ActiveBrowser));
        OnPropertyChanged(nameof(DualPaneToggleText));
        OnPropertyChanged(nameof(PrimaryPaneBadgeText));
        OnPropertyChanged(nameof(SecondaryPaneBadgeText));
        OnPropertyChanged(nameof(CanTransferPrimaryToSecondary));
        OnPropertyChanged(nameof(CanTransferSecondaryToPrimary));
        OnPropertyChanged(nameof(IsSecondaryNotSearching));
        OnPropertyChanged(nameof(ShowSecondaryTabs));
    }

    private void ApplyActivePanePreviewSource()
    {
        var useSecondary = ShowDualPane && IsSecondaryPaneActive;
        Browser.DrivesSharedPreview = !useSecondary;
        SecondaryBrowser.DrivesSharedPreview = useSecondary;
        _ = ActiveBrowser.PublishSelectionToPreviewAsync();
    }

    public void ActivatePrimaryPane()
    {
        if (IsSecondaryPaneActive)
            IsSecondaryPaneActive = false;
        else
            ApplyActivePanePreviewSource();
    }

    public void ActivateSecondaryPane()
    {
        if (!ShowDualPane)
            return;
        if (!IsSecondaryPaneActive)
            IsSecondaryPaneActive = true;
        else
            ApplyActivePanePreviewSource();
    }

    private async Task EnsureSecondaryBrowserAsync()
    {
        if (_secondaryBrowserInitialized && !string.IsNullOrWhiteSpace(SecondaryBrowser.CurrentPath))
            return;

        var candidate = _services.SettingsStore.Settings.DualPanePath;
        if (string.IsNullOrWhiteSpace(candidate) || !_services.FileSystem.Exists(candidate))
            candidate = Browser.CurrentPath ?? _services.StartFolder;
        await SecondaryBrowser.NavigateToAsync(candidate, pushHistory: false).ConfigureAwait(true);
        _secondaryBrowserInitialized = true;
    }

    [RelayCommand]
    private async Task ToggleDualPaneAsync()
    {
        if (IsDualPaneEnabled)
        {
            IsDualPaneEnabled = false;
            ActivatePrimaryPane();
            return;
        }

        if (!CanUseDualPane)
        {
            Notify("对照窗格需要至少 1600 dp 宽的窗口");
            return;
        }

        Page = ShellPage.Browser;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await EnsureSecondaryBrowserAsync().ConfigureAwait(true);
        IsDualPaneEnabled = true;
        ActivateSecondaryPane();
    }

    [RelayCommand]
    private async Task CopyPrimaryToSecondaryAsync() =>
        await TransferBetweenPanesAsync(Browser, SecondaryBrowser, isMove: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task MovePrimaryToSecondaryAsync() =>
        await TransferBetweenPanesAsync(Browser, SecondaryBrowser, isMove: true).ConfigureAwait(true);

    [RelayCommand]
    private async Task CopySecondaryToPrimaryAsync() =>
        await TransferBetweenPanesAsync(SecondaryBrowser, Browser, isMove: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task MoveSecondaryToPrimaryAsync() =>
        await TransferBetweenPanesAsync(SecondaryBrowser, Browser, isMove: true).ConfigureAwait(true);

    private async Task TransferBetweenPanesAsync(
        BrowserViewModel sourceBrowser, BrowserViewModel destinationBrowser, bool isMove)
    {
        if (!ShowDualPane)
            return;
        var sources = sourceBrowser.SelectedItems.Select(item => item.FullPath).ToArray();
        if (sources.Length == 0)
        {
            Notify("请先在来源窗格中选择项目");
            return;
        }
        if (string.IsNullOrWhiteSpace(destinationBrowser.CurrentPath) || destinationBrowser.IsSearchMode)
        {
            Notify("目标窗格当前不能接收文件");
            return;
        }

        var destination = destinationBrowser.CurrentPath;
        var outcome = await sourceBrowser.TransferItemsAsync(sources, destination, isMove).ConfigureAwait(true);
        if (outcome is not null)
            await RefreshBrowserAfterTransferAsync(destinationBrowser, sources, destination).ConfigureAwait(true);
    }

    /// <summary>一个窗格完成传输后，刷新另一个正显示来源父目录或目标目录的窗格（SAF 无文件夹监视时尤其需要）。</summary>
    internal async Task RefreshBrowserAfterTransferAsync(
        BrowserViewModel browser, IReadOnlyList<string> sources, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(browser.CurrentPath) || browser.IsSearchMode)
            return;
        var affected = BrowserViewModel.PathsEqual(browser.CurrentPath, destinationFolder) ||
                         sources.Any(source => BrowserViewModel.PathsEqual(
                             _services.FileSystem.GetParentPath(source), browser.CurrentPath));
        if (affected)
            await browser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private void CollapseDualPaneForSize()
    {
        if (!IsDualPaneEnabled || CanUseDualPane)
            return;
        IsSecondaryPaneActive = false;
        if (!string.IsNullOrWhiteSpace(SecondaryBrowser.CurrentPath))
            Browser.EnsureBackgroundTab(SecondaryBrowser.CurrentPath);
        ApplyActivePanePreviewSource();
    }

    // ---------- 页面与断点 ----------
    [ObservableProperty] private ShellPage _page = ShellPage.Home;
    [ObservableProperty] private SizeClass _sizeClass = SizeClass.Large;
    [ObservableProperty] private bool _isDrawerOpen;
    [ObservableProperty] private bool _isPreviewPageOpen;

    public string DataDirectoryText => _services.DataDirectory;

    /// <summary>设置页版本串：跟随程序集信息版本，避免每次发版手工改 XAML。</summary>
    public string AppVersionText
    {
        get
        {
            var assembly = typeof(ShellViewModel).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+', 2)[0];
            var version = assembly.GetName().Version;
            return version is null ? "开发版" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string PageTitle => Page switch
    {
        ShellPage.Home => "文件",
        ShellPage.Favorites => "收藏",
        ShellPage.Trash => Capabilities.UsesAppTrash ? "应用内回收站" : "回收站",
        ShellPage.Settings => "设置",
        ShellPage.SearchCenter => "搜索中心",
        ShellPage.StorageInsights => "存储洞察",
        ShellPage.ArchiveWorkbench => string.IsNullOrWhiteSpace(ArchiveName) ? "压缩包" : ArchiveName,
        ShellPage.DocumentWorkbench => string.IsNullOrWhiteSpace(DocumentWorkbench.Title) ? "文档" : DocumentWorkbench.Title,
        ShellPage.Automation => "自动化",
        ShellPage.ExtensionCenter => "扩展中心",
        _ => ActiveBrowser.IsSearchMode ? "搜索结果" : ActiveBrowser.CurrentFolderName,
    };

    public bool HasFavorites => FavoriteItems.Count > 0;
    public bool HasRecentFolders => RecentFolders.Count > 0;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool HasRecents => HasRecentFolders || HasRecentFiles;
    /// <summary>可浏览回收站为空时展示占位（Windows 仍走平台说明分支）。</summary>
    public bool ShowTrashEmpty => Capabilities.CanBrowseTrash && TrashItems.Count == 0;
    public bool HasTrashItems => TrashItems.Count > 0;
    public string TrashItemCountText => TrashItems.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public bool IsHome => Page == ShellPage.Home;
    public bool IsBrowser => Page == ShellPage.Browser;
    public bool IsFavoritesPage => Page == ShellPage.Favorites;
    public bool IsTrashPage => Page == ShellPage.Trash;
    public bool IsSettingsPage => Page == ShellPage.Settings;
    public bool IsSearchCenterPage => Page == ShellPage.SearchCenter;
    public bool IsStorageInsightsPage => Page == ShellPage.StorageInsights;
    public bool IsArchiveWorkbenchPage => Page == ShellPage.ArchiveWorkbench;
    public bool IsDocumentWorkbenchPage => Page == ShellPage.DocumentWorkbench;
    public bool IsAutomationPage => Page == ShellPage.Automation;
    public bool IsExtensionCenterPage => Page == ShellPage.ExtensionCenter;

    private double _viewportHeight;
    private double _viewportWidth;
    /// <summary>折叠屏姿态（v0.58）：宿主没登记折叠检测时永远是平铺。</summary>
    private readonly IFoldablePostureSource _foldablePosture;
    private FoldableLayoutResult _foldableLayout = FoldableLayoutResult.Flat;

    public bool IsCompact => SizeClass == SizeClass.Compact;
    public bool IsMedium => SizeClass == SizeClass.Medium;
    public bool IsExpandedOrAbove => SizeClass >= SizeClass.Expanded;
    public bool IsLargeOrAbove => SizeClass >= SizeClass.Large;
    public bool IsCompactOrMedium => SizeClass <= SizeClass.Medium;
    /// <summary>
    /// 矮视口阈值（DIP）。手机横屏常见 360–440、Android 分屏小窗也常低于 560，
    /// 这类屏幕上竖向空间比宽度更稀缺：底部导航与常驻抽屉都会把内容挤成一条缝。
    /// </summary>
    public const double ShortViewportThreshold = 560;

    /// <summary>
    /// 参与「矮视口」判定的高度：桌面式半折叠（横铰链）时内容只有半边可用，
    /// 要按半边高度算，否则整窗 841 会骗过判定、把抽屉塞进 400 高的半屏里（v0.58）。
    /// </summary>
    private double EffectiveViewportHeight => _foldableLayout.ShortViewportReferenceHeight(_viewportHeight);

    /// <summary>当前视口是不是「矮」的（手机横屏、分屏小窗、矮桌面窗口、桌面式半折叠）。</summary>
    public bool IsShortViewport => EffectiveViewportHeight > 0 && EffectiveViewportHeight < ShortViewportThreshold;

    /// <summary>是否处于半折叠（铰链把可视区分成两半，其中一半是看不见的铰链带）。</summary>
    public bool IsHalfFolded => _foldableLayout.IsHalfFolded;

    /// <summary>
    /// 主区要让出的边距：竖铰链让右、横铰链让下。
    /// 半折叠时另一半是折进去的，内容跨过去就等于把一半行藏进铰链里。
    /// </summary>
    public Avalonia.Thickness FoldPrimaryMargin =>
        new(0, 0, _foldableLayout.PrimaryMarginRight, _foldableLayout.PrimaryMarginBottom);

    /// <summary>副半屏是否够大、值得单独摆一块面板。</summary>
    public bool ShowFoldSecondarySurface => _foldableLayout.ShowSecondarySurface;

    /// <summary>书本式半折叠：副半屏在右边。</summary>
    public bool IsFoldSecondaryBook =>
        _foldableLayout.ShowSecondarySurface && _foldableLayout.Axis == FoldHingeAxis.Vertical;

    /// <summary>桌面式半折叠：副半屏在下边。</summary>
    public bool IsFoldSecondaryTabletop =>
        _foldableLayout.ShowSecondarySurface && _foldableLayout.Axis == FoldHingeAxis.Horizontal;

    public double FoldSecondaryWidth => _foldableLayout.SecondaryWidth;
    public double FoldSecondaryHeight => _foldableLayout.SecondaryHeight;

    /// <summary>副半屏现在没有可显示的内容：放一段说明，别留白得让人以为卡住了。</summary>
    public bool ShowFoldSecondaryHint => ShowFoldSecondarySurface && !Preview.HasItem;

    /// <summary>副半屏有内容可显示（选中了文件）。</summary>
    public bool ShowFoldSecondaryPreview => ShowFoldSecondarySurface && Preview.HasItem;

    /// <summary>
    /// 副半屏的预览视图模型：**不显示时给 null**。副半屏用 ContentControl 绑它，
    /// null 就不会造出第二份预览视图——媒体画面只许挂一处，多一份就是两份原生画面。
    /// </summary>
    public PreviewViewModel? FoldSecondaryPreviewSource => ShowFoldSecondaryPreview ? Preview : null;

    /// <summary>副半屏的说明文字：说清楚它为什么空着，以及怎么把内容放进去。</summary>
    public string FoldSecondaryHint => IsFoldSecondaryTabletop
        ? "下半屏留给预览：选中一个文件即在这里显示。"
        : "右半屏留给预览：选中一个文件即在这里显示。";

    /// <summary>
    /// 底部导航：只在「窄且不矮」时出现（小屏竖屏手机、折叠屏合起态）。
    /// 半折叠时一律不出现（v0.58）：底栏挂在窗口最底下，书本式会横跨铰链、桌面式会落进副半屏，
    /// 两种都是「一半按钮藏在折缝里」，宁可退回导轨。
    /// </summary>
    public bool ShowBottomNav => IsCompact && !IsShortViewport && !IsHalfFolded;

    /// <summary>常驻抽屉：Expanded 以上且不矮。矮视口一律退成导轨，把高度还给内容。</summary>
    public bool ShowDrawerPane => IsExpandedOrAbove && !IsShortViewport;

    /// <summary>
    /// 导轨兜底：三种导航面（底栏 / 抽屉 / 导轨）永远恰好有一个可用——
    /// 折叠屏展开（宽度变大）与手机横屏（高度变小）都不会出现「没有导航」的状态。
    /// </summary>
    public bool ShowRail => !ShowDrawerPane && !ShowBottomNav;

    /// <summary>
    /// 常驻预览栏：半折叠时一律让位给副半屏（v0.58）——两半各摆一套预览只会互相挤。
    /// </summary>
    public bool ShowPreviewPaneNow => !IsHalfFolded && IsExpandedOrAbove && !IsShortViewport &&
        SettingsStore.Settings.ShowPreviewPane && Preview.HasItem;
    public bool ShowPreviewPaneEffective => IsBrowser && ShowPreviewPaneNow;
    /// <summary>预览页：没有常驻预览栏时（含矮视口）改为覆盖式预览页，而不是挤一列。</summary>
    public bool ShowPreviewPageNow => IsPreviewPageOpen && !ShowPreviewPaneNow;
    /// <summary>Compact 单标签时隐藏标签轨，避免与顶栏标题重复；出现第二个标签后即显示。</summary>
    public bool ShowBrowserTabs => !IsCompact || Browser.HasMultipleTabs;
    /// <summary>完整面包屑只在非 Compact 显示；窄屏折叠为当前目录胶囊。</summary>
    public bool ShowBreadcrumbBar => !IsCompact && !Browser.IsAddressEditing;
    public bool ShowCompactPath => IsCompact && !Browser.IsAddressEditing;
    /// <summary>Compact 编辑地址时让路径输入独占整行，导航/搜索/溢出按钮暂时让位。</summary>
    public bool ShowPathRowActions => !IsCompact || !Browser.IsAddressEditing;

    /// <summary>
    /// 矮视口（手机横屏 / 分屏小窗）里路径行只剩一百来像素：把「前进 / 刷新」收起来，
    /// 优先保证「后退 / 上一级 / 路径 / 搜索」可读可点。刷新仍有下拉刷新与 F5，
    /// 前进可用后退后重走或转回竖屏，不是不可达。
    /// </summary>
    public bool ShowPathRowExtras => !IsShortViewport;

    partial void OnPageChanged(ShellPage value)
    {
        if (value != ShellPage.Browser)
        {
            Browser.IsSearchPanelOpen = false;
            SecondaryBrowser.IsSearchPanelOpen = false;
            IsSecondaryPaneActive = false;
        }
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsBrowser));
        OnPropertyChanged(nameof(IsFavoritesPage));
        OnPropertyChanged(nameof(IsTrashPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsSearchCenterPage));
        OnPropertyChanged(nameof(IsStorageInsightsPage));
        OnPropertyChanged(nameof(IsArchiveWorkbenchPage));
        OnPropertyChanged(nameof(IsDocumentWorkbenchPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsExtensionCenterPage));
        OnPropertyChanged(nameof(ShowPreviewPaneEffective));
        NotifyDualPaneState();
        ApplyActivePanePreviewSource();
    }

    partial void OnSizeClassChanged(SizeClass value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsCompact), nameof(IsMedium), nameof(IsExpandedOrAbove), nameof(IsLargeOrAbove), nameof(IsCompactOrMedium),
                     nameof(IsShortViewport),
                     nameof(ShowBottomNav), nameof(ShowRail), nameof(ShowDrawerPane), nameof(ShowBrowserTabs),
                     nameof(ShowBreadcrumbBar), nameof(ShowCompactPath), nameof(ShowPathRowActions),
                     nameof(ShowPreviewPaneNow), nameof(ShowPreviewPageNow), nameof(ShowPreviewPaneEffective),
                 })
            OnPropertyChanged(name);
        NotifyDualPaneState();
        if (!CanUseDualPane)
            CollapseDualPaneForSize();
        else
            ApplyActivePanePreviewSource();
        if (IsExpandedOrAbove)
        {
            IsDrawerOpen = false;
            IsPreviewPageOpen = false;
        }
    }

    partial void OnIsPreviewPageOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPreviewPageNow));
        // 浮窗/预览页一关就把重负载放掉（直接 PDF 控件与位图都是按页分配的），
        // 重新打开时再按当前选集把内容取回来。
        if (!value)
        {
            Preview.ReleaseHeavyContent();
            return;
        }

        // 只有上一轮真的放掉过负载才重载；正常打开走既有选集管线，别在这里抢跑解码。
        ReloadReleasedPreview();
    }

    /// <summary>
    /// 关闭预览时放掉的负载，在重新打开时补回来：优先按当前选集，
    /// 选集被清空（换标签、刷新目录）时退回“上一次预览的那个文件”。
    /// </summary>
    private void ReloadReleasedPreview()
    {
        if (!Preview.IsPayloadReleased)
            return;
        if (Browser.SelectedItems.Count > 0)
        {
            _ = EnsurePreviewContentAsync(Browser.SelectedItems[0]);
            return;
        }

        if (Preview.CurrentItem is { } current)
            _ = Preview.LoadAsync(current);
    }

    /// <summary>由视图在尺寸变化时调用（开发文档 §7：MD3 五档断点）。</summary>
    public void SetViewportSize(double widthDip, double heightDip)
    {
        var wasShort = IsShortViewport;
        _viewportWidth = widthDip;
        _viewportHeight = heightDip;
        ApplyViewport(wasShort);
    }

    /// <summary>
    /// 断点与折叠几何一起算（v0.58 起两者互相牵制，拆开算就会前后矛盾）：
    /// 折叠几何先出「有效高度 / 有效宽度」，矮视口与五档断点再按有效尺寸判定。
    /// </summary>
    private void ApplyViewport(bool wasShort)
    {
        var previousFold = _foldableLayout;
        _foldableLayout = FoldableLayout.Compute(_foldablePosture.Current, _viewportWidth, _viewportHeight);
        if (wasShort != IsShortViewport)
            NotifyShortViewportChanged();
        if (!previousFold.Equals(_foldableLayout))
            NotifyFoldableLayoutChanged();

        // 书本式半折叠时主区只有一半宽：断点要按**主区**算，
        // 否则 1200 的书本式会被当成 Large，抽屉+预览栏一起塞进 584 宽里。
        var effectiveWidth = _foldableLayout.Axis == FoldHingeAxis.Vertical && _foldableLayout.ShowSecondarySurface
            ? _foldableLayout.SecondaryWidth
            : _viewportWidth;

        var next = effectiveWidth switch
        {
            < 600 => SizeClass.Compact,
            < 840 => SizeClass.Medium,
            < 1200 => SizeClass.Expanded,
            < 1600 => SizeClass.Large,
            _ => SizeClass.ExtraLarge,
        };
        // 先算再改：SizeClass 赋过值之后它自然等于 next，「next == SizeClass.Compact」恒为真，
        // 会让文档工作台永远以为自己是紧凑布局（PDF 宽度、状态文案、Office 排版全跟着错）。
        var isCompact = next == SizeClass.Compact;
        if (next != SizeClass)
            SizeClass = next;
        DocumentWorkbench.IsCompactLayout = isCompact;
    }

    /// <summary>矮视口状态翻转后要一起刷新的那些可见性（导航面、预览栏）。</summary>
    private void NotifyShortViewportChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsShortViewport), nameof(ShowBottomNav), nameof(ShowRail), nameof(ShowDrawerPane),
                     nameof(ShowPreviewPaneNow), nameof(ShowPreviewPaneEffective), nameof(ShowPreviewPageNow),
                 })
            OnPropertyChanged(name);
        if (ShowDrawerPane)
            IsDrawerOpen = false;
    }

    /// <summary>折叠几何变了：主区留白与副半屏跟着动，其余状态一概不碰。</summary>
    private void NotifyFoldableLayoutChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsHalfFolded), nameof(FoldPrimaryMargin), nameof(ShowFoldSecondarySurface),
                     nameof(IsFoldSecondaryBook), nameof(IsFoldSecondaryTabletop),
                     nameof(FoldSecondaryWidth), nameof(FoldSecondaryHeight), nameof(FoldSecondaryHint),
                     nameof(ShowFoldSecondaryHint), nameof(ShowFoldSecondaryPreview),
                     nameof(FoldSecondaryPreviewSource),
                     // 导航面也随半折叠变：底栏收起、导轨兜底。
                     nameof(IsShortViewport), nameof(ShowBottomNav), nameof(ShowRail), nameof(ShowDrawerPane),
                     nameof(ShowPreviewPaneNow), nameof(ShowPreviewPaneEffective), nameof(ShowPreviewPageNow),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>
    /// 折叠姿态变化（v0.58）。传感器回调可能在别的线程，统一回 UI 线程；
    /// 姿态没真的变化时直接返回——铰链角度在区间内连续上报，不该每来一次就重排一次布局。
    /// </summary>
    private void OnFoldablePostureChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyFoldablePosture);
            return;
        }

        ApplyFoldablePosture();
    }

    private void ApplyFoldablePosture()
    {
        if (_viewportWidth <= 0 && _viewportHeight <= 0)
            return;
        var previous = _foldableLayout;
        ApplyViewport(IsShortViewport);
        if (previous.Equals(_foldableLayout))
            return;
        // 姿态真的变了：断点可能跟着换档（书本式半折叠按主区半宽算），补一次重算。
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsExpandedOrAbove));
        OnPropertyChanged(nameof(IsLargeOrAbove));
        OnPropertyChanged(nameof(IsCompactOrMedium));
        RefreshPreviewVisibility();
    }

    public void RefreshPreviewVisibility()
    {
        OnPropertyChanged(nameof(ShowPreviewPaneNow));
        OnPropertyChanged(nameof(ShowPreviewPaneEffective));
    }

    // ---------- 主页数据 ----------
    public ObservableCollection<VolumeItemModel> Volumes { get; } = [];
    public ObservableCollection<NavEntry> Places { get; } = [];
    public ObservableCollection<FileItemViewModel> FavoriteItems { get; } = [];
    public ObservableCollection<NavEntry> RecentFolders { get; } = [];
    public ObservableCollection<FileItemViewModel> RecentFiles { get; } = [];
    public ObservableCollection<TrashItemModel> TrashItems { get; } = [];
    public ObservableCollection<SearchIndexRootModel> SearchIndexRoots { get; } = [];
    /// <summary>最近搜索词的小胶囊（v0.53）：最新在前，最多 8 条。</summary>
    public ObservableCollection<RecentSearchChipModel> RecentSearchChips { get; } = [];
    public bool HasRecentSearches => RecentSearchChips.Count > 0;
    public ObservableCollection<FileItemViewModel> SearchCenterResults { get; } = [];
    public ObservableCollection<StorageKindUsageModel> StorageKindUsages { get; } = [];
    public ObservableCollection<StorageFolderUsageModel> StorageTopFolders { get; } = [];
    public ObservableCollection<FileItemViewModel> StorageLargestFiles { get; } = [];
    public ObservableCollection<StorageDuplicateGroupModel> StorageDuplicateGroups { get; } = [];
    public ObservableCollection<ArchiveWorkbenchEntryViewModel> ArchiveVisibleEntries { get; } = [];
    public ObservableCollection<ArchiveBreadcrumbModel> ArchiveBreadcrumbs { get; } = [];
    public ObservableCollection<OperationTaskViewModel> OperationTasks { get; } = [];

    [ObservableProperty] private bool _isTaskCenterOpen;

    public bool HasOperationTasks => OperationTasks.Count > 0;
    public int ActiveOperationTaskCount => OperationTasks.Count(task => task.IsActive);
    public int WaitingConfirmationTaskCount => OperationTasks.Count(task => task.IsWaitingConfirmation);
    public int FailedOperationTaskCount => OperationTasks.Count(task => task.IsFailed);
    public bool HasActiveOperationTasks => ActiveOperationTaskCount > 0;
    public string ActiveOperationTaskCountText => ActiveOperationTaskCount > 99
        ? "99+"
        : ActiveOperationTaskCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string TaskCenterText => WaitingConfirmationTaskCount > 0
        ? "任务 · 等待确认"
        : ActiveOperationTaskCount switch
        {
            0 => "任务",
            1 => "任务 · 运行中",
            var count => $"任务 · {count} 个进行中",
        };
    public string TaskCenterSummaryText
    {
        get
        {
            if (OperationTasks.Count == 0) return "暂无任务";
            var parts = new List<string>(4);
            if (WaitingConfirmationTaskCount > 0) parts.Add($"等待确认 {WaitingConfirmationTaskCount}");
            if (ActiveOperationTaskCount > 0) parts.Add($"进行中 {ActiveOperationTaskCount}");
            if (FailedOperationTaskCount > 0) parts.Add($"失败 {FailedOperationTaskCount}");
            var completed = OperationTasks.Count(task => task.IsCompleted);
            if (completed > 0) parts.Add($"已完成 {completed}");
            return parts.Count > 0 ? string.Join(" · ", parts) : $"{OperationTasks.Count} 个任务";
        }
    }


    private static readonly (KnownFolder Folder, string Title, Func<string?> Glyph)[] KnownPlaceDefs =
    [
        (KnownFolder.Documents, "文档", () => MdSymbols.Description),
        (KnownFolder.Downloads, "下载", () => MdSymbols.Download),
        (KnownFolder.Pictures, "图片", () => MdSymbols.Image),
        (KnownFolder.Music, "音乐", () => MdSymbols.MusicNote),
        (KnownFolder.Videos, "视频", () => MdSymbols.Movie),
        (KnownFolder.Desktop, "桌面", () => MdSymbols.Terminal),
        (KnownFolder.Home, "主目录", () => MdSymbols.Home),
    ];

    public async Task InitializeAsync()
    {
        await RefreshHomeAsync().ConfigureAwait(true);
        await Browser.RestoreTabsAsync(_services.StartFolder).ConfigureAwait(true);
        if (IsDualPaneEnabled)
            await EnsureSecondaryBrowserAsync().ConfigureAwait(true);
    }

    public async Task RefreshHomeAsync()
    {
        Places.Clear();
        foreach (var (folder, title, glyph) in KnownPlaceDefs)
        {
            var path = _services.KnownFolders.Get(folder);
            if (path is not null)
                Places.Add(new NavEntry(title, glyph(), path));
        }
        await RefreshFavoritesAsync().ConfigureAwait(true);
        await RefreshRecentsAsync().ConfigureAwait(true);
        Volumes.Clear();
        var volumes = await _services.Volumes.GetVolumesAsync(CancellationToken.None).ConfigureAwait(true);
        foreach (var volume in volumes)
        {
            var usage = volume.TotalBytes is not null
                ? $"已用 {Formatting.Bytes(volume.UsedBytes)} / {Formatting.Bytes(volume.TotalBytes)}"
                : volume.Root.Path;
            Volumes.Add(new VolumeItemModel(
                volume.DisplayName, volume.Root.Path, usage,
                (volume.UsedFraction ?? 0) * 100,
                volume.IsRemovable ? MdSymbols.SdCard : MdSymbols.Storage));
        }
    }

    private async Task RefreshFavoritesAsync()
    {
        var generation = Interlocked.Increment(ref _favoritesRefreshGeneration);
        await _favoritesRefreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var resolved = new List<FileItemViewModel>();
            foreach (var path in _services.SettingsStore.Favorites)
            {
                var item = await _services.FileSystem.StatAsync(path, CancellationToken.None).ConfigureAwait(true);
                if (item is not null)
                    resolved.Add(new FileItemViewModel(item) { IsFavorite = true });
            }
            if (Volatile.Read(ref _favoritesRefreshGeneration) != generation)
                return;
            FavoriteItems.Clear();
            foreach (var item in resolved)
                FavoriteItems.Add(item);
        }
        finally
        {
            _favoritesRefreshGate.Release();
        }
    }

    private async Task RefreshRecentsAsync()
    {
        // 初始化、导航与打开文件可连续触发刷新；串行化并先构建快照，避免 Clear/Add 交错产生重复项。
        // 代次同时让清除动作失效所有在途快照，防止旧结果在清除后回填。
        var generation = Interlocked.Increment(ref _recentsRefreshGeneration);
        await _recentsRefreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var folders = new List<NavEntry>();
            foreach (var path in _services.SettingsStore.Settings.RecentFolderPaths.Take(RecentItemLimit))
            {
                var item = await _services.FileSystem.StatAsync(path, CancellationToken.None).ConfigureAwait(true);
                if (item?.IsFolder == true)
                {
                    // 字形由 Home 视图模板提供：MdSymbols 需要 Avalonia 字体服务，VM 保持可在无头单元测试中独立刷新。
                    folders.Add(new NavEntry(
                        item.Name,
                        null,
                        item.FullPath,
                        _services.FileSystem.GetParentPath(item.FullPath)));
                }
            }

            var files = new List<FileItemViewModel>();
            foreach (var path in _services.SettingsStore.Settings.RecentFilePaths.Take(RecentItemLimit))
            {
                var item = await _services.FileSystem.StatAsync(path, CancellationToken.None).ConfigureAwait(true);
                if (item is not null && !item.IsFolder)
                    files.Add(new FileItemViewModel(item) { IsFavorite = _services.SettingsStore.IsFavorite(path) });
            }

            if (Volatile.Read(ref _recentsRefreshGeneration) != generation)
                return;
            RecentFolders.Clear();
            foreach (var folder in folders)
                RecentFolders.Add(folder);
            RecentFiles.Clear();
            foreach (var file in files)
                RecentFiles.Add(file);
        }
        finally
        {
            _recentsRefreshGate.Release();
        }
    }

    [RelayCommand]
    private async Task ClearRecentsAsync()
    {
        Interlocked.Increment(ref _recentsRefreshGeneration);
        _services.SettingsStore.Settings.RecentFolderPaths.Clear();
        _services.SettingsStore.Settings.RecentFilePaths.Clear();
        RecentFolders.Clear();
        RecentFiles.Clear();
        await SaveAsync().ConfigureAwait(true);
        Notify("已清除最近访问");
    }

    public async Task OpenRecentFileAsync(FileItemViewModel item)
    {
        Page = ShellPage.Browser;
        ActivatePrimaryPane();
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await Browser.NavigateToFileLocationAsync(item.FullPath).ConfigureAwait(true);
        await Browser.RecordRecentFileAsync(item.FullPath).ConfigureAwait(true);
    }

    // ---------- 导航命令 ----------
    [RelayCommand]
    private void GoHome()
    {
        Page = ShellPage.Home;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        _ = RefreshHomeAsync();
    }

    [RelayCommand]
    private void GoFavorites()
    {
        Page = ShellPage.Favorites;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        _ = RefreshFavoritesAsync();
    }

    [RelayCommand]
    private async Task GoTrashAsync()
    {
        Page = ShellPage.Trash;
        IsDrawerOpen = false;
        await RefreshTrashAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GoSettings()
    {
        Page = ShellPage.Settings;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        _ = RefreshSearchIndexAsync();
        if (Capabilities.CanBrowseTrash)
            _ = RefreshTrashAsync();
    }

    [RelayCommand]
    private async Task GoSearchCenterAsync()
    {
        Page = ShellPage.SearchCenter;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await RefreshSearchIndexAsync().ConfigureAwait(true);
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoStorageInsightsAsync()
    {
        Page = ShellPage.StorageInsights;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        if (!HasStorageRoot)
        {
            var current = Browser.CurrentPath;
            if (!string.IsNullOrWhiteSpace(current) && !FileLocator.IsStorageUri(current) && Directory.Exists(current))
                TrySetStorageRoot(current, notifyOnFailure: false);
            else
                TrySetStorageRoot(_services.StartFolder, notifyOnFailure: false);
        }

        await RefreshHomeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GoAutomation()
    {
        Page = ShellPage.Automation;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        Automation.RefreshRuntimeState();
    }

    [RelayCommand]
    private void GoExtensionCenter()
    {
        Page = ShellPage.ExtensionCenter;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        ExtensionCenter.Refresh();
    }

    private async Task RefreshBrowsersAfterAutomationAsync()
    {
        if (!string.IsNullOrWhiteSpace(Browser.CurrentPath))
            await Browser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (_secondaryBrowserInitialized && !string.IsNullOrWhiteSpace(SecondaryBrowser.CurrentPath))
            await SecondaryBrowser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    public async Task OpenFolderAsync(string path)
    {
        Page = ShellPage.Browser;
        ActivatePrimaryPane();
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await Browser.NavigateToAsync(path).ConfigureAwait(true);
    }

    /// <summary>MainView 挂载后注入平台 StorageProvider 并恢复书签授权（Android SAF；桌面不调用）。</summary>
    public async Task AttachStorageAsync(IStorageProvider provider)
    {
        _services.StorageFileSystem.StorageProvider = provider;
        foreach (var root in _persistedRoots.ToArray())
        {
            IStorageFolder? folder = null;
            try
            {
                folder = await provider.OpenFolderBookmarkAsync(root.Bookmark).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                // 在下方保留失效行，供用户定点重新授权。
            }

            if (folder is null)
            {
                AddAuthorizedRootModel(root.Name, string.Empty, root.Bookmark, isAvailable: false, isPersistent: true);
                continue;
            }

            var uri = _services.StorageFileSystem.RegisterRoot(folder);
            if (await IsAuthorizedRootReachableAsync(uri).ConfigureAwait(true))
            {
                AddAuthorizedRootModel(folder.Name, uri, root.Bookmark, isAvailable: true, isPersistent: true);
            }
            else
            {
                _services.StorageFileSystem.ForgetRoot(uri);
                AddAuthorizedRootModel(root.Name, string.Empty, root.Bookmark, isAvailable: false, isPersistent: true);
            }
        }
    }

    private async Task<bool> IsAuthorizedRootReachableAsync(string uri)
    {
        try
        {
            await using var enumerator = _services.FileSystem
                .ListAsync(uri, includeHidden: false, CancellationToken.None)
                .GetAsyncEnumerator();
            _ = await enumerator.MoveNextAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task PickAuthorizedFolderAsync() =>
        await PickAuthorizedFolderCoreAsync().ConfigureAwait(true);

    private async Task<bool> PickAuthorizedFolderCoreAsync()
    {
        var provider = _services.StorageFileSystem.StorageProvider;
        if (provider is null || !provider.CanPickFolder)
        {
            Notify("当前环境不支持文件夹选择");
            return false;
        }
        IReadOnlyList<IStorageFolder> picked;
        try
        {
            picked = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择要在 Eidoria 中浏览的文件夹",
                AllowMultiple = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Notify("无法打开系统文件夹选择器");
            return false;
        }
        string? firstUri = null;
        foreach (var folder in picked)
        {
            var uri = _services.StorageFileSystem.RegisterRoot(folder);
            firstUri ??= uri;
            string? bookmark = null;
            try
            {
                // 书签持久化尽力而为：失败时该授权仅本次会话有效，管理行会如实标注。
                if (folder.CanBookmark)
                    bookmark = await folder.SaveBookmarkAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException) { }

            if (bookmark is not null)
            {
                _persistedRoots.RemoveAll(r => r.Name == folder.Name || r.Bookmark == bookmark);
                _persistedRoots.Add(new AuthorizedRoot(folder.Name, bookmark));
            }
            AddAuthorizedRootModel(folder.Name, uri, bookmark, isAvailable: true, isPersistent: bookmark is not null);
        }
        if (picked.Count > 0)
        {
            await SaveRootsAsync().ConfigureAwait(true);
            if (firstUri is not null)
                await OpenFolderAsync(firstUri).ConfigureAwait(true);
            return true;
        }
        return false;
    }

    /// <summary>可用授权直接打开；失效授权定点进入系统选择器续授。</summary>
    public async Task OpenAuthorizedRootAsync(AuthorizedRootModel root)
    {
        if (root.IsAvailable && !string.IsNullOrWhiteSpace(root.Path))
            await OpenFolderAsync(root.Path).ConfigureAwait(true);
        else
            await ReauthorizeAuthorizedRootAsync(root).ConfigureAwait(true);
    }

    public async Task ReauthorizeAuthorizedRootAsync(AuthorizedRootModel root)
    {
        if (root.IsAvailable)
        {
            await OpenAuthorizedRootAsync(root).ConfigureAwait(true);
            return;
        }

        if (!await PickAuthorizedFolderCoreAsync().ConfigureAwait(true))
            return;

        AuthorizedRoots.Remove(root);
        if (!string.IsNullOrWhiteSpace(root.Bookmark))
            _persistedRoots.RemoveAll(r => r.Bookmark == root.Bookmark);
        await SaveRootsAsync().ConfigureAwait(true);
    }

    /// <summary>移除 Eidoria 中的授权入口与书签；不删除真实文件，系统侧授权仍可在系统设置中撤销。</summary>
    public async Task RemoveAuthorizedRootAsync(AuthorizedRootModel root)
    {
        var leavingCurrent = root.IsAvailable &&
                             Browser.CurrentPath is { } current &&
                             IsPathWithinAuthorizedRoot(current, root.Path);
        AuthorizedRoots.Remove(root);
        _persistedRoots.RemoveAll(r =>
            (!string.IsNullOrWhiteSpace(root.Bookmark) && r.Bookmark == root.Bookmark) ||
            r.Name == root.Name);
        if (root.IsAvailable && !string.IsNullOrWhiteSpace(root.Path))
            _services.StorageFileSystem.ForgetRoot(root.Path);
        await SaveRootsAsync().ConfigureAwait(true);
        if (leavingCurrent)
            await OpenFolderAsync(_services.StartFolder).ConfigureAwait(true);
        Notify($"已移除授权入口：{root.Name}（未删除文件）");
    }

    private bool IsPathWithinAuthorizedRoot(string path, string rootPath)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = path;
        while (current is not null && visited.Add(current))
        {
            if (string.Equals(current, rootPath, StringComparison.Ordinal))
                return true;
            current = _services.FileSystem.GetParentPath(current);
        }
        return false;
    }

    /// <summary>父文件夹定位串（双形态统一，供收藏跳转等跨界调用）。</summary>
    public string? BrowserParentOf(string path) => _services.FileSystem.GetParentPath(path);

    private void AddAuthorizedRootModel(
        string name,
        string uri,
        string? bookmark,
        bool isAvailable,
        bool isPersistent)
    {
        var model = new AuthorizedRootModel(name, uri, bookmark, isAvailable, isPersistent);
        var index = -1;
        for (var i = 0; i < AuthorizedRoots.Count; i++)
        {
            var existing = AuthorizedRoots[i];
            if ((isAvailable && existing.Path == uri) ||
                (!string.IsNullOrWhiteSpace(bookmark) && existing.Bookmark == bookmark) ||
                (!existing.IsAvailable && existing.Name == name))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
            AuthorizedRoots[index] = model;
        else
            AuthorizedRoots.Add(model);
    }

    private async Task SaveRootsAsync()
    {
        try { await AuthorizedRootsStore.SaveAsync(_services.DataDirectory, _persistedRoots).ConfigureAwait(true); }
        catch (IOException) { }
    }

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;

    [RelayCommand]
    private void ClosePreviewPage() => IsPreviewPageOpen = false;

    [RelayCommand]
    private void ToggleTaskCenter() => IsTaskCenterOpen = !IsTaskCenterOpen;

    [RelayCommand]
    private void CloseTaskCenter() => IsTaskCenterOpen = false;

    [RelayCommand]
    private void ClearFinishedTasks()
    {
        foreach (var task in OperationTasks.Where(task => task.IsFinished).ToArray())
        {
            OperationTasks.Remove(task);
            task.Dispose();
        }
    }

    [RelayCommand]
    private async Task ShowKeyboardShortcutsAsync()
    {
        await ShowPropertiesAsync("键盘快捷键", string.Join("\n", [
            "导航",
            "· Ctrl+L：编辑路径    Ctrl+F / Ctrl+K：悬浮搜索面板（回车递归搜索）",
            "· Alt+← / Alt+→：后退 / 前进    Alt+↑：上一级",
            "· F5 或 Ctrl+R：刷新    Ctrl+Shift+D：打开/关闭对照窗格（仅 1600+ dp）    F6：切换活动窗格",
            "",
            "标签页",
            "· Ctrl+T：复制当前标签    Ctrl+W：关闭标签",
            "· Ctrl+Tab / Ctrl+Shift+Tab：切换标签    Ctrl+Shift+T：恢复已关闭标签",
            "",
            "文件操作",
            "· Ctrl+C / Ctrl+X / Ctrl+V：复制 / 剪切 / 粘贴",
            "· Ctrl+Shift+C：复制路径    Ctrl+A：全选",
            "· F2：重命名    Delete：删除    Enter：打开",
            "",
            "F1：打开此快捷键说明",
        ])).ConfigureAwait(true);
    }

    private void RefreshTaskCenterSummary()
    {
        OnPropertyChanged(nameof(HasOperationTasks));
        OnPropertyChanged(nameof(ActiveOperationTaskCount));
        OnPropertyChanged(nameof(WaitingConfirmationTaskCount));
        OnPropertyChanged(nameof(FailedOperationTaskCount));
        OnPropertyChanged(nameof(HasActiveOperationTasks));
        OnPropertyChanged(nameof(ActiveOperationTaskCountText));
        OnPropertyChanged(nameof(TaskCenterText));
        OnPropertyChanged(nameof(TaskCenterSummaryText));
    }

    /// <summary>文件被点开时由视图调用：小断点进预览页，大断点预览窗格随选集变化即可。</summary>
    public void OnFileOpened(FileItemViewModel item)
    {
        _ = Browser.RecordRecentFileAsync(item.FullPath);
        // 矮视口（手机横屏 / 分屏小窗）没有常驻预览栏，同样走覆盖式预览页。
        if (!IsCompactOrMedium && !IsShortViewport)
            return;
        IsPreviewPageOpen = true;
        // 浮窗只显示 Preview 的内容。选集没有把这一项交给预览时（重复点同一项、
        // 上一轮操作把预览清空、B 窗格正握着预览源），浮窗会展开成一片空白——
        // 所以这里显式核对一次内容来源，而不是假设选集管线一定喂过。
        _ = EnsurePreviewContentAsync(item);
    }

    /// <summary>确保共享预览真的停在 <paramref name="item"/> 上；已就位时不重复解码。</summary>
    public async Task EnsurePreviewContentAsync(FileItemViewModel item)
    {
        if (Preview is { HasItem: true, IsPayloadReleased: false, CurrentItem: { } current } &&
            BrowserViewModel.PathsEqual(current.FullPath, item.FullPath))
            return;

        try
        {
            await Preview.LoadAsync(item.Model).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or NotSupportedException)
        {
            // 预览失败会落到 Preview.FailureText；这里只保证浮窗不会因异常停在半路。
        }
    }

    /// <summary>类别入口：优先打开对应已知目录。</summary>
    public async Task OpenCategoryAsync(FileKind kind)
    {
        var folder = kind switch
        {
            FileKind.Image => KnownFolder.Pictures,
            FileKind.Video => KnownFolder.Videos,
            FileKind.Audio => KnownFolder.Music,
            FileKind.Document => KnownFolder.Documents,
            _ => (KnownFolder?)null,
        };
        var path = folder is { } f ? _services.KnownFolders.Get(f) : null;
        if (path is not null)
        {
            await OpenFolderAsync(path).ConfigureAwait(true);
        }
        else
        {
            Page = ShellPage.Browser;
            var home = _services.KnownFolders.Get(KnownFolder.Home) ?? _services.StartFolder;
            await Browser.SearchRecursiveAsync(string.Empty, new HashSet<FileKind> { kind }, home).ConfigureAwait(true);
        }
    }

    /// <summary>同步预判系统返回是否会被消费（决定 RoutedEventArgs.Handled）。</summary>
    public bool WouldHandleBack =>
        IsDialogOpen || IsTaskCenterOpen || IsDrawerOpen || IsPreviewPageOpen || Browser.IsSearchPanelOpen ||
        Page != ShellPage.Home ||
        ActiveBrowser.CanConsumeBack;

    /// <summary>顶级返回处理（Android 返回键/桌面 Alt+← 的兜底序）。返回是否已消费。</summary>
    public async Task<bool> TryGoBackAsync()
    {
        if (IsDialogOpen) return true; // 模态开着时吞掉返回，由对话框按钮收尾
        if (IsTaskCenterOpen) { IsTaskCenterOpen = false; return true; }
        if (Browser.IsSearchPanelOpen) { Browser.IsSearchPanelOpen = false; return true; }
        if (ShowDualPane && SecondaryBrowser.IsSearchPanelOpen) { SecondaryBrowser.IsSearchPanelOpen = false; return true; }
        if (IsDrawerOpen) { IsDrawerOpen = false; return true; }
        if (IsPreviewPageOpen) { IsPreviewPageOpen = false; return true; }
        if (Page == ShellPage.DocumentWorkbench)
        {
            if (!await DocumentWorkbench.ConfirmDiscardAsync().ConfigureAwait(true)) return true;
            DocumentWorkbench.Clear();
            Page = ShellPage.Browser;
            return true;
        }
        if (Page == ShellPage.ArchiveWorkbench)
        {
            if (!string.IsNullOrEmpty(ArchiveCurrentFolder))
            {
                NavigateArchiveFolder(ArchiveParentFolder(ArchiveCurrentFolder));
            }
            else
            {
                Page = ShellPage.Browser;
            }
            return true;
        }
        if (Page == ShellPage.Browser && await ActiveBrowser.TryGoBackAsync().ConfigureAwait(true)) return true;
        if (Page != ShellPage.Home) { GoHome(); return true; }
        return false;
    }

    // ---------- 回收站页 ----------
    [ObservableProperty] private long _trashUsedBytes;
    [ObservableProperty] private long? _trashQuotaBytes;
    [ObservableProperty] private int? _trashRetentionDays;
    [ObservableProperty] private double _trashUsedPercent;

    public string TrashTitle => Capabilities.UsesAppTrash ? "应用内回收站" : "回收站";
    public bool HasTrashQuota => TrashQuotaBytes is > 0;
    public string TrashUsedText => Formatting.Bytes(TrashUsedBytes);
    public string TrashQuotaText => TrashQuotaBytes is { } quota ? Formatting.Bytes(quota) : "系统管理";
    public string TrashRetentionText => TrashRetentionDays is { } days ? $"{days} 天后自动清理" : "由系统管理";
    public string TrashSummaryText => $"{TrashItemCountText} 项 · {TrashUsedText}";

    partial void OnTrashUsedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(TrashUsedText));
        OnPropertyChanged(nameof(TrashSummaryText));
    }

    partial void OnTrashQuotaBytesChanged(long? value)
    {
        OnPropertyChanged(nameof(HasTrashQuota));
        OnPropertyChanged(nameof(TrashQuotaText));
    }

    partial void OnTrashRetentionDaysChanged(int? value) => OnPropertyChanged(nameof(TrashRetentionText));

    /// <summary>只刷新回收站占用/配额快照（设置变更后立刻反映新上限），不重建条目列表。</summary>
    private async Task RefreshTrashStateAsync()
    {
        if (!Capabilities.CanBrowseTrash)
            return;
        try
        {
            ApplyTrashStats(await _services.Trash.GetStatsAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (IOException)
        {
            // 统计失败不应把设置页带崩：保留上一次快照，条目的真实状态仍在回收站页。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task RefreshTrashAsync()
    {
        TrashItems.Clear();
        if (!Capabilities.CanBrowseTrash)
        {
            ApplyTrashStats(new TrashStats(0, 0));
            return;
        }

        try
        {
            var items = await _services.Trash.ListAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var item in items)
                TrashItems.Add(new TrashItemModel(
                    item.Id,
                    item.OriginalName,
                    item.OriginalPath,
                    item.DeletedAt == default ? "" : Formatting.DateTime(item.DeletedAt, DateTimeOffset.Now),
                    item.IsFolder,
                    item.SizeBytes));
            ApplyTrashStats(await _services.Trash.GetStatsAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ApplyTrashStats(new TrashStats(0, 0));
            Notify($"无法读取回收站：{ex.Message}");
        }
    }

    private void ApplyTrashStats(TrashStats stats)
    {
        TrashUsedBytes = stats.UsedBytes;
        TrashQuotaBytes = stats.QuotaBytes;
        TrashRetentionDays = stats.RetentionDays;
        TrashUsedPercent = stats.UsedPercent;
        OnPropertyChanged(nameof(TrashSummaryText));
    }

    [RelayCommand]
    private async Task RestoreTrashAsync(TrashItemModel? row)
    {
        if (row is null) return;
        try
        {
            await _services.Trash.RestoreAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            Notify($"已还原 {row.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await ConfirmAsync("还原失败", ex.Message, "知道了").ConfigureAwait(true);
        }
        await RefreshTrashAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteTrashAsync(TrashItemModel? row)
    {
        if (row is null) return;
        if (!await ConfirmAsync("永久删除？", $"将永久删除回收站中的「{row.Name}」。", "永久删除", destructive: true).ConfigureAwait(true))
            return;
        try { await _services.Trash.DeletePermanentlyAsync(row.Id, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await ConfirmAsync("删除失败", ex.Message, "知道了").ConfigureAwait(true);
        }
        await RefreshTrashAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        if (!HasTrashItems) return;
        var title = Capabilities.UsesAppTrash ? "清空应用内回收站？" : "清空回收站？";
        var message = $"将永久删除 {TrashSummaryText}。此操作无法撤销。";
        if (!await ConfirmAsync(title, message, "清空回收站", destructive: true).ConfigureAwait(true))
            return;

        try
        {
            await _services.Trash.EmptyAsync(CancellationToken.None).ConfigureAwait(true);
            Notify("回收站已清空");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await ConfirmAsync("清空失败", ex.Message, "知道了").ConfigureAwait(true);
        }
        await RefreshTrashAsync().ConfigureAwait(true);
    }

    // ---------- 存储洞察（只读） ----------
    [ObservableProperty] private string _storageRootPath = string.Empty;
    [ObservableProperty] private bool _isStorageAnalyzing;
    [ObservableProperty] private bool _isStorageFindingDuplicates;
    [ObservableProperty] private bool _hasStorageAnalysis;
    [ObservableProperty] private bool _hasStorageDuplicateScan;
    [ObservableProperty] private string _storageStatusText = "选择文件夹后开始分析";
    [ObservableProperty] private string _storageDuplicateStatusText = "尚未查找重复文件";
    [ObservableProperty] private long _storageTotalBytes;
    [ObservableProperty] private long _storageFileCount;
    [ObservableProperty] private long _storageFolderCount;
    [ObservableProperty] private bool _storageIsPartial;
    [ObservableProperty] private bool _storageWasTruncated;
    [ObservableProperty] private long _storageDuplicateReclaimableBytes;
    [ObservableProperty] private int _storageDuplicateCandidateCount;
    [ObservableProperty] private int _storageDuplicateFilesSkipped;
    [ObservableProperty] private bool _storageDuplicateIsPartial;
    [ObservableProperty] private bool _storageDuplicateWasTruncated;

    public bool HasStorageRoot => !string.IsNullOrWhiteSpace(StorageRootPath);
    public bool IsStorageBusy => IsStorageAnalyzing || IsStorageFindingDuplicates;
    public bool ShowStorageEmpty => !HasStorageAnalysis && !IsStorageAnalyzing;
    public bool HasStorageKindUsages => StorageKindUsages.Count > 0;
    public bool HasStorageTopFolders => StorageTopFolders.Count > 0;
    public bool HasStorageLargestFiles => StorageLargestFiles.Count > 0;
    public bool HasStorageDuplicateGroups => StorageDuplicateGroups.Count > 0;
    public string StorageRootName
    {
        get
        {
            if (!HasStorageRoot)
                return "未选择";
            var trimmed = StorageRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? StorageRootPath : name;
        }
    }
    public string StorageTotalSizeText => Formatting.Bytes(StorageTotalBytes);
    public string StorageFileCountText => StorageFileCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string StorageFolderCountText => StorageFolderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string StorageAnalysisStateText =>
        !HasStorageAnalysis ? "未分析" :
        StorageWasTruncated ? "已截断" :
        StorageIsPartial ? "部分结果" : "完整";
    public string StorageDuplicateReclaimableText => Formatting.Bytes(StorageDuplicateReclaimableBytes);
    public string StorageDuplicateStateText =>
        !HasStorageDuplicateScan ? "未查找" :
        StorageDuplicateWasTruncated ? "已截断" :
        StorageDuplicateIsPartial ? "部分结果" : "完整";

    partial void OnStorageRootPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasStorageRoot));
        OnPropertyChanged(nameof(StorageRootName));
        ClearStorageResults(HasStorageRoot ? "等待分析" : "选择文件夹后开始分析");
    }

    partial void OnIsStorageAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStorageBusy));
        OnPropertyChanged(nameof(ShowStorageEmpty));
    }

    partial void OnIsStorageFindingDuplicatesChanged(bool value) => OnPropertyChanged(nameof(IsStorageBusy));
    partial void OnHasStorageAnalysisChanged(bool value) => OnPropertyChanged(nameof(ShowStorageEmpty));

    public bool TrySetStorageRoot(string? path, bool notifyOnFailure = true)
    {
        if (TryNormalizeSearchIndexRoot(path) is not { } normalized || !Directory.Exists(normalized))
        {
            if (notifyOnFailure)
                Notify("存储洞察只能分析本地文件夹");
            return false;
        }

        StorageRootPath = normalized;
        return true;
    }

    [RelayCommand]
    private void UseCurrentFolderForStorage()
    {
        var path = Browser.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || FileLocator.IsStorageUri(path) || !Directory.Exists(path))
        {
            Notify("当前文件夹不能用于存储洞察");
            return;
        }

        TrySetStorageRoot(path);
    }

    [RelayCommand]
    private async Task AnalyzeStorageAsync()
    {
        if (IsStorageBusy)
            return;
        if (TryNormalizeSearchIndexRoot(StorageRootPath) is not { } root || !Directory.Exists(root))
        {
            StorageStatusText = "只能分析本地文件夹";
            Notify("存储洞察只能分析本地文件夹");
            return;
        }

        StorageRootPath = root;
        ClearStorageResults("正在分析…");
        IsStorageAnalyzing = true;
        StorageAnalysisResult? result = null;
        try
        {
            var options = new StorageAnalysisOptions(IncludeHidden: ShowHidden);
            var outcome = await RunOperationAsync($"正在分析「{StorageRootName}」…", async (progress, ct) =>
            {
                result = await _services.StorageInsights
                    .AnalyzeAsync(root, options, progress, ct).ConfigureAwait(true);
                return new OperationOutcome(
                    result.FileCount > int.MaxValue ? int.MaxValue : (int)result.FileCount,
                    [],
                    []);
            }).ConfigureAwait(true);

            if (outcome.Cancelled)
            {
                StorageStatusText = "分析已取消";
                return;
            }
            if (!outcome.Success || result is null)
            {
                StorageStatusText = "分析未完成";
                return;
            }

            ApplyStorageAnalysis(result);
        }
        finally
        {
            IsStorageAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeStorageFolderAsync(StorageFolderUsageModel? folder)
    {
        if (folder is not null && TrySetStorageRoot(folder.Path))
            await AnalyzeStorageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task FindStorageDuplicatesAsync()
    {
        if (IsStorageBusy)
            return;
        if (TryNormalizeSearchIndexRoot(StorageRootPath) is not { } root || !Directory.Exists(root))
        {
            StorageDuplicateStatusText = "只能分析本地文件夹";
            Notify("存储洞察只能分析本地文件夹");
            return;
        }

        StorageRootPath = root;
        ClearStorageDuplicateResults("正在查找重复文件…");
        IsStorageFindingDuplicates = true;
        StorageDuplicateResult? result = null;
        try
        {
            var options = new StorageAnalysisOptions(IncludeHidden: ShowHidden);
            var outcome = await RunOperationAsync("正在查找重复文件…", async (progress, ct) =>
            {
                result = await _services.StorageInsights
                    .FindDuplicatesAsync(root, options, progress, ct).ConfigureAwait(true);
                return new OperationOutcome(result.Groups.Count, [], []);
            }).ConfigureAwait(true);

            if (outcome.Cancelled)
            {
                StorageDuplicateStatusText = "重复查找已取消";
                return;
            }
            if (!outcome.Success || result is null)
            {
                StorageDuplicateStatusText = "重复查找未完成";
                return;
            }

            ApplyStorageDuplicates(result);
        }
        finally
        {
            IsStorageFindingDuplicates = false;
        }
    }

    public async Task OpenStorageItemAsync(FileItemViewModel item)
    {
        if (item.IsFolder)
        {
            await OpenFolderAsync(item.FullPath).ConfigureAwait(true);
            return;
        }

        Page = ShellPage.Browser;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await Browser.NavigateToFileLocationAsync(item.FullPath).ConfigureAwait(true);
    }

    private void ClearStorageResults(string status)
    {
        StorageKindUsages.Clear();
        StorageTopFolders.Clear();
        StorageLargestFiles.Clear();
        StorageTotalBytes = 0;
        StorageFileCount = 0;
        StorageFolderCount = 0;
        StorageIsPartial = false;
        StorageWasTruncated = false;
        HasStorageAnalysis = false;
        ClearStorageDuplicateResults("尚未查找重复文件");
        StorageStatusText = status;
        NotifyStorageComputed();
    }

    private void ClearStorageDuplicateResults(string status)
    {
        StorageDuplicateGroups.Clear();
        StorageDuplicateReclaimableBytes = 0;
        StorageDuplicateCandidateCount = 0;
        StorageDuplicateFilesSkipped = 0;
        StorageDuplicateIsPartial = false;
        StorageDuplicateWasTruncated = false;
        HasStorageDuplicateScan = false;
        StorageDuplicateStatusText = status;
        OnPropertyChanged(nameof(StorageDuplicateReclaimableText));
        OnPropertyChanged(nameof(StorageDuplicateStateText));
    }

    private void ApplyStorageAnalysis(StorageAnalysisResult result)
    {
        StorageTotalBytes = result.TotalBytes;
        StorageFileCount = result.FileCount;
        StorageFolderCount = result.FolderCount;
        StorageIsPartial = result.IsPartial;
        StorageWasTruncated = result.WasTruncated;

        var denominator = Math.Max(1, result.TotalBytes);
        foreach (var usage in result.KindUsages)
        {
            StorageKindUsages.Add(new StorageKindUsageModel(
                usage.Kind,
                StorageKindName(usage.Kind),
                StorageKindGlyph(usage.Kind),
                usage.TotalBytes,
                usage.FileCount,
                (double)usage.TotalBytes / denominator * 100));
        }

        foreach (var folder in result.TopFolders)
        {
            StorageTopFolders.Add(new StorageFolderUsageModel(
                folder.IsRootFiles ? "根目录文件" : folder.Name,
                folder.Path,
                folder.TotalBytes,
                folder.FileCount,
                folder.FolderCount,
                (double)folder.TotalBytes / denominator * 100,
                folder.IsRootFiles));
        }

        foreach (var file in result.LargestFiles)
        {
            StorageLargestFiles.Add(new FileItemViewModel(file)
            {
                IsFavorite = _services.SettingsStore.IsFavorite(file.FullPath),
            });
        }

        HasStorageAnalysis = true;
        StorageStatusText = $"{StorageFileCountText} 个文件 · {StorageTotalSizeText}" +
                             (result.WasTruncated ? " · 已截断" : result.IsPartial ? " · 部分结果" : string.Empty);
        NotifyStorageComputed();
    }

    private void ApplyStorageDuplicates(StorageDuplicateResult result)
    {
        StorageDuplicateReclaimableBytes = result.ReclaimableBytes;
        StorageDuplicateCandidateCount = result.CandidateFileCount;
        StorageDuplicateFilesSkipped = result.FilesSkipped;
        StorageDuplicateIsPartial = result.IsPartial;
        StorageDuplicateWasTruncated = result.WasTruncated;

        foreach (var group in result.Groups.Take(50))
        {
            var files = group.Files
                .Select(file => new FileItemViewModel(file)
                {
                    IsFavorite = _services.SettingsStore.IsFavorite(file.FullPath),
                })
                .ToArray();
            StorageDuplicateGroups.Add(new StorageDuplicateGroupModel(group.FileSize, group.Sha256, files));
        }

        HasStorageDuplicateScan = true;
        StorageDuplicateStatusText = result.Groups.Count == 0
            ? "未发现完全相同的文件"
            : $"{result.Groups.Count} 组 · {result.CandidateFileCount} 个候选 · 可复核 {StorageDuplicateReclaimableText}" +
              (result.WasTruncated ? " · 已截断" : result.IsPartial ? " · 部分结果" : string.Empty) +
              (result.FilesSkipped > 0 ? $" · 跳过 {result.FilesSkipped}" : string.Empty);
        OnPropertyChanged(nameof(StorageDuplicateReclaimableText));
        OnPropertyChanged(nameof(StorageDuplicateStateText));
    }

    private void NotifyStorageComputed()
    {
        OnPropertyChanged(nameof(StorageTotalSizeText));
        OnPropertyChanged(nameof(StorageFileCountText));
        OnPropertyChanged(nameof(StorageFolderCountText));
        OnPropertyChanged(nameof(StorageAnalysisStateText));
        OnPropertyChanged(nameof(StorageDuplicateReclaimableText));
        OnPropertyChanged(nameof(StorageDuplicateStateText));
    }

    private static string StorageKindName(FileKind kind) => kind switch
    {
        FileKind.Image => "图像",
        FileKind.Video => "视频",
        FileKind.Audio => "音频",
        FileKind.Document => "文档",
        FileKind.Archive => "压缩包",
        FileKind.Code => "代码",
        FileKind.Text => "文本",
        FileKind.Data => "数据",
        FileKind.Font => "字体",
        FileKind.Executable => "程序",
        FileKind.Folder => "文件夹",
        _ => "其他",
    };

    private static string? StorageKindGlyph(FileKind kind) => kind switch
    {
        FileKind.Folder => MdSymbols.Folder,
        FileKind.Image => MdSymbols.Image,
        FileKind.Video => MdSymbols.Movie,
        FileKind.Audio => MdSymbols.MusicNote,
        FileKind.Document => MdSymbols.Description,
        FileKind.Archive => MdSymbols.FolderZip,
        FileKind.Code => MdSymbols.Code,
        FileKind.Data => MdSymbols.DataObject,
        FileKind.Font => MdSymbols.FontDownload,
        FileKind.Executable => MdSymbols.Terminal,
        FileKind.Text => MdSymbols.TextSnippet,
        _ => MdSymbols.InsertDriveFile,
    };

    // ---------- 压缩包工作台（ZIP 家族只读虚拟文件夹） ----------
    private const int MaxArchiveEntries = 10_000;
    private const int MaxVisibleArchiveEntries = 2_000;
    private const int MaxArchivePreviewBytes = 16 * 1024 * 1024;
    private const int MaxArchivePreviewTextBytes = 256 * 1024;

    [ObservableProperty] private string _archivePath = string.Empty;
    [ObservableProperty] private string _archiveName = string.Empty;
    [ObservableProperty] private string _archiveCurrentFolder = string.Empty;
    [ObservableProperty] private string _archiveFilterText = string.Empty;
    [ObservableProperty] private bool _isArchiveBusy;
    [ObservableProperty] private bool _hasArchive;
    [ObservableProperty] private bool _archiveIsTruncated;
    [ObservableProperty] private string _archiveStatusText = "从文件列表打开 ZIP、JAR 或 APK";
    [ObservableProperty] private string _archiveDestinationPath = string.Empty;
    [ObservableProperty] private bool _archivePreserveStructure = true;
    [ObservableProperty] private bool _archiveOpenAfterExtract;
    [ObservableProperty] private string _archiveConflictPolicyName = nameof(ConflictPolicy.KeepBoth);
    [ObservableProperty] private ArchiveWorkbenchEntryViewModel? _selectedArchiveEntry;
    [ObservableProperty] private Bitmap? _archivePreviewImage;
    [ObservableProperty] private string? _archivePreviewText;
    [ObservableProperty] private string _archivePreviewMessage = "选择一个条目查看详情";
    [ObservableProperty] private int _archiveEntryCount;
    [ObservableProperty] private int _archiveFileCount;
    [ObservableProperty] private int _archiveFolderCount;
    [ObservableProperty] private long _archiveUncompressedBytes;
    [ObservableProperty] private long _archiveCompressedBytes;
    [ObservableProperty] private int _archiveSelectedCount;
    [ObservableProperty] private long _archiveSelectedBytes;

    public bool HasArchiveVisibleEntries => ArchiveVisibleEntries.Count > 0;
    public bool ShowArchiveEmpty => HasArchive && !IsArchiveBusy && ArchiveVisibleEntries.Count == 0;
    public bool IsArchiveFiltering => !string.IsNullOrWhiteSpace(ArchiveFilterText);
    public bool CanArchiveGoUp => HasArchive && !IsArchiveFiltering && !string.IsNullOrEmpty(ArchiveCurrentFolder);
    public bool HasSelectedArchiveEntry => SelectedArchiveEntry is not null;
    public bool HasArchivePreviewImage => ArchivePreviewImage is not null;
    public bool HasArchivePreviewText => ArchivePreviewText is not null;
    public bool HasArchiveSelection => ArchiveSelectedCount > 0;
    public string ArchiveUncompressedSizeText => Formatting.Bytes(ArchiveUncompressedBytes);
    public string ArchiveCompressedSizeText => Formatting.Bytes(ArchiveCompressedBytes);
    public string ArchiveCompressionRatioText => ArchiveUncompressedBytes > 0
        ? (ArchiveCompressedBytes * 100.0 / ArchiveUncompressedBytes).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%"
        : "—";
    public string ArchiveCountsText =>
        $"{ArchiveFileCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 个文件 · " +
        $"{ArchiveFolderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 个文件夹";
    public string ArchiveSelectedSummaryText => ArchiveSelectedCount == 0
        ? "尚未勾选条目"
        : $"已选 {ArchiveSelectedCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 个文件 · {Formatting.Bytes(ArchiveSelectedBytes)}";
    public string ArchiveDestinationName => string.IsNullOrWhiteSpace(ArchiveDestinationPath)
        ? "未选择"
        : Path.GetFileName(ArchiveDestinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string ArchiveConflictPolicyText => ArchiveConflictPolicyName switch
    {
        nameof(ConflictPolicy.Overwrite) => "覆盖同名文件",
        nameof(ConflictPolicy.Skip) => "跳过同名文件",
        _ => "两者保留（自动改名）",
    };
    public bool IsArchiveKeepBoth => ArchiveConflictPolicyName == nameof(ConflictPolicy.KeepBoth);
    public bool IsArchiveSkipConflicts => ArchiveConflictPolicyName == nameof(ConflictPolicy.Skip);
    public bool IsArchiveOverwriteConflicts => ArchiveConflictPolicyName == nameof(ConflictPolicy.Overwrite);
    public string? SelectedArchiveGlyph => SelectedArchiveEntry?.Glyph;
    public string SelectedArchiveTitle => SelectedArchiveEntry?.Name ?? "压缩包概览";
    public string SelectedArchivePathText => SelectedArchiveEntry?.DisplayPath ?? ArchivePath;
    public string SelectedArchiveOriginalSizeText => SelectedArchiveEntry is { } entry ? Formatting.Bytes(entry.Length) : ArchiveUncompressedSizeText;
    public string SelectedArchiveCompressedSizeText => SelectedArchiveEntry is { } entry ? Formatting.Bytes(entry.CompressedLength) : ArchiveCompressedSizeText;
    public string SelectedArchiveRatioText => SelectedArchiveEntry?.CompressionRatioText ?? ArchiveCompressionRatioText;
    public string SelectedArchiveModifiedText => SelectedArchiveEntry?.FullModifiedText ?? "—";

    partial void OnArchiveNameChanged(string value) => OnPropertyChanged(nameof(PageTitle));

    partial void OnArchiveCurrentFolderChanged(string value)
    {
        OnPropertyChanged(nameof(CanArchiveGoUp));
        BuildArchiveBreadcrumbs();
    }

    partial void OnArchiveFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsArchiveFiltering));
        OnPropertyChanged(nameof(CanArchiveGoUp));
        if (HasArchive)
            RefreshArchiveVisibleEntries();
    }

    partial void OnIsArchiveBusyChanged(bool value) => OnPropertyChanged(nameof(ShowArchiveEmpty));
    partial void OnHasArchiveChanged(bool value) => OnPropertyChanged(nameof(ShowArchiveEmpty));

    partial void OnArchiveDestinationPathChanged(string value) => OnPropertyChanged(nameof(ArchiveDestinationName));

    partial void OnArchiveConflictPolicyNameChanged(string value)
    {
        OnPropertyChanged(nameof(ArchiveConflictPolicyText));
        OnPropertyChanged(nameof(IsArchiveKeepBoth));
        OnPropertyChanged(nameof(IsArchiveSkipConflicts));
        OnPropertyChanged(nameof(IsArchiveOverwriteConflicts));
    }

    partial void OnSelectedArchiveEntryChanged(ArchiveWorkbenchEntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedArchiveEntry));
        OnPropertyChanged(nameof(SelectedArchiveGlyph));
        OnPropertyChanged(nameof(SelectedArchiveTitle));
        OnPropertyChanged(nameof(SelectedArchivePathText));
        OnPropertyChanged(nameof(SelectedArchiveOriginalSizeText));
        OnPropertyChanged(nameof(SelectedArchiveCompressedSizeText));
        OnPropertyChanged(nameof(SelectedArchiveRatioText));
        OnPropertyChanged(nameof(SelectedArchiveModifiedText));
    }

    partial void OnArchivePreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasArchivePreviewImage));
    partial void OnArchivePreviewTextChanged(string? value) => OnPropertyChanged(nameof(HasArchivePreviewText));

    partial void OnArchiveSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ArchiveSelectedSummaryText));
        OnPropertyChanged(nameof(HasArchiveSelection));
    }

    partial void OnArchiveSelectedBytesChanged(long value) => OnPropertyChanged(nameof(ArchiveSelectedSummaryText));

    public async Task OpenDocumentWorkbenchAsync(FileItem item)
    {
        if (!DocumentWorkbenchViewModel.CanOpen(item))
        {
            Notify(DocumentWorkbenchViewModel.IsSupportedText(item) && FileLocator.IsStorageUri(item.FullPath)
                ? "授权文件夹中的文本暂仅支持只读预览"
                : "此类型暂不支持文档工作台");
            return;
        }

        if (DocumentWorkbench.IsSameDocument(item.FullPath))
        {
            Page = ShellPage.DocumentWorkbench;
            IsDrawerOpen = false;
            IsPreviewPageOpen = false;
            OnPropertyChanged(nameof(PageTitle));
            return;
        }

        if (DocumentWorkbench.HasDocument &&
            !await DocumentWorkbench.ConfirmDiscardAsync().ConfigureAwait(true))
            return;

        FileItem fresh = item;
        try
        {
            fresh = await _services.FileSystem.StatAsync(item.FullPath, CancellationToken.None).ConfigureAwait(true) ?? item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Notify($"无法读取此文档：{ex.Message}");
            return;
        }
        if (!DocumentWorkbenchViewModel.CanOpen(fresh))
        {
            Notify("此类型暂不支持文档工作台");
            return;
        }

        Page = ShellPage.DocumentWorkbench;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await DocumentWorkbench.LoadAsync(fresh).ConfigureAwait(true);
        OnPropertyChanged(nameof(PageTitle));
    }

    public async Task CloseDocumentWorkbenchAsync()
    {
        if (!await DocumentWorkbench.ConfirmDiscardAsync().ConfigureAwait(true))
            return;
        DocumentWorkbench.Clear();
        Page = ShellPage.Browser;
    }

    private async Task OnDocumentSavedAsync(string path)
    {
        await Browser.RecordRecentFileAsync(path).ConfigureAwait(true);
        await Browser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (_secondaryBrowserInitialized)
            await SecondaryBrowser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (string.Equals(Preview.CurrentItem?.FullPath, path, StringComparison.Ordinal))
        {
            var fresh = await _services.FileSystem.StatAsync(path, CancellationToken.None).ConfigureAwait(true) ?? Preview.CurrentItem;
            await Preview.LoadAsync(fresh).ConfigureAwait(true);
        }
    }

    public async Task OpenArchiveWorkbenchAsync(string path)
    {
        if (FileLocator.IsStorageUri(path))
        {
            Notify("授权文件夹中的压缩包暂不支持工作台");
            return;
        }
        if (!_services.Archive.CanOpen(path))
        {
            Notify("当前仅支持 ZIP、JAR、APK 压缩包");
            return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Notify("压缩包路径无效");
            return;
        }
        if (!File.Exists(fullPath))
        {
            Notify("压缩包不存在");
            return;
        }

        var generation = ++_archiveGeneration;
        _archivePreviewGeneration++;
        Page = ShellPage.ArchiveWorkbench;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        ResetArchiveWorkbench(fullPath);
        IsArchiveBusy = true;
        try
        {
            var listed = await _services.Archive
                .ListEntriesAsync(fullPath, MaxArchiveEntries + 1, CancellationToken.None)
                .ConfigureAwait(true);
            if (generation != _archiveGeneration) return;

            ArchiveIsTruncated = listed.Count > MaxArchiveEntries;
            _archiveEntries = listed.Take(MaxArchiveEntries).ToList();
            BuildArchiveTree();
            HasArchive = true;
            UpdateArchiveStats();
            RefreshArchiveVisibleEntries();
            ArchivePreviewMessage = ArchiveFileCount == 0
                ? "这个压缩包里没有文件条目"
                : "选择一个条目查看详情；文本与图片可直接预览";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (generation == _archiveGeneration)
            {
                HasArchive = false;
                ArchiveStatusText = "无法读取这个压缩包";
                ArchivePreviewMessage = $"无法读取：{ex.Message}";
                Notify("无法打开压缩包");
            }
        }
        finally
        {
            if (generation == _archiveGeneration)
                IsArchiveBusy = false;
        }
    }

    [RelayCommand]
    private void CloseArchiveWorkbench()
    {
        _archivePreviewGeneration++;
        Page = ShellPage.Browser;
    }

    public void NavigateArchiveFolder(string? folderPath)
    {
        var normalized = NormalizeArchiveFolderPath(folderPath);
        if (normalized.Length > 0 && !_archiveFolders.ContainsKey(normalized))
            return;

        SelectedArchiveEntry = null;
        ClearArchivePreview("选择一个条目查看详情");
        if (!string.IsNullOrEmpty(ArchiveFilterText))
            ArchiveFilterText = string.Empty; // 先清筛选会在旧目录刷新一次，随后由目标目录刷新覆盖。
        ArchiveCurrentFolder = normalized;
        RefreshArchiveVisibleEntries();
    }

    [RelayCommand]
    private void ArchiveGoUp()
    {
        if (CanArchiveGoUp)
            NavigateArchiveFolder(ArchiveParentFolder(ArchiveCurrentFolder));
    }

    [RelayCommand]
    private void ClearArchiveFilter() => ArchiveFilterText = string.Empty;

    [RelayCommand]
    private void SelectVisibleArchiveEntries()
    {
        foreach (var row in ArchiveVisibleEntries)
            row.IsChecked = true;
        RefreshArchiveSelectionSummary();
    }

    [RelayCommand]
    private void ClearArchiveSelection()
    {
        _selectedArchiveEntries.Clear();
        foreach (var row in ArchiveVisibleEntries)
            row.IsChecked = false;
        RefreshArchiveSelectionSummary();
    }

    [RelayCommand]
    private void SetArchiveConflictPolicy(string? policy)
    {
        if (Enum.TryParse<ConflictPolicy>(policy, out var parsed))
            ArchiveConflictPolicyName = parsed.ToString();
    }

    public async Task SelectArchiveEntryAsync(ArchiveWorkbenchEntryViewModel? row)
    {
        if (row is null) return;
        SelectedArchiveEntry = row;
        var generation = ++_archivePreviewGeneration;
        ClearArchivePreview(row.IsFolder
            ? $"{row.DescendantFileCount:N0} 个后代文件；双击或点箭头进入"
            : "正在读取条目…");
        if (row.IsFolder) return;

        if (row.Length > MaxArchivePreviewBytes)
        {
            ArchivePreviewMessage = $"条目为 {Formatting.Bytes(row.Length)}，超过 {Formatting.Bytes(MaxArchivePreviewBytes)} 的免解压预览上限";
            return;
        }
        if (row.Kind is not (FileKind.Image or FileKind.Text or FileKind.Code or FileKind.Data))
        {
            ArchivePreviewMessage = "此类型在工作台仅显示元数据，可解压后用完整预览打开";
            return;
        }
        if (row.Entry is null)
        {
            ArchivePreviewMessage = "找不到这个条目的原始记录";
            return;
        }

        try
        {
            var bytes = await _services.Archive.ReadEntryBytesAsync(
                ArchivePath, row.Entry.FullName, MaxArchivePreviewBytes, CancellationToken.None).ConfigureAwait(true);
            if (generation != _archivePreviewGeneration) return;
            if (bytes is null)
            {
                ArchivePreviewMessage = "无法在预览上限内读取这个条目";
                return;
            }

            if (row.Kind == FileKind.Image)
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    SetArchivePreviewImage(new Bitmap(stream));
                    ArchivePreviewMessage = "免整包解压预览";
                }
                catch (Exception)
                {
                    ArchivePreviewMessage = "这张图片无法解码，可解压后重试";
                }
                return;
            }

            if (ContainsNullByte(bytes))
            {
                ArchivePreviewMessage = "检测到二进制内容，工作台仅显示元数据";
                return;
            }

            var text = DecodeArchivePreviewText(bytes);
            ArchivePreviewText = bytes.Length > MaxArchivePreviewTextBytes
                ? text + "\n\n… 预览已截断"
                : text;
            ArchivePreviewMessage = bytes.Length == 0 ? "空文件" : "免整包解压预览";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (generation == _archivePreviewGeneration)
                ArchivePreviewMessage = $"预览失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExtractArchiveAllAsync() => await ExtractArchiveAsync(selectedOnly: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task ExtractArchiveSelectedAsync() => await ExtractArchiveAsync(selectedOnly: true).ConfigureAwait(true);

    private async Task ExtractArchiveAsync(bool selectedOnly)
    {
        if (IsArchiveBusy || !HasArchive) return;
        if (ArchiveFileCount == 0)
        {
            Notify("压缩包中没有可解压的文件");
            return;
        }

        HashSet<string>? selectedNames = null;
        if (selectedOnly)
        {
            selectedNames = _archiveEntries
                .Where(entry => !entry.IsFolder && _selectedArchiveEntries.Contains(NormalizeArchiveVirtualPath(entry.FullName)))
                .Select(entry => entry.FullName)
                .ToHashSet(StringComparer.Ordinal);
            if (selectedNames.Count == 0)
            {
                Notify("先勾选要解压的条目");
                return;
            }
        }

        string destination;
        try { destination = Path.GetFullPath(ArchiveDestinationPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Notify("解压目标路径无效");
            return;
        }

        var policy = Enum.TryParse<ConflictPolicy>(ArchiveConflictPolicyName, out var parsed)
            ? parsed
            : ConflictPolicy.KeepBoth;
        var options = new ArchiveExtractOptions(selectedNames, ArchivePreserveStructure, policy);
        var archivePath = ArchivePath;
        var title = selectedOnly
            ? $"正在解压所选 {selectedNames!.Count} 个文件…"
            : $"正在解压「{ArchiveName}」…";

        IsArchiveBusy = true;
        try
        {
            var outcome = await RunOperationAsync(title, (progress, ct) =>
                _services.Archive.ExtractEntriesAsync(archivePath, destination, options, progress, ct)).ConfigureAwait(true);

            if (outcome.Cancelled)
            {
                ArchiveStatusText = "解压已取消；已完成的文件保留在目标文件夹";
            }
            else if (outcome.Success)
            {
                ArchiveStatusText = $"已解压 {outcome.SucceededCount:N0} 项到 {destination}";
            }
            else
            {
                ArchiveStatusText = "解压未全部完成，逐项原因见任务中心";
            }

            await Browser.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (outcome.Success && ArchiveOpenAfterExtract && Directory.Exists(destination))
                await OpenFolderAsync(destination).ConfigureAwait(true);
        }
        finally
        {
            IsArchiveBusy = false;
        }
    }

    private void ResetArchiveWorkbench(string fullPath)
    {
        HasArchive = false;
        ArchivePath = fullPath;
        ArchiveName = Path.GetFileName(fullPath);
        var parent = Path.GetDirectoryName(fullPath);
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        ArchiveDestinationPath = string.IsNullOrWhiteSpace(parent)
            ? fullPath + "-extracted"
            : Path.Combine(parent, string.IsNullOrWhiteSpace(stem) ? "extracted" : stem);
        ArchiveCurrentFolder = string.Empty;
        ArchiveFilterText = string.Empty;
        ArchiveIsTruncated = false;
        ArchivePreserveStructure = true;
        ArchiveOpenAfterExtract = false;
        ArchiveConflictPolicyName = nameof(ConflictPolicy.KeepBoth);
        SelectedArchiveEntry = null;
        _archiveEntries = [];
        _archiveFolders.Clear();
        _archiveFilesByParent.Clear();
        _archiveChildFolders.Clear();
        _archiveDescendantsByFolder.Clear();
        _archiveExplicitFolders.Clear();
        _selectedArchiveEntries.Clear();
        ArchiveVisibleEntries.Clear();
        ArchiveEntryCount = 0;
        ArchiveFileCount = 0;
        ArchiveFolderCount = 0;
        ArchiveUncompressedBytes = 0;
        ArchiveCompressedBytes = 0;
        ArchiveSelectedCount = 0;
        ArchiveSelectedBytes = 0;
        ArchiveStatusText = "正在读取条目…";
        ClearArchivePreview("正在读取条目…");
        BuildArchiveBreadcrumbs();
        NotifyArchiveStats();
    }

    private void BuildArchiveTree()
    {
        _archiveFolders.Clear();
        _archiveFilesByParent.Clear();
        _archiveChildFolders.Clear();
        _archiveDescendantsByFolder.Clear();
        _archiveExplicitFolders.Clear();
        GetArchiveFolderAggregate(string.Empty);
        _archiveDescendantsByFolder[string.Empty] = [];

        foreach (var entry in _archiveEntries)
        {
            var virtualPath = NormalizeArchiveVirtualPath(entry.FullName);
            if (entry.IsFolder)
            {
                var folderPath = NormalizeArchiveFolderPath(virtualPath);
                EnsureArchiveFolderChain(folderPath);
                _archiveExplicitFolders[folderPath] = entry;
                var aggregate = GetArchiveFolderAggregate(folderPath);
                if (entry.LastWriteTime > aggregate.LastWriteTime)
                    aggregate.LastWriteTime = entry.LastWriteTime;
                continue;
            }

            var parent = ArchiveParentFolder(virtualPath);
            if (parent.Length > 0)
                EnsureArchiveFolderChain(parent);
            if (!_archiveFilesByParent.TryGetValue(parent, out var siblings))
                _archiveFilesByParent[parent] = siblings = [];
            siblings.Add(entry);

            foreach (var ancestor in ArchiveAncestorFolders(parent))
            {
                var aggregate = GetArchiveFolderAggregate(ancestor);
                aggregate.FileCount++;
                aggregate.Length += entry.Length;
                aggregate.CompressedLength += entry.CompressedLength;
                if (entry.LastWriteTime > aggregate.LastWriteTime)
                    aggregate.LastWriteTime = entry.LastWriteTime;
                if (!_archiveDescendantsByFolder.TryGetValue(ancestor, out var descendants))
                    _archiveDescendantsByFolder[ancestor] = descendants = [];
                descendants.Add(virtualPath);
            }
        }
    }

    private void RefreshArchiveVisibleEntries()
    {
        ArchiveVisibleEntries.Clear();
        if (!HasArchive) return;

        var rows = new List<ArchiveWorkbenchEntryViewModel>();
        var query = ArchiveFilterText.Trim();
        int total;
        if (query.Length > 0)
        {
            var matches = _archiveEntries
                .Where(entry => !entry.IsFolder && NormalizeArchiveVirtualPath(entry.FullName)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => NormalizeArchiveVirtualPath(entry.FullName), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            total = matches.Length;
            foreach (var entry in matches.Take(MaxVisibleArchiveEntries))
                rows.Add(CreateArchiveFileRow(entry));
        }
        else
        {
            var folderPaths = _archiveChildFolders.TryGetValue(ArchiveCurrentFolder, out var children)
                ? children.OrderBy(path => ArchiveSegmentName(path), StringComparer.CurrentCultureIgnoreCase).ToArray()
                : [];
            var files = _archiveFilesByParent.TryGetValue(ArchiveCurrentFolder, out var directFiles)
                ? directFiles.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
                : [];
            total = folderPaths.Length + files.Length;
            foreach (var folderPath in folderPaths)
            {
                if (rows.Count >= MaxVisibleArchiveEntries) break;
                rows.Add(CreateArchiveFolderRow(folderPath));
            }
            foreach (var entry in files)
            {
                if (rows.Count >= MaxVisibleArchiveEntries) break;
                rows.Add(CreateArchiveFileRow(entry));
            }
        }

        foreach (var row in rows)
            ArchiveVisibleEntries.Add(row);

        ArchiveStatusText = IsArchiveFiltering
            ? $"匹配 {total:N0} 个文件"
            : ArchiveCountsText;
        if (rows.Count < total)
            ArchiveStatusText += $" · 显示前 {rows.Count:N0} 项";
        if (ArchiveIsTruncated)
            ArchiveStatusText += $" · 仅载入前 {MaxArchiveEntries:N0} 条";
        OnPropertyChanged(nameof(ShowArchiveEmpty));
    }

    private ArchiveWorkbenchEntryViewModel CreateArchiveFileRow(ArchiveEntryItem entry)
    {
        var virtualPath = NormalizeArchiveVirtualPath(entry.FullName);
        var row = ArchiveWorkbenchEntryViewModel.FromFile(entry, _selectedArchiveEntries.Contains(virtualPath));
        row.CheckedChanged += OnArchiveEntryCheckedChanged;
        return row;
    }

    private ArchiveWorkbenchEntryViewModel CreateArchiveFolderRow(string folderPath)
    {
        var aggregate = GetArchiveFolderAggregate(folderPath);
        var descendants = _archiveDescendantsByFolder.TryGetValue(folderPath, out var names)
            ? names.ToArray()
            : [];
        var allChecked = descendants.Length > 0 && descendants.All(name => _selectedArchiveEntries.Contains(name));
        _archiveExplicitFolders.TryGetValue(folderPath, out var explicitEntry);
        var row = ArchiveWorkbenchEntryViewModel.FromFolder(
            folderPath,
            explicitEntry,
            aggregate.Length,
            aggregate.CompressedLength,
            aggregate.LastWriteTime,
            descendants,
            allChecked);
        row.CheckedChanged += OnArchiveEntryCheckedChanged;
        return row;
    }

    private void OnArchiveEntryCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not ArchiveWorkbenchEntryViewModel row) return;
        if (row.IsChecked)
        {
            foreach (var name in row.DescendantEntryNames)
                _selectedArchiveEntries.Add(name);
        }
        else
        {
            foreach (var name in row.DescendantEntryNames)
                _selectedArchiveEntries.Remove(name);
        }
        RefreshArchiveSelectionSummary();
    }

    private void RefreshArchiveSelectionSummary()
    {
        var selected = _archiveEntries
            .Where(entry => !entry.IsFolder && _selectedArchiveEntries.Contains(NormalizeArchiveVirtualPath(entry.FullName)))
            .ToArray();
        ArchiveSelectedCount = selected.Length;
        ArchiveSelectedBytes = selected.Sum(entry => entry.Length);
    }

    private void UpdateArchiveStats()
    {
        ArchiveEntryCount = _archiveEntries.Count;
        ArchiveFileCount = _archiveEntries.Count(entry => !entry.IsFolder);
        ArchiveFolderCount = _archiveFolders.Keys.Count(path => path.Length > 0);
        var root = GetArchiveFolderAggregate(string.Empty);
        ArchiveUncompressedBytes = root.Length;
        ArchiveCompressedBytes = root.CompressedLength;
        RefreshArchiveSelectionSummary();
        NotifyArchiveStats();
    }

    private void NotifyArchiveStats()
    {
        OnPropertyChanged(nameof(ArchiveUncompressedSizeText));
        OnPropertyChanged(nameof(ArchiveCompressedSizeText));
        OnPropertyChanged(nameof(ArchiveCompressionRatioText));
        OnPropertyChanged(nameof(ArchiveCountsText));
        OnPropertyChanged(nameof(SelectedArchiveOriginalSizeText));
        OnPropertyChanged(nameof(SelectedArchiveCompressedSizeText));
        OnPropertyChanged(nameof(SelectedArchiveRatioText));
    }

    private void BuildArchiveBreadcrumbs()
    {
        ArchiveBreadcrumbs.Clear();
        ArchiveBreadcrumbs.Add(new ArchiveBreadcrumbModel(
            string.IsNullOrWhiteSpace(ArchiveName) ? "压缩包" : ArchiveName, string.Empty));
        var current = string.Empty;
        foreach (var segment in ArchiveCurrentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += segment + "/";
            ArchiveBreadcrumbs.Add(new ArchiveBreadcrumbModel(segment, current));
        }
    }

    private ArchiveFolderAggregate GetArchiveFolderAggregate(string folderPath)
    {
        if (!_archiveFolders.TryGetValue(folderPath, out var aggregate))
            _archiveFolders[folderPath] = aggregate = new ArchiveFolderAggregate();
        return aggregate;
    }

    private void EnsureArchiveFolderChain(string folderPath)
    {
        var normalized = NormalizeArchiveFolderPath(folderPath);
        GetArchiveFolderAggregate(string.Empty);
        if (normalized.Length == 0) return;

        var current = string.Empty;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = current + segment + "/";
            GetArchiveFolderAggregate(child);
            if (!_archiveChildFolders.TryGetValue(current, out var children))
                _archiveChildFolders[current] = children = [];
            if (!children.Contains(child))
                children.Add(child);
            current = child;
        }
    }

    private void ClearArchivePreview(string message)
    {
        SetArchivePreviewImage(null);
        ArchivePreviewText = null;
        ArchivePreviewMessage = message;
    }

    private void SetArchivePreviewImage(Bitmap? next)
    {
        var previous = ArchivePreviewImage;
        ArchivePreviewImage = next;
        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
    }

    private static bool ContainsNullByte(byte[] bytes)
    {
        var sample = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sample; i++)
        {
            if (bytes[i] == 0) return true;
        }
        return false;
    }

    private static string DecodeArchivePreviewText(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, MaxArchivePreviewTextBytes);
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, length - 3);
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 0, length);
        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string NormalizeArchiveVirtualPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string NormalizeArchiveFolderPath(string? path)
    {
        var normalized = NormalizeArchiveVirtualPath(path ?? string.Empty).Trim();
        if (normalized.Length == 0) return string.Empty;
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string ArchiveParentFolder(string path)
    {
        var normalized = NormalizeArchiveVirtualPath(path).TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
    }

    private static IEnumerable<string> ArchiveAncestorFolders(string parent)
    {
        yield return string.Empty;
        var current = string.Empty;
        foreach (var segment in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += segment + "/";
            yield return current;
        }
    }

    private static string ArchiveSegmentName(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private sealed class ArchiveFolderAggregate
    {
        public int FileCount;
        public long Length;
        public long CompressedLength;
        public DateTimeOffset LastWriteTime;
    }

    // ---------- 搜索中心 / 文件名索引 ----------
    [ObservableProperty] private bool _useSearchIndex = true;
    [ObservableProperty] private bool _isSemanticSearchMode;
    [ObservableProperty] private bool _isContentSearchMode;
    [ObservableProperty] private bool _enableContentIndex;
    [ObservableProperty] private string _contentIndexStatusText = "尚未建立内容索引";
    [ObservableProperty] private int _contentIndexDocumentCount;
    [ObservableProperty] private long _contentIndexBytes;
    [ObservableProperty] private string _contentIndexUpdatedText = "尚未建立";
    [ObservableProperty] private string _searchCenterQuery = string.Empty;
    [ObservableProperty] private string _searchCenterKindFilter = "All";
    [ObservableProperty] private string _searchSizeFilter = SearchFilterPresets.Any;
    [ObservableProperty] private string _searchDateFilter = SearchFilterPresets.Any;
    [ObservableProperty] private string _semanticSearchIndexStatusText = "尚未建立 AI 语义索引";
    [ObservableProperty] private int _semanticSearchIndexedItemCount;
    [ObservableProperty] private bool _semanticSearchIndexIsCurrent;
    [ObservableProperty] private bool _semanticSearchIndexIsPartial;
    [ObservableProperty] private long _semanticSearchIndexBytes;
    [ObservableProperty] private string _semanticSearchIndexUpdatedText = "尚未建立";
    [ObservableProperty] private bool _isSearchCenterBusy;
    [ObservableProperty] private string _searchCenterStatusText = "输入关键词开始搜索";
    [ObservableProperty] private int _searchIndexEntryCount;
    [ObservableProperty] private long _searchIndexBytes;
    [ObservableProperty] private string _searchIndexUpdatedText = "尚未建立";
    [ObservableProperty] private bool _searchIndexIsCurrent;
    [ObservableProperty] private bool _searchIndexIsPartial;

    public bool HasSearchIndexRoots => SearchIndexRoots.Count > 0;
    public bool HasSearchCenterResults => SearchCenterResults.Count > 0;
    public bool HasSemanticSearchIndex => SemanticSearchIndexedItemCount > 0 || SemanticSearchIndexBytes > 0;
    public string SemanticSearchIndexCountText => SemanticSearchIndexedItemCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string SemanticSearchIndexSizeText => Formatting.Bytes(SemanticSearchIndexBytes);
    public string SemanticSearchIndexSummaryText =>
        $"{SemanticSearchIndexCountText} 项 · {SemanticSearchIndexSizeText} · {SemanticSearchIndexUpdatedText}";
    public bool IsKeywordSearchMode => !IsSemanticSearchMode && !IsContentSearchMode;
    public bool HasContentIndex => ContentIndexDocumentCount > 0 || ContentIndexBytes > 0;
    public string ContentIndexCountText =>
        ContentIndexDocumentCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string ContentIndexSizeText => Formatting.Bytes(ContentIndexBytes);
    public string ContentIndexSummaryText =>
        HasContentIndex
            ? $"{ContentIndexCountText} 篇 · {ContentIndexSizeText} · {ContentIndexUpdatedText}"
            : "尚未建立内容索引";
    public bool CanRebuildContentIndex => EnableContentIndex && HasSearchIndexRoots && !IsSearchCenterBusy;
    public string SearchCenterSearchPlaceholderText => IsContentSearchMode
        ? "搜索已建索引的文件正文"
        : IsSemanticSearchMode
            ? "用自然语言描述要找的文件或笔记"
            : "搜索已索引文件名，也可搜正文";
    public string SearchCenterModeHintText => IsContentSearchMode
        ? ContentSearchHintText()
        : !IsSemanticSearchMode
        ? KeywordSearchHintText()
        : !EnableLocalAi
            ? "请先到设置启用本地 AI；所有嵌入推理均在本机运行，不上传内容。"
            : !HasLocalAiTextModel
                ? "请先按设置页命令下载 BGE small zh 或 MiniLM 文本模型。"
                : !UseSearchIndex || !HasSearchIndexRoots
                    ? "请先启用文件名索引、加入本地文件夹并建立文件名索引。"
                    : !SearchIndexIsCurrent
                        ? "文件名索引需要先重建，之后才能建立 AI 向量索引。"
                        : $"只在本机索引文件名与受限文本摘要，不保存原文或上传内容。{SemanticSearchIndexStatusText}"
                          + "结果按余弦相似度排序；标注「低置信」的是陪跑项。";
    public bool CanBuildSemanticSearchIndex => EnableLocalAi && HasLocalAiTextModel &&
        UseSearchIndex && HasSearchIndexRoots && SearchIndexIsCurrent && !IsSearchCenterBusy;
    public bool HasLocalAiTextModel => LocalAiModels.Any(model =>
        model.Installed && (model.Id is "text-embed-zh" or "text-embed-en"));
    public bool ShowSearchCenterEmpty => !IsSearchCenterBusy && SearchCenterResults.Count == 0;
    public string SearchIndexRootCountText => SearchIndexRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string SearchIndexEntryCountText => SearchIndexEntryCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string SearchIndexSizeText => Formatting.Bytes(SearchIndexBytes);
    public string SearchIndexStateText =>
        !UseSearchIndex ? "已暂停" :
        SearchIndexRoots.Count == 0 ? "未配置" :
        // v0.56：快照跟着目录变化自己补，用户不必每加一个文件就重建一次。
        SearchIndexIsCurrent ? (SearchIndexIsPartial ? "已跟随 · 部分" : "已跟随改动") :
        SearchIndexEntryCount > 0 ? "需重建" : "未建立";

    public bool IsSearchKindAll => SearchCenterKindFilter == "All";
    public bool IsSearchKindFolder => SearchCenterKindFilter == nameof(FileKind.Folder);
    public bool IsSearchKindImage => SearchCenterKindFilter == nameof(FileKind.Image);
    public bool IsSearchKindVideo => SearchCenterKindFilter == nameof(FileKind.Video);
    public bool IsSearchKindAudio => SearchCenterKindFilter == nameof(FileKind.Audio);
    public bool IsSearchKindDocument => SearchCenterKindFilter == nameof(FileKind.Document);
    public bool IsSearchKindArchive => SearchCenterKindFilter == nameof(FileKind.Archive);
    public bool IsSearchKindCode => SearchCenterKindFilter == nameof(FileKind.Code);

    // 大小 / 修改时间筛选（v0.54）
    public bool IsSearchSizeAll => SearchSizeFilter == SearchFilterPresets.Any;
    public bool IsSearchSizeSmall => SearchSizeFilter == SearchFilterPresets.SizeSmall;
    public bool IsSearchSizeMedium => SearchSizeFilter == SearchFilterPresets.SizeMedium;
    public bool IsSearchSizeLarge => SearchSizeFilter == SearchFilterPresets.SizeLarge;
    public bool IsSearchSizeHuge => SearchSizeFilter == SearchFilterPresets.SizeHuge;
    public bool IsSearchDateAll => SearchDateFilter == SearchFilterPresets.Any;
    public bool IsSearchDateToday => SearchDateFilter == SearchFilterPresets.DateToday;
    public bool IsSearchDateWeek => SearchDateFilter == SearchFilterPresets.DateWeek;
    public bool IsSearchDateMonth => SearchDateFilter == SearchFilterPresets.DateMonth;
    public bool IsSearchDateYear => SearchDateFilter == SearchFilterPresets.DateYear;
    public bool HasActiveSearchFilter => BuildSearchFilter() is not null;
    /// <summary>筛选生效时的说明（状态栏与提示共用一句话）。</summary>
    public string ActiveSearchFilterText
    {
        get
        {
            var parts = new List<string>(2);
            if (SearchSizeFilter != SearchFilterPresets.Any)
                parts.Add(SearchSizeFilter switch
                {
                    SearchFilterPresets.SizeSmall => "小于 100 KB",
                    SearchFilterPresets.SizeMedium => "100 KB – 1 MB",
                    SearchFilterPresets.SizeLarge => "1 – 10 MB",
                    _ => "大于 10 MB",
                });
            if (SearchDateFilter != SearchFilterPresets.Any)
                parts.Add(SearchDateFilter switch
                {
                    SearchFilterPresets.DateToday => "今天修改",
                    SearchFilterPresets.DateWeek => "最近 7 天",
                    SearchFilterPresets.DateMonth => "最近 30 天",
                    _ => "最近一年",
                });
            return string.Join(" · ", parts);
        }
    }

    partial void OnUseSearchIndexChanged(bool value)
    {
        _services.SettingsStore.Settings.UseSearchIndex = value;
        OnPropertyChanged(nameof(SearchIndexStateText));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
        _ = SaveAsync();
        _ = RefreshSemanticSearchIndexAsync();
    }

    partial void OnIsSemanticSearchModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsKeywordSearchMode));
        OnPropertyChanged(nameof(SearchCenterSearchPlaceholderText));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        ApplySearchCenterModeStatus();
    }

    partial void OnIsContentSearchModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsKeywordSearchMode));
        OnPropertyChanged(nameof(SearchCenterSearchPlaceholderText));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        ApplySearchCenterModeStatus();
        if (value)
            _ = RefreshContentIndexAsync();
    }

    partial void OnEnableContentIndexChanged(bool value)
    {
        _services.SettingsStore.Settings.EnableContentIndex = value;
        if (!value && _services.ContentIndex is IContentIndexMaintenance maintenance)
            maintenance.StopWatching();
        OnPropertyChanged(nameof(CanRebuildContentIndex));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        ContentIndexStatusText = value ? "内容索引已启用；建立后才会占用磁盘" : "内容索引已关闭";
        _ = SaveAsync();
        if (value)
            _ = RefreshContentIndexAsync();
    }

    private void ApplySearchCenterModeStatus() => SearchCenterStatusText = IsContentSearchMode
        ? "全文搜索：在已建索引的本地文本里按词查找，不联网。"
        : IsSemanticSearchMode
            ? "AI 语义搜索：先建立文件名索引与 AI 向量索引，再输入自然语言描述。"
            : "输入文件名关键词开始搜索";

    /// <summary>内容搜索的引导文案：缺什么就说什么，别让用户对着空列表猜。</summary>
    /// <summary>
    /// 关键词模式的提示（v0.50）：按相关度排序，并把正文命中接在后面——
    /// 用户不必先想「这个词在文件名里还是在正文里」再决定切哪个模式。
    /// </summary>
    private string KeywordSearchHintText()
    {
        if (!EnableContentIndex)
            return "按名称匹配已加入索引的文件与文件夹；排序按相关度（同名 > 前缀 > 包含）。";
        return HasContentIndex
            ? "按相关度匹配文件名（同名 > 前缀 > 包含），并把已建索引的正文命中也一并列出、标为「正文」。"
            : "按相关度匹配文件名（同名 > 前缀 > 包含）；启用并建立内容索引后，正文命中也在这里出现。";
    }

    private string ContentSearchHintText() =>
        !EnableContentIndex ? "请先在设置页启用全文内容索引。" :
        !HasSearchIndexRoots ? "请先在设置页加入索引根。" :
        // v0.57：正文来源补上「文本型 PDF」；扫描件要 OCR，本版不做，文案不吹。
        !HasContentIndex ? "内容索引尚未建立，建立后可跨文件搜正文（含文本型 PDF 与 Office 文档；扫描件需 OCR，本版不做）。" :
        ContentIndexStatusText;

    partial void OnSearchSizeFilterChanged(string value) => NotifySearchFilterChanged();

    partial void OnSearchDateFilterChanged(string value) => NotifySearchFilterChanged();

    private void NotifySearchFilterChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsSearchSizeAll), nameof(IsSearchSizeSmall), nameof(IsSearchSizeMedium),
                     nameof(IsSearchSizeLarge), nameof(IsSearchSizeHuge),
                     nameof(IsSearchDateAll), nameof(IsSearchDateToday), nameof(IsSearchDateWeek),
                     nameof(IsSearchDateMonth), nameof(IsSearchDateYear),
                     nameof(HasActiveSearchFilter), nameof(ActiveSearchFilterText),
                 })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void SetSearchSizeFilter(string size) =>
        SearchSizeFilter = SearchFilterPresets.Normalize(
            size,
            [SearchFilterPresets.SizeSmall, SearchFilterPresets.SizeMedium,
             SearchFilterPresets.SizeLarge, SearchFilterPresets.SizeHuge]);

    [RelayCommand]
    private void SetSearchDateFilter(string date) =>
        SearchDateFilter = SearchFilterPresets.Normalize(
            date,
            [SearchFilterPresets.DateToday, SearchFilterPresets.DateWeek,
             SearchFilterPresets.DateMonth, SearchFilterPresets.DateYear]);

    /// <summary>两档筛选取并集；都为「全部」时返回 null（调用方完全不筛）。</summary>
    private SearchFilter? BuildSearchFilter() =>
        SearchFilterPresets.Build(SearchSizeFilter, SearchDateFilter, DateTimeOffset.Now);

    partial void OnSearchCenterKindFilterChanged(string value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsSearchKindAll), nameof(IsSearchKindFolder), nameof(IsSearchKindImage),
                     nameof(IsSearchKindVideo), nameof(IsSearchKindAudio), nameof(IsSearchKindDocument),
                     nameof(IsSearchKindArchive), nameof(IsSearchKindCode),
                 })
            OnPropertyChanged(name);
    }

    partial void OnIsSearchCenterBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSearchCenterEmpty));
        OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
        OnPropertyChanged(nameof(CanRebuildContentIndex));
    }

    public async Task RefreshSearchIndexAsync()
    {
        RefreshSearchIndexRoots();
        try
        {
            var stats = await _services.SearchIndex.GetStatsAsync(CancellationToken.None).ConfigureAwait(true);
            SearchIndexEntryCount = stats.EntryCount;
            SearchIndexBytes = stats.IndexBytes;
            SearchIndexIsCurrent = stats.IsCurrent;
            SearchIndexIsPartial = stats.IsPartial;
            SearchIndexUpdatedText = stats.UpdatedAt is { } updated
                ? Formatting.FullDateTime(updated)
                : "尚未建立";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SearchIndexUpdatedText = "不可读取";
        }

        OnPropertyChanged(nameof(SearchIndexEntryCountText));
        OnPropertyChanged(nameof(SearchIndexSizeText));
        OnPropertyChanged(nameof(SearchIndexStateText));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
    }

    private async Task RefreshSemanticSearchIndexAsync()
    {
        try
        {
            var stats = await _services.SemanticSearch.GetStatsAsync(CancellationToken.None).ConfigureAwait(true);
            SemanticSearchIndexedItemCount = stats.IndexedItemCount;
            SemanticSearchIndexBytes = stats.IndexBytes;
            SemanticSearchIndexIsCurrent = stats.IsCurrent;
            SemanticSearchIndexIsPartial = stats.IsPartial;
            SemanticSearchIndexUpdatedText = stats.UpdatedAt is { } updated
                ? Formatting.FullDateTime(updated)
                : "尚未建立";

            if (!EnableLocalAi)
                SemanticSearchIndexStatusText = "请先在设置中启用本地 AI";
            else if (stats.ModelNames.Count == 0)
                SemanticSearchIndexStatusText = "未检测到 BGE / MiniLM 文本模型，请先下载轻量包";
            else if (!UseSearchIndex || !HasSearchIndexRoots)
                SemanticSearchIndexStatusText = "先建立文件名索引并加入本地文件夹";
            else if (!SearchIndexIsCurrent)
                SemanticSearchIndexStatusText = "文件名索引已过期，请先重建";
            else if (stats.IsCurrent)
                SemanticSearchIndexStatusText = $"已就绪 · {stats.IndexedItemCount:N0} 项 · {string.Join(" / ", stats.ModelNames)}";
            else if (stats.IndexedItemCount > 0)
                SemanticSearchIndexStatusText = "AI 向量索引已过期，请重建";
            else
                SemanticSearchIndexStatusText = "尚未建立 AI 语义索引";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SemanticSearchIndexStatusText = "AI 语义索引不可读取";
            SemanticSearchIndexIsCurrent = false;
        }
        OnPropertyChanged(nameof(SemanticSearchIndexStatusText));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
        OnPropertyChanged(nameof(SemanticSearchIndexUpdatedText));
        OnPropertyChanged(nameof(SemanticSearchIndexCountText));
        OnPropertyChanged(nameof(SemanticSearchIndexSizeText));
        OnPropertyChanged(nameof(SemanticSearchIndexSummaryText));
        OnPropertyChanged(nameof(HasSemanticSearchIndex));
        OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
    }

    private void RefreshSearchIndexRoots()
    {
        SearchIndexRoots.Clear();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in _services.SettingsStore.Settings.SearchIndexRootPaths ?? [])
        {
            if (TryNormalizeSearchIndexRoot(path) is not { } normalized || !seen.Add(normalized))
                continue;
            var trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            SearchIndexRoots.Add(new SearchIndexRootModel(
                string.IsNullOrWhiteSpace(name) ? normalized : name, normalized));
        }
    }

    [RelayCommand]
    private void SetSearchCenterKind(string kind) => SearchCenterKindFilter = kind;

    [RelayCommand]
    private void SetSearchCenterMode(string mode)
    {
        // 三个模式互斥；先落两个布尔再统一刷状态文案，避免回调互相覆盖。
        IsSemanticSearchMode = string.Equals(mode, "Semantic", StringComparison.OrdinalIgnoreCase);
        IsContentSearchMode = string.Equals(mode, "Content", StringComparison.OrdinalIgnoreCase);
        ApplySearchCenterModeStatus();
    }

    [RelayCommand]
    private async Task RunSearchCenterSearchAsync()
    {
        _searchCenterCts?.Cancel();
        _searchCenterCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCenterCts = cts;

        SearchCenterResults.Clear();
        var modeQuery = SearchCenterQuery.Trim();
        var modeKinds = SearchCenterKinds();
        if (IsContentSearchMode)
        {
            await RunContentSearchAsync(modeQuery, modeKinds, cts).ConfigureAwait(true);
            await RecordRecentSearchAsync(modeQuery).ConfigureAwait(true);
            return;
        }

        await RefreshSearchIndexAsync().ConfigureAwait(true);
        if (!UseSearchIndex)
        {
            SearchCenterStatusText = "文件名索引已暂停";
            return;
        }
        if (!HasSearchIndexRoots)
        {
            SearchCenterStatusText = "先加入一个索引根";
            return;
        }
        if (!SearchIndexIsCurrent)
        {
            SearchCenterStatusText = "索引需要重建";
            return;
        }

        var query = SearchCenterQuery.Trim();
        var kinds = SearchCenterKinds();
        if (query.Length == 0 && (IsSemanticSearchMode || kinds is null))
        {
            SearchCenterStatusText = IsSemanticSearchMode ? "输入自然语言描述开始 AI 搜索" : "输入关键词或选择类型";
            return;
        }

        if (IsSemanticSearchMode)
        {
            await RunSemanticSearchAsync(query, kinds, cts).ConfigureAwait(true);
            await RecordRecentSearchAsync(query).ConfigureAwait(true);
            return;
        }

        IsSearchCenterBusy = true;
        SearchCenterStatusText = "正在搜索本地索引…";
        try
        {
            var spec = new SearchSpec(
                query, string.Empty, kinds, ShowHidden, MaxResults: SearchResultLimit, Filter: BuildSearchFilter());
            var nameHits = new List<FileItem>(64);
            await foreach (var item in _services.SearchIndex.SearchAsync(spec, cts.Token).ConfigureAwait(true))
                nameHits.Add(item);

            // v0.50：关键词不再按字母序返回。先按相关性排（同名 > 主名相同 > 前缀 > 词首 > 包含），
            // 再把正文命中接在后面；两条通道按路径去重，文件名命中优先保留（它也能带上正文摘要）。
            var ranked = SearchRanking.Rank(nameHits, query);
            var contentHits = await TrySearchContentForKeywordAsync(query, cts.Token).ConfigureAwait(true);
            var merged = MergeKeywordResults(ranked, contentHits, kinds, SearchResultLimit);
            foreach (var result in merged)
                SearchCenterResults.Add(result);

            var nameCount = merged.Count(result => string.Equals(result.SearchSourceText, NameHitLabel, StringComparison.Ordinal));
            var contentCount = merged.Count - nameCount;
            SearchCenterStatusText = merged.Count == 0
                ? "没有匹配结果"
                : contentCount > 0
                    ? $"{merged.Count} 项 · 文件名 {nameCount} · 正文 {contentCount}{(SearchIndexIsPartial ? " · 部分" : string.Empty)}"
                    : $"{merged.Count} 项 · 本地索引{(SearchIndexIsPartial ? " · 部分" : string.Empty)}"
                      + (HasActiveSearchFilter ? $" · 筛选：{ActiveSearchFilterText}" : string.Empty);
            await RecordRecentSearchAsync(query).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SearchCenterStatusText = "搜索已取消";
        }
        finally
        {
            if (ReferenceEquals(_searchCenterCts, cts))
                IsSearchCenterBusy = false;
        }
    }

    private const string NameHitLabel = "文件名";
    private const string ContentHitLabel = "正文";

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// 关键词模式的正文通道（v0.50 融合）：只有内容索引已启用且已建立时才参与，查询至少 2 个字符
    /// （一个字的全文搜索几乎全是噪声）。读不出来就当没有——不能让正文索引把文件名结果一起拖垮。
    /// </summary>
    private async Task<IReadOnlyList<ContentSearchHit>> TrySearchContentForKeywordAsync(
        string query, CancellationToken cancellationToken)
    {
        if (!EnableContentIndex || query.Length < 2 || !HasContentIndex)
            return [];

        try
        {
            return await _services.ContentIndex
                .SearchAsync(query, SearchResultLimit, cancellationToken, BuildSearchFilter()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// 把「文件名命中」与「正文命中」合成一份结果：文件名在前（已按相关性排好），正文按索引给的
    /// bm25 顺序接在后面，同一路径只留一条。文件名命中也一并带上正文摘要，用户能看到命中上下文。
    /// </summary>
    private List<FileItemViewModel> MergeKeywordResults(
        IReadOnlyList<FileItem> nameHits, IReadOnlyList<ContentSearchHit> contentHits,
        IReadOnlySet<FileKind>? kinds, int limit)
    {
        var results = new List<FileItemViewModel>(Math.Min(limit, nameHits.Count + contentHits.Count));
        var seen = new HashSet<string>(PathComparer);
        var snippets = new Dictionary<string, ContentSearchHit>(PathComparer);
        foreach (var hit in contentHits)
            snippets[hit.Path] = hit;

        foreach (var item in nameHits)
        {
            if (results.Count >= limit)
                return results;
            if (!seen.Add(item.FullPath))
                continue;
            snippets.TryGetValue(item.FullPath, out var snippet);
            results.Add(new FileItemViewModel(item)
            {
                IsFavorite = _services.SettingsStore.IsFavorite(item.FullPath),
                SearchSourceText = NameHitLabel,
                SearchSnippetText = snippet?.Snippet,
                SearchSnippetHighlightStart = snippet?.MatchOffset ?? -1,
                SearchSnippetHighlightLength = snippet?.MatchLength ?? 0,
            });
        }

        foreach (var hit in contentHits)
        {
            if (results.Count >= limit)
                return results;
            if (!seen.Add(hit.Path))
                continue;
            var extension = Path.GetExtension(hit.Name);
            var item = new FileItem(
                FileLocator.FromPath(hit.Path),
                hit.Name,
                FileKindClassifier.Classify(hit.Name),
                hit.Length,
                hit.LastWriteTime,
                false,
                false,
                extension.Length == 0 ? null : extension);
            if (kinds is not null && !kinds.Contains(item.Kind))
                continue;
            results.Add(new FileItemViewModel(item)
            {
                IsFavorite = _services.SettingsStore.IsFavorite(hit.Path),
                SearchSourceText = ContentHitLabel,
                SearchSnippetText = hit.Snippet,
                SearchSnippetHighlightStart = hit.MatchOffset,
                SearchSnippetHighlightLength = hit.MatchLength,
            });
        }

        return results;
    }

    // ---------- 最近搜索词（v0.53） ----------

    /// <summary>
    /// 重建最近搜索的小胶囊。每枚自带宽命令，XAML 里就不必做父级 DataContext 查找
    /// （那种写法一旦模板类型写错就是一屏绑定错误）。
    /// </summary>
    private void RefreshRecentSearches(IReadOnlyList<string>? queries)
    {
        var normalized = RecentSearches.Normalize(queries);
        _services.SettingsStore.Settings.RecentSearchQueries = normalized;
        RecentSearchChips.Clear();
        foreach (var query in normalized)
            RecentSearchChips.Add(new RecentSearchChipModel(query, new AsyncRelayCommand(() => RunRecentSearchAsync(query))));
        OnPropertyChanged(nameof(HasRecentSearches));
    }

    /// <summary>只记「真有结果的搜索」：一无所获的词留在最近列表里只会误导下一次点击。</summary>
    private async Task RecordRecentSearchAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || SearchCenterResults.Count == 0)
            return;

        var current = _services.SettingsStore.Settings.RecentSearchQueries;
        var updated = RecentSearches.Push(current, query);
        if (updated.Count == current.Count && updated.SequenceEqual(current, StringComparer.Ordinal))
            return; // 与现状一致：不写设置文件
        RefreshRecentSearches(updated);
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task RunRecentSearchAsync(string query)
    {
        SearchCenterQuery = query;
        await RunSearchCenterSearchAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearRecentSearchesAsync()
    {
        if (!HasRecentSearches)
            return;
        RefreshRecentSearches([]);
        await SaveAsync().ConfigureAwait(true);
        Notify("已清除最近搜索");
    }

    private async Task RunSemanticSearchAsync(string query, IReadOnlySet<FileKind>? kinds, CancellationTokenSource cts)
    {
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        if (!EnableLocalAi)
        {
            SearchCenterStatusText = "AI 搜索已关闭；请先在设置启用本地 AI";
            return;
        }
        if (!HasLocalAiTextModel)
        {
            SearchCenterStatusText = "未检测到文本嵌入模型；请先下载 BGE small zh 或 MiniLM";
            return;
        }
        if (!SemanticSearchIndexIsCurrent)
        {
            SearchCenterStatusText = SemanticSearchIndexStatusText;
            return;
        }

        IsSearchCenterBusy = true;
        SearchCenterStatusText = "正在本机计算语义相似度…";
        try
        {
            var hits = await _services.SemanticSearch.SearchAsync(
                    query, kinds, SearchResultLimit, cts.Token)
                .ConfigureAwait(true);
            // v0.54：语义结果自带文件事实，大小 / 修改时间筛选在这里就地过一遍；
            // 置信度按「筛完之后的结果」重算，否则「低置信」的阈值会对不上你实际看到的那几条。
            var filter = BuildSearchFilter();
            var visible = filter is null ? hits.ToList() : hits.Where(hit => filter.Matches(hit.Item.Length, hit.Item.LastWriteTime)).ToList();
            // v0.45 可解释性：分数照旧展示，另外标出「陪跑项」与「整次查询没有强匹配」。
            var confidence = SemanticSearchConfidence.Evaluate([.. visible.Select(hit => hit.Score)]);
            foreach (var hit in visible)
            {
                SearchCenterResults.Add(new FileItemViewModel(hit.Item)
                {
                    IsFavorite = _services.SettingsStore.IsFavorite(hit.Item.FullPath),
                    SearchScoreText = $"语义 {hit.Score:P0}",
                    SearchConfidenceText = confidence.IsLowConfidence(hit.Score) ? "低置信" : null,
                });
            }

            var semanticFilterNote = HasActiveSearchFilter ? $" · 筛选：{ActiveSearchFilterText}" : string.Empty;
            SearchCenterStatusText = visible.Count == 0
                ? (filter is null
                    ? "没有足够相似的结果；可换个描述，或扩展并重建索引"
                    : $"没有符合筛选条件的语义结果{semanticFilterNote}")
                : confidence.HasStrongMatch
                    ? $"{visible.Count} 项 · 本机语义检索 · {visible[0].ModelName}{(confidence.HasLowConfidence ? $" · {confidence.LowConfidenceCount} 项低置信" : string.Empty)}{(SemanticSearchIndexIsPartial ? " · 索引有上限" : string.Empty)}{semanticFilterNote}"
                    : $"{visible.Count} 项 · 最高相似度只有 {confidence.TopScore:P0}，约等于没找到；已按相似度排序供参考，换个说法通常更准{semanticFilterNote}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SearchCenterStatusText = "AI 搜索已取消";
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            SearchCenterStatusText = $"AI 搜索暂不可用：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_searchCenterCts, cts))
                IsSearchCenterBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebuildSemanticSearchIndexAsync()
    {
        if (!CanBuildSemanticSearchIndex)
        {
            await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
            Notify(SemanticSearchIndexStatusText);
            return;
        }

        var outcome = await RunOperationAsync("正在建立本地 AI 语义索引…", async (progress, ct) =>
        {
            var stats = await _services.SemanticSearch.RebuildAsync(progress, ct).ConfigureAwait(false);
            return new OperationOutcome(stats.IndexedItemCount, [], []);
        }).ConfigureAwait(true);

        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        if (outcome.Success)
            SearchCenterStatusText = $"AI 语义索引已更新 · {SemanticSearchIndexedItemCount:N0} 项";
    }

    [RelayCommand]
    private async Task ClearSemanticSearchIndexAsync()
    {
        if (!await ConfirmAsync(
                "清除 AI 语义索引？",
                "只删除本机保存的 AI 向量索引，不会删除用户文件或文件名索引。",
                "清除 AI 索引",
                destructive: true).ConfigureAwait(true))
            return;

        _searchCenterCts?.Cancel();
        await _services.SemanticSearch.ClearAsync(CancellationToken.None).ConfigureAwait(true);
        SearchCenterResults.Clear();
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        SearchCenterStatusText = "AI 语义索引已清除";
        Notify("已清除 AI 语义索引");
    }

    private IReadOnlySet<FileKind>? SearchCenterKinds() => SearchCenterKindFilter switch
    {
        nameof(FileKind.Folder) => new HashSet<FileKind> { FileKind.Folder },
        nameof(FileKind.Image) => new HashSet<FileKind> { FileKind.Image },
        nameof(FileKind.Video) => new HashSet<FileKind> { FileKind.Video },
        nameof(FileKind.Audio) => new HashSet<FileKind> { FileKind.Audio },
        nameof(FileKind.Document) => new HashSet<FileKind> { FileKind.Document },
        nameof(FileKind.Archive) => new HashSet<FileKind> { FileKind.Archive },
        nameof(FileKind.Code) => new HashSet<FileKind> { FileKind.Code },
        _ => null,
    };

    public async Task OpenSearchCenterResultAsync(FileItemViewModel item)
    {
        if (item.IsFolder)
        {
            await OpenFolderAsync(item.FullPath).ConfigureAwait(true);
            return;
        }

        Page = ShellPage.Browser;
        IsDrawerOpen = false;
        IsPreviewPageOpen = false;
        await Browser.NavigateToFileLocationAsync(item.FullPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddCurrentFolderToSearchIndexAsync()
    {
        var path = Browser.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || FileLocator.IsStorageUri(path) || !Directory.Exists(path))
        {
            Notify("当前文件夹不能加入本地索引");
            return;
        }

        await AddSearchIndexRootAsync(path).ConfigureAwait(true);
    }

    /// <summary>加入本地索引根；已被父根覆盖时不重复加入，加入父根时收拢其子根。</summary>
    public async Task<bool> AddSearchIndexRootAsync(string path)
    {
        if (TryNormalizeSearchIndexRoot(path) is not { } normalized || !Directory.Exists(normalized))
        {
            Notify("只能加入现有的本地文件夹");
            return false;
        }

        var roots = new List<string>();
        foreach (var existingPath in _services.SettingsStore.Settings.SearchIndexRootPaths ?? [])
        {
            if (TryNormalizeSearchIndexRoot(existingPath) is { } existing &&
                !roots.Contains(existing, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                roots.Add(existing);
        }

        if (roots.Any(existing => IsSameOrUnderSearchRoot(normalized, existing)))
        {
            Notify("该文件夹已在索引范围内");
            return false;
        }

        roots.RemoveAll(existing => IsSameOrUnderSearchRoot(existing, normalized));
        roots.Add(normalized);
        _services.SettingsStore.Settings.SearchIndexRootPaths = roots;
        await SaveAsync().ConfigureAwait(true);
        await RefreshSearchIndexAsync().ConfigureAwait(true);
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        SearchCenterStatusText = "索引根已更新，重建后生效";
        Notify("已加入文件名索引");
        return true;
    }

    [RelayCommand]
    private async Task RemoveSearchIndexRootAsync(SearchIndexRootModel? root)
    {
        if (root is null)
            return;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _services.SettingsStore.Settings.SearchIndexRootPaths.RemoveAll(path =>
            TryNormalizeSearchIndexRoot(path) is { } normalized &&
            string.Equals(normalized, root.Path, comparison));
        await SaveAsync().ConfigureAwait(true);
        SearchCenterResults.Clear();
        await RefreshSearchIndexAsync().ConfigureAwait(true);
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        SearchCenterStatusText = "索引根已更新，重建后生效";
        Notify($"已移除索引根：{root.Name}");
    }

    [RelayCommand]
    private async Task RebuildSearchIndexAsync()
    {
        if (!UseSearchIndex)
        {
            Notify("文件名索引已暂停");
            return;
        }
        if (!HasSearchIndexRoots)
        {
            Notify("先加入一个索引根");
            return;
        }

        var outcome = await RunOperationAsync("正在重建文件名索引…", async (progress, ct) =>
        {
            var stats = await _services.SearchIndex.RebuildAsync(progress, ct).ConfigureAwait(true);
            return new OperationOutcome(stats.EntryCount, [], []);
        }).ConfigureAwait(true);

        await RefreshSearchIndexAsync().ConfigureAwait(true);
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        if (outcome.Success)
            SearchCenterStatusText = $"索引已更新 · {SearchIndexEntryCountText} 项";
    }

    [RelayCommand]
    private async Task ClearSearchIndexAsync()
    {
        if (!await ConfirmAsync(
                "清除本地搜索索引？",
                "只删除本机应用数据目录中的文件名索引与 AI 向量索引，不会删除任何用户文件。索引根配置会保留。",
                "清除索引",
                destructive: true).ConfigureAwait(true))
            return;

        _searchCenterCts?.Cancel();
        await _services.SearchIndex.ClearAsync(CancellationToken.None).ConfigureAwait(true);
        await _services.SemanticSearch.ClearAsync(CancellationToken.None).ConfigureAwait(true);
        SearchCenterResults.Clear();
        await RefreshSearchIndexAsync().ConfigureAwait(true);
        await RefreshSemanticSearchIndexAsync().ConfigureAwait(true);
        SearchCenterStatusText = "本地搜索索引已清除";
        Notify("已清除文件名与 AI 语义索引");
    }

    // ---------- 全文内容索引（FTS5） ----------

    /// <summary>内容搜索：查询已建索引的正文，命中按 bm25 相关度排序。</summary>
    private async Task RunContentSearchAsync(
        string query, IReadOnlySet<FileKind>? kinds, CancellationTokenSource cts)
    {
        if (!EnableContentIndex)
        {
            SearchCenterStatusText = "内容索引已关闭；请先在设置启用";
            return;
        }
        if (query.Length == 0)
        {
            SearchCenterStatusText = "输入要在正文里查找的词";
            return;
        }

        await RefreshContentIndexAsync().ConfigureAwait(true);
        if (!HasContentIndex)
        {
            SearchCenterStatusText = "内容索引尚未建立；到设置页点「建立内容索引」";
            return;
        }

        IsSearchCenterBusy = true;
        SearchCenterStatusText = "正在已建索引的正文里查找…";
        try
        {
            var hits = await _services.ContentIndex
                .SearchAsync(query, SearchResultLimit, cts.Token, BuildSearchFilter()).ConfigureAwait(true);
            foreach (var hit in hits)
            {
                var extension = Path.GetExtension(hit.Name);
                var item = new FileItem(
                    FileLocator.FromPath(hit.Path),
                    hit.Name,
                    FileKindClassifier.Classify(hit.Name),
                    hit.Length,
                    hit.LastWriteTime,
                    false,
                    false,
                    extension.Length == 0 ? null : extension);
                if (kinds is not null && !kinds.Contains(item.Kind))
                    continue;
                SearchCenterResults.Add(new FileItemViewModel(item)
                {
                    IsFavorite = _services.SettingsStore.IsFavorite(hit.Path),
                    SearchSnippetText = hit.Snippet,
                    SearchSnippetHighlightStart = hit.MatchOffset,
                    SearchSnippetHighlightLength = hit.MatchLength,
                });
            }

            var filterNote = HasActiveSearchFilter ? $" · 筛选：{ActiveSearchFilterText}" : string.Empty;
            SearchCenterStatusText = SearchCenterResults.Count == 0
                ? $"正文里没有匹配；索引收白名单文本（≤ 512 KB）与 Office/PDF 正文{filterNote}"
                : $"{SearchCenterResults.Count} 项 · 全文命中 · {ContentIndexCountText} 篇已索引{filterNote}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SearchCenterStatusText = "搜索已取消";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SearchCenterStatusText = "内容索引读取失败";
        }
        finally
        {
            if (ReferenceEquals(_searchCenterCts, cts))
                IsSearchCenterBusy = false;
        }
    }

    private void OnContentIndexIncrementallyUpdated(object? sender, int writes)
    {
        if (writes <= 0)
            return;
        // 回调来自监听线程：有 Avalonia 应用时切回 UI 线程再动观察属性。
        if (Avalonia.Application.Current is not null &&
            !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshContentIndexAsync());
            return;
        }

        _ = RefreshContentIndexAsync();
    }

    private void OnContentIndexWatchFailed(object? sender, EventArgs e)
    {
        // 同样来自监听线程：切回 UI 线程再改观察属性。
        if (Avalonia.Application.Current is not null &&
            !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ContentIndexStatusText = "文件变动太频繁，内容索引已停止自动跟随；请重建一次";
                OnPropertyChanged(nameof(SearchCenterModeHintText));
            });
            return;
        }

        ContentIndexStatusText = "文件变动太频繁，内容索引已停止自动跟随；请重建一次";
        OnPropertyChanged(nameof(SearchCenterModeHintText));
    }

    /// <summary>
    /// 内容索引的目录监听只在「开着 + 已建立 + 有索引根」时才值得跑；
    /// 其余情况继续监听只会白读盘，直接停掉。
    /// </summary>
    private void SyncContentIndexWatcher(ContentIndexStats stats)
    {
        if (_services.ContentIndex is not IContentIndexMaintenance maintenance)
            return;
        if (EnableContentIndex && stats.IsBuilt && SearchIndexRoots.Count > 0)
        {
            maintenance.StartWatching(SearchIndexRoots.Select(root => root.Path).ToArray());
            return;
        }

        maintenance.StopWatching();
    }

    [RelayCommand]
    private async Task RefreshContentIndexAsync()
    {
        try
        {
            var stats = await _services.ContentIndex.GetStatsAsync(CancellationToken.None).ConfigureAwait(true);
            ContentIndexDocumentCount = stats.DocumentCount;
            ContentIndexBytes = stats.IndexBytes;
            ContentIndexUpdatedText = stats.UpdatedAt is { } updatedAt
                ? Formatting.DateTime(updatedAt, DateTimeOffset.Now)
                : "尚未建立";
            ContentIndexStatusText = !EnableContentIndex
                ? "内容索引已关闭"
                : stats.IsBuilt
                    ? $"内容索引已就绪 · {stats.DocumentCount:N0} 篇"
                    : "尚未建立内容索引";
            // v0.56：建立过索引就跟着目录变化走，不再等用户手动重建。
            SyncContentIndexWatcher(stats);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContentIndexStatusText = "无法读取内容索引状态";
        }

        OnPropertyChanged(nameof(HasContentIndex));
        OnPropertyChanged(nameof(ContentIndexCountText));
        OnPropertyChanged(nameof(ContentIndexSizeText));
        OnPropertyChanged(nameof(ContentIndexSummaryText));
        OnPropertyChanged(nameof(CanRebuildContentIndex));
        OnPropertyChanged(nameof(SearchCenterModeHintText));
    }

    [RelayCommand]
    private async Task RebuildContentIndexAsync()
    {
        if (!EnableContentIndex)
        {
            Notify("内容索引已关闭；请先在设置启用");
            return;
        }
        if (!HasSearchIndexRoots)
        {
            Notify("先加入一个索引根");
            return;
        }

        // 内容索引与文件名索引共用索引根：用户只维护一处“要搜哪些文件夹”。
        var roots = SearchIndexRoots.Select(root => root.Path).ToArray();
        var outcome = await RunOperationAsync("正在建立内容索引…", async (progress, ct) =>
        {
            var stats = await _services.ContentIndex
                .RebuildAsync(roots, progress, ct).ConfigureAwait(true);
            return new OperationOutcome(stats.DocumentCount, [], []);
        }).ConfigureAwait(true);

        await RefreshContentIndexAsync().ConfigureAwait(true);
        if (outcome.Cancelled)
            ContentIndexStatusText = "内容索引建立已取消；已完成的条目保留";
        else if (outcome.Success)
            ContentIndexStatusText = $"内容索引已更新 · {ContentIndexCountText} 篇";
        else
            ContentIndexStatusText = "内容索引建立失败；原有索引未被清除";
        OnPropertyChanged(nameof(SearchCenterModeHintText));
    }

    [RelayCommand]
    private async Task ClearContentIndexAsync()
    {
        if (!await ConfirmAsync(
                "清除全文内容索引？",
                "只删除本机应用数据目录中的正文索引（content-index.db），不会删除任何用户文件，也不影响文件名索引与索引根配置。",
                "清除内容索引",
                destructive: true).ConfigureAwait(true))
            return;

        _searchCenterCts?.Cancel();
        await _services.ContentIndex.ClearAsync(CancellationToken.None).ConfigureAwait(true);
        SearchCenterResults.Clear();
        await RefreshContentIndexAsync().ConfigureAwait(true);
        ContentIndexStatusText = "内容索引已清除";
        Notify("已清除全文内容索引");
        OnPropertyChanged(nameof(SearchCenterModeHintText));
    }

    private static string? TryNormalizeSearchIndexRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || FileLocator.IsStorageUri(path))
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            while (full.Length > root.Length &&
                   (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
                full = full[..^1];
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnderSearchRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, comparison) || path.StartsWith(prefix, comparison);
    }

    // ---------- 设置 ----------
    [ObservableProperty] private string _themeMode = "System";
    [ObservableProperty] private string _seedColor = "#006A6A";
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _watchFolderChanges = true;
    [ObservableProperty] private bool _foldersFirst = true;
    [ObservableProperty] private bool _showPreviewPane = true;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private bool _rememberFolderView;
    [ObservableProperty] private bool _notifyOnTaskCompletion;
    [ObservableProperty] private bool _playVideoInline;
    [ObservableProperty] private bool _mediaAutoPlay;
    [ObservableProperty] private bool _showMediaDiagnostics = true;

    // ---------- 缩略图档位与磁盘缓存 ----------

    /// <summary>
    /// 三档只影响解码目标宽度；换档即换缓存键，旧档缩略图会被清掉重解。
    /// 属性名刻意叫 Size 而不是 ThumbnailTier：避免与枚举类型同名带来的解析歧义（Color Color 规则）。
    /// </summary>
    [ObservableProperty] private ThumbnailTier _thumbnailSize;

    public bool IsThumbnailSizeSmall => ThumbnailSize == ThumbnailTier.Small;
    public bool IsThumbnailSizeMedium => ThumbnailSize == ThumbnailTier.Medium;
    public bool IsThumbnailSizeLarge => ThumbnailSize == ThumbnailTier.Large;

    /// <summary>缓存占用的一句话摘要（条目数 + 体积）；统计失败时如实说不知道。</summary>
    public string ThumbnailCacheSummary => _thumbnailCacheSummary;
    private string _thumbnailCacheSummary = "正在统计缩略图缓存…";

    [RelayCommand]
    private void SetThumbnailTier(string? value) => ThumbnailSize = ThumbnailTierNames.Parse(value);

    [RelayCommand]
    private async Task ClearThumbnailCacheAsync()
    {
        try
        {
            var freed = await _services.Thumbnails.ClearAsync(CancellationToken.None).ConfigureAwait(true);
            _thumbnailCacheSummary = $"已清空（释放 {Formatting.Bytes(freed)}）；缩略图会按需重新生成。";
            // 缓存没了但界面里还挂着旧位图：清掉让它们按当前档位重新走一遍。
            Browser.ApplyThumbnailTierPreference();
            SecondaryBrowser.ApplyThumbnailTierPreference();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _thumbnailCacheSummary = $"清空失败：{ex.Message}";
        }
        OnPropertyChanged(nameof(ThumbnailCacheSummary));
    }

    private async Task RefreshThumbnailCacheStatsAsync()
    {
        try
        {
            var stats = await _services.Thumbnails.GetStatsAsync(CancellationToken.None).ConfigureAwait(true);
            _thumbnailCacheSummary = stats.Entries == 0 && stats.Failures == 0
                ? "缓存为空；打开含图片的文件夹后会在这里累积。"
                : $"{stats.Entries} 张 · {Formatting.Bytes(stats.Bytes)}"
                  + (stats.Failures > 0 ? $" · {stats.Failures} 个失败标记" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _thumbnailCacheSummary = $"统计失败：{ex.Message}";
        }
        OnPropertyChanged(nameof(ThumbnailCacheSummary));
    }

    partial void OnThumbnailSizeChanged(ThumbnailTier value)
    {
        _services.SettingsStore.Settings.ThumbnailTier = ThumbnailTierNames.ToSettingValue(value);
        _ = SaveAsync();
        OnPropertyChanged(nameof(IsThumbnailSizeSmall));
        OnPropertyChanged(nameof(IsThumbnailSizeMedium));
        OnPropertyChanged(nameof(IsThumbnailSizeLarge));
        if (!_enableThumbnails)
            return; // 缩略图关着就别为档位变化做解码了
        Browser.ApplyThumbnailTierPreference();
        SecondaryBrowser.ApplyThumbnailTierPreference();
        _ = RefreshThumbnailCacheStatsAsync();
    }
    /// <summary>端侧 AI 总开关（默认关）；关闭后语义索引保留在本机，但不进行推理或检索。</summary>
    [ObservableProperty] private bool _enableLocalAi;
    [ObservableProperty] private bool _isSendingTestSystemNotification;
    [ObservableProperty] private int _itemPageSize = BrowserViewModel.DefaultItemPageSize;
    [ObservableProperty] private bool _enablePullRefresh = true;
    [ObservableProperty] private double _autoLoadMoreDistance = 320;
    [ObservableProperty] private int _searchResultLimit = 2000;
    [ObservableProperty] private long _appTrashQuotaBytes = 1_000_000_000;
    [ObservableProperty] private int _appTrashRetentionDays = 30;
    [ObservableProperty] private bool _autoLoadMore = true;
    [ObservableProperty] private bool _enableThumbnails = true;
    [ObservableProperty] private string _density = "Comfortable";
    [ObservableProperty] private bool _confirmBeforeTrash = true;
    [ObservableProperty] private bool _restoreTabsOnLaunch = true;
    [ObservableProperty] private int _recentItemLimit = 12;
    [ObservableProperty] private double _pullRefreshTriggerDistance = 72;
    [ObservableProperty] private string _systemNotificationStatusText = "尚未发送测试通知。";

    public string ThemeModeText => ThemeMode switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随系统" };
    public bool IsCompactDensity => string.Equals(Density, "Compact", StringComparison.Ordinal);
    public string DensityText => IsCompactDensity ? "紧凑" : "舒适";
    public string ItemPageSizeText => $"{ItemPageSize} 项/页";
    public string RecentLimitText => $"{RecentItemLimit} 条";
    public string PullTriggerText => $"{PullRefreshTriggerDistance:0} DIP";
    public string AutoLoadMoreDistanceText => $"{AutoLoadMoreDistanceClamped:0} DIP";
    public string SearchResultLimitText => $"{SearchResultLimit} 条";
    public bool IsDensityComfortable => !IsCompactDensity;
    public bool IsDensityCompact => IsCompactDensity;
    public bool IsPageSize60 => ItemPageSize == 60;
    public bool IsPageSize120 => ItemPageSize == 120;
    public bool IsPageSize240 => ItemPageSize == 240;
    public bool IsRecentLimit12 => RecentItemLimit == 12;
    public bool IsRecentLimit24 => RecentItemLimit == 24;
    public bool IsRecentLimit48 => RecentItemLimit == 48;
    public bool IsPullTrigger72 => Math.Abs(PullRefreshTriggerDistance - 72) < 0.1;
    public bool IsPullTrigger96 => Math.Abs(PullRefreshTriggerDistance - 96) < 0.1;
    public bool IsAutoLoadDistance160 => Math.Abs(AutoLoadMoreDistanceClamped - 160) < 0.1;
    public bool IsAutoLoadDistance320 => Math.Abs(AutoLoadMoreDistanceClamped - 320) < 0.1;
    public bool IsAutoLoadDistance480 => Math.Abs(AutoLoadMoreDistanceClamped - 480) < 0.1;
    public bool IsSearchLimit1000 => SearchResultLimit == 1000;
    public bool IsSearchLimit2000 => SearchResultLimit == 2000;
    public bool IsSearchLimit5000 => SearchResultLimit == 5000;

    /// <summary>滚动续页的统一门槛：关闭自动加载后只剩页脚按钮与快捷键。</summary>
    public bool ShouldAutoLoadMore(double remainingFromBottom) =>
        AutoLoadMore && remainingFromBottom <= AutoLoadMoreDistanceClamped;
    /// <summary>只有 Android 走应用内回收站；桌面用系统回收站，配额与保留期由系统管理。</summary>
    public bool UsesAppTrash => Capabilities.UsesAppTrash;
    public string AppTrashQuotaText => Formatting.Bytes(AppTrashQuotaClamped);
    public string AppTrashRetentionText => $"{Math.Clamp(AppTrashRetentionDays, 1, 365)} 天";
    public long AppTrashQuotaClamped => Math.Clamp(AppTrashQuotaBytes, 200_000_000, 10_000_000_000);
    public bool IsAppTrashQuota500Mb => AppTrashQuotaClamped == 500_000_000;
    public bool IsAppTrashQuota1Gb => AppTrashQuotaClamped == 1_000_000_000;
    public bool IsAppTrashQuota2Gb => AppTrashQuotaClamped == 2_000_000_000;
    public bool IsAppTrashRetention7 => Math.Clamp(AppTrashRetentionDays, 1, 365) == 7;
    public bool IsAppTrashRetention30 => Math.Clamp(AppTrashRetentionDays, 1, 365) == 30;
    public bool IsAppTrashRetention90 => Math.Clamp(AppTrashRetentionDays, 1, 365) == 90;
    public double AutoLoadMoreDistanceClamped => Math.Clamp(
        double.IsFinite(AutoLoadMoreDistance) ? AutoLoadMoreDistance : 320, 120, 600);
    public string SystemNotificationPlatformText => _services.SystemNotifications.PlatformName;
    public string SystemNotificationSupportText => _services.SystemNotifications.SupportDescription;

    public event EventHandler? ThemeSettingsChanged;

    partial void OnThemeModeChanged(string value)
    {
        OnPropertyChanged(nameof(ThemeModeText));
        _services.SettingsStore.Settings.ThemeMode = value;
        PersistAndApplyTheme();
    }

    partial void OnSeedColorChanged(string value)
    {
        _services.SettingsStore.Settings.SeedColor = value;
        PersistAndApplyTheme();
    }

    partial void OnShowHiddenChanged(bool value)
    {
        _services.SettingsStore.Settings.ShowHidden = value;
        _ = SaveAsync();
        _ = Browser.RefreshCommand.ExecuteAsync(null);
        if (!string.IsNullOrWhiteSpace(SecondaryBrowser.CurrentPath))
            _ = SecondaryBrowser.RefreshCommand.ExecuteAsync(null);
    }

    partial void OnFoldersFirstChanged(bool value)
    {
        _services.SettingsStore.Settings.FoldersFirst = value;
        _ = SaveAsync();
        Browser.RefilterAndSort();
        SecondaryBrowser.RefilterAndSort();
    }

    partial void OnShowPreviewPaneChanged(bool value)
    {
        _services.SettingsStore.Settings.ShowPreviewPane = value;
        _ = SaveAsync();
        RefreshPreviewVisibility();
        // Expanded+ 的预览栏是常驻列：关掉它等于关掉预览，重负载必须显式释放，
        // 否则第三方直接 PDF 控件会把整本页面树留在内存里。
        if (!value)
        {
            Preview.ReleaseHeavyContent();
            return;
        }

        ReloadReleasedPreview();
    }

    partial void OnReduceMotionChanged(bool value)
    {
        _services.SettingsStore.Settings.ReduceMotion = value;
        _ = SaveAsync();
    }

    partial void OnRememberFolderViewChanged(bool value)
    {
        _services.SettingsStore.Settings.RememberFolderView = value;
        Browser.OnFolderViewMemorySettingChanged(value);
    }

    [RelayCommand]
    private async Task ClearFolderViewPreferencesAsync()
    {
        await Browser.ClearFolderViewPreferencesAsync().ConfigureAwait(true);
        Notify("已清除文件夹视图记忆");
    }

    partial void OnWatchFolderChangesChanged(bool value)
    {
        _services.SettingsStore.Settings.WatchFolderChanges = value;
        _ = SaveAsync();
        Browser.SetWatchEnabled(value);
        SecondaryBrowser.SetWatchEnabled(value);
    }

    partial void OnNotifyOnTaskCompletionChanged(bool value)
    {
        _services.SettingsStore.Settings.NotifyOnTaskCompletion = value;
        _ = SaveAsync();
    }

    partial void OnPlayVideoInlineChanged(bool value)
    {
        _services.SettingsStore.Settings.PlayVideoInline = value;
        _ = SaveAsync();
        Preview.RefreshSettings();
    }

    partial void OnMediaAutoPlayChanged(bool value)
    {
        _services.SettingsStore.Settings.MediaAutoPlay = value;
        _ = SaveAsync();
        Preview.RefreshSettings();
    }

    partial void OnShowMediaDiagnosticsChanged(bool value)
    {
        _services.SettingsStore.Settings.ShowMediaDiagnostics = value;
        _ = SaveAsync();
        Preview.RefreshSettings();
    }

    // ---------- 端侧 AI（本地模型分发 + 语义搜索） ----------

    /// <summary>清单里每个模型在本机的落地情况；设置页直接绑它。</summary>
    public ObservableCollection<AiModelStatusModel> LocalAiModels { get; } = [];

    public string LocalAiModelsDirectoryText => _services.AiModels.ModelsDirectory;

    /// <summary>不同平台用不同脚本——别让 Windows 用户去跑 bash。</summary>
    public string LocalAiDownloadCommandText => OperatingSystem.IsWindows()
        ? "pwsh scripts/fetch-ai-models.ps1"
        : "scripts/fetch-ai-models.sh";

    public string LocalAiOptionalModelsText =>
        "可选：Whisper tiny 语音转写约 57 MB；YAMNet 声音分类约 16 MB；SmolVLM 图片问答约 194 MB。\n" +
        (OperatingSystem.IsWindows()
            ? "用 -Only audio-asr-tiny / audio-classify-yamnet / image-vlm-smol 单独下载；-All 约 316 MB。"
            : "用 --only audio-asr-tiny / audio-classify-yamnet / image-vlm-smol 单独下载；--all 约 316 MB。");

    [ObservableProperty] private string _localAiStatusText = string.Empty;

    /// <summary>装了至少一个模型才允许尝试端侧 AI 推理；推理仍由语义搜索显式触发。</summary>
    public bool HasLocalAiModel => LocalAiModels.Any(model => model.Installed);

    /// <summary>重新扫一遍模型目录（只查文件，不加载权重）。</summary>
    private void RefreshLocalAiState()
    {
        LocalAiModels.Clear();
        var statuses = _services.AiModels.Scan();
        foreach (var status in statuses)
        {
            // 每个模型自带一个下载命令：模板里直接绑 Command，不需要跨模板找父级 DataContext。
            var definition = status.Definition;
            LocalAiModels.Add(new AiModelStatusModel(
                definition.Id,
                definition.Name,
                definition.Purpose,
                definition.License,
                definition.ApproxSizeMb,
                status.Installed,
                status.Partial,
                status.MissingFiles.Count,
                status.InstalledBytes,
                new AsyncRelayCommand(() => RunAiModelDownloadAsync(
                    $"正在下载 {definition.Name}…", [definition.Id], verifyOnly: false))));
        }

        var ready = statuses.Count(status => status.Installed);
        var bytes = statuses.Sum(status => status.InstalledBytes);
        LocalAiStatusText = ready == 0
            ? "尚未下载任何模型，端侧 AI 现在不可用（不会假装可用）"
            : $"{ready}/{statuses.Count} 个模型就绪 · 已占用 {Formatting.Bytes(bytes)}";
        OnPropertyChanged(nameof(LocalAiStatusText));
        OnPropertyChanged(nameof(LocalAiModelsDirectoryText));
        OnPropertyChanged(nameof(LocalAiDownloadCommandText));
        OnPropertyChanged(nameof(LocalAiOptionalModelsText));
        OnPropertyChanged(nameof(HasLocalAiModel));
        OnPropertyChanged(nameof(HasLocalAiTextModel));
        OnPropertyChanged(nameof(AiModelSource));
        OnPropertyChanged(nameof(AiModelSourceHintText));
        OnPropertyChanged(nameof(IsAiSourceAuto));
        OnPropertyChanged(nameof(IsAiSourceHuggingFace));
        OnPropertyChanged(nameof(IsAiSourceHfMirror));
        OnPropertyChanged(nameof(IsAiSourceModelScope));
        OnPropertyChanged(nameof(LocalAiDefaultPackageText));
        OnPropertyChanged(nameof(CanBuildSemanticSearchIndex));
        _ = RefreshSemanticSearchIndexAsync();
    }

    [RelayCommand]
    private void RefreshLocalAi()
    {
        RefreshLocalAiState();
        Notify(LocalAiStatusText);
    }

    [RelayCommand]
    private async Task CopyLocalAiDownloadCommandAsync()
    {
        try
        {
            await _services.SystemClipboard
                .SetTextAsync(LocalAiDownloadCommandText, CancellationToken.None)
                .ConfigureAwait(true);
            Notify("下载命令已复制到剪贴板");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify("复制失败：可在终端里手动输入这条命令");
        }
    }

    // ---------- 内置模型下载器（v0.43：不再要求用户去跑脚本） ----------

    /// <summary>当前下载源名（字符串形态，存进设置）；非法值一律收回 Auto。</summary>
    [ObservableProperty] private string _aiModelSourceName = "Auto";
    [ObservableProperty] private string _localAiDownloadStatusText = string.Empty;
    [ObservableProperty] private bool _isLocalAiDownloadBusy;

    public AiModelSource AiModelSource => AiModelSourceNames.Parse(AiModelSourceName);
    public bool IsAiSourceAuto => AiModelSource == AiModelSource.Auto;
    public bool IsAiSourceHuggingFace => AiModelSource == AiModelSource.HuggingFace;
    public bool IsAiSourceHfMirror => AiModelSource == AiModelSource.HfMirror;
    public bool IsAiSourceModelScope => AiModelSource == AiModelSource.ModelScope;
    public string AiModelSourceHintText => AiModelSourceNames.Describe(AiModelSource);
    public bool CanDownloadAiModels => !IsLocalAiDownloadBusy;

    /// <summary>默认轻量包的体积口径，直接由清单算，别手写一个会过期的数字。</summary>
    public string LocalAiDefaultPackageText
    {
        get
        {
            var defaults = _services.AiModels.Models.Where(model => model.DownloadByDefault).ToArray();
            var megabytes = defaults.Sum(model => model.ApproxSizeMb);
            return defaults.Length == 0
                ? "清单里没有默认模型"
                : $"默认轻量包：{string.Join(" + ", defaults.Select(model => model.Name))} 共约 {megabytes} MB";
        }
    }

    partial void OnAiModelSourceNameChanged(string value)
    {
        // 归一化后再落盘：手改设置文件写错也只会退回 Auto，不会变成「哪个源都不去」。
        var normalized = AiModelSourceNames.ToName(AiModelSourceNames.Parse(value));
        _services.SettingsStore.Settings.AiModelSource = normalized;
        _ = SaveAsync();
        OnPropertyChanged(nameof(AiModelSource));
        OnPropertyChanged(nameof(AiModelSourceHintText));
        OnPropertyChanged(nameof(IsAiSourceAuto));
        OnPropertyChanged(nameof(IsAiSourceHuggingFace));
        OnPropertyChanged(nameof(IsAiSourceHfMirror));
        OnPropertyChanged(nameof(IsAiSourceModelScope));
    }

    partial void OnIsLocalAiDownloadBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanDownloadAiModels));

    [RelayCommand]
    private void SetAiModelSource(string source) => AiModelSourceName = source;

    [RelayCommand]
    private Task DownloadDefaultAiModelsAsync() => RunAiModelDownloadAsync(
        "正在下载默认文本模型…",
        _services.AiModels.Models.Where(model => model.DownloadByDefault).Select(model => model.Id).ToArray(),
        verifyOnly: false);

    [RelayCommand]
    private Task DownloadAiModelAsync(string modelId) => RunAiModelDownloadAsync(
        "正在下载模型…", [modelId], verifyOnly: false);

    [RelayCommand]
    private Task DownloadAllAiModelsAsync() => RunAiModelDownloadAsync(
        "正在下载全部模型…",
        _services.AiModels.Models.Select(model => model.Id).ToArray(),
        verifyOnly: false);

    [RelayCommand]
    private Task VerifyAiModelsAsync() => RunAiModelDownloadAsync(
        "正在校验模型文件…",
        _services.AiModels.Models.Select(model => model.Id).ToArray(),
        verifyOnly: true);

    /// <summary>
    /// 下载/校验都走任务中心：串行执行、可取消、退出前可看到失败明细。
    /// 取消时保留 <c>.part</c>，下次接着续；不会留下半截文件被推理读到。
    /// </summary>
    private async Task RunAiModelDownloadAsync(string title, IReadOnlyList<string> modelIds, bool verifyOnly)
    {
        if (modelIds.Count == 0)
        {
            Notify("清单里没有可下载的模型");
            return;
        }
        if (IsLocalAiDownloadBusy)
        {
            Notify("已有模型下载在进行，等它结束");
            return;
        }

        var source = AiModelSource;
        AiModelDownloadOutcome? last = null;
        IsLocalAiDownloadBusy = true;
        LocalAiDownloadStatusText = verifyOnly
            ? "正在校验已下载文件…"
            : $"正在从 {AiModelSourceNames.ToName(source)} 下载…";
        try
        {
            await RunOperationAsync(title, async (progress, ct) =>
            {
                last = verifyOnly
                    ? await _services.AiModelDownloads.VerifyAsync(modelIds, progress, ct).ConfigureAwait(true)
                    : await _services.AiModelDownloads.DownloadAsync(modelIds, source, progress, ct)
                        .ConfigureAwait(true);
                return new OperationOutcome(
                    last.SucceededCount,
                    last.Errors,
                    [],
                    last.Cancelled,
                    last.FilesFailed);
            }).ConfigureAwait(true);
        }
        finally
        {
            IsLocalAiDownloadBusy = false;
            RefreshLocalAiState();
        }

        if (last is null)
        {
            LocalAiDownloadStatusText = "下载没有产生结果";
            return;
        }

        var summary = verifyOnly
            ? $"校验：一致 {last.FilesSkipped}、失败 {last.FilesFailed}"
            : $"下载完成 {last.FilesDownloaded} 个文件、已存在 {last.FilesSkipped} 个、失败 {last.FilesFailed} 个";
        var notes = last.Notes.Count == 0 ? string.Empty : " · " + string.Join("；", last.Notes.Take(3));
        LocalAiDownloadStatusText = summary + notes;
    }

    [RelayCommand]
    private async Task OpenLocalAiFolderAsync()
    {
        var directory = LocalAiModelsDirectoryText;
        try
        {
            Directory.CreateDirectory(directory);
            if (!await Launcher.RevealAsync(directory, CancellationToken.None).ConfigureAwait(true))
                Notify("系统没有可以打开这个目录的文件管理器");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"无法打开模型目录：{ex.Message}");
        }
    }

    partial void OnEnableLocalAiChanged(bool value)
    {
        _services.SettingsStore.Settings.EnableLocalAi = value;
        _ = SaveAsync();
        RefreshLocalAiState();
        _ = RefreshSemanticSearchIndexAsync();
    }

    partial void OnItemPageSizeChanged(int value)
    {
        _services.SettingsStore.Settings.ItemPageSize = value;
        _ = SaveAsync();
        NotifyItemPageSizeState();
        Browser.ApplyPageSizePreference();
        SecondaryBrowser.ApplyPageSizePreference();
    }

    private void NotifyItemPageSizeState()
    {
        OnPropertyChanged(nameof(ItemPageSizeText));
        OnPropertyChanged(nameof(IsPageSize60));
        OnPropertyChanged(nameof(IsPageSize120));
        OnPropertyChanged(nameof(IsPageSize240));
    }

    partial void OnAutoLoadMoreChanged(bool value)
    {
        _services.SettingsStore.Settings.AutoLoadMore = value;
        _ = SaveAsync();
    }

    partial void OnEnableThumbnailsChanged(bool value)
    {
        _services.SettingsStore.Settings.EnableThumbnails = value;
        _ = SaveAsync();
        Browser.ApplyThumbnailPreference();
        SecondaryBrowser.ApplyThumbnailPreference();
    }

    partial void OnDensityChanged(string value)
    {
        _services.SettingsStore.Settings.Density = value;
        _ = SaveAsync();
        OnPropertyChanged(nameof(IsCompactDensity));
        OnPropertyChanged(nameof(IsDensityComfortable));
        OnPropertyChanged(nameof(IsDensityCompact));
        OnPropertyChanged(nameof(DensityText));
    }

    partial void OnConfirmBeforeTrashChanged(bool value)
    {
        _services.SettingsStore.Settings.ConfirmBeforeTrash = value;
        _ = SaveAsync();
    }

    partial void OnRestoreTabsOnLaunchChanged(bool value)
    {
        _services.SettingsStore.Settings.RestoreTabsOnLaunch = value;
        _ = SaveAsync();
    }

    partial void OnRecentItemLimitChanged(int value)
    {
        var limit = Math.Clamp(value, 6, 60);
        var settings = _services.SettingsStore.Settings;
        settings.RecentItemLimit = limit;
        if (settings.RecentFolderPaths.Count > limit)
            settings.RecentFolderPaths.RemoveRange(limit, settings.RecentFolderPaths.Count - limit);
        if (settings.RecentFilePaths.Count > limit)
            settings.RecentFilePaths.RemoveRange(limit, settings.RecentFilePaths.Count - limit);
        _ = SaveAsync();
        OnPropertyChanged(nameof(RecentLimitText));
        OnPropertyChanged(nameof(IsRecentLimit12));
        OnPropertyChanged(nameof(IsRecentLimit24));
        OnPropertyChanged(nameof(IsRecentLimit48));
        _ = RefreshRecentsAsync();
    }

    partial void OnPullRefreshTriggerDistanceChanged(double value)
    {
        var distance = Math.Clamp(double.IsFinite(value) ? value : 72, 48, 120);
        _services.SettingsStore.Settings.PullRefreshTriggerDistance = distance;
        _ = SaveAsync();
        OnPropertyChanged(nameof(PullTriggerText));
        OnPropertyChanged(nameof(IsPullTrigger72));
        OnPropertyChanged(nameof(IsPullTrigger96));
    }

    partial void OnEnablePullRefreshChanged(bool value)
    {
        _services.SettingsStore.Settings.EnablePullRefresh = value;
        _ = SaveAsync();
    }

    partial void OnAutoLoadMoreDistanceChanged(double value)
    {
        var distance = Math.Clamp(double.IsFinite(value) ? value : 320, 120, 600);
        _services.SettingsStore.Settings.AutoLoadMoreDistance = distance;
        _ = SaveAsync();
        OnPropertyChanged(nameof(AutoLoadMoreDistanceText));
        OnPropertyChanged(nameof(AutoLoadMoreDistanceClamped));
        OnPropertyChanged(nameof(IsAutoLoadDistance160));
        OnPropertyChanged(nameof(IsAutoLoadDistance320));
        OnPropertyChanged(nameof(IsAutoLoadDistance480));
    }

    partial void OnAppTrashQuotaBytesChanged(long value)
    {
        var quota = Math.Clamp(value, 200_000_000, 10_000_000_000);
        _services.SettingsStore.Settings.AppTrashQuotaBytes = quota;
        _ = SaveAsync();
        if (_services.Trash is AppTrashService appTrash)
            appTrash.QuotaBytes = quota;
        OnPropertyChanged(nameof(AppTrashQuotaText));
        OnPropertyChanged(nameof(AppTrashQuotaClamped));
        OnPropertyChanged(nameof(IsAppTrashQuota500Mb));
        OnPropertyChanged(nameof(IsAppTrashQuota1Gb));
        OnPropertyChanged(nameof(IsAppTrashQuota2Gb));
        _ = RefreshTrashStateAsync();
    }

    partial void OnAppTrashRetentionDaysChanged(int value)
    {
        var days = Math.Clamp(value, 1, 365);
        _services.SettingsStore.Settings.AppTrashRetentionDays = days;
        _ = SaveAsync();
        if (_services.Trash is AppTrashService appTrash)
            appTrash.RetentionDays = days;
        OnPropertyChanged(nameof(AppTrashRetentionText));
        OnPropertyChanged(nameof(IsAppTrashRetention7));
        OnPropertyChanged(nameof(IsAppTrashRetention30));
        OnPropertyChanged(nameof(IsAppTrashRetention90));
        _ = RefreshTrashStateAsync();
    }

    [RelayCommand]
    private void SetAppTrashQuota(string? value)
    {
        // 分段按 MB 传参，避免在 XAML 里写长整数
        if (long.TryParse(value, out var megabytes))
            AppTrashQuotaBytes = Math.Clamp(megabytes * 1_000_000, 200_000_000, 10_000_000_000);
    }

    [RelayCommand]
    private void SetAppTrashRetentionDays(string? value)
    {
        if (int.TryParse(value, out var days))
            AppTrashRetentionDays = Math.Clamp(days, 1, 365);
    }

    partial void OnSearchResultLimitChanged(int value)
    {
        var limit = Math.Clamp(value, 200, 20000);
        _services.SettingsStore.Settings.SearchResultLimit = limit;
        _ = SaveAsync();
        OnPropertyChanged(nameof(SearchResultLimitText));
        OnPropertyChanged(nameof(IsSearchLimit1000));
        OnPropertyChanged(nameof(IsSearchLimit2000));
        OnPropertyChanged(nameof(IsSearchLimit5000));
    }

    [RelayCommand]
    private void SetAutoLoadMoreDistance(string? value)
    {
        if (double.TryParse(value, out var distance))
            AutoLoadMoreDistance = Math.Clamp(distance, 120, 600);
    }

    [RelayCommand]
    private void SetSearchResultLimit(string? value)
    {
        if (int.TryParse(value, out var limit))
            SearchResultLimit = Math.Clamp(limit, 200, 20000);
    }

    [RelayCommand]
    private void SetItemPageSize(string? value)
    {
        if (int.TryParse(value, out var size))
            ItemPageSize = Math.Clamp(size, 60, 480);
    }

    [RelayCommand]
    private void SetDensity(string? value) =>
        Density = string.Equals(value, "Compact", StringComparison.Ordinal) ? "Compact" : "Comfortable";

    [RelayCommand]
    private void SetRecentItemLimit(string? value)
    {
        if (int.TryParse(value, out var limit))
            RecentItemLimit = Math.Clamp(limit, 6, 60);
    }

    [RelayCommand]
    private void SetPullRefreshTriggerDistance(string? value)
    {
        if (double.TryParse(value, out var distance))
            PullRefreshTriggerDistance = Math.Clamp(distance, 48, 120);
    }

    [RelayCommand]
    private async Task SendTestSystemNotificationAsync()
    {
        if (IsSendingTestSystemNotification)
            return;

        IsSendingTestSystemNotification = true;
        SystemNotificationStatusText = "正在向操作系统发送测试通知…";
        try
        {
            var result = await _services.SystemNotifications.ShowAsync(
                "Eidoria 测试通知",
                "系统通知已接入。长任务和自动化流程可以把结果送到这里。",
                CancellationToken.None).ConfigureAwait(true);
            SystemNotificationStatusText = result.Success
                ? "测试通知已提交；请在系统通知中心确认。"
                : $"测试通知未发送：{result.Message}";
            Notify(result.Success ? "测试系统通知已发送" : "系统通知发送失败，详情见设置");
        }
        catch (OperationCanceledException)
        {
            SystemNotificationStatusText = "测试通知已取消。";
        }
        finally
        {
            IsSendingTestSystemNotification = false;
        }
    }

    private void MaybeNotifyTaskCompleted(OperationTaskViewModel task, OperationOutcome outcome, bool suppress)
    {
        if (suppress || !NotifyOnTaskCompletion || outcome.Cancelled)
            return;

        var duration = (task.FinishedAt ?? DateTimeOffset.Now) - task.StartedAt;
        if (outcome.Errors.Count == 0 && duration < TimeSpan.FromSeconds(3))
            return;

        var title = outcome.Errors.Count > 0 ? "Eidoria 任务未完全完成" : "Eidoria 任务已完成";
        var summary = !string.IsNullOrWhiteSpace(task.ResultSummaryText)
            ? task.ResultSummaryText
            : task.Detail;
        var body = string.IsNullOrWhiteSpace(summary) ? task.Title : $"{task.Title} · {summary}";
        _ = SendTaskSystemNotificationAsync(title, body);
    }

    private async Task SendTaskSystemNotificationAsync(string title, string body)
    {
        try
        {
            var result = await _services.SystemNotifications.ShowAsync(title, body, CancellationToken.None)
                .ConfigureAwait(true);
            SystemNotificationStatusText = result.Success
                ? "最近一次任务通知已提交到操作系统。"
                : $"最近一次任务通知未发送：{result.Message}";
        }
        catch (OperationCanceledException)
        {
            // 通知是旁路能力，取消不影响已经结束的文件任务。
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SystemNotificationStatusText = $"最近一次任务通知未发送：{ex.Message}";
        }
    }

    [RelayCommand]
    private void SetThemeMode(string mode) => ThemeMode = mode;

    [RelayCommand]
    private void SetSeedColor(string seed) => SeedColor = seed;

    private void PersistAndApplyTheme()
    {
        _ = SaveAsync();
        ThemeSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync()
    {
        try { await _services.SettingsStore.SaveSettingsAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (IOException) { }
    }

    // ---------- IDialogs ----------
    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private string _dialogTitle = string.Empty;
    [ObservableProperty] private string _dialogMessage = string.Empty;
    [ObservableProperty] private string _dialogInput = string.Empty;
    [ObservableProperty] private string? _dialogInputError;
    [ObservableProperty] private string _dialogConfirmLabel = "确定";
    [ObservableProperty] private bool _dialogDestructive;
    [ObservableProperty] private double _operationPercent;
    [ObservableProperty] private string _operationDetail = string.Empty;
    [ObservableProperty] private TransferConflictPrompt? _currentConflict;
    [ObservableProperty] private string _conflictPolicyName = nameof(ConflictPolicy.KeepBoth);
    [ObservableProperty] private bool _conflictApplyToRemaining;
    [ObservableProperty] private int _pendingConflictCount;

    private DialogKind _dialogKind = DialogKind.None;
    private OperationTaskViewModel? _currentOperationTask;
    private TaskCompletionSource<object?>? _dialogTcs;
    private Func<string, string?>? _dialogValidate;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool IsPromptDialog => IsDialogOpen && _dialogKind == DialogKind.Prompt;
    public bool IsProgressDialog => IsDialogOpen && _dialogKind == DialogKind.Progress;
    public bool IsMessageDialog => IsDialogOpen && _dialogKind is DialogKind.Confirm or DialogKind.Message;
    public bool IsConflictDialog => IsDialogOpen && _dialogKind == DialogKind.Conflict;
    public bool IsBatchRenameDialog => IsDialogOpen && _dialogKind == DialogKind.BatchRename;
    public bool ShowDialogCancel => _dialogKind is DialogKind.Prompt or DialogKind.Confirm or DialogKind.Conflict or DialogKind.BatchRename;
    public bool ShowDialogConfirm => _dialogKind is DialogKind.Prompt or DialogKind.Confirm or DialogKind.Message or DialogKind.Conflict or DialogKind.BatchRename;
    public double DialogMaxWidth => IsConflictDialog ? 720 : 560;
    public bool HasPendingConflicts => PendingConflictCount > 0;
    public ConflictPolicy SelectedConflictPolicy =>
        Enum.TryParse<ConflictPolicy>(ConflictPolicyName, out var policy) ? policy : ConflictPolicy.KeepBoth;
    public bool IsConflictKeepBoth => SelectedConflictPolicy == ConflictPolicy.KeepBoth;
    public bool IsConflictOverwrite => SelectedConflictPolicy == ConflictPolicy.Overwrite;
    public bool IsConflictSkip => SelectedConflictPolicy == ConflictPolicy.Skip;
    public string ConflictPolicyDescription => SelectedConflictPolicy switch
    {
        ConflictPolicy.Overwrite => "替换将先删除目标已有项目，再写入来源项目。",
        ConflictPolicy.Skip => "跳过此来源项目；移动时来源仍保留在原处。",
        _ => "保留目标项目，来源项目将以新名称写入。",
    };

    partial void OnConflictPolicyNameChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedConflictPolicy));
        OnPropertyChanged(nameof(IsConflictKeepBoth));
        OnPropertyChanged(nameof(IsConflictOverwrite));
        OnPropertyChanged(nameof(IsConflictSkip));
        OnPropertyChanged(nameof(ConflictPolicyDescription));
    }

    partial void OnPendingConflictCountChanged(int value) => OnPropertyChanged(nameof(HasPendingConflicts));

    private void NotifyDialogKinds()
    {
        OnPropertyChanged(nameof(IsPromptDialog));
        OnPropertyChanged(nameof(IsProgressDialog));
        OnPropertyChanged(nameof(IsMessageDialog));
        OnPropertyChanged(nameof(IsConflictDialog));
        OnPropertyChanged(nameof(IsBatchRenameDialog));
        OnPropertyChanged(nameof(ShowDialogCancel));
        OnPropertyChanged(nameof(ShowDialogConfirm));
        OnPropertyChanged(nameof(DialogMaxWidth));
    }

    public async Task<string?> PromptAsync(string title, string? initialText, string confirmLabel, Func<string, string?>? validate = null)
    {
        _dialogKind = DialogKind.Prompt;
        _dialogValidate = validate;
        DialogTitle = title;
        DialogInput = initialText ?? string.Empty;
        DialogInputError = null;
        DialogConfirmLabel = confirmLabel;
        DialogDestructive = false;
        var result = await ShowDialogAsync().ConfigureAwait(true);
        return result as string;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
    {
        _dialogKind = DialogKind.Confirm;
        _dialogValidate = null;
        DialogTitle = title;
        DialogMessage = message;
        DialogConfirmLabel = confirmLabel;
        DialogDestructive = destructive;
        var result = await ShowDialogAsync().ConfigureAwait(true);
        return result is true;
    }

    public async Task ShowPropertiesAsync(string title, string body)
    {
        _dialogKind = DialogKind.Message;
        _dialogValidate = null;
        DialogTitle = title;
        DialogMessage = body;
        DialogConfirmLabel = "关闭";
        DialogDestructive = false;
        await ShowDialogAsync().ConfigureAwait(true);
    }

    public async Task<TransferConflictDecision?> AskConflictAsync(TransferConflictPrompt prompt)
    {
        _dialogKind = DialogKind.Conflict;
        _dialogValidate = null;
        CurrentConflict = prompt;
        ConflictPolicyName = nameof(ConflictPolicy.KeepBoth);
        ConflictApplyToRemaining = false;
        PendingConflictCount = prompt.RemainingIncludingCurrent;
        DialogTitle = "目标已有同名项目";
        DialogMessage = prompt.IsMove
            ? "移动前先比对来源与目标，再决定如何处理此项目。"
            : "复制前先比对来源与目标，再决定如何处理此项目。";
        DialogConfirmLabel = "继续";
        DialogDestructive = false;

        var waitingTask = _currentOperationTask;
        waitingTask?.MarkWaitingConfirmation(
            $"等待确认：{prompt.Source.Name}（{prompt.Index + 1}/{Math.Max(1, prompt.TotalCount)}）");
        RefreshTaskCenterSummary();
        try
        {
            var result = await ShowDialogAsync().ConfigureAwait(true);
            return result as TransferConflictDecision;
        }
        finally
        {
            PendingConflictCount = 0;
            waitingTask?.ResumeAfterConfirmation();
            CurrentConflict = null;
            RefreshTaskCenterSummary();
        }
    }

    // ---------- 批量重命名对话框 ----------
    [ObservableProperty] private string _batchPattern = "{name}_{n}";
    [ObservableProperty] private string _batchFind = string.Empty;
    [ObservableProperty] private string _batchReplace = string.Empty;
    [ObservableProperty] private string _batchStartText = "1";
    [ObservableProperty] private string _batchStepText = "1";
    [ObservableProperty] private string _batchCaseMode = "Keep";
    [ObservableProperty] private bool _batchHasErrors;

    public ObservableCollection<BatchRenameRowModel> BatchRows { get; } = [];
    private IReadOnlyList<string> _batchNames = [];
    private IReadOnlyList<string> _batchSiblings = [];

    public async Task<IReadOnlyList<string>?> BatchRenameAsync(IReadOnlyList<string> names, IReadOnlyList<string> siblingNames)
    {
        _dialogKind = DialogKind.BatchRename;
        _dialogValidate = null;
        _batchNames = names;
        _batchSiblings = siblingNames;
        DialogTitle = $"批量重命名（{names.Count} 项）";
        DialogConfirmLabel = "重命名";
        DialogDestructive = false;
        BatchPattern = "{name}_{n}";
        BatchFind = string.Empty;
        BatchReplace = string.Empty;
        BatchStartText = "1";
        BatchStepText = "1";
        BatchCaseMode = "Keep";
        RecomputeBatchRename();
        var result = await ShowDialogAsync().ConfigureAwait(true);
        return result as IReadOnlyList<string>;
    }

    [RelayCommand]
    private void SetBatchCaseMode(string mode) => BatchCaseMode = mode;

    partial void OnBatchPatternChanged(string value) => RecomputeBatchRename();
    partial void OnBatchFindChanged(string value) => RecomputeBatchRename();
    partial void OnBatchReplaceChanged(string value) => RecomputeBatchRename();
    partial void OnBatchStartTextChanged(string value) => RecomputeBatchRename();
    partial void OnBatchStepTextChanged(string value) => RecomputeBatchRename();
    partial void OnBatchCaseModeChanged(string value) => RecomputeBatchRename();

    private void RecomputeBatchRename()
    {
        if (_batchNames.Count == 0) return;
        var start = int.TryParse(BatchStartText, out var s) ? s : 1;
        var step = int.TryParse(BatchStepText, out var st) ? st : 1;
        var options = new BatchRenameOptions(BatchPattern, BatchFind, BatchReplace, start, step, BatchCaseMode);
        var rows = BatchRenamePlanner.Plan(_batchNames, options, _batchSiblings);
        BatchRows.Clear();
        foreach (var row in rows)
            BatchRows.Add(new BatchRenameRowModel(row.Original, row.Proposed, row.Error));
        BatchHasErrors = rows.Any(r => r.Error is not null);
    }

    private async Task<object?> ShowDialogAsync()
    {
        _dialogTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsDialogOpen = true;
        NotifyDialogKinds();
        var result = await _dialogTcs.Task.ConfigureAwait(true);
        IsDialogOpen = false;
        _dialogKind = DialogKind.None;
        NotifyDialogKinds();
        return result;
    }

    [RelayCommand]
    private void DialogConfirm()
    {
        object? result = _dialogKind switch
        {
            DialogKind.Prompt => ValidatePrompt() ? DialogInput : _sentinel,
            DialogKind.Confirm or DialogKind.Message => true,
            DialogKind.Conflict => new TransferConflictDecision(
                SelectedConflictPolicy,
                CurrentConflict?.HasRemaining == true && ConflictApplyToRemaining),
            DialogKind.BatchRename => BatchHasErrors ? _sentinel : BatchRows.Select(r => r.Proposed).ToArray(),
            _ => null,
        };
        if (ReferenceEquals(result, _sentinel)) return;
        _dialogTcs?.TrySetResult(result);
    }

    partial void OnDialogInputChanged(string value)
    {
        if (_dialogKind == DialogKind.Prompt)
            DialogInputError = _dialogValidate?.Invoke(value);
    }

    private bool ValidatePrompt()
    {
        DialogInputError = _dialogValidate?.Invoke(DialogInput);
        return DialogInputError is null;
    }

    private static readonly object _sentinel = new();

    [RelayCommand]
    private void DialogCancel() =>
        _dialogTcs?.TrySetResult(_dialogKind == DialogKind.Confirm ? false : null);

    [RelayCommand]
    private void SelectConflictPolicy(string policy)
    {
        if (Enum.TryParse<ConflictPolicy>(policy, out var parsed))
            ConflictPolicyName = parsed.ToString();
    }

    // ---------- Snackbar ----------
    [ObservableProperty] private bool _isSnackbarVisible;
    [ObservableProperty] private string _snackbarText = string.Empty;
    private int _snackbarGeneration;

    public void Notify(string message)
    {
        SnackbarText = message;
        IsSnackbarVisible = true;
        var generation = ++_snackbarGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3.2)).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_snackbarGeneration == generation)
                    IsSnackbarVisible = false;
            });
        });
    }

    [RelayCommand]
    private void DismissSnackbar() => IsSnackbarVisible = false;

    // ---------- 任务中心 ----------
    public async Task<OperationOutcome> RunOperationAsync(
        string title,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationOutcome>> operation,
        Func<OperationOutcome, Task>? retryFailed = null,
        bool suppressSystemNotification = false)
    {
        var task = new OperationTaskViewModel(title);
        task.SetRetryHandler(retryFailed);
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OperationTaskViewModel.Status))
                RefreshTaskCenterSummary();
            if (e.PropertyName is nameof(OperationTaskViewModel.ProgressPercent))
                OperationPercent = task.ProgressPercent;
            if (e.PropertyName is nameof(OperationTaskViewModel.Detail))
                OperationDetail = task.Detail;
        };
        OperationTasks.Insert(0, task);
        TrimOperationHistory();
        OperationPercent = 0;
        OperationDetail = task.Detail;

        var acquired = false;
        OperationOutcome outcome;
        try
        {
            await _operationGate.WaitAsync(task.CancellationToken).ConfigureAwait(true);
            acquired = true;
            _currentOperationTask = task;
            task.MarkRunning();
            var progress = new Progress<OperationProgress>(task.Report);
            outcome = await operation(progress, task.CancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            outcome = new OperationOutcome(0, [], [], Cancelled: true);
        }
        catch (Exception ex)
        {
            outcome = new OperationOutcome(0, [new OperationError(title, ex.Message)], []);
        }
        finally
        {
            if (ReferenceEquals(_currentOperationTask, task))
                _currentOperationTask = null;
            if (acquired)
                _operationGate.Release();
        }

        task.Complete(outcome);
        RefreshTaskCenterSummary();
        MaybeNotifyTaskCompleted(task, outcome, suppressSystemNotification);
        if (outcome.Cancelled)
        {
            Notify("已取消");
        }
        else if (outcome.Errors.Count > 0)
        {
            IsTaskCenterOpen = true;
            Notify("部分操作未完成，详情见任务中心");
        }
        else
        {
            Notify("操作完成");
        }

        return outcome;
    }

    private void TrimOperationHistory()
    {
        const int maxTasks = 20;
        while (OperationTasks.Count > maxTasks)
        {
            var oldestFinished = OperationTasks.LastOrDefault(task => task.IsFinished);
            if (oldestFinished is null)
                return;
            OperationTasks.Remove(oldestFinished);
            oldestFinished.Dispose();
        }
    }
}
