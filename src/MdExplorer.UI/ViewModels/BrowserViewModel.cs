using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;

namespace MdExplorer.UI.ViewModels;

/// <summary>一个浏览会话的全部状态：当前位置、条目、选集、搜索、剪贴板与写操作。</summary>
/// <summary>文件区呈现模式（与 AppSettings.ViewMode 字符串一一对应）。</summary>
public enum BrowserViewMode { List, Grid, Details }

/// <summary>可在双窗格两个浏览会话之间共享的应用内/系统文件剪贴板状态。</summary>
internal sealed class BrowserClipboardState
{
    public event EventHandler? Changed;

    public IReadOnlyList<string> Paths { get; private set; } = [];
    public bool IsCut { get; private set; }
    public bool IsOwnedByApp { get; private set; }
    public IReadOnlyList<string> ExternalBaseline { get; private set; } = [];

    public void SetApplicationClipboard(IReadOnlyList<string> paths, bool cut, IReadOnlyList<string> externalBaseline)
    {
        Paths = paths.ToList();
        IsCut = cut;
        IsOwnedByApp = true;
        ExternalBaseline = externalBaseline.ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetExternalClipboard(IReadOnlyList<string> paths)
    {
        Paths = paths.ToList();
        IsCut = false;
        IsOwnedByApp = false;
        ExternalBaseline = paths.ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetExternalBaseline(IReadOnlyList<string> paths) =>
        ExternalBaseline = paths.ToList();

    public void Clear()
    {
        Paths = [];
        IsCut = false;
        IsOwnedByApp = false;
        ExternalBaseline = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class BrowserViewModel : ObservableObject
{
    /// <summary>文件区默认每次物化的行数；目录仍完整枚举和排序，分页只延后创建行 VM 与控件。</summary>
    public const int DefaultItemPageSize = 120;

    /// <summary>当前设置下的每页行数（60–480）；改设置后只影响下一次分页，不改底层枚举。</summary>
    public int PageSize => Math.Clamp(_settingsStore.Settings.ItemPageSize, 60, 480);

    /// <summary>图片缩略图是否仍参与解码；关闭时列表只保留类型图标。</summary>
    public bool ThumbnailDecodingEnabled => _settingsStore.Settings.EnableThumbnails;

    /// <summary>移入回收站前是否需要确认；永久删除不受此开关影响，始终确认。</summary>
    public bool RequiresTrashConfirmation =>
        _trash.CanMoveToTrash && _settingsStore.Settings.ConfirmBeforeTrash;

    private readonly IFileSystemService _fileSystem;
    private readonly IFileOperationService _operations;
    private readonly ITrashService _trash;
    private readonly IArchiveService _archive;
    private readonly ISearchService _search;
    private readonly IFileInspectorService _inspector;
    private readonly IFolderWatchService _folderWatch;
    private readonly ISystemClipboardService _systemClipboard;
    private readonly ISettingsStore _settingsStore;
    private readonly IDialogs _dialogs;
    private readonly PreviewViewModel _preview;
    private readonly Func<IFileLauncherService> _launcher;
    private IFileLauncherService Launcher => _launcher();

    // 指向当前标签页的历史栈（切换标签时随 ActiveTab 一起换绑）
    private Stack<string> _backStack;
    private Stack<string> _forwardStack;
    private bool _suppressTabFilterRefilter;
    private List<FileItem> _allItems = [];
    private List<FileItem> _filteredItems = [];
    private int _visibleItemCount;
    private int _pagingGeneration;
    // 用户滚到底之后的“后台补齐”：一次只物化一小批并让出 UI 线程，直到列表全部就绪。
    private Task? _backgroundFillTask;
    private IDisposable? _watchSubscription;
    private bool _watchEnabled;
    private SortField _currentSortField = SortField.Name;
    private SortDirection _currentSortDirection = SortDirection.Ascending;
    private bool _applyingFolderViewPreferences;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _thumbCts;
    private readonly SemaphoreSlim _thumbnailGate = new(OperatingSystem.IsAndroid() ? 2 : 4);
    private Task _idleTask = Task.CompletedTask;

    /// <summary>对照窗格的第二会话不写主标签页持久化，避免两个 Browser 互相覆盖 OpenTabPaths。</summary>
    internal bool PersistSessionTabs { get; set; } = true;

    /// <summary>
    /// A 会话把视图/排序写回全局默认；B 会话改存 DualPane*，否则两个窗格会互相覆盖对方的呈现偏好。
    /// </summary>
    internal bool PersistSharedViewPreferences { get; set; } = true;

    private bool _applyingViewPreferences;

    /// <summary>双窗格中只有活动会话可以驱动共享预览；非活动会话导航/改选集不得抢占预览。</summary>
    internal bool DrivesSharedPreview { get; set; } = true;

    private BrowserClipboardState _clipboard = new();

    /// <summary>Shell 让 A/B 两个窗格共享同一剪贴板状态，保证一侧剪切后可在另一侧粘贴。</summary>
    internal BrowserClipboardState ClipboardState
    {
        get => _clipboard;
        set
        {
            if (ReferenceEquals(_clipboard, value))
                return;
            _clipboard.Changed -= OnClipboardStateChanged;
            _clipboard = value;
            _clipboard.Changed += OnClipboardStateChanged;
            OnPropertyChanged(nameof(HasClipboard));
        }
    }

    private void OnClipboardStateChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(HasClipboard));
    private readonly Stack<ClosedTabSnapshot> _closedTabs = [];

    private sealed record ClosedTabSnapshot(
        string Path,
        string Title,
        string[] BackPaths,
        string[] ForwardPaths,
        string FilterText,
        IReadOnlyList<string> SelectedPaths,
        int Index);

    public BrowserViewModel(
        IFileSystemService fileSystem, IFileOperationService operations, ITrashService trash,
        IArchiveService archive, ISearchService search, ISettingsStore settingsStore,
        IFileInspectorService inspector, IFolderWatchService folderWatch,
        ISystemClipboardService systemClipboard,
        IDialogs dialogs, PreviewViewModel preview, Func<IFileLauncherService> launcher)
    {
        _fileSystem = fileSystem;
        _operations = operations;
        _trash = trash;
        _archive = archive;
        _search = search;
        _inspector = inspector;
        _folderWatch = folderWatch;
        _systemClipboard = systemClipboard;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _preview = preview;
        _launcher = launcher;
        _clipboard.Changed += OnClipboardStateChanged;

        var s = settingsStore.Settings;
        _watchEnabled = s.WatchFolderChanges;
        _viewMode = s.ViewMode switch
        {
            "Grid" => BrowserViewMode.Grid,
            "Details" => BrowserViewMode.Details,
            _ => BrowserViewMode.List,
        };
        _currentSortField = ParseSort(s.SortField);
        _currentSortDirection = ParseSortDirection(s.SortDirection);
        _sortFieldLabel = SortFieldText(_currentSortField);

        var initialTab = new BrowserTabViewModel(null, "新标签页") { IsActive = true };
        Tabs.Add(initialTab);
        _activeTab = initialTab;
        _backStack = initialTab.BackStack;
        _forwardStack = initialTab.ForwardStack;
        Tabs.CollectionChanged += (_, _) => UpdateTabMetadata();
        UpdateTabMetadata();

        SelectedItems.CollectionChanged += (_, _) => OnSelectionChanged();
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SearchResultBadgeText));
            RaisePagingState();
        };
    }

    // ---------- 基本状态 ----------
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _currentFolderName = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _loadError;

    public bool HasError => LoadError is not null;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private BrowserViewMode _viewMode = BrowserViewMode.List;
    [ObservableProperty] private string _sortFieldLabel = "名称";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private bool _isSearchPanelOpen;
    [ObservableProperty] private bool _isSelectionMode;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private bool _isAddressEditing;
    [ObservableProperty] private string _addressText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressError))]
    private string? _addressError;

    public bool HasAddressError => !string.IsNullOrWhiteSpace(AddressError);

    public ObservableCollection<FileItemViewModel> Items { get; } = [];
    public ObservableCollection<FileItemViewModel> SelectedItems { get; } = [];
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    [ObservableProperty] private BrowserTabViewModel? _activeTab;
    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];
    public bool HasMultipleTabs => Tabs.Count > 1;
    public string TabCountText => Tabs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasClosedTabs => _closedTabs.Count > 0;

    /// <summary>最近访问文件夹/文件变化时通知 Shell 刷新主页入口。</summary>
    public event EventHandler? RecentsChanged;

    /// <summary>请求 Shell 打开本地 ZIP 家族压缩包工作台；参数为压缩包完整路径。</summary>
    public event EventHandler<string>? ArchiveWorkbenchRequested;

    /// <summary>请求 Shell 打开文本/PDF 文档工作台；参数为当前行模型快照。</summary>
    public event EventHandler<FileItem>? DocumentWorkbenchRequested;

    public bool IsListView => ViewMode == BrowserViewMode.List;
    public bool IsGridView => ViewMode == BrowserViewMode.Grid;
    public bool IsDetailsView => ViewMode == BrowserViewMode.Details;
    public bool HasSelection => SelectedCount > 0;
    public bool HasClipboard => _clipboard.Paths.Count > 0;
    public string SelectionText => SelectedCount == 0 ? StatusText : $"已选 {SelectedCount} 项";

    /// <summary>当前已物化的文件行数；与 <see cref="TotalItemCount"/> 分开，避免大文件夹首屏创建全部行 VM。</summary>
    public int VisibleItemCount => Items.Count;
    public bool HasMoreItems => _visibleItemCount < _filteredItems.Count;
    public bool CanLoadMoreItems => HasMoreItems && !IsLoading && !IsLoadingMore && !IsBackgroundPaging;
    public bool ShowLoadMoreFooter => HasMoreItems || IsLoadingMore || IsBackgroundPaging;
    /// <summary>页脚转圈：手点“加载更多”和后台补齐都算在忙。</summary>
    public bool IsPagingBusy => IsLoadingMore || IsBackgroundPaging;
    public string PagingStatusText => TotalItemCount == 0
        ? string.Empty
        : $"已显示 {VisibleItemCount:N0} / {TotalItemCount:N0} 项";

    /// <summary>后台补齐是否正在跑；页脚据此显示转圈并禁用“加载更多”。</summary>
    [ObservableProperty] private bool _isBackgroundPaging;

    partial void OnIsLoadingChanged(bool value) => RaisePagingState();

    partial void OnIsLoadingMoreChanged(bool value) => RaisePagingState();

    partial void OnIsBackgroundPagingChanged(bool value) => RaisePagingState();

    partial void OnTotalItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(SearchResultBadgeText));
        OnPropertyChanged(nameof(SearchPanelStatusText));
        RaisePagingState();
    }

    private void RaisePagingState()
    {
        OnPropertyChanged(nameof(VisibleItemCount));
        OnPropertyChanged(nameof(HasMoreItems));
        OnPropertyChanged(nameof(CanLoadMoreItems));
        OnPropertyChanged(nameof(ShowLoadMoreFooter));
        OnPropertyChanged(nameof(IsPagingBusy));
        OnPropertyChanged(nameof(PagingStatusText));
    }

    /// <summary>迷你搜索岛是否应显示清除入口。</summary>
    public bool HasActiveSearch => IsSearchMode || !string.IsNullOrWhiteSpace(FilterText);

    /// <summary>迷你搜索岛文案：闲置只说“搜索”，活动中回显关键词或实时结果数。</summary>
    public string SearchIslandText
    {
        get
        {
            var query = FilterText.Trim();
            if (IsSearchMode)
                return string.IsNullOrWhiteSpace(StatusText) ? "搜索中" : StatusText;
            return query.Length == 0 ? "搜索" : query;
        }
    }

    /// <summary>图标型搜索岛的角标：活动搜索只回显结果数，完整语义保留在 SearchIslandText（ToolTip/无障碍名）。</summary>
    public string SearchResultBadgeText => !HasActiveSearch
        ? string.Empty
        : TotalItemCount > 99 ? "99+" : TotalItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string SearchPanelScopeText => IsSearchMode
        ? $"递归搜索 · {CurrentFolderName}"
        : $"当前列表筛选 · {CurrentFolderName}";

    /// <summary>搜索面板状态行：列表筛选报匹配数，递归搜索报搜索进度/结果数。</summary>
    public string SearchPanelStatusText => !IsSearchMode && !string.IsNullOrWhiteSpace(FilterText)
        ? $"当前列表匹配 {TotalItemCount:N0} 项"
        : StatusText;

    /// <summary>截图/测试等待当前加载与缩略图完成。</summary>
    public Task WhenIdleAsync() => _idleTask;

    /// <summary>同步预判「返回」能否被浏览器消费（系统返回事件需同步决定 Handled）。</summary>
    public bool CanConsumeBack => IsSelectionMode || IsSearchMode || _backStack.Count > 0;

    /// <summary>工具栏按钮：列表 → 网格 → 详细信息循环切换。</summary>
    [RelayCommand]
    private void ToggleViewMode() => ViewMode = ViewMode switch
    {
        BrowserViewMode.List => BrowserViewMode.Grid,
        BrowserViewMode.Grid => BrowserViewMode.Details,
        _ => BrowserViewMode.List,
    };

    [RelayCommand]
    private void SetViewMode(string? mode) => ViewMode = mode switch
    {
        "Grid" => BrowserViewMode.Grid,
        "Details" => BrowserViewMode.Details,
        _ => BrowserViewMode.List,
    };

    partial void OnViewModeChanged(BrowserViewMode value)
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsDetailsView));
        if (_applyingFolderViewPreferences || _applyingViewPreferences)
            return;

        if (!PersistSharedViewPreferences)
        {
            _settingsStore.Settings.DualPaneViewMode = value.ToString();
            _ = SaveSettingsAsync();
            return;
        }

        if (IsFolderViewMemoryEnabled)
        {
            // 记忆开启时，全局 ViewMode 只代表未记忆文件夹的默认值；活动文件夹单独落盘。
            CaptureCurrentFolderViewPreferences();
        }
        else if (!IsSearchMode)
        {
            _settingsStore.Settings.ViewMode = value.ToString();
        }
        _ = SaveSettingsAsync();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveSearch));
        OnPropertyChanged(nameof(SearchIslandText));
        OnPropertyChanged(nameof(SearchResultBadgeText));
        if (!IsSearchMode && !_suppressTabFilterRefilter)
            RefilterAndSort();
        OnPropertyChanged(nameof(SearchPanelStatusText));
    }

    partial void OnIsSearchModeChanged(bool value)
    {
        OnPropertyChanged(nameof(HasActiveSearch));
        OnPropertyChanged(nameof(SearchIslandText));
        OnPropertyChanged(nameof(SearchResultBadgeText));
        OnPropertyChanged(nameof(SearchPanelScopeText));
        OnPropertyChanged(nameof(SearchPanelStatusText));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(SearchIslandText));
        OnPropertyChanged(nameof(SearchPanelStatusText));
    }

    partial void OnCurrentFolderNameChanged(string value) => OnPropertyChanged(nameof(SearchPanelScopeText));

    [RelayCommand]
    private void OpenSearchPanel() => IsSearchPanelOpen = true;

    [RelayCommand]
    private void CloseSearchPanel() => IsSearchPanelOpen = false;

    [RelayCommand]
    private void ToggleSearchPanel() => IsSearchPanelOpen = !IsSearchPanelOpen;

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    [RelayCommand]
    private void CancelCurrentSearch() => CancelSearch();

    [RelayCommand]
    private async Task SearchRecursiveFromPanelAsync()
    {
        var query = FilterText.Trim();
        if (query.Length == 0)
            return;
        IsSearchPanelOpen = false;
        await SearchRecursiveAsync(query).ConfigureAwait(true);
    }

    partial void OnCurrentPathChanged(string? value)
    {
        if (!IsAddressEditing)
            AddressText = value ?? string.Empty;
    }

    // ---------- 按文件夹记忆视图/排序 ----------
    private bool IsFolderViewMemoryEnabled => _settingsStore.Settings.RememberFolderView;

    /// <summary>Shell 切换设置后同步当前文件夹：开启时立即记下当前呈现，关闭时把当前呈现收回全局默认。</summary>
    public void OnFolderViewMemorySettingChanged(bool enabled)
    {
        if (enabled)
        {
            CaptureCurrentFolderViewPreferences();
        }
        else if (!IsSearchMode)
        {
            var settings = _settingsStore.Settings;
            settings.ViewMode = ViewMode.ToString();
            settings.SortField = _currentSortField.ToString();
            settings.SortDirection = _currentSortDirection.ToString();
        }
        _ = SaveSettingsAsync();
    }

    public async Task ClearFolderViewPreferencesAsync()
    {
        _settingsStore.Settings.FolderViewStates.Clear();
        // 清除后不立刻改变眼前的文件夹；若记忆仍开启，下一次改视图/排序会重新建档。
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>B 会话启动时套用自己的视图/排序；未记录时跟随全局默认，且不写回全局偏好。</summary>
    internal void ApplyIndependentViewPreferences(string? viewMode, string? sortField, string? sortDirection)
    {
        var settings = _settingsStore.Settings;
        _applyingViewPreferences = true;
        try
        {
            ViewMode = ParseViewMode(viewMode ?? settings.ViewMode);
            _currentSortField = ParseSort(sortField ?? settings.SortField);
            _currentSortDirection = ParseSortDirection(sortDirection ?? settings.SortDirection);
            SortFieldLabel = SortFieldText(_currentSortField);
            RaiseSortGlyphs();
        }
        finally
        {
            _applyingViewPreferences = false;
        }
    }

    private void ApplyFolderViewPreferences(string path)
    {
        // 按文件夹记忆只属于主窗格：B 用独立的 DualPane* 偏好，避免覆盖 A 的目录记忆。
        if (!IsFolderViewMemoryEnabled || !PersistSharedViewPreferences)
            return;

        var settings = _settingsStore.Settings;
        FolderViewPreferences preferences;
        if (settings.FolderViewStates.TryGetValue(FolderViewKey(path), out var saved))
        {
            preferences = saved;
        }
        else
        {
            preferences = new FolderViewPreferences
            {
                ViewMode = settings.ViewMode,
                SortField = settings.SortField,
                SortDirection = settings.SortDirection,
            };
        }

        _applyingFolderViewPreferences = true;
        try
        {
            ViewMode = ParseViewMode(preferences.ViewMode);
            _currentSortField = ParseSort(preferences.SortField);
            _currentSortDirection = ParseSortDirection(preferences.SortDirection);
            SortFieldLabel = SortFieldText(_currentSortField);
            RaiseSortGlyphs();
        }
        finally
        {
            _applyingFolderViewPreferences = false;
        }
    }

    private void CaptureCurrentFolderViewPreferences()
    {
        if (!IsFolderViewMemoryEnabled || !PersistSharedViewPreferences ||
            _applyingFolderViewPreferences || IsSearchMode || CurrentPath is null)
            return;

        var states = _settingsStore.Settings.FolderViewStates;
        var key = FolderViewKey(CurrentPath);
        // 删除后重插使字典近似保持最近使用顺序，裁剪时优先丢最久未改过的文件夹。
        states.Remove(key);
        states[key] = new FolderViewPreferences
        {
            ViewMode = ViewMode.ToString(),
            SortField = _currentSortField.ToString(),
            SortDirection = _currentSortDirection.ToString(),
        };
        const int maxRememberedFolders = 200;
        while (states.Count > maxRememberedFolders)
            states.Remove(states.Keys.First());
    }

    private static string FolderViewKey(string path)
    {
        if (FileLocator.IsStorageUri(path))
            return path;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            if (full.Length > root.Length)
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
        }
    }

    private static BrowserViewMode ParseViewMode(string? value) => value switch
    {
        "Grid" => BrowserViewMode.Grid,
        "Details" => BrowserViewMode.Details,
        _ => BrowserViewMode.List,
    };

    private static SortDirection ParseSortDirection(string? value) =>
        string.Equals(value, "Descending", StringComparison.Ordinal)
            ? SortDirection.Descending
            : SortDirection.Ascending;

    partial void OnAddressTextChanged(string value)
    {
        if (IsAddressEditing)
            AddressError = null;
    }

    partial void OnActiveTabChanged(BrowserTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsActive = ReferenceEquals(tab, value);
        if (value is not null)
        {
            _backStack = value.BackStack;
            _forwardStack = value.ForwardStack;
        }
    }

    // ---------- 导航与加载 ----------
    public async Task NavigateToAsync(string path, bool pushHistory = true)
    {
        if (pushHistory && CurrentPath is not null && !PathsEqual(CurrentPath, path))
        {
            _backStack.Push(CurrentPath);
            _forwardStack.Clear();
        }
        await LoadFolderAsync(path).ConfigureAwait(true);
    }

    private async Task LoadFolderAsync(string path)
    {
        _loadCts?.Cancel();
        _thumbCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsAddressEditing = false;
        AddressError = null;
        CurrentPath = path;
        CurrentFolderName = TabTitleFor(path);
        if (ActiveTab is { } activeTab)
        {
            activeTab.Path = path;
            activeTab.Title = CurrentFolderName;
        }
        IsSearchMode = false;
        ExitSelectionMode();
        ApplyFolderViewPreferences(path);
        RebuildBreadcrumbs(path);
        UpdateHistoryFlags(path);
        LoadError = null;
        IsLoading = true;
        ClearVisibleItems();
        SelectedItems.Clear();
        if (DrivesSharedPreview)
            await _preview.LoadAsync(null).ConfigureAwait(true);

        _idleTask = LoadCoreAsync(path, ct);
        await _idleTask.ConfigureAwait(true);
        ArmFolderWatch(path);
        _ = PersistTabsAsync();
    }

    // ---------- 文件夹外部变更监视 ----------
    private void ArmFolderWatch(string path)
    {
        _watchSubscription?.Dispose();
        _watchSubscription = null;
        if (!_watchEnabled) return;
        if (FileLocator.IsStorageUri(path)) return; // 存储形态无变更通知，靠手动刷新
        _watchSubscription = _folderWatch.Watch(path, () =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsSearchMode && CurrentPath is not null && PathsEqual(CurrentPath, path))
                    _ = SoftReloadAsync();
            }));
    }

    private void DisarmFolderWatch()
    {
        _watchSubscription?.Dispose();
        _watchSubscription = null;
    }

    /// <summary>设置页开关即时生效（Shell 调用）。</summary>
    public void SetWatchEnabled(bool enabled)
    {
        _watchEnabled = enabled;
        if (enabled && CurrentPath is not null && !IsSearchMode)
            ArmFolderWatch(CurrentPath);
        else
            DisarmFolderWatch();
    }

    /// <summary>软刷新：仅重枚举与再排序，保留选集与浏览模式（手动刷新/外部变更共用）。</summary>
    private async Task SoftReloadAsync()
    {
        if (CurrentPath is null || IsSearchMode) return;
        _loadCts?.Cancel();
        _thumbCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        LoadError = null;
        IsLoading = true;
        _idleTask = LoadCoreAsync(CurrentPath, ct);
        await _idleTask.ConfigureAwait(true);
    }

    private async Task LoadCoreAsync(string path, CancellationToken ct)
    {
        try
        {
            var collected = new List<FileItem>();
            await foreach (var item in _fileSystem.ListAsync(path, _settingsStore.Settings.ShowHidden, ct).ConfigureAwait(true))
            {
                collected.Add(item);
                if (collected.Count % 250 == 0)
                {
                    TotalItemCount = collected.Count;
                    StatusText = $"正在读取 {collected.Count:N0} 项…";
                }
            }

            _allItems = collected;
            RefilterAndSort();
            StatusText = HasMoreItems
                ? PagingStatusText
                : $"{TotalItemCount:N0} 个项目";
            await RecordRecentFolderAsync(path).ConfigureAwait(true);
            StartThumbnails();
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            LoadError = "没有访问此文件夹的权限。可尝试选择其他位置，或在系统设置中调整权限。";
            _allItems = [];
            ClearVisibleItems();
        }
        catch (DirectoryNotFoundException)
        {
            LoadError = "此文件夹已不存在，可能已被移动或删除。";
            _allItems = [];
            ClearVisibleItems();
        }
        catch (IOException ex)
        {
            LoadError = $"无法打开此文件夹：{ex.Message}";
            _allItems = [];
            ClearVisibleItems();
        }
        finally
        {
            IsLoading = false;
            IsEmpty = TotalItemCount == 0 && LoadError is null;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;
        IsRefreshing = true;
        try
        {
            if (IsSearchMode) { await ExitSearchAsync().ConfigureAwait(true); return; }
            await SoftReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// MdRefreshIndicator 的手势入口：只软刷新当前文件夹，不借下拉手势退出递归搜索，
    /// 也不与正在进行的首次导航重复枚举。
    /// </summary>
    [RelayCommand]
    private async Task PullRefreshAsync()
    {
        // 设置里关掉下拉刷新后，手势入口只把指示器收回；工具栏刷新与 F5/Ctrl+R 不受影响。
        if (!_settingsStore.Settings.EnablePullRefresh)
        {
            IsRefreshing = false;
            return;
        }
        if (IsSearchMode || IsLoading)
        {
            IsRefreshing = false;
            return;
        }

        IsRefreshing = true;
        try
        {
            await SoftReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (IsSearchMode) { await ExitSearchAsync().ConfigureAwait(true); return; }
        if (_backStack.Count == 0 || CurrentPath is null) return;
        _forwardStack.Push(CurrentPath);
        await LoadFolderAsync(_backStack.Pop()).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0 || CurrentPath is null) return;
        _backStack.Push(CurrentPath);
        await LoadFolderAsync(_forwardStack.Pop()).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (CurrentPath is null) return;
        var parent = _fileSystem.GetParentPath(CurrentPath);
        if (parent is null || PathsEqual(parent, CurrentPath)) return;
        await NavigateToAsync(parent).ConfigureAwait(true);
    }

    /// <summary>宿主/手势层询问浏览器能否消费一次「返回」；能则执行并返回 true。</summary>
    public async Task<bool> TryGoBackAsync()
    {
        if (IsSelectionMode) { ExitSelectionMode(); return true; }
        if (IsSearchMode) { await ExitSearchAsync().ConfigureAwait(true); return true; }
        if (_backStack.Count > 0) { await GoBackAsync().ConfigureAwait(true); return true; }
        return false;
    }

    // ---------- 地址栏与定位 ----------
    [RelayCommand]
    private void BeginEditAddress()
    {
        if (CurrentPath is null || IsSearchMode)
            return;
        if (FileLocator.IsStorageUri(CurrentPath))
        {
            _dialogs.Notify("已授权文件夹不支持手动输入路径，请用面包屑导航");
            return;
        }
        AddressText = CurrentPath;
        AddressError = null;
        IsAddressEditing = true;
    }

    [RelayCommand]
    private void CancelEditAddress()
    {
        IsAddressEditing = false;
        AddressError = null;
        AddressText = CurrentPath ?? string.Empty;
    }

    [RelayCommand]
    private async Task CommitAddressAsync() => await NavigateToAddressAsync(AddressText).ConfigureAwait(true);

    /// <summary>解析地址栏输入并导航。文件路径会进入其所在文件夹并选中该文件，方便继续在 Eidoria 内预览。</summary>
    public async Task<bool> NavigateToAddressAsync(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            AddressError = "请输入文件夹或文件路径";
            IsAddressEditing = true;
            return false;
        }

        string candidate;
        try
        {
            candidate = ResolveAddressInput(input.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException)
        {
            AddressError = "路径格式无效";
            IsAddressEditing = true;
            return false;
        }

        if (FileLocator.IsStorageUri(candidate))
        {
            AddressError = "不能手动输入存储提供者 URI";
            IsAddressEditing = true;
            return false;
        }

        FileItem? target;
        try
        {
            target = await _fileSystem.StatAsync(candidate, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AddressError = $"无法访问：{ex.Message}";
            IsAddressEditing = true;
            return false;
        }

        if (target is null)
        {
            AddressError = "找不到这个路径";
            IsAddressEditing = true;
            return false;
        }

        if (target.IsFolder)
        {
            await NavigateToAsync(candidate).ConfigureAwait(true);
        }
        else
        {
            await NavigateToFileLocationAsync(candidate).ConfigureAwait(true);
        }
        return true;
    }

    private string ResolveAddressInput(string input)
    {
        var value = input.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Trim();

        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var fileUri))
            value = fileUri.LocalPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value == "~")
        {
            value = home;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(home, value[2..]);
        }
        else if (value == "$HOME")
        {
            value = home;
        }
        else if (value.StartsWith("$HOME/", StringComparison.Ordinal) || value.StartsWith("$HOME\\", StringComparison.Ordinal))
        {
            value = Path.Combine(home, value[6..]);
        }

        value = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(value, CurrentPath ?? home);
    }

    /// <summary>进入目标所在文件夹并单选目标（搜索结果/最近文件/文件地址输入共用）。</summary>
    public async Task NavigateToFileLocationAsync(string path)
    {
        FilterText = string.Empty;
        var parent = _fileSystem.GetParentPath(path) ?? path;
        if (IsSearchMode || CurrentPath is null || !PathsEqual(CurrentPath, parent))
            await NavigateToAsync(parent).ConfigureAwait(true);

        SelectedItems.Clear();
        RevealItemByPath(path);
        if (Items.FirstOrDefault(item => PathsEqual(item.FullPath, path)) is { } target)
            SelectedItems.Add(target);
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is not null)
            await NavigateToFileLocationAsync(item.FullPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RevealInSystemFileManagerAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null)
            return;
        if (FileLocator.IsStorageUri(item.FullPath))
        {
            _dialogs.Notify("已授权文件夹中的项目无法在系统文件管理器中定位");
            return;
        }

        var revealed = await Launcher.RevealAsync(item.FullPath, CancellationToken.None).ConfigureAwait(true);
        if (!revealed)
            _dialogs.Notify("当前平台不支持在系统文件管理器中显示");
    }

    [RelayCommand]
    private async Task CopyPathAsync(FileItemViewModel? item)
    {
        IReadOnlyList<string> paths = item is not null
            ? [item.FullPath]
            : SelectedItems.Select(selected => selected.FullPath).ToArray();
        await CopyPathsTextAsync(paths).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyCurrentPathAsync()
    {
        if (CurrentPath is not null)
            await CopyPathsTextAsync([CurrentPath]).ConfigureAwait(true);
    }

    private async Task CopyPathsTextAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        await _systemClipboard.SetTextAsync(string.Join(Environment.NewLine, paths), CancellationToken.None).ConfigureAwait(true);

        // 文本写入会替换系统文件剪贴板；同步清掉应用内待粘贴集合，避免 Ctrl+V 仍粘贴旧文件。
        _clipboard.Clear();
        _dialogs.Notify(paths.Count == 1 ? "已复制路径" : $"已复制 {paths.Count} 条路径");
    }

    // ---------- 最近访问 ----------
    public async Task RecordRecentFileAsync(string path)
    {
        if (FileLocator.IsStorageUri(path))
            return;
        await RecordRecentAsync(_settingsStore.Settings.RecentFilePaths, path).ConfigureAwait(true);
    }

    private async Task RecordRecentFolderAsync(string path)
    {
        if (FileLocator.IsStorageUri(path))
            return;
        await RecordRecentAsync(_settingsStore.Settings.RecentFolderPaths, path).ConfigureAwait(true);
    }

    private async Task RecordRecentAsync(List<string> target, string path)
    {
        if (target.Count > 0 && PathsEqual(target[0], path))
            return;
        target.RemoveAll(existing => PathsEqual(existing, path));
        target.Insert(0, path);
        var limit = Math.Clamp(_settingsStore.Settings.RecentItemLimit, 6, 60);
        if (target.Count > limit)
            target.RemoveRange(limit, target.Count - limit);
        await SaveSettingsAsync().ConfigureAwait(true);
        RecentsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 标签页 ----------
    // 每个标签页保存自己的路径、前进/后退栈、列表过滤词与选集快照；剪贴板与视图/排序是全局会话状态，不随标签拆分。
    // 切换时按快照重载目标文件夹（顺带拿到外部最新状态），滚动位置与一次性递归搜索结果不持久化。
    private string TabTitleFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "新标签页";
        return FileLocator.IsStorageUri(path)
            ? _fileSystem.GetBreadcrumbs(path).LastOrDefault()?.Name ?? path
            : FolderDisplayName(path);
    }

    private BrowserTabViewModel CreateTab(string? path) => new(path, TabTitleFor(path));

    private void SnapshotActiveTab()
    {
        if (ActiveTab is not { } tab)
            return;
        tab.Path = CurrentPath;
        tab.Title = string.IsNullOrWhiteSpace(CurrentFolderName) ? TabTitleFor(CurrentPath) : CurrentFolderName;
        tab.FilterTextSnapshot = IsSearchMode ? string.Empty : FilterText;
        tab.SelectedPathsSnapshot = IsSearchMode
            ? []
            : SelectedItems.Select(item => item.FullPath).ToArray();
    }

    private async Task ActivateTabCoreAsync(BrowserTabViewModel tab, bool snapshotCurrent)
    {
        if (string.IsNullOrWhiteSpace(tab.Path))
            return;
        if (snapshotCurrent)
            SnapshotActiveTab();

        ActiveTab = tab;
        var selectedPaths = tab.SelectedPathsSnapshot;
        _suppressTabFilterRefilter = true;
        FilterText = tab.FilterTextSnapshot;
        _suppressTabFilterRefilter = false;

        await LoadFolderAsync(tab.Path).ConfigureAwait(true);

        if (selectedPaths.Count > 0)
        {
            RevealItemsByPaths(selectedPaths);
            foreach (var item in Items.Where(item => selectedPaths.Any(path => PathsEqual(path, item.FullPath))))
                SelectedItems.Add(item);
        }
        _ = PersistTabsAsync();
    }

    [RelayCommand]
    private async Task ActivateTabAsync(BrowserTabViewModel? tab)
    {
        if (tab is null || ReferenceEquals(tab, ActiveTab))
            return;
        await ActivateTabCoreAsync(tab, snapshotCurrent: true).ConfigureAwait(true);
    }

    public async Task OpenInNewTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        SnapshotActiveTab();
        var tab = CreateTab(path);
        Tabs.Add(tab);
        await ActivateTabCoreAsync(tab, snapshotCurrent: false).ConfigureAwait(true);
    }

    /// <summary>把一条会话路径收纳为后台标签（不切换当前标签）。双窗格在宽度跌出 XL 时用它保留 B 会话。</summary>
    public bool EnsureBackgroundTab(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        SnapshotActiveTab();
        if (Tabs.Any(tab => PathsEqual(tab.Path, path)))
            return false;

        var tab = CreateTab(path);
        tab.IsActive = false;
        Tabs.Add(tab);
        _ = PersistTabsAsync();
        return true;
    }

    public async Task OpenItemInNewTabAsync(FileItemViewModel? item)
    {
        if (item is null)
            return;
        if (!item.IsFolder)
        {
            _dialogs.Notify("只有文件夹可以在新标签页中打开");
            return;
        }
        await OpenInNewTabAsync(item.FullPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewTabAsync()
    {
        if (ActiveTab is not null)
            await DuplicateTabAsync(ActiveTab).ConfigureAwait(true);
        else if (CurrentPath is not null)
            await OpenInNewTabAsync(CurrentPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DuplicateTabAsync(BrowserTabViewModel? tab)
    {
        if (tab is null || string.IsNullOrWhiteSpace(tab.Path))
            return;
        if (ReferenceEquals(tab, ActiveTab))
            SnapshotActiveTab();

        var copy = CreateTab(tab.Path);
        copy.Title = tab.Title;
        CopyStack(tab.BackStack, copy.BackStack);
        CopyStack(tab.ForwardStack, copy.ForwardStack);
        copy.FilterTextSnapshot = tab.FilterTextSnapshot;
        copy.SelectedPathsSnapshot = tab.SelectedPathsSnapshot.ToArray();
        Tabs.Insert(Tabs.IndexOf(tab) + 1, copy);
        await ActivateTabCoreAsync(copy, snapshotCurrent: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 把一个标签移动到目标下标（拖拽重排的模型落点）。只调整顺序，不切换活动标签、
    /// 不重载当前目录；活动标签对象保持不变，并把新顺序/活动下标一并持久化。
    /// </summary>
    public bool MoveTab(BrowserTabViewModel? tab, int targetIndex)
    {
        if (tab is null || Tabs.Count < 2)
            return false;
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0)
            return false;

        targetIndex = Math.Clamp(targetIndex, 0, Tabs.Count - 1);
        if (targetIndex == oldIndex)
            return false;

        Tabs.Move(oldIndex, targetIndex);
        _ = PersistTabsAsync();
        return true;
    }

    [RelayCommand]
    private async Task CloseTabAsync(BrowserTabViewModel? tab)
    {
        if (tab is null || Tabs.Count <= 1)
            return;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;

        var closingActive = ReferenceEquals(tab, ActiveTab);
        if (closingActive)
            SnapshotActiveTab();
        RememberClosedTab(tab, index);
        Tabs.Remove(tab);
        tab.IsActive = false;
        if (!closingActive)
        {
            _ = PersistTabsAsync();
            return;
        }

        var target = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        await ActivateTabCoreAsync(target, snapshotCurrent: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(BrowserTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null || Tabs.Count <= 1 || !Tabs.Contains(tab))
            return;

        SnapshotActiveTab();
        var others = Tabs.Where(candidate => !ReferenceEquals(candidate, tab)).ToArray();
        // 反序入栈：连续按 Ctrl+Shift+T 时按原来的从左到右顺序恢复。
        for (var i = others.Length - 1; i >= 0; i--)
            RememberClosedTab(others[i], Tabs.IndexOf(others[i]), trim: false);
        TrimClosedTabs();

        foreach (var other in others)
        {
            Tabs.Remove(other);
            other.IsActive = false;
        }

        if (!ReferenceEquals(tab, ActiveTab))
            await ActivateTabCoreAsync(tab, snapshotCurrent: false).ConfigureAwait(true);
        else
            _ = PersistTabsAsync();
    }

    [RelayCommand]
    private async Task ReopenClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
            return;
        SnapshotActiveTab();
        var snapshot = _closedTabs.Pop();
        OnPropertyChanged(nameof(HasClosedTabs));

        var tab = CreateTab(snapshot.Path);
        tab.Title = snapshot.Title;
        RestoreStack(tab.BackStack, snapshot.BackPaths);
        RestoreStack(tab.ForwardStack, snapshot.ForwardPaths);
        tab.FilterTextSnapshot = snapshot.FilterText;
        tab.SelectedPathsSnapshot = snapshot.SelectedPaths;
        Tabs.Insert(Math.Clamp(snapshot.Index, 0, Tabs.Count), tab);
        await ActivateTabCoreAsync(tab, snapshotCurrent: false).ConfigureAwait(true);
    }

    private void RememberClosedTab(BrowserTabViewModel tab, int index, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(tab.Path))
            return;
        _closedTabs.Push(new ClosedTabSnapshot(
            tab.Path,
            tab.Title,
            tab.BackStack.ToArray(),
            tab.ForwardStack.ToArray(),
            tab.FilterTextSnapshot,
            tab.SelectedPathsSnapshot.ToArray(),
            index));
        if (trim)
            TrimClosedTabs();
        OnPropertyChanged(nameof(HasClosedTabs));
    }

    private void TrimClosedTabs()
    {
        const int maxClosedTabs = 10;
        if (_closedTabs.Count <= maxClosedTabs)
            return;
        var retained = _closedTabs.Take(maxClosedTabs).Reverse().ToArray();
        _closedTabs.Clear();
        foreach (var snapshot in retained)
            _closedTabs.Push(snapshot);
    }

    private static void CopyStack(Stack<string> source, Stack<string> destination) =>
        RestoreStack(destination, source.ToArray());

    private static void RestoreStack(Stack<string> destination, IReadOnlyList<string> topToBottom)
    {
        destination.Clear();
        for (var i = topToBottom.Count - 1; i >= 0; i--)
            destination.Push(topToBottom[i]);
    }

    [RelayCommand]
    private async Task CloseActiveTabAsync() => await CloseTabAsync(ActiveTab).ConfigureAwait(true);

    [RelayCommand]
    private async Task NextTabAsync()
    {
        if (Tabs.Count < 2 || ActiveTab is null)
            return;
        var index = Tabs.IndexOf(ActiveTab);
        await ActivateTabAsync(Tabs[(index + 1) % Tabs.Count]).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PreviousTabAsync()
    {
        if (Tabs.Count < 2 || ActiveTab is null)
            return;
        var index = Tabs.IndexOf(ActiveTab);
        await ActivateTabAsync(Tabs[(index - 1 + Tabs.Count) % Tabs.Count]).ConfigureAwait(true);
    }

    /// <summary>启动恢复：开启时会话恢复上次标签页（最多 12 个）；关闭或没有记录时只打开宿主起始文件夹。</summary>
    public async Task RestoreTabsAsync(string fallbackPath)
    {
        string[] savedPaths = _settingsStore.Settings.RestoreTabsOnLaunch
            ? _settingsStore.Settings.OpenTabPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(12)
                .ToArray()
            : [];

        Tabs.Clear();
        foreach (var path in savedPaths)
            Tabs.Add(CreateTab(path));
        if (Tabs.Count == 0)
            Tabs.Add(CreateTab(fallbackPath));

        var index = Math.Clamp(_settingsStore.Settings.ActiveTabIndex, 0, Tabs.Count - 1);
        await ActivateTabCoreAsync(Tabs[index], snapshotCurrent: false).ConfigureAwait(true);
    }

    private async Task PersistTabsAsync()
    {
        if (!PersistSessionTabs)
            return;
        var settings = _settingsStore.Settings;
        settings.OpenTabPaths = Tabs
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Path))
            .Select(tab => tab.Path!)
            .Take(12)
            .ToList();
        settings.ActiveTabIndex = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab));
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private void UpdateTabMetadata()
    {
        foreach (var tab in Tabs)
            tab.CanClose = Tabs.Count > 1;
        OnPropertyChanged(nameof(HasMultipleTabs));
        OnPropertyChanged(nameof(TabCountText));
        OnPropertyChanged(nameof(HasClosedTabs));
    }

    // ---------- 打开与选择 ----------
    public async Task OpenItemAsync(FileItemViewModel? item)
    {
        if (item is null) return;
        if (IsSelectionMode)
        {
            ToggleChecked(item);
            return;
        }
        if (item.IsFolder)
            await NavigateToAsync(item.FullPath).ConfigureAwait(true);
        // 文件的打开语义由视图层按断点决定（Compact 进预览页、桌面进预览窗格），在此只做目录进入。
    }

    public async Task OpenItemExternalAsync(FileItemViewModel? item)
    {
        if (item is null || item.IsFolder) return;
        await RecordRecentFileAsync(item.FullPath).ConfigureAwait(true);
        await _preview.OpenCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private void OnSelectionChanged()
    {
        SelectedCount = SelectedItems.Count;
        if (!DrivesSharedPreview)
            return;
        var single = SelectedItems.Count == 1 ? SelectedItems[0].Model : null;
        _ = _preview.LoadAsync(single);
    }

    /// <summary>Shell 切换活动窗格时，把本会话当前单选（或空选集）同步到共享预览。</summary>
    internal Task PublishSelectionToPreviewAsync()
    {
        if (!DrivesSharedPreview)
            return Task.CompletedTask;
        var single = SelectedItems.Count == 1 ? SelectedItems[0].Model : null;
        return _preview.LoadAsync(single);
    }

    [RelayCommand]
    private void EnterSelectionMode() => IsSelectionMode = true;

    [RelayCommand]
    private void ExitSelectionMode()
    {
        IsSelectionMode = false;
        foreach (var item in Items) item.IsChecked = false;
    }

    public void ToggleChecked(FileItemViewModel item)
    {
        item.IsChecked = !item.IsChecked;
        if (item.IsChecked && !SelectedItems.Contains(item)) SelectedItems.Add(item);
        else if (!item.IsChecked) SelectedItems.Remove(item);
    }

    [RelayCommand]
    private void SelectAll()
    {
        // Ctrl+A 的语义仍是“当前筛选结果全部”，不能因分页只选已物化的前几页。
        LoadAllVisibleItems();
        SelectedItems.Clear();
        foreach (var item in Items)
        {
            item.IsChecked = IsSelectionMode;
            SelectedItems.Add(item);
        }
    }

    // ---------- 排序 / 过滤 ----------
    public SortField CurrentSortField => _currentSortField;
    public SortDirection CurrentSortDirection => _currentSortDirection;

    public void SetSort(SortField field, SortDirection direction)
    {
        _currentSortField = field;
        _currentSortDirection = direction;
        if (!PersistSharedViewPreferences)
        {
            _settingsStore.Settings.DualPaneSortField = field.ToString();
            _settingsStore.Settings.DualPaneSortDirection = direction.ToString();
            SortFieldLabel = SortFieldText(field);
            RaiseSortGlyphs();
            _ = SaveSettingsAsync();
            RefilterAndSort();
            return;
        }
        if (IsFolderViewMemoryEnabled)
        {
            CaptureCurrentFolderViewPreferences();
        }
        else if (!IsSearchMode)
        {
            _settingsStore.Settings.SortField = field.ToString();
            _settingsStore.Settings.SortDirection = direction.ToString();
        }
        SortFieldLabel = SortFieldText(field);
        RaiseSortGlyphs();
        _ = SaveSettingsAsync();
        RefilterAndSort();
    }

    /// <summary>详细信息视图点列头：同字段翻转方向、新字段升序；最终仍经 SetSort → RefilterAndSort 管线，
    /// 保证自然序与「文件夹在前」不被控件自排覆盖（视图层把 DataGrid 的 Sorting 事件标记 Handled 后调用至此）。</summary>
    public void ApplyColumnSort(SortField field)
    {
        var direction = CurrentSortField == field && CurrentSortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;
        SetSort(field, direction);
    }

    // 详细信息列头方向指示（控件默认箭头随其自排机制走，我们自己管排序故自绘）
    public string NameSortGlyph => SortGlyph(SortField.Name);
    public string SizeSortGlyph => SortGlyph(SortField.Size);
    public string KindSortGlyph => SortGlyph(SortField.Kind);
    public string ModifiedSortGlyph => SortGlyph(SortField.LastWriteTime);

    private string SortGlyph(SortField field) =>
        CurrentSortField != field ? "" : CurrentSortDirection == SortDirection.Ascending ? "↑" : "↓";

    private void RaiseSortGlyphs()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(KindSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
    }

    public void RefilterAndSort()
    {
        IEnumerable<FileItem> filtered = _allItems;
        if (!IsSearchMode && !string.IsNullOrWhiteSpace(FilterText))
        {
            var query = FilterText.Trim();
            filtered = filtered.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var sorted = FileItemOrdering.Sort(filtered, CurrentSortField, CurrentSortDirection, _settingsStore.Settings.FoldersFirst)
            .ToList();
        var previousSelection = SelectedItems.Select(s => s.FullPath).ToHashSet();
        ResetVisibleItems(sorted, previousSelection);
        IsEmpty = TotalItemCount == 0 && LoadError is null && !IsLoading;
        OnPropertyChanged(nameof(SearchPanelStatusText));
    }

    private void ClearVisibleItems()
    {
        _pagingGeneration++;
        IsLoadingMore = false;
        _filteredItems = [];
        _visibleItemCount = 0;
        TotalItemCount = 0;
        Items.Clear();
        RaisePagingState();
    }

    private void ResetVisibleItems(IReadOnlyList<FileItem> source, IReadOnlySet<string> previousSelection)
    {
        _pagingGeneration++;
        IsLoadingMore = false;
        // 上一轮的补齐任务靠代次自行退出；这里只把它的可见状态收回来。
        IsBackgroundPaging = false;
        _backgroundFillTask = null;
        _filteredItems = source.ToList();
        TotalItemCount = _filteredItems.Count;
        _visibleItemCount = 0;
        Items.Clear();
        SelectedItems.Clear();

        var initialCount = Math.Min(PageSize, _filteredItems.Count);
        if (previousSelection.Count > 0)
        {
            var lastSelectedIndex = -1;
            for (var i = 0; i < _filteredItems.Count; i++)
            {
                if (previousSelection.Contains(_filteredItems[i].FullPath))
                    lastSelectedIndex = i;
            }
            if (lastSelectedIndex >= initialCount)
            {
                initialCount = Math.Min(
                    _filteredItems.Count,
                    (lastSelectedIndex / PageSize + 1) * PageSize);
            }
        }

        var added = AppendVisibleItems(initialCount, previousSelection);
        foreach (var vm in added.Where(vm => previousSelection.Contains(vm.FullPath)))
            SelectedItems.Add(vm);
        RaisePagingState();
    }

    private List<FileItemViewModel> AppendVisibleItems(
        int count, IReadOnlySet<string>? selectedPaths = null)
    {
        var target = (int)Math.Min(
            (long)_filteredItems.Count,
            (long)_visibleItemCount + Math.Max(0, count));
        var added = new List<FileItemViewModel>(Math.Max(0, target - _visibleItemCount));
        while (_visibleItemCount < target)
        {
            var item = _filteredItems[_visibleItemCount++];
            var vm = new FileItemViewModel(item)
            {
                IsFavorite = _settingsStore.IsFavorite(item.FullPath),
                IsChecked = selectedPaths?.Contains(item.FullPath) == true && IsSelectionMode,
            };
            Items.Add(vm);
            added.Add(vm);
        }
        RaisePagingState();
        return added;
    }

    /// <summary>
    /// 用户滚到底部时调用（v0.33）：先补齐屏幕可见范围，再把剩余条目在后台一小批一小批地物化完。
    /// 与页脚“加载更多”不同，这条路径代表“我已经到底了”，所以它不会让用户再滚一次。
    /// </summary>
    public void BeginBackgroundFill()
    {
        if (!HasMoreItems || IsLoading || IsLoadingMore || IsBackgroundPaging)
            return;
        IsBackgroundPaging = true;
        _backgroundFillTask = FillRemainingAsync(_pagingGeneration);
    }

    /// <summary>等待后台补齐结束（无补齐任务时立即完成）；单测与截图用例的确定性入口。</summary>
    public Task WhenPagingIdleAsync() => _backgroundFillTask ?? Task.CompletedTask;

    /// <summary>后台补齐每批物化的行数：够小才不会让一次布局超过一帧，够大才不会退化成逐行抖动。</summary>
    public const int BackgroundFillBatchSize = 24;

    private async Task FillRemainingAsync(int generation)
    {
        try
        {
            while (generation == _pagingGeneration && HasMoreItems)
            {
                var added = AppendVisibleItems(BackgroundFillBatchSize);
                StartThumbnailsFor(added);
                UpdatePagingStatusText();
                await YieldToUiAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            // 导航/筛选/刷新已经把列表换掉时代次不同，旗标由 ResetVisibleItems 收尾。
            if (generation == _pagingGeneration)
            {
                IsBackgroundPaging = false;
                UpdatePagingStatusText();
            }
        }
    }

    /// <summary>
    /// 视图报告“这一屏大概放得下多少行”时调用：保证可见范围先有内容。
    /// 只增不减——缩小窗口不会把已经物化的行撤掉，否则选集、滚动位置与缩略图都会抖。
    /// </summary>
    public void EnsureVisibleRange(int visibleCapacity)
    {
        if (visibleCapacity <= 0 || !HasMoreItems)
            return;
        const int overScan = 8;
        var required = Math.Min(_filteredItems.Count, visibleCapacity + overScan);
        if (required <= _visibleItemCount)
            return;
        var added = AppendVisibleItems(required - _visibleItemCount);
        StartThumbnailsFor(added);
        UpdatePagingStatusText();
    }

    /// <summary>滚动接近底部或点击页脚时追加下一页；分小块让出 UI 线程，避免大目录一次物化卡住。</summary>
    [RelayCommand]
    private async Task LoadMoreItemsAsync()
    {
        if (!CanLoadMoreItems)
            return;

        var generation = _pagingGeneration;
        var target = Math.Min(_filteredItems.Count, _visibleItemCount + PageSize);
        var added = new List<FileItemViewModel>(target - _visibleItemCount);
        IsLoadingMore = true;
        try
        {
            while (_visibleItemCount < target && generation == _pagingGeneration)
            {
                added.AddRange(AppendVisibleItems(Math.Min(24, target - _visibleItemCount)));
                if (_visibleItemCount < target)
                    await YieldToUiAsync().ConfigureAwait(true);
            }

            if (generation == _pagingGeneration)
            {
                StartThumbnailsFor(added);
                UpdatePagingStatusText();
            }
        }
        finally
        {
            if (generation == _pagingGeneration)
                IsLoadingMore = false;
        }
    }

    private static async Task YieldToUiAsync()
    {
        try
        {
            // 无 Application 的普通单元测试没有 Avalonia 消息泵；尤其 Windows 上
            // Dispatcher.UIThread 可能绑定到当前测试线程，若等待 InvokeAsync 会永久挂起。
            if (global::Avalonia.Application.Current is not null && Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // 未启动 Avalonia Application：下面按“没有消息泵”处理。
        }

        // 没有 UI 线程时不能把批次丢到线程池：分页会跨线程改 ObservableCollection，
        // 单测也就失去确定性。此时连续执行，批次边界照旧保留给将来接别的调度器。
    }

    private void LoadAllVisibleItems()
    {
        if (!HasMoreItems)
            return;
        var added = AppendVisibleItems(int.MaxValue);
        StartThumbnailsFor(added);
        UpdatePagingStatusText();
    }

    /// <summary>地址/搜索/收藏定位到尚未物化的文件时，同步展开到包含该项的页。</summary>
    public void RevealItemByPath(string path) => RevealItemsByPaths([path]);

    public void RevealItemsByPaths(IEnumerable<string> paths)
    {
        var targets = paths.Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0)
            return;

        var required = _visibleItemCount;
        for (var i = 0; i < _filteredItems.Count; i++)
        {
            if (targets.Any(path => TransferPathsEqual(_filteredItems[i].FullPath, path)))
                required = Math.Max(required, i + 1);
        }

        if (required <= _visibleItemCount)
            return;
        var added = AppendVisibleItems(required - _visibleItemCount);
        StartThumbnailsFor(added);
        UpdatePagingStatusText();
    }

    private void UpdatePagingStatusText()
    {
        if (IsSearchMode)
        {
            StatusText = HasMoreItems
                ? $"搜索到 {TotalItemCount:N0} 项 · 已显示 {VisibleItemCount:N0} 项"
                : $"搜索到 {TotalItemCount:N0} 项";
            return;
        }
        StatusText = HasMoreItems
            ? PagingStatusText
            : $"{TotalItemCount:N0} 个项目";
    }

    // ---------- 搜索（递归） ----------
    public async Task SearchRecursiveAsync(string query, IReadOnlySet<FileKind>? kinds = null, string? rootPath = null)
    {
        var root = rootPath ?? CurrentPath;
        if (root is null) return;
        if (FileLocator.IsStorageUri(root))
        {
            _dialogs.Notify("授权文件夹内暂不支持递归搜索，可用列表内筛选按名称过滤");
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        DisarmFolderWatch();
        IsSearchMode = true;
        ExitSelectionMode();
        _allItems = [];
        ClearVisibleItems();
        SelectedItems.Clear();
        LoadError = null;
        IsLoading = true;
        StatusText = "正在搜索…";
        var found = 0;
        var batch = new List<FileItem>();
        _idleTask = Task.CompletedTask;
        try
        {
            var spec = new SearchSpec(
                query, root, kinds, _settingsStore.Settings.ShowHidden,
                MaxResults: Math.Clamp(_settingsStore.Settings.SearchResultLimit, 200, 20000));
            await foreach (var hit in _search.SearchByNameAsync(spec, ct).ConfigureAwait(true))
            {
                batch.Add(hit);
                found++;
                if (batch.Count >= 50)
                {
                    FlushBatch(batch);
                    batch.Clear();
                }
                StatusText = $"已找到 {found:N0} 项…";
            }
            FlushBatch(batch);
            _allItems = _filteredItems.ToList();
            StatusText = HasMoreItems
                ? $"搜索到 {found:N0} 项 · 已显示 {VisibleItemCount:N0} 项"
                : $"搜索到 {found:N0} 项";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"搜索已取消 · {found:N0} 项";
        }
        finally
        {
            IsLoading = false;
            IsEmpty = TotalItemCount == 0;
            StartThumbnails();
        }
    }

    private void FlushBatch(List<FileItem> batch)
    {
        if (batch.Count == 0)
            return;
        _allItems.AddRange(batch);
        _filteredItems.AddRange(batch);
        TotalItemCount = _filteredItems.Count;
        if (_visibleItemCount < PageSize)
        {
            AppendVisibleItems(Math.Min(
                PageSize - _visibleItemCount,
                _filteredItems.Count - _visibleItemCount));
        }
        RaisePagingState();
    }

    [RelayCommand]
    private async Task ExitSearchAsync()
    {
        IsSearchPanelOpen = false;
        IsSearchMode = false;
        FilterText = string.Empty;
        if (CurrentPath is not null)
            await LoadFolderAsync(CurrentPath).ConfigureAwait(true);
    }

    public void CancelSearch() => _loadCts?.Cancel();

    // ---------- 写操作 ----------
    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (CurrentPath is null) return;
        var name = await _dialogs.PromptAsync("新建文件夹", "新建文件夹", "创建", ValidateNewName).ConfigureAwait(true);
        if (name is null) return;
        await RunSimpleAsync("正在创建…", async () =>
        {
            await _fileSystem.CreateFolderAsync(CurrentPath, name.Trim(), CancellationToken.None).ConfigureAwait(true);
            _dialogs.Notify($"已创建 {name.Trim()}");
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (CurrentPath is null) return;
        var name = await _dialogs.PromptAsync("新建文件", "新建文本文件.txt", "创建", ValidateNewName).ConfigureAwait(true);
        if (name is null) return;
        await RunSimpleAsync("正在创建…", async () =>
        {
            await _fileSystem.CreateFileAsync(CurrentPath, name.Trim(), CancellationToken.None).ConfigureAwait(true);
            _dialogs.Notify($"已创建 {name.Trim()}");
        }).ConfigureAwait(true);
    }

    private string? ValidateNewName(string value)
    {
        var error = NameValidator.Validate(value);
        if (error is not null) return error;
        if (CurrentPath is not null && _fileSystem.Exists(Path.Combine(CurrentPath, value.Trim())))
            return "已存在同名项目";
        return null;
    }

    [RelayCommand]
    private async Task RenameAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null) return;
        var name = await _dialogs.PromptAsync("重命名", item.Name, "重命名", value =>
        {
            var error = NameValidator.Validate(value);
            if (error is not null) return error;
            var dest = Path.Combine(Path.GetDirectoryName(item.FullPath)!, value.Trim());
            if (!PathsEqual(dest, item.FullPath) && _fileSystem.Exists(dest)) return "已存在同名项目";
            return null;
        }).ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), item.Name, StringComparison.Ordinal)) return;
        await RunSimpleAsync("正在重命名…", async () =>
        {
            await _fileSystem.RenameAsync(item.FullPath, name.Trim(), CancellationToken.None).ConfigureAwait(true);
            _dialogs.Notify("已重命名");
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopySelectionAsync() => await SetClipboardAsync(cut: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task CutSelectionAsync() => await SetClipboardAsync(cut: true).ConfigureAwait(true);

    private async Task SetClipboardAsync(bool cut)
    {
        if (SelectedItems.Count == 0) return;
        var paths = SelectedItems.Select(i => i.FullPath).ToList();

        // 先记住操作前系统剪贴板的文件集合：粘贴/窗口激活时据此判断外部内容是否
        // 在本应用复制/剪切之后又被另一个应用更新，避免旧外部内容覆盖应用内剪切状态。
        var externalBaseline = await _systemClipboard.TryGetFilesAsync(CancellationToken.None).ConfigureAwait(true);
        _clipboard.SetApplicationClipboard(paths, cut, externalBaseline);

        if (!cut)
        {
            // Avalonia 高层文件格式没有跨应用的统一“剪切效果”编码：复制与系统同步，
            // 剪切保留在应用内，避免把用户预期为移动的动作在外部悄悄变成复制。
            await _systemClipboard.SetFilesAsync(paths, CancellationToken.None).ConfigureAwait(true);
            _clipboard.SetExternalBaseline(paths.Where(p => !FileLocator.IsStorageUri(p)).ToList());
        }

        _dialogs.Notify(cut ? $"已剪切 {paths.Count} 项" : $"已复制 {paths.Count} 项");
    }

    /// <summary>视图挂载/窗口激活时导入其他应用新写入的文件剪贴板。</summary>
    public async Task RefreshExternalClipboardAsync() =>
        await ImportExternalClipboardAsync().ConfigureAwait(true);

    private async Task ImportExternalClipboardAsync()
    {
        var external = await _systemClipboard.TryGetFilesAsync(CancellationToken.None).ConfigureAwait(true);
        if (external.Count == 0)
            return;
        if (_clipboard.IsOwnedByApp && PathSetsEqual(external, _clipboard.ExternalBaseline))
            return;
        if (PathSetsEqual(external, _clipboard.Paths))
        {
            _clipboard.SetExternalBaseline(external);
            return;
        }

        _clipboard.SetExternalClipboard(external);
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (CurrentPath is null) return;
        await ImportExternalClipboardAsync().ConfigureAwait(true);
        if (_clipboard.Paths.Count == 0) return;

        var sources = _clipboard.Paths.ToList();
        var cut = _clipboard.IsCut;
        var outcome = await TransferItemsAsync(sources, CurrentPath, cut).ConfigureAwait(true);
        if (cut && outcome?.Success == true)
            _clipboard.Clear();
    }

    private sealed class TransferPlanItem(string sourcePath, FileItem sourceItem)
    {
        public string SourcePath { get; } = sourcePath;
        public FileItem SourceItem { get; } = sourceItem;
        public string DisplayName => SourceItem.Name;
        public ConflictPolicy Policy { get; set; } = ConflictPolicy.KeepBoth;
    }

    private sealed record TransferExecutionResult(
        OperationOutcome Outcome,
        IReadOnlyList<TransferPlanItem> Plan);

    /// <summary>
    /// 通用文件传输：粘贴与拖放共用。含同源/后代目标防护、逐项冲突确认、任务中心进度与必要时刷新。
    /// 全程使用文件系统服务的名称/父链语义，本地路径与 storage URI 均不做 Path.* 拼接。
    /// </summary>
    public async Task<OperationOutcome?> TransferItemsAsync(
        IReadOnlyList<string> sourcePaths, string destinationFolder, bool isMove)
    {
        var sources = sourcePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(destinationFolder))
            return null;
        if (!CanTransferItems(sources, destinationFolder))
        {
            _dialogs.Notify("目标文件夹不能是所选文件夹本身或其子文件夹");
            return null;
        }

        var outcome = await RunTransferTaskAsync(
            sources, destinationFolder, isMove, predeterminedPolicies: null, isRetry: false).ConfigureAwait(true);
        await ReloadIfTransferAffectsCurrentAsync(sources, destinationFolder).ConfigureAwait(true);
        return outcome;
    }

    private async Task<OperationOutcome> RunTransferTaskAsync(
        IReadOnlyList<string> sources,
        string destinationFolder,
        bool isMove,
        IReadOnlyDictionary<string, ConflictPolicy>? predeterminedPolicies,
        bool isRetry)
    {
        IReadOnlyList<TransferPlanItem> executedPlan = [];
        var title = (isMove, isRetry) switch
        {
            (true, true) => "正在重试移动失败项…",
            (false, true) => "正在重试复制失败项…",
            (true, false) => "正在移动…",
            _ => "正在复制…",
        };

        return await _dialogs.RunOperationAsync(
            title,
            async (progress, ct) =>
            {
                var result = await ExecuteTransferAsync(
                    sources, destinationFolder, isMove, predeterminedPolicies, progress, ct).ConfigureAwait(true);
                executedPlan = result.Plan;
                return result.Outcome;
            },
            previous => RetryFailedTransferAsync(executedPlan, destinationFolder, isMove, previous)).ConfigureAwait(true);
    }

    private async Task<TransferExecutionResult> ExecuteTransferAsync(
        IReadOnlyList<string> sources,
        string destinationFolder,
        bool isMove,
        IReadOnlyDictionary<string, ConflictPolicy>? predeterminedPolicies,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, "正在检查目标…", 0, sources.Count));

        var sourceItems = new List<FileItem>(sources.Count);
        foreach (var source in sources)
            sourceItems.Add(await GetTransferSourceItemAsync(source).ConfigureAwait(true));

        var destinationItems = await GetDestinationItemsAsync(destinationFolder).ConfigureAwait(true);
        var promptTotal = CountTransferConflictPrompts(
            sources, sourceItems, destinationItems, destinationFolder, isMove, predeterminedPolicies);

        var plannedTargets = new Dictionary<string, FileItem>(destinationItems, StringComparer.OrdinalIgnoreCase);
        var plan = new List<TransferPlanItem>(sources.Count);
        ConflictPolicy? blanketPolicy = null;
        var promptIndex = 0;

        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[i];
            var sourceItem = sourceItems[i];
            var itemPlan = new TransferPlanItem(source, sourceItem);
            var predeterminedPolicy = ConflictPolicy.KeepBoth;
            var hasPredeterminedPolicy = predeterminedPolicies is not null &&
                                         predeterminedPolicies.TryGetValue(source, out predeterminedPolicy);
            if (hasPredeterminedPolicy)
                itemPlan.Policy = predeterminedPolicy;

            var hasPlannedTarget = plannedTargets.TryGetValue(sourceItem.Name, out var plannedTarget);
            var isSelfTransfer = hasPlannedTarget && IsSelfTransfer(source, plannedTarget!, destinationFolder, isMove);
            if (hasPlannedTarget && !isSelfTransfer)
            {
                if (!hasPredeterminedPolicy)
                {
                    if (blanketPolicy is { } blanket)
                    {
                        itemPlan.Policy = blanket;
                    }
                    else
                    {
                        progress.Report(new OperationProgress(
                            sources.Count == 0 ? 0 : (double)i / sources.Count,
                            $"正在比对 {sourceItem.Name}",
                            i,
                            sources.Count));
                        var targetHeading = destinationItems.Values.Any(item =>
                            TransferPathsEqual(item.FullPath, plannedTarget!.FullPath))
                            ? "目标已有"
                            : "本批先写入";
                        using var sourceSnapshot = await CreateConflictSnapshotAsync(
                            sourceItem, "来源", cancellationToken).ConfigureAwait(true);
                        using var targetSnapshot = await CreateConflictSnapshotAsync(
                            plannedTarget!, targetHeading, cancellationToken).ConfigureAwait(true);
                        var decision = await _dialogs.AskConflictAsync(new TransferConflictPrompt(
                            sourceSnapshot,
                            targetSnapshot,
                            promptIndex,
                            promptTotal,
                            isMove)).ConfigureAwait(true);
                        promptIndex++;
                        if (decision is null)
                        {
                            return new TransferExecutionResult(
                                new OperationOutcome(0, [], [], Cancelled: true),
                                plan);
                        }

                        itemPlan.Policy = decision.Policy;
                        if (decision.ApplyToRemaining)
                            blanketPolicy = decision.Policy;
                    }
                }

                if (itemPlan.Policy == ConflictPolicy.Overwrite)
                    plannedTargets[sourceItem.Name] = sourceItem;
            }
            else if (!hasPlannedTarget)
            {
                plannedTargets[sourceItem.Name] = sourceItem;
            }

            plan.Add(itemPlan);
        }

        var errors = new List<OperationError>();
        var produced = new List<string>();
        var succeeded = 0;
        var skipped = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new TransferExecutionResult(
                    new OperationOutcome(succeeded, errors, produced, Cancelled: true, SkippedCount: skipped),
                    plan);
            }

            var itemPlan = plan[i];
            var itemOutcome = isMove
                ? await _operations.MoveAsync(
                    [itemPlan.SourcePath], destinationFolder, itemPlan.Policy, null, cancellationToken).ConfigureAwait(true)
                : await _operations.CopyAsync(
                    [itemPlan.SourcePath], destinationFolder, itemPlan.Policy, null, cancellationToken).ConfigureAwait(true);

            errors.AddRange(itemOutcome.Errors.Select(error =>
                error.ItemPath is null ? error with { ItemPath = itemPlan.SourcePath } : error));
            produced.AddRange(itemOutcome.ProducedPaths);
            if (itemOutcome.Cancelled)
            {
                return new TransferExecutionResult(
                    new OperationOutcome(succeeded, errors, produced, Cancelled: true, SkippedCount: skipped),
                    plan);
            }
            if (itemOutcome.Errors.Count == 0)
            {
                if (itemOutcome.ProducedPaths.Count > 0)
                    succeeded += itemOutcome.ProducedPaths.Count;
                else
                    skipped++;
            }

            progress.Report(new OperationProgress(
                (double)(i + 1) / Math.Max(1, plan.Count),
                itemPlan.DisplayName,
                i + 1,
                plan.Count));
        }

        return new TransferExecutionResult(
            new OperationOutcome(succeeded, errors, produced, SkippedCount: skipped),
            plan);
    }

    private int CountTransferConflictPrompts(
        IReadOnlyList<string> sources,
        IReadOnlyList<FileItem> sourceItems,
        IReadOnlyDictionary<string, FileItem> destinationItems,
        string destinationFolder,
        bool isMove,
        IReadOnlyDictionary<string, ConflictPolicy>? predeterminedPolicies)
    {
        var occupied = new Dictionary<string, FileItem>(destinationItems, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            var name = sourceItems[i].Name;
            if (occupied.TryGetValue(name, out var target))
            {
                if (!IsSelfTransfer(sources[i], target, destinationFolder, isMove) &&
                    !(predeterminedPolicies?.ContainsKey(sources[i]) ?? false))
                    count++;
            }
            else
            {
                occupied[name] = sourceItems[i];
            }
        }
        return count;
    }

    private bool IsSelfTransfer(string source, FileItem plannedTarget, string destinationFolder, bool isMove)
    {
        if (!TransferPathsEqual(_fileSystem.GetParentPath(source), destinationFolder))
            return false;
        // 同夹移动是无操作；同夹复制的目标就是来源本身，服务层会安全地保留两者。
        return isMove || TransferPathsEqual(source, plannedTarget.FullPath);
    }

    private async Task RetryFailedTransferAsync(
        IReadOnlyList<TransferPlanItem> executedPlan,
        string destinationFolder,
        bool isMove,
        OperationOutcome previous)
    {
        var failedPaths = previous.Errors
            .Select(error => error.ItemPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        IEnumerable<TransferPlanItem> failed;
        if (failedPaths.Count > 0)
        {
            failed = executedPlan.Where(item =>
                failedPaths.Any(path => TransferPathsEqual(path, item.SourcePath)));
        }
        else
        {
            // 旧服务或聚合错误没有定位串时，只在名称能唯一映射时重试，禁止整批重跑。
            failed = previous.Errors.SelectMany(error =>
            {
                var matches = executedPlan
                    .Where(item => string.Equals(item.DisplayName, error.ItemName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return matches.Count == 1 ? matches : [];
            });
        }

        var retryPlan = failed
            .DistinctBy(item => item.SourcePath, StringComparer.Ordinal)
            .Where(item => _fileSystem.Exists(item.SourcePath))
            .ToList();
        if (retryPlan.Count == 0)
        {
            _dialogs.Notify("失败项的来源已不存在或无法精确对应，无法重试");
            return;
        }

        var policies = retryPlan.ToDictionary(item => item.SourcePath, item => item.Policy, StringComparer.Ordinal);
        var sources = retryPlan.Select(item => item.SourcePath).ToList();
        _ = await RunTransferTaskAsync(
            sources, destinationFolder, isMove, policies, isRetry: true).ConfigureAwait(true);
        await ReloadIfTransferAffectsCurrentAsync(sources, destinationFolder).ConfigureAwait(true);
    }

    private async Task ReloadIfTransferAffectsCurrentAsync(
        IReadOnlyList<string> sources, string destinationFolder)
    {
        var affectsCurrentFolder = TransferPathsEqual(destinationFolder, CurrentPath)
            || sources.Any(s => TransferPathsEqual(_fileSystem.GetParentPath(s), CurrentPath));
        if (affectsCurrentFolder)
            await ReloadAfterOperationAsync().ConfigureAwait(true);
    }

    private async Task<ConflictItemSnapshot> CreateConflictSnapshotAsync(
        FileItem item, string heading, CancellationToken cancellationToken)
    {
        string? sha256 = null;
        Bitmap? thumbnail = null;
        if (!item.IsFolder && item.Length is >= 0 and <= 64L * 1024 * 1024)
        {
            try { sha256 = await ComputeTransferSha256Async(item.FullPath, cancellationToken).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
        }
        if (item.Kind == FileKind.Image && item.Length is >= 0 and <= 32L * 1024 * 1024)
        {
            try { thumbnail = await LoadConflictThumbnailAsync(item.FullPath, cancellationToken).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
        }
        return new ConflictItemSnapshot(heading, item, GetConflictLocationText(item.FullPath), sha256, thumbnail);
    }

    private async Task<string> ComputeTransferSha256Async(string path, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = await _fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(true);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(true)) > 0)
            hasher.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private async Task<Bitmap> LoadConflictThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await _fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(true);
        return await Task.Run(() => Bitmap.DecodeToWidth(stream, 320), cancellationToken).ConfigureAwait(true);
    }

    private string GetConflictLocationText(string path)
    {
        var parent = _fileSystem.GetParentPath(path);
        if (parent is null)
            return FileLocator.IsStorageUri(path) ? "授权文件夹" : path;
        if (!FileLocator.IsStorageUri(parent))
            return parent;
        return _fileSystem.GetBreadcrumbs(parent).LastOrDefault()?.Name ?? "授权文件夹";
    }

    /// <summary>拖放 DragOver 阶段的目标合法性预判（禁止落入来源自身或其子文件夹）。</summary>
    public bool CanTransferItems(IReadOnlyList<string> sourcePaths, string destinationFolder) =>
        sourcePaths.All(source => !FileTransferRules.IsSameOrDescendant(
            destinationFolder, source, _fileSystem.GetParentPath, static (a, b) => TransferPathsEqual(a, b)));

    private async Task<FileItem> GetTransferSourceItemAsync(string source)
    {
        var known = _allItems.FirstOrDefault(i => TransferPathsEqual(i.FullPath, source));
        if (known is not null)
            return known;
        if (await _fileSystem.StatAsync(source, CancellationToken.None).ConfigureAwait(true) is { } stat)
            return stat;

        var name = await GetTransferDisplayNameAsync(source).ConfigureAwait(true);
        var isFolder = !FileLocator.IsStorageUri(source) && Directory.Exists(source);
        long? length = null;
        var modified = DateTimeOffset.UnixEpoch;
        if (!FileLocator.IsStorageUri(source))
        {
            try
            {
                if (isFolder)
                    modified = Directory.GetLastWriteTime(source);
                else if (File.Exists(source))
                {
                    var info = new FileInfo(source);
                    length = info.Length;
                    modified = info.LastWriteTime;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new FileItem(
            FileLocator.IsStorageUri(source) ? FileLocator.FromStorage(source) : FileLocator.FromPath(source),
            name,
            isFolder ? FileKind.Folder : FileKindClassifier.Classify(name),
            length,
            modified,
            IsHidden: false,
            IsSymlink: false,
            Path.GetExtension(name));
    }

    private async Task<string> GetTransferDisplayNameAsync(string source)
    {
        var known = _allItems.FirstOrDefault(i => TransferPathsEqual(i.FullPath, source));
        if (known is not null)
            return known.Name;
        if (!FileLocator.IsStorageUri(source))
        {
            var localName = Path.GetFileName(source.TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(localName) ? source : localName;
        }
        var stat = await _fileSystem.StatAsync(source, CancellationToken.None).ConfigureAwait(true);
        return stat?.Name ?? source;
    }

    private async Task<Dictionary<string, FileItem>> GetDestinationItemsAsync(string destinationFolder)
    {
        var items = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var item in _fileSystem.ListAsync(destinationFolder, includeHidden: true, CancellationToken.None).ConfigureAwait(true))
                items[item.Name] = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            // 冲突预检尽力而为；真正的打开/写入错误由操作管线汇总报告。
        }
        return items;
    }

    internal static bool PathSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.All(a => right.Any(b => TransferPathsEqual(a, b)))
        && right.All(b => left.Any(a => TransferPathsEqual(a, b)));

    internal static bool TransferPathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (FileLocator.IsStorageUri(a) || FileLocator.IsStorageUri(b))
            return string.Equals(a, b, StringComparison.Ordinal);
        return PathsEqual(a, b);
    }

    [RelayCommand]
    private async Task DeleteSelectionAsync()
    {
        if (SelectedItems.Count == 0) return;
        var items = SelectedItems.ToList();
        var canTrash = _trash.CanMoveToTrash;
        var isAppTrash = _trash is AppTrashService;
        var trashName = isAppTrash ? "应用内回收站" : "回收站";
        var title = canTrash ? $"移入{trashName}？" : "永久删除？";
        var message = canTrash
            ? $"将 {items.Count} 个项目移入{trashName}，之后可从{trashName}恢复。"
            : $"将永久删除 {items.Count} 个项目，此操作无法撤销。";
        // 移入回收站可按设置跳过确认；永久删除没有恢复路径，始终要求确认。
        if ((!canTrash || RequiresTrashConfirmation) &&
            !await _dialogs.ConfirmAsync(title, message, canTrash ? $"移入{trashName}" : "永久删除", destructive: true).ConfigureAwait(true))
            return;

        if (canTrash)
        {
            var outcome = await _dialogs.RunOperationAsync($"正在移入{trashName}…", async (progress, ct) =>
            {
                var errors = new List<OperationError>();
                var done = 0;
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await _trash.MoveToTrashAsync(item.FullPath, ct).ConfigureAwait(true); done++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
                    {
                        errors.Add(new OperationError(item.Name, ex.Message));
                    }
                    progress.Report(new OperationProgress((done + errors.Count) / (double)Math.Max(1, items.Count), item.Name, done + errors.Count, items.Count));
                }
                return new OperationOutcome(done, errors, []);
            }).ConfigureAwait(true);
            _ = outcome;
        }
        else
        {
            await _dialogs.RunOperationAsync("正在删除…", (progress, ct) =>
                _operations.DeletePermanentlyAsync(items.Select(i => i.FullPath).ToList(), progress, ct)).ConfigureAwait(true);
        }
        await ReloadAfterOperationAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CompressSelectionAsync()
    {
        if (SelectedItems.Count == 0 || CurrentPath is null) return;
        if (FileLocator.IsStorageUri(CurrentPath))
        {
            _dialogs.Notify("授权文件夹内暂不支持压缩");
            return;
        }
        var first = SelectedItems[0];
        var defaultName = (SelectedItems.Count == 1 ? Path.GetFileNameWithoutExtension(first.Name) : CurrentFolderName) + ".zip";
        var dest = Path.Combine(CurrentPath, defaultName);
        var i = 2;
        while (_fileSystem.Exists(dest))
            dest = Path.Combine(CurrentPath, PathText.KeepBothName(defaultName, i++));
        var sources = SelectedItems.Select(s => s.FullPath).ToList();
        await _dialogs.RunOperationAsync("正在压缩…", (progress, ct) =>
            _archive.CompressToZipAsync(sources, dest, progress, ct)).ConfigureAwait(true);
        await ReloadAfterOperationAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenDocumentWorkbenchAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null || item.IsFolder) return;
        if (!DocumentWorkbenchViewModel.CanOpen(item.Model))
        {
            _dialogs.Notify(DocumentWorkbenchViewModel.IsSupportedText(item.Model) &&
                            FileLocator.IsStorageUri(item.FullPath)
                ? "授权文件夹中的文本暂仅支持只读预览"
                : "此类型暂不支持文档工作台");
            return;
        }
        await RecordRecentFileAsync(item.FullPath).ConfigureAwait(true);
        DocumentWorkbenchRequested?.Invoke(this, item.Model);
    }

    [RelayCommand]
    private void OpenArchiveWorkbench(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null || item.IsFolder) return;
        if (FileLocator.IsStorageUri(item.FullPath))
        {
            _dialogs.Notify("授权文件夹中的压缩包暂不支持工作台");
            return;
        }
        if (!_archive.CanOpen(item.FullPath))
        {
            _dialogs.Notify("当前仅支持 ZIP、JAR、APK 压缩包");
            return;
        }

        ArchiveWorkbenchRequested?.Invoke(this, item.FullPath);
    }

    [RelayCommand]
    private async Task ExtractSelectionAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null || CurrentPath is null) return;
        if (FileLocator.IsStorageUri(item.FullPath) || FileLocator.IsStorageUri(CurrentPath))
        {
            _dialogs.Notify("授权文件夹内暂不支持解压");
            return;
        }
        var destDir = Path.Combine(CurrentPath, Path.GetFileNameWithoutExtension(item.Name));
        // 快捷整包解压也改走显式选项：保留结构、同名两者保留，不再静默覆盖。
        var options = new ArchiveExtractOptions(
            EntryFullNames: null,
            PreserveStructure: true,
            ConflictPolicy: ConflictPolicy.KeepBoth);
        await _dialogs.RunOperationAsync("正在解压…", (progress, ct) =>
            _archive.ExtractEntriesAsync(item.FullPath, destDir, options, progress, ct)).ConfigureAwait(true);
        await ReloadAfterOperationAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null) return;
        var now = !_settingsStore.IsFavorite(item.FullPath);
        await _settingsStore.SetFavoriteAsync(item.FullPath, now, CancellationToken.None).ConfigureAwait(true);
        item.IsFavorite = now;
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        _dialogs.Notify(now ? "已加入收藏" : "已取消收藏");
    }

    public event EventHandler? FavoritesChanged;

    [RelayCommand]
    private async Task ShowPropertiesAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null) return;
        var m = item.Model;
        var body =
            $"名称：{m.Name}\n" +
            $"类型：{FileKindClassifier.Describe(m)}\n" +
            (m.IsFolder ? "" : $"大小：{Formatting.Bytes(m.Length)}（{m.Length:N0} 字节）\n") +
            $"修改时间：{Formatting.FullDateTime(m.LastWriteTime)}\n" +
            $"路径：{m.FullPath}" +
            (m.IsSymlink ? "\n链接：符号链接" : "") +
            (m.IsHidden ? "\n可见性：隐藏" : "");
        if (m.IsFolder && !m.Locator.IsStorage)
        {
            try
            {
                var size = await _inspector.GetFolderSizeAsync(m.FullPath, CancellationToken.None).ConfigureAwait(true);
                body += $"\n大小：{Formatting.Bytes(size.TotalBytes)}（{size.FileCount:N0} 个文件、{size.FolderCount:N0} 个文件夹）"
                        + (size.IsPartial ? "（部分项目无权访问，结果为下限）" : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                body += "\n大小：（计算失败）";
            }
        }
        await _dialogs.ShowPropertiesAsync("属性", body).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ComputeChecksumsAsync(FileItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null || item.Model.IsFolder) return;
        if (item.Model.Locator.IsStorage)
        {
            _dialogs.Notify("授权文件夹内暂不支持计算校验和");
            return;
        }
        var path = item.FullPath;
        var name = item.Name;
        string? sha256 = null, md5 = null;
        var outcome = await _dialogs.RunOperationAsync("计算校验和", async (progress, ct) =>
        {
            var fraction = new Progress<double>(v =>
                progress.Report(new OperationProgress(v, name, 0, 0)));
            sha256 = await _inspector.ComputeHashAsync(path, "SHA256", fraction, ct).ConfigureAwait(true);
            md5 = await _inspector.ComputeHashAsync(path, "MD5", fraction, ct).ConfigureAwait(true);
            return new OperationOutcome(1, [], []);
        }).ConfigureAwait(true);
        if (outcome.Success && sha256 is not null)
        {
            await _dialogs.ShowPropertiesAsync("校验和",
                $"文件：{name}\n\nSHA-256：\n{sha256}\n\nMD5：\n{md5}").ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BatchRenameSelectionAsync()
    {
        if (CurrentPath is null) return;
        var targets = SelectedItems.ToList();
        if (targets.Count < 2)
        {
            _dialogs.Notify("请先选择至少 2 个项目");
            return;
        }
        var names = targets.Select(t => t.Name).ToList();
        var siblings = _allItems.Select(i => i.Name)
            .Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var proposed = await _dialogs.BatchRenameAsync(names, siblings).ConfigureAwait(true);
        if (proposed is null) return;

        var folder = CurrentPath;
        await RunSimpleAsync("批量重命名", async () =>
        {
            // 两段改名：先全部改至临时名再落终名，消除互换名/占用顺序问题
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var changed = targets
                .Select((t, i) => (t, i))
                .Where(x => !string.Equals(names[x.i], proposed[x.i], StringComparison.Ordinal))
                .ToList();
            var temps = new Dictionary<int, string>();
            foreach (var (target, i) in changed)
            {
                var temp = $".mdexplorer-rename-{stamp}-{i}";
                await _fileSystem.RenameAsync(target.FullPath, temp, CancellationToken.None).ConfigureAwait(true);
                temps[i] = Path.Combine(folder, temp);
            }
            foreach (var (_, i) in changed)
                await _fileSystem.RenameAsync(temps[i], proposed[i], CancellationToken.None).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RunSimpleAsync(string title, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ConfirmAsync("操作失败", ex.Message, "知道了").ConfigureAwait(true);
        }
        await ReloadAfterOperationAsync().ConfigureAwait(true);
    }

    private async Task ReloadAfterOperationAsync()
    {
        if (IsSearchMode)
        {
            await ExitSearchAsync().ConfigureAwait(true);
        }
        else if (CurrentPath is not null)
        {
            await LoadFolderAsync(CurrentPath).ConfigureAwait(true);
        }
    }

    // ---------- 缩略图 ----------
    /// <summary>设置切换后同步当前列表：关闭时取消解码并释放已生成的缩略图，开启时补做当前已物化行。</summary>
    public void ApplyThumbnailPreference()
    {
        if (_settingsStore.Settings.EnableThumbnails)
        {
            StartThumbnails();
            return;
        }

        _thumbCts?.Cancel();
        foreach (var item in Items)
        {
            if (item.Thumbnail is null)
                continue;
            item.Thumbnail.Dispose();
            item.Thumbnail = null;
        }
    }

    /// <summary>每页行数改大后补足当前页；改小时不移除已呈现行，避免用户眼前的条目突然消失。</summary>
    public void ApplyPageSizePreference()
    {
        if (!HasMoreItems || _visibleItemCount >= PageSize)
            return;
        var added = AppendVisibleItems(Math.Min(PageSize, _filteredItems.Count) - _visibleItemCount);
        StartThumbnailsFor(added);
        UpdatePagingStatusText();
    }

    private void StartThumbnails()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        StartThumbnailsFor(Items);
    }

    private void StartThumbnailsFor(IEnumerable<FileItemViewModel> candidates)
    {
        if (!_settingsStore.Settings.EnableThumbnails)
            return;
        if (_thumbCts is null || _thumbCts.IsCancellationRequested)
            _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;
        var targets = candidates
            .Where(i => i.Kind == FileKind.Image && i.Thumbnail is null)
            .Take(300)
            .ToList();
        if (targets.Count == 0) return;
        var tasks = targets.Select(async item =>
        {
            try
            {
                await _thumbnailGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await using var stream = await _fileSystem.OpenReadAsync(item.FullPath, ct).ConfigureAwait(false);
                    var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(stream, 192), ct).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bitmap);
                }
                finally { _thumbnailGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* 坏图回落图标 */ }
        });
        var all = Task.WhenAll(tasks);
        var previous = _idleTask;
        _idleTask = Task.WhenAll(previous, all);
    }

    // ---------- 杂项 ----------
    private void RebuildBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        foreach (var segment in _fileSystem.GetBreadcrumbs(path))
            Breadcrumbs.Add(new BreadcrumbSegment(segment.Name, segment.FullPath));
    }

    private void UpdateHistoryFlags(string currentPath)
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
        var parent = _fileSystem.GetParentPath(currentPath);
        CanGoUp = parent is not null && !PathsEqual(parent, currentPath);
    }

    public async Task NavigateBreadcrumbAsync(BreadcrumbSegment segment) => await NavigateToAsync(segment.FullPath).ConfigureAwait(true);

    private async Task SaveSettingsAsync()
    {
        try { await _settingsStore.SaveSettingsAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (IOException) { }
    }

    private static string FolderDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static SortField ParseSort(string? value) => Enum.TryParse<SortField>(value, out var field) ? field : SortField.Name;

    private static string SortFieldText(SortField field) => field switch
    {
        SortField.Name => "名称",
        SortField.LastWriteTime => "日期",
        SortField.Size => "大小",
        SortField.Kind => "类型",
        _ => "名称",
    };

    internal static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), cmp);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}

/// <summary>一个浏览器标签页的会话快照。条目集合仍由 BrowserViewModel 持有，切换标签时按路径重载。</summary>
public sealed partial class BrowserTabViewModel : ObservableObject
{
    internal BrowserTabViewModel(string? path, string title)
    {
        _path = path;
        _title = string.IsNullOrWhiteSpace(title) ? "新标签页" : title;
    }

    [ObservableProperty] private string? _path;
    [ObservableProperty] private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInactive))]
    private bool _isActive;

    [ObservableProperty] private bool _canClose;

    public bool IsInactive => !IsActive;

    internal Stack<string> BackStack { get; } = [];
    internal Stack<string> ForwardStack { get; } = [];
    internal string FilterTextSnapshot { get; set; } = string.Empty;
    internal IReadOnlyList<string> SelectedPathsSnapshot { get; set; } = [];
}
