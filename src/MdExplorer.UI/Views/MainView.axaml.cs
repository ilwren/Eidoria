using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.UI.ViewModels;
using Md3.Avalonia.Controls;

namespace MdExplorer.UI.Views;

public partial class MainView : UserControl
{
    // 应用内拖放专用格式：可承载 storage URI；标准 File 格式同时提供给外部应用。
    private static readonly DataFormat<string> DragPathsFormat =
        DataFormat.CreateStringApplicationFormat("x-mdexplorer-paths");

    private bool _suppressBottomNavEcho;
    private WindowBase? _clipboardWindow;
    private Window? _hostWindow;
    private DragCandidate? _dragCandidate;
    private bool _dragInProgress;
    private bool _breadcrumbEndScrollPending;
    private bool _secondaryBreadcrumbEndScrollPending;
    private const double DefaultDrawerPaneWidth = 288;
    private const double DefaultPreviewPaneWidth = 316;
    private const double DefaultDualPaneWidth = 560;
    private double _drawerPaneWidth = DefaultDrawerPaneWidth;
    private double _previewPaneWidth = DefaultPreviewPaneWidth;
    private double _dualPaneWidth = DefaultDualPaneWidth;
    private BrowserViewModel? _menuBrowser;
    private readonly Dictionary<Control, Transitions> _motionTransitions = [];
    private bool _reduceMotion;
    private bool _previewPaneVisible;
    private string? _lastBrowserPath;
    private bool _pendingFileContentAnimation;
    private double _fileContentSlideX = 12;

    private sealed record DragCandidate(
        PointerPressedEventArgs Trigger,
        BrowserViewModel SourceBrowser,
        IReadOnlyList<FileItemViewModel> Items,
        Point Origin);

    private sealed class TabDragState(
        IPointer pointer,
        BrowserTabViewModel tab,
        Panel panel,
        IReadOnlyList<Control> containers,
        IReadOnlyList<Rect> slots,
        Point startPoint,
        int startIndex)
    {
        public IPointer Pointer { get; } = pointer;
        public BrowserTabViewModel Tab { get; } = tab;
        public Panel Panel { get; } = panel;
        public IReadOnlyList<Control> Containers { get; } = containers;
        public IReadOnlyList<Rect> Slots { get; } = slots;
        public Point StartPoint { get; } = startPoint;
        public int StartIndex { get; } = startIndex;
        public int TargetIndex { get; set; } = startIndex;
        public bool IsDragging { get; set; }
        public Control DraggedContainer => Containers[StartIndex];
    }

    private TabDragState? _tabDrag;
    private bool _suppressNextTabTap;

    public MainView()
    {
        InitializeComponent();
        CaptureMotionTransitions();
        // 右键菜单打开前确保命中项被选中（ListBox 默认不处理）
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        FileGrid.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        FileDetails.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        SecondaryFileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        SecondaryFileGrid.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        SecondaryFileDetails.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);

        // 拖拽源：主/对照窗格共用阈值判定，避免普通点击被误判为拖动。
        foreach (var control in new Control[] { FileList, FileGrid, FileDetails, SecondaryFileList, SecondaryFileGrid, SecondaryFileDetails })
        {
            control.AddHandler(PointerPressedEvent, OnDragPointerPressed, RoutingStrategies.Tunnel);
            control.AddHandler(PointerMovedEvent, OnDragPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(PointerReleasedEvent, OnDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(PointerCaptureLostEvent, OnDragPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(ScrollViewer.ScrollChangedEvent, OnFileScrollChanged, RoutingStrategies.Bubble);
        }

        RegisterTabStripDrag(TabStrip);
        RegisterTabStripDrag(DesktopTabStrip);

        // 投放目标：两个窗格的文件区（含条目与空白）与各自面包屑。
        foreach (var target in new Control[] { FileDropArea, BreadcrumbBar, SecondaryDropArea, SecondaryBreadcrumbBar })
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragEnterEvent, OnFileDragOver);
            target.AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
            target.AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave);
            target.AddHandler(DragDrop.DropEvent, OnFileDrop);
        }
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.AddHandler(TopLevel.BackRequestedEvent, OnBackRequested, RoutingStrategies.Bubble);
            Shell?.UseSystemClipboard(() => TopLevel.GetTopLevel(this));
            if (topLevel is WindowBase window)
            {
                _clipboardWindow = window;
                window.Activated += OnTopLevelActivated;
            }
            if (topLevel is Window hostWindow)
            {
                _hostWindow = hostWindow;
                hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
            }
            // Android 的打开方式走 Avalonia Launcher；桌面沿用进程实现
            if (Shell is { Capabilities.IsAndroid: true } shell)
            {
                shell.UseLauncher(new Services.AvaloniaFileLauncher(() => TopLevel.GetTopLevel(this)));
                // SAF：注入 StorageProvider 并恢复书签授权文件夹
                _ = shell.AttachStorageAsync(topLevel.StorageProvider);
            }
        }
        if (Shell is { } vm)
        {
            _drawerPaneWidth = Math.Clamp(vm.SettingsStore.Settings.DrawerPaneWidth, 216, 440);
            _previewPaneWidth = Math.Clamp(vm.SettingsStore.Settings.PreviewPaneWidth, 260, 520);
            _dualPaneWidth = Math.Clamp(vm.SettingsStore.Settings.DualPaneWidth, 360, 760);
            _lastBrowserPath = vm.Browser.CurrentPath;
            ApplyMotionPreference(vm.ReduceMotion);
            ApplyPaneColumnWidths();
            ApplyBrowserPaneWidths();
            vm.PropertyChanged += OnShellPropertyChanged;
            // DataGrid.SelectedItems 为只读 IList（不可绑定整集），选集与 VM 手动双向同步
            vm.Browser.SelectedItems.CollectionChanged += OnBrowserSelectionChanged;
            vm.Browser.Breadcrumbs.CollectionChanged += OnBreadcrumbsChanged;
            vm.SecondaryBrowser.SelectedItems.CollectionChanged += OnSecondarySelectionChanged;
            vm.SecondaryBrowser.Breadcrumbs.CollectionChanged += OnSecondaryBreadcrumbsChanged;
            ScheduleBreadcrumbEndScroll();
            ScheduleSecondaryBreadcrumbEndScroll();
            vm.Browser.PropertyChanged += OnBrowserPropertyChanged;
            vm.SecondaryBrowser.PropertyChanged += OnSecondaryBrowserPropertyChanged;
            vm.Preview.PropertyChanged += OnPreviewPropertyChanged;
            ApplyWindowChromeLayout();
            _ = vm.Browser.RefreshExternalClipboardAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        BreadcrumbScroller.LayoutUpdated -= OnBreadcrumbScrollerLayoutUpdated;
        SecondaryBreadcrumbScroller.LayoutUpdated -= OnSecondaryBreadcrumbScrollerLayoutUpdated;
        _secondaryBreadcrumbEndScrollPending = false;
        _breadcrumbEndScrollPending = false;
        if (_clipboardWindow is not null)
        {
            _clipboardWindow.Activated -= OnTopLevelActivated;
            _clipboardWindow = null;
        }
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }
        if (Shell is { } vm)
        {
            vm.PropertyChanged -= OnShellPropertyChanged;
            vm.Browser.SelectedItems.CollectionChanged -= OnBrowserSelectionChanged;
            vm.Browser.Breadcrumbs.CollectionChanged -= OnBreadcrumbsChanged;
            vm.SecondaryBrowser.SelectedItems.CollectionChanged -= OnSecondarySelectionChanged;
            vm.SecondaryBrowser.Breadcrumbs.CollectionChanged -= OnSecondaryBreadcrumbsChanged;
            vm.Browser.PropertyChanged -= OnBrowserPropertyChanged;
            vm.SecondaryBrowser.PropertyChanged -= OnSecondaryBrowserPropertyChanged;
            vm.Preview.PropertyChanged -= OnPreviewPropertyChanged;
        }
        if (TopLevel.GetTopLevel(this) is { } topLevel)
            topLevel.RemoveHandler(TopLevel.BackRequestedEvent, OnBackRequested);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Shell?.SetViewportSize(e.NewSize.Width, e.NewSize.Height);
        ApplyWindowChromeLayout();
        LayoutFloatingPreview(e.NewSize);
        ScheduleBreadcrumbEndScroll();
        ScheduleSecondaryBreadcrumbEndScroll();
    }

    private void LayoutFloatingPreview(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var maxWidth = Math.Max(300, size.Width - 20);
        var maxHeight = Math.Max(360, size.Height - 96);
        var width = double.IsNaN(FloatingPreview.Width) || FloatingPreview.Width <= 0
            ? Math.Min(340, maxWidth)
            : FloatingPreview.Width;
        FloatingPreview.Width = Math.Clamp(width, 300, maxWidth);
        FloatingPreview.MaxHeight = maxHeight;
        if (double.IsNaN(FloatingPreview.Height) || FloatingPreview.Height <= 0)
            FloatingPreview.Height = DesignedPreviewHeight(size);
        else
            FloatingPreview.Height = Math.Min(FloatingPreview.Height, maxHeight);

        if (FloatingPreview.X + FloatingPreview.Width > size.Width - 8)
            FloatingPreview.X = Math.Max(8, size.Width - FloatingPreview.Width - 8);
        if (FloatingPreview.Y < 54)
            FloatingPreview.Y = 58;
        if (FloatingPreview.Y > size.Height - 64)
            FloatingPreview.Y = Math.Max(58, size.Height - 64);
    }

    // ---------- 桌面无边框窗口 Chrome ----------
    private bool UseDesktopWindowChrome =>
        _hostWindow is MainWindow && Shell?.Capabilities.IsDesktop == true;

    private void ApplyWindowChromeLayout()
    {
        if (Shell is not { } shell)
        {
            DesktopWindowChrome.IsVisible = false;
            CompactWindowControls.IsVisible = false;
            WindowResizeGrips.IsVisible = false;
            WorkspaceTabStripHost.IsVisible = true;
            return;
        }

        var useChrome = UseDesktopWindowChrome;
        var showExpandedChrome = useChrome && shell.IsExpandedOrAbove;
        DesktopWindowChrome.IsVisible = showExpandedChrome;
        DesktopTabScroller.IsVisible = shell.IsBrowser && shell.ShowBrowserTabs;
        WorkspaceTabStripHost.IsVisible = shell.ShowBrowserTabs && !showExpandedChrome;
        CompactWindowControls.IsVisible = useChrome && shell.IsCompactOrMedium;
        SelectionModeBar.Margin = CompactWindowControls.IsVisible
            ? new Thickness(0, 0, 120, 0)
            : default;
        BrowserWorkspace.Margin = showExpandedChrome
            ? new Thickness(10, 0, 10, 10)
            : new Thickness(10);
        WindowResizeGrips.IsVisible = useChrome && _hostWindow?.WindowState == WindowState.Normal;
        UpdateMaximizeButtons();
    }

    private void UpdateMaximizeButtons()
    {
        var isMaximized = _hostWindow?.WindowState is WindowState.Maximized or WindowState.FullScreen;
        foreach (var button in new[] { WindowMaximizeButton, CompactWindowMaximizeButton })
        {
            button.Icon = isMaximized ? MdSymbols.FilterNone : MdSymbols.CropSquare;
            ToolTip.SetTip(button, isMaximized ? "还原" : "最大化");
            AutomationProperties.SetName(button, isMaximized ? "还原窗口" : "最大化窗口");
        }
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            ApplyWindowChromeLayout();
    }

    private void OnWindowChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!UseDesktopWindowChrome || _hostWindow is not { } window)
            return;
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            return;
        if (TabFromEvent(e) is not null)
            return;
        if (e.Source is Visual source
            && source.GetSelfAndVisualAncestors().Any(static visual => visual is Button or TextBox))
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeWindow();
            e.Handled = true;
            return;
        }
        if (window.WindowState == WindowState.Normal)
        {
            window.BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnWindowResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!UseDesktopWindowChrome || _hostWindow is not { } window
            || window.WindowState != WindowState.Normal
            || sender is not Control { Tag: string tag }
            || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            return;
        var edge = tag switch
        {
            "NorthWest" => WindowEdge.NorthWest,
            "North" => WindowEdge.North,
            "NorthEast" => WindowEdge.NorthEast,
            "West" => WindowEdge.West,
            "East" => WindowEdge.East,
            "SouthWest" => WindowEdge.SouthWest,
            "South" => WindowEdge.South,
            "SouthEast" => WindowEdge.SouthEast,
            _ => (WindowEdge?)null,
        };
        if (edge is not { } value)
            return;
        window.BeginResizeDrag(value, e);
        e.Handled = true;
    }

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        if (_hostWindow is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e) => ToggleMaximizeWindow();

    private void ToggleMaximizeWindow()
    {
        if (_hostWindow is not { } window)
            return;
        window.WindowState = window.WindowState == WindowState.Normal
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e) => _hostWindow?.Close();

    // ---------- 三栏分隔条与入场动画 ----------
    // 本项目 XAML 编译器不为命名 ColumnDefinition 生成字段，按 PaneGrid 列索引取（列序见 MainView.axaml）。
    private ColumnDefinition DrawerColumn => PaneGrid.ColumnDefinitions[1];
    private ColumnDefinition PreviewColumn => PaneGrid.ColumnDefinitions[5];

    private void ApplyPaneColumnWidths()
    {
        if (Shell is not { } shell)
            return;

        if (shell.ShowDrawerPane)
        {
            DrawerColumn.MinWidth = 216;
            DrawerColumn.MaxWidth = 440;
            DrawerColumn.Width = new GridLength(_drawerPaneWidth);
        }
        else
        {
            if (DrawerColumn.ActualWidth > 0)
                _drawerPaneWidth = Math.Clamp(DrawerColumn.ActualWidth, 216, 440);
            DrawerColumn.MinWidth = 0;
            DrawerColumn.Width = new GridLength(0);
        }

        if (shell.ShowPreviewPaneEffective)
        {
            PreviewColumn.MinWidth = 260;
            PreviewColumn.MaxWidth = 520;
            PreviewColumn.Width = new GridLength(_previewPaneWidth);
        }
        else
        {
            if (PreviewColumn.ActualWidth > 0)
                _previewPaneWidth = Math.Clamp(PreviewColumn.ActualWidth, 260, 520);
            PreviewColumn.MinWidth = 0;
            PreviewColumn.Width = new GridLength(0);
        }
        _previewPaneVisible = shell.ShowPreviewPaneEffective;
    }

    private ColumnDefinition SecondaryPaneColumn => BrowserPanesGrid.ColumnDefinitions[2];

    private void ApplyBrowserPaneWidths()
    {
        if (Shell is not { } shell)
            return;

        if (shell.ShowDualPane)
        {
            SecondaryPaneColumn.MinWidth = 360;
            SecondaryPaneColumn.MaxWidth = 760;
            SecondaryPaneColumn.Width = new GridLength(_dualPaneWidth);
        }
        else
        {
            if (SecondaryPaneColumn.ActualWidth > 0)
                _dualPaneWidth = Math.Clamp(SecondaryPaneColumn.ActualWidth, 360, 760);
            SecondaryPaneColumn.MinWidth = 0;
            SecondaryPaneColumn.MaxWidth = double.PositiveInfinity;
            SecondaryPaneColumn.Width = new GridLength(0);
        }
    }

    private void OnDualPaneSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Dispatcher.UIThread.Post(() => PersistDualPaneWidth(SecondaryPaneColumn.ActualWidth), DispatcherPriority.Loaded);

    private void OnDualPaneSplitterResetRequested(object? sender, EventArgs e)
    {
        _dualPaneWidth = DefaultDualPaneWidth;
        ApplyBrowserPaneWidths();
        PersistDualPaneWidth(DefaultDualPaneWidth);
    }

    private void PersistDualPaneWidth(double width)
    {
        if (Shell is not { } shell || width <= 0)
            return;
        _dualPaneWidth = Math.Clamp(width, 360, 760);
        shell.SettingsStore.Settings.DualPaneWidth = _dualPaneWidth;
        _ = shell.SettingsStore.SaveSettingsAsync(CancellationToken.None);
    }

    private void OnDrawerSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Dispatcher.UIThread.Post(() => PersistPaneWidths(DrawerColumn.ActualWidth, null), DispatcherPriority.Loaded);

    private void OnPreviewSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Dispatcher.UIThread.Post(() => PersistPaneWidths(null, PreviewColumn.ActualWidth), DispatcherPriority.Loaded);

    private void OnDrawerSplitterResetRequested(object? sender, EventArgs e)
    {
        _drawerPaneWidth = DefaultDrawerPaneWidth;
        ApplyPaneColumnWidths();
        PersistPaneWidths(DefaultDrawerPaneWidth, null);
    }

    private void OnPreviewSplitterResetRequested(object? sender, EventArgs e)
    {
        _previewPaneWidth = DefaultPreviewPaneWidth;
        ApplyPaneColumnWidths();
        PersistPaneWidths(null, DefaultPreviewPaneWidth);
    }

    private Control[] MotionHosts => [PageHost, FileDropArea, PreviewPane, ModalDrawerHost, TaskCenterCard, SearchIslandPanel];

    private void CaptureMotionTransitions()
    {
        foreach (var host in MotionHosts)
        {
            if (host.Transitions is { } transitions)
                _motionTransitions[host] = transitions;
        }
    }

    private void ApplyMotionPreference(bool reduceMotion)
    {
        _reduceMotion = reduceMotion;
        const string className = "reduce-motion";
        if (reduceMotion)
        {
            if (!Classes.Contains(className))
                Classes.Add(className);
        }
        else
        {
            Classes.Remove(className);
        }

        foreach (var host in MotionHosts)
        {
            if (reduceMotion)
            {
                host.Transitions = null;
                host.Opacity = 1;
                host.RenderTransform = new TranslateTransform(0, 0);
            }
            else if (_motionTransitions.TryGetValue(host, out var transitions))
            {
                host.Transitions = transitions;
            }
        }
        if (reduceMotion)
            _pendingFileContentAnimation = false;
    }

    private void PersistPaneWidths(double? drawerWidth, double? previewWidth)
    {
        if (Shell is not { } shell)
            return;
        if (drawerWidth is > 0)
        {
            _drawerPaneWidth = Math.Clamp(drawerWidth.Value, 216, 440);
            shell.SettingsStore.Settings.DrawerPaneWidth = _drawerPaneWidth;
        }
        if (previewWidth is > 0)
        {
            _previewPaneWidth = Math.Clamp(previewWidth.Value, 260, 520);
            shell.SettingsStore.Settings.PreviewPaneWidth = _previewPaneWidth;
        }
        _ = shell.SettingsStore.SaveSettingsAsync(CancellationToken.None);
    }

    /// <summary>短时入场：先无过渡地放到起点，再恢复 XAML Transitions 回到原位（Shared Axis 的轻量版）。</summary>
    private void AnimateIn(Control control, double offsetX, double offsetY)
    {
        if (_reduceMotion || Shell?.ReduceMotion == true)
            return;
        if (control.Transitions is not { } transitions)
            return;
        control.Transitions = null;
        control.Opacity = 0.45;
        control.RenderTransform = new TranslateTransform(offsetX, offsetY);
        Dispatcher.UIThread.Post(() =>
        {
            if (_reduceMotion)
                return;
            control.Transitions = transitions;
            control.Opacity = 1;
            control.RenderTransform = new TranslateTransform(0, 0);
        }, DispatcherPriority.Loaded);
    }

    private static int PathDepth(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? 0
            : path.TrimEnd('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length;

    private void OnBreadcrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleBreadcrumbEndScroll();

    private void ScheduleBreadcrumbEndScroll()
    {
        if (_breadcrumbEndScrollPending)
            return;
        _breadcrumbEndScrollPending = true;
        BreadcrumbScroller.LayoutUpdated += OnBreadcrumbScrollerLayoutUpdated;
    }

    private void OnBreadcrumbScrollerLayoutUpdated(object? sender, EventArgs e)
    {
        // 首个 LayoutUpdated 可能早于面包屑容器全部生成；Extent 尚未超过视口时继续等待下一轮。
        var maxOffset = Math.Max(0, BreadcrumbScroller.Extent.Width - BreadcrumbScroller.Viewport.Width);
        if (maxOffset <= 0)
            return;
        BreadcrumbScroller.LayoutUpdated -= OnBreadcrumbScrollerLayoutUpdated;
        _breadcrumbEndScrollPending = false;
        // 布局完成后再滚到末端，窄屏优先露出当前目录而非根目录。
        if (Shell?.Browser.Breadcrumbs.LastOrDefault() is { } last)
            BreadcrumbBar.ScrollIntoView(last);
        BreadcrumbScroller.Offset = new Vector(maxOffset, 0);
    }

    private void OnSecondaryBreadcrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleSecondaryBreadcrumbEndScroll();

    private void ScheduleSecondaryBreadcrumbEndScroll()
    {
        if (_secondaryBreadcrumbEndScrollPending)
            return;
        _secondaryBreadcrumbEndScrollPending = true;
        SecondaryBreadcrumbScroller.LayoutUpdated += OnSecondaryBreadcrumbScrollerLayoutUpdated;
    }

    private void OnSecondaryBreadcrumbScrollerLayoutUpdated(object? sender, EventArgs e)
    {
        if (SecondaryBreadcrumbScroller.Viewport.Width <= 0)
            return;
        SecondaryBreadcrumbScroller.LayoutUpdated -= OnSecondaryBreadcrumbScrollerLayoutUpdated;
        _secondaryBreadcrumbEndScrollPending = false;

        var maxOffset = Math.Max(0, SecondaryBreadcrumbScroller.Extent.Width - SecondaryBreadcrumbScroller.Viewport.Width);
        if (maxOffset <= 0)
            return;
        if (Shell?.SecondaryBrowser.Breadcrumbs.LastOrDefault() is { } last)
            SecondaryBreadcrumbBar.ScrollIntoView(last);
        SecondaryBreadcrumbScroller.Offset = new Vector(maxOffset, 0);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        if (shell.WouldHandleBack)
        {
            e.Handled = true;
            _ = shell.TryGoBackAsync();
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shell is not { } shell)
            return;

        if (e.PropertyName is nameof(ShellViewModel.SizeClass)
            or nameof(ShellViewModel.IsExpandedOrAbove)
            or nameof(ShellViewModel.IsCompactOrMedium)
            or nameof(ShellViewModel.ShowBrowserTabs)
            or nameof(ShellViewModel.Page)
            or nameof(ShellViewModel.IsBrowser))
            ApplyWindowChromeLayout();

        if (e.PropertyName is nameof(ShellViewModel.ShowDrawerPane)
            or nameof(ShellViewModel.ShowPreviewPaneEffective))
        {
            var previewWasVisible = _previewPaneVisible;
            ApplyPaneColumnWidths();
            if (!previewWasVisible && shell.ShowPreviewPaneEffective)
                AnimateIn(PreviewPane, 12, 0);
        }
        if (e.PropertyName is nameof(ShellViewModel.IsDrawerOpen) && shell.IsDrawerOpen)
            AnimateIn(ModalDrawerHost, -18, 0);
        if (e.PropertyName is nameof(ShellViewModel.IsTaskCenterOpen) && shell.IsTaskCenterOpen)
            AnimateIn(TaskCenterCard, 0, 12);
        if (e.PropertyName is nameof(ShellViewModel.IsPreviewPageOpen) && shell.IsPreviewPageOpen)
            ExpandFloatingPreview();
        if (e.PropertyName is nameof(ShellViewModel.ReduceMotion))
            ApplyMotionPreference(shell.ReduceMotion);
        if (e.PropertyName is nameof(ShellViewModel.ShowDualPane)
            or nameof(ShellViewModel.IsDualPaneEnabled)
            or nameof(ShellViewModel.CanUseDualPane))
        {
            ApplyBrowserPaneWidths();
            ScheduleSecondaryBreadcrumbEndScroll();
        }

        if (e.PropertyName != nameof(ShellViewModel.Page))
            return;
        ApplyPaneColumnWidths();
        ApplyBrowserPaneWidths();
        AnimateIn(PageHost, 0, 7);
        var index = shell.Page switch
        {
            ShellPage.Home => 0,
            ShellPage.Favorites => 1,
            ShellPage.Trash => 2,
            ShellPage.Settings => 3,
            _ => -1,
        };
        _suppressBottomNavEcho = true;
        BottomNav.SelectedIndex = index;
        _suppressBottomNavEcho = false;
    }

    // ---------- 底部导航 ----------
    private void OnBottomNavChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressBottomNavEcho || Shell is not { } shell)
            return;
        switch (BottomNav.SelectedIndex)
        {
            case 0: shell.GoHomeCommand.Execute(null); break;
            case 1: shell.GoFavoritesCommand.Execute(null); break;
            case 2: shell.GoTrashCommand.Execute(null); break;
            case 3: shell.GoSettingsCommand.Execute(null); break;
        }
        // 浏览器页没有对应底栏项：清零选中态
        if (shell.Page == ShellPage.Browser)
        {
            _suppressBottomNavEcho = true;
            BottomNav.SelectedIndex = -1;
            _suppressBottomNavEcho = false;
        }
    }

    // ---------- 双窗格活动会话 ----------
    private void OnPrimaryPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Shell is { } shell)
        {
            // B 的搜索浮层只属于 B：切到 A 时收掉，避免浮层压在非活动窗格上。
            shell.SecondaryBrowser.IsSearchPanelOpen = false;
            shell.ActivatePrimaryPane();
        }
    }

    private void OnPrimaryPaneGotFocus(object? sender, FocusChangedEventArgs e) => Shell?.ActivatePrimaryPane();

    private void OnSecondaryPanePointerPressed(object? sender, PointerPressedEventArgs e) => Shell?.ActivateSecondaryPane();

    private void OnSecondaryPaneGotFocus(object? sender, FocusChangedEventArgs e) => Shell?.ActivateSecondaryPane();

    private BrowserViewModel? BrowserForItem(FileItemViewModel? item)
    {
        if (Shell is not { } shell || item is null)
            return Shell?.Browser;
        return shell.SecondaryBrowser.Items.Contains(item) ? shell.SecondaryBrowser : shell.Browser;
    }

    private BrowserViewModel? BrowserForControl(object? sender)
    {
        if (Shell is not { } shell)
            return null;
        if (ReferenceEquals(sender, SecondaryFileList) || ReferenceEquals(sender, SecondaryFileGrid) ||
            ReferenceEquals(sender, SecondaryFileDetails) ||
            ReferenceEquals(sender, SecondaryDropArea) || ReferenceEquals(sender, SecondaryBreadcrumbBar))
            return shell.SecondaryBrowser;
        return shell.Browser;
    }

    private BrowserViewModel? BrowserForEvent(RoutedEventArgs e, FileItemViewModel? item = null)
    {
        if (Shell is not { } shell)
            return null;
        if (e.Source is Visual source && source.FindAncestorOfType<Border>() is { } border &&
            ReferenceEquals(border, SecondaryPane))
            return shell.SecondaryBrowser;
        if (e.Source is Control control)
        {
            foreach (var ancestor in control.GetSelfAndVisualAncestors())
            {
                if (ReferenceEquals(ancestor, SecondaryPane))
                    return shell.SecondaryBrowser;
                if (ReferenceEquals(ancestor, PrimaryPane))
                    return shell.Browser;
            }
        }
        return item is null ? shell.ActiveBrowser : BrowserForItem(item);
    }

    // ---------- 分页滚动 ----------
    private void OnFileScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (BrowserForControl(sender) is not { } browser)
            return;
        if (e.Source is not ScrollViewer scroller || scroller.Viewport.Height <= 0)
            return;

        // 先保证这一屏放得下的行都物化了；窄屏/小窗口不该等分页。
        browser.EnsureVisibleRange(EstimateVisibleRows(browser, scroller.Viewport.Height));

        if (!browser.HasMoreItems || browser.IsBackgroundPaging)
            return;
        if (scroller.Extent.Height <= 0)
            return; // 还没量出内容高度，等下一次布局事件

        // 到底就自动续页，并把剩余条目交给后台分批补齐：用户不必反复滚，界面也不会被整目录冻住。
        // 自动续页受设置控制；关闭后仍可点页脚“加载更多”，不改变分页语义。
        var remaining = scroller.Extent.Height - (scroller.Offset.Y + scroller.Viewport.Height);
        if (Shell?.ShouldAutoLoadMore(remaining) == true)
            browser.BeginBackgroundFill();
    }

    /// <summary>按当前视图模式估算一屏能放多少行；只用来抬高已物化行数，估小了也无害。</summary>
    private static int EstimateVisibleRows(BrowserViewModel browser, double viewportHeight)
    {
        if (viewportHeight <= 0)
            return 0;
        var rowHeight = browser.ViewMode switch
        {
            BrowserViewMode.Grid => 124d,
            BrowserViewMode.Details => 38d,
            _ => 56d,
        };
        return (int)Math.Ceiling(viewportHeight / rowHeight) + 4;
    }

    private static bool IsFileSurfaceAtTop(object? sender)
    {
        if (sender is not Control control)
            return false;
        var scroller = control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        return scroller is null || scroller.Offset.Y <= 0.5;
    }

    // ---------- 列表交互 ----------
    private async void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Properties.IsMiddleButtonPressed && ItemFromEvent(e) is { IsFolder: true } folder && Browser is { } browser)
        {
            await browser.OpenItemInNewTabAsync(folder);
            e.Handled = true;
            return;
        }
        if (!e.Properties.IsRightButtonPressed)
            return;
        if (ItemFromEvent(e) is { } item && BrowserForEvent(e, item) is { } ownerBrowser)
        {
            _menuBrowser = ownerBrowser;
            if (!ownerBrowser.SelectedItems.Contains(item))
            {
                ownerBrowser.SelectedItems.Clear();
                ownerBrowser.SelectedItems.Add(item);
            }
        }
    }

    private async void OnTopLevelActivated(object? sender, EventArgs e)
    {
        if (Shell is { } shell)
            await shell.Browser.RefreshExternalClipboardAsync();
    }

    // ---------- 文件拖放 ----------

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragInProgress || Shell is not { } shell)
            return;
        if (e.Pointer.Type != PointerType.Mouse)
            return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            return;
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return;
        if (ItemFromEvent(e) is not { } item)
            return;
        var sourceBrowser = BrowserForControl(sender) ?? BrowserForItem(item) ?? shell.Browser;
        if (sourceBrowser.IsSelectionMode)
            return;
        // Tunnel 阶段先拍来源窗格的选集快照：控件在随后的按下处理中可能把多选收敛为单选。
        IReadOnlyList<FileItemViewModel> draggedItems = sourceBrowser.SelectedItems.Contains(item)
            ? sourceBrowser.SelectedItems.ToList()
            : [item];
        _dragCandidate = new DragCandidate(e, sourceBrowser, draggedItems, e.GetPosition(this));
    }

    private async void OnDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _dragCandidate is not { } candidate)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        var delta = e.GetPosition(this) - candidate.Origin;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        // 文件面已在顶部且手势明显向下时，让 MdRefreshIndicator 接管下拉刷新；
        // 横向或向上起手仍按原语义拖拽文件。
        if (delta.Y >= 6 && delta.Y > Math.Abs(delta.X) * 1.25 && IsFileSurfaceAtTop(sender))
        {
            _dragCandidate = null;
            return;
        }

        _dragCandidate = null;
        _dragInProgress = true;
        e.Handled = true;
        try
        {
            await StartItemDragAsync(candidate.Trigger, candidate.SourceBrowser, candidate.Items);
        }
        finally
        {
            _dragInProgress = false;
            HideDropHint();
        }
    }

    private void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragCandidate = null;

    private void OnDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragCandidate = null;

    private async Task StartItemDragAsync(
        PointerPressedEventArgs trigger, BrowserViewModel sourceBrowser, IReadOnlyList<FileItemViewModel> draggedItems)
    {
        if (Shell is not { } shell || draggedItems.Count == 0)
            return;

        // 恢复按下前快照，确保拖的是来源窗格中用户看到的多选集合。
        if (!sourceBrowser.SelectedItems.SequenceEqual(draggedItems))
        {
            sourceBrowser.SelectedItems.Clear();
            foreach (var item in draggedItems)
                sourceBrowser.SelectedItems.Add(item);
        }

        var paths = draggedItems.Select(i => i.FullPath).ToList();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragPathsFormat, string.Join('\n', paths)));

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            foreach (var item in draggedItems.Where(i => !i.Model.Locator.IsStorage))
            {
                IStorageItem? storageItem = item.IsFolder
                    ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(item.FullPath).ConfigureAwait(true)
                    : await topLevel.StorageProvider.TryGetFileFromPathAsync(item.FullPath).ConfigureAwait(true);
                if (storageItem is not null)
                    data.Add(DataTransferItem.Create(DataFormat.File, storageItem));
            }
        }

        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move).ConfigureAwait(true);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (Shell is not { } shell || DropBrowserForEvent(e) is not { } dropBrowser || dropBrowser.CurrentPath is null)
        {
            RejectDrop(e);
            return;
        }
        if (!e.DataTransfer.Contains(DragPathsFormat) && !e.DataTransfer.Contains(DataFormat.File))
        {
            RejectDrop(e);
            return;
        }
        if (ResolveDropDestination(e) is not { } destination)
        {
            RejectDrop(e);
            return;
        }

        var sources = ExtractDraggedPaths(e.DataTransfer);
        if (sources.Count == 0 || !dropBrowser.CanTransferItems(sources, destination))
        {
            RejectDrop(e);
            return;
        }

        var isMove = FileTransferRules.ShouldMove(
            e.DataTransfer.Contains(DragPathsFormat),
            (e.KeyModifiers & KeyModifiers.Control) != 0,
            (e.KeyModifiers & KeyModifiers.Shift) != 0);
        e.DragEffects = isMove ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowDropHint(dropBrowser, isMove
            ? $"释放以移动到「{DropDestinationLabel(e)}」（Ctrl = 复制）"
            : $"释放以复制到「{DropDestinationLabel(e)}」");
        e.Handled = true;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e) => HideDropHint();

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        HideDropHint();
        if (Shell is not { } shell || DropBrowserForEvent(e) is not { } dropBrowser ||
            ResolveDropDestination(e) is not { } destination)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var sources = ExtractDraggedPaths(e.DataTransfer);
        if (sources.Count == 0 || !dropBrowser.CanTransferItems(sources, destination))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var isMove = FileTransferRules.ShouldMove(
            e.DataTransfer.Contains(DragPathsFormat),
            (e.KeyModifiers & KeyModifiers.Control) != 0,
            (e.KeyModifiers & KeyModifiers.Shift) != 0);
        e.DragEffects = isMove ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
        var outcome = await dropBrowser.TransferItemsAsync(sources, destination, isMove);
        if (outcome is not null)
        {
            var other = ReferenceEquals(dropBrowser, shell.Browser) ? shell.SecondaryBrowser : shell.Browser;
            await shell.RefreshBrowserAfterTransferAsync(other, sources, destination);
        }
    }

    private void RejectDrop(DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        HideDropHint();
        e.Handled = true;
    }

    private void HideDropHint()
    {
        DropHint.IsVisible = false;
        SecondaryDropHint.IsVisible = false;
    }

    private void ShowDropHint(BrowserViewModel browser, string text)
    {
        var secondary = Shell is { } shell && ReferenceEquals(browser, shell.SecondaryBrowser);
        DropHint.IsVisible = !secondary;
        SecondaryDropHint.IsVisible = secondary;
        if (secondary)
            SecondaryDropHintText.Text = text;
        else
            DropHintText.Text = text;
    }

    private BrowserViewModel? DropBrowserForEvent(DragEventArgs e)
    {
        if (ItemFromEvent(e) is { } item)
            return BrowserForEvent(e, item);
        return BrowserForEvent(e);
    }

    private string? ResolveDropDestination(DragEventArgs e)
    {
        if (Shell is not { } shell)
            return null;
        if (ItemFromEvent(e) is { IsFolder: true } folder)
            return folder.FullPath;
        if (BreadcrumbFromEvent(e) is { } segment)
            return segment.FullPath;
        // 搜索结果的条目来自不同父目录：空格区域不猜当前目录，只允许明确投到某个文件夹条目。
        var browser = DropBrowserForEvent(e) ?? shell.ActiveBrowser;
        return browser.IsSearchMode ? null : browser.CurrentPath;
    }

    private string DropDestinationLabel(DragEventArgs e)
    {
        if (ItemFromEvent(e) is { IsFolder: true } folder)
            return folder.Name;
        if (BreadcrumbFromEvent(e) is { } segment)
            return segment.Name;
        return DropBrowserForEvent(e)?.CurrentFolderName ?? Shell?.ActiveBrowser.CurrentFolderName ?? "此处";
    }

    private static BreadcrumbSegment? BreadcrumbFromEvent(RoutedEventArgs e)
    {
        if (e.Source is not Visual source)
            return null;
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is Control { DataContext: BreadcrumbSegment segment })
                return segment;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractDraggedPaths(IDataTransfer dataTransfer)
    {
        if (dataTransfer.TryGetValue(DragPathsFormat) is { Length: > 0 } payload)
        {
            // 不用 TrimEntries：合法文件名可以以空格开头或结尾。
            return payload
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.TrimEnd('\r'))
                .Where(path => path.Length > 0)
                .ToArray();
        }

        var files = dataTransfer.TryGetFiles();
        if (files is null)
            return [];
        return files
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is not { } shell || ItemFromEvent(e) is not { } item ||
            BrowserForEvent(e, item) is not { } browser)
            return;
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return; // ⋮ 按钮自己处理
        if (browser.IsSelectionMode)
        {
            browser.ToggleChecked(item);
        }
        else if (shell.IsCompactOrMedium)
        {
            if (item.IsFolder)
            {
                await browser.OpenItemAsync(item);
            }
            else
            {
                browser.SelectedItems.Clear();
                browser.SelectedItems.Add(item);
                shell.OnFileOpened(item);
            }
        }
    }

    private async void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is not { } shell || ItemFromEvent(e) is not { } item ||
            BrowserForEvent(e, item) is not { } browser)
            return;
        if (item.IsFolder)
        {
            if (!BrowserViewModel.PathsEqual(browser.CurrentPath, item.FullPath))
                await browser.OpenItemAsync(item);
        }
        else if (!shell.IsCompactOrMedium)
        {
            browser.SelectedItems.Clear();
            browser.SelectedItems.Add(item);
            await browser.OpenItemExternalAsync(item);
        }
    }

    private async void OnFavoriteItemTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is not { } shell || ItemFromEvent(e) is not { } item)
            return;
        if (item.IsFolder)
            await shell.OpenFolderAsync(item.FullPath);
        else if (Path.GetDirectoryName(item.FullPath) is { } parent)
            await shell.OpenFolderAsync(parent);
    }

    private void OnItemMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || Shell is not { } shell)
            return;
        _menuBrowser = shell.Browser;
        if (button.DataContext is FileItemViewModel item)
        {
            var browser = BrowserForEvent(e, item) ?? shell.Browser;
            _menuBrowser = browser;
            if (!browser.SelectedItems.Contains(item))
            {
                browser.SelectedItems.Clear();
                browser.SelectedItems.Add(item);
            }
        }
        // ContextMenu.Open 只允许以已挂载该菜单的控件为目标；⋮ 按钮在模板里同样挂载 ItemMenu，
        // 这里必须从按钮自身取菜单，不能拿资源里的同一实例对未挂载控件强行 Open（会抛 ArgumentException）。
        button.ContextMenu?.Open(button);
        e.Handled = true;
    }

    private static FileItemViewModel? ItemFromEvent(RoutedEventArgs e)
    {
        if (e.Source is not Control source)
            return null;
        if (source.DataContext is FileItemViewModel direct)
            return direct;
        return source.FindAncestorOfType<ListBoxItem>()?.DataContext as FileItemViewModel
               ?? source.FindAncestorOfType<DataGridRow>()?.DataContext as FileItemViewModel;
    }

    // ---------- 标签页 ----------
    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (_suppressNextTabTap)
        {
            _suppressNextTabTap = false;
            e.Handled = true;
            return;
        }
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return; // 关闭按钮自己处理
        if (sender is Control { DataContext: BrowserTabViewModel tab })
            Browser?.ActivateTabCommand.Execute(tab);
    }

    private void OnCompactTabTapped(object? sender, TappedEventArgs e)
    {
        OnTabTapped(sender, e);
        CompactTabsButton.Flyout?.Hide();
    }

    private void OnCompactNewTabClick(object? sender, RoutedEventArgs e)
    {
        Browser?.NewTabCommand.Execute(null);
        CompactTabsButton.Flyout?.Hide();
    }

    private void RegisterTabStripDrag(ItemsControl strip)
    {
        // 与文件拖拽一样在 Tunnel 阶段接管：标签模板子控件/滚动容器可能先消费 Bubble 指针，
        // 只在标签 Border 上挂 Bubble Move/Release 不可靠。
        strip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        strip.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        strip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        strip.AddHandler(PointerCaptureLostEvent, OnTabPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl strip || Browser is not { } browser ||
            TabFromEvent(e) is not { } tab)
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed ||
            e.Pointer.Type != PointerType.Mouse || browser.Tabs.Count < 2 ||
            e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return;

        if (strip.ItemsPanelRoot is not { } panel)
            return;
        var startIndex = browser.Tabs.IndexOf(tab);
        if (startIndex < 0)
            return;

        var containers = new List<Control>(browser.Tabs.Count);
        for (var i = 0; i < browser.Tabs.Count; i++)
        {
            if (strip.ContainerFromIndex(i) is not { } container)
                return;
            containers.Add(container);
        }

        _tabDrag = new TabDragState(
            e.Pointer,
            tab,
            panel,
            containers,
            containers.Select(container => container.Bounds).ToArray(),
            e.GetPosition(panel),
            startIndex);
    }

    private static BrowserTabViewModel? TabFromEvent(RoutedEventArgs e)
    {
        if (e.Source is not Visual source)
            return null;
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is Control { DataContext: BrowserTabViewModel tab })
                return tab;
        }
        return null;
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            return;
        if (sender is Control { DataContext: BrowserTabViewModel tab })
        {
            Browser?.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDrag is not { } drag || !ReferenceEquals(e.Pointer, drag.Pointer) || Browser is not { } browser)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelTabDrag();
            return;
        }

        var current = e.GetPosition(drag.Panel);
        var delta = current - drag.StartPoint;
        if (!drag.IsDragging)
        {
            if (Math.Abs(delta.X) < 7 || Math.Abs(delta.Y) > Math.Abs(delta.X) * 1.5)
                return;
            drag.IsDragging = true;
            // 不主动改捕获：按下时指针已隐式捕获到命中元素，改捕获会先触发
            // PointerCaptureLost 并冒泡到标签，反而把刚开始的拖拽取消。
            drag.DraggedContainer.ZIndex = 10;
            drag.DraggedContainer.Opacity = 0.88;
            if (!_reduceMotion && Shell?.ReduceMotion != true)
            {
                var transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(140),
                    },
                };
                foreach (var container in drag.Containers.Where(container => !ReferenceEquals(container, drag.DraggedContainer)))
                    container.Transitions = transitions;
            }
        }

        var startSlot = drag.Slots[drag.StartIndex];
        var minDelta = -startSlot.X;
        var maxDelta = drag.Slots[^1].Right - startSlot.Right;
        var clampedDeltaX = Math.Clamp(delta.X, minDelta, maxDelta);
        drag.DraggedContainer.RenderTransform = new TranslateTransform(clampedDeltaX, 0);

        var draggedCenter = startSlot.Center.X + clampedDeltaX;
        // 等宽标签拖到相邻槽位时，中心会精确重合；到达中心即视为已跨过该槽位。
        drag.TargetIndex = drag.Slots
            .Where((_, index) => index != drag.StartIndex)
            .Count(slot => slot.Center.X <= draggedCenter);

        var slotExtent = startSlot.Width + 4;
        for (var i = 0; i < drag.Containers.Count; i++)
        {
            if (i == drag.StartIndex)
                continue;
            var shift = 0.0;
            if (drag.TargetIndex < drag.StartIndex && i >= drag.TargetIndex && i < drag.StartIndex)
                shift = slotExtent;
            else if (drag.TargetIndex > drag.StartIndex && i > drag.StartIndex && i <= drag.TargetIndex)
                shift = -slotExtent;
            drag.Containers[i].RenderTransform = new TranslateTransform(shift, 0);
        }
        e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_tabDrag is not { } drag || !ReferenceEquals(e.Pointer, drag.Pointer))
            return;

        var shouldMove = drag.IsDragging;
        var tab = drag.Tab;
        var targetIndex = drag.TargetIndex;
        ClearTabDragVisuals(drag);
        _tabDrag = null;
        if (!shouldMove)
            return;

        _suppressNextTabTap = true;
        Browser?.MoveTab(tab, targetIndex);
        e.Handled = true;
        // 若释放点已离开标签本体而没有后续 Tapped，也不能让抑制标志吞掉下一次正常点选。
        Dispatcher.UIThread.Post(() => _suppressNextTabTap = false, DispatcherPriority.Loaded);
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_tabDrag is { } drag && ReferenceEquals(e.Pointer, drag.Pointer))
            CancelTabDrag();
    }

    private void CancelTabDrag()
    {
        if (_tabDrag is not { } drag)
            return;
        ClearTabDragVisuals(drag);
        _tabDrag = null;
    }

    private static void ClearTabDragVisuals(TabDragState drag)
    {
        foreach (var container in drag.Containers)
        {
            container.Transitions = null;
            container.RenderTransform = new TranslateTransform(0, 0);
            container.Opacity = 1;
            container.ZIndex = 0;
        }
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BrowserTabViewModel tab })
            Browser?.CloseTabCommand.Execute(tab);
    }

    private void OnDuplicateTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: BrowserTabViewModel tab })
            Browser?.DuplicateTabCommand.Execute(tab);
    }

    private void OnCloseOtherTabsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: BrowserTabViewModel tab })
            Browser?.CloseOtherTabsCommand.Execute(tab);
    }

    private void OnCloseTabMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: BrowserTabViewModel tab })
            Browser?.CloseTabCommand.Execute(tab);
    }

    // ---------- 面包屑/排序/搜索 ----------
    private async void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BreadcrumbSegment segment } && Shell is { } shell)
            await shell.Browser.NavigateBreadcrumbAsync(segment);
    }

    private async void OnSecondaryBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BreadcrumbSegment segment } && Shell is { } shell)
        {
            shell.ActivateSecondaryPane();
            await shell.SecondaryBrowser.NavigateBreadcrumbAsync(segment);
        }
    }

    private void OnBreadcrumbSegmentTapped(object? sender, TappedEventArgs e)
    {
        // 分段（含其中的 chevron）只负责跳转，阻止事件继续落到路径胶囊的“点空白变输入框”逻辑。
        e.Handled = true;
    }

    private void OnBreadcrumbBarTapped(object? sender, TappedEventArgs e)
    {
        if (Browser?.IsAddressEditing == true)
            return;
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return;
        Browser?.BeginEditAddressCommand.Execute(null);
    }

    private void OnSecondaryBreadcrumbBarTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        var browser = shell.SecondaryBrowser;
        if (browser.IsAddressEditing)
            return;
        // 分段按钮自己处理跳转并 eat 掉事件，这里只负责“点空白进编辑”。
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
            return;
        shell.ActivateSecondaryPane();
        browser.BeginEditAddressCommand.Execute(null);
        if (browser.IsAddressEditing)
            SecondaryAddressBox.Focus();
    }

    private async void OnSecondaryAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        var browser = shell.SecondaryBrowser;
        if (e.Key == Key.Enter)
        {
            await browser.NavigateToAddressAsync(SecondaryAddressBox.Text);
            if (!browser.IsAddressEditing)
            {
                shell.ActivateSecondaryPane();
                FocusActiveFileView();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            browser.CancelEditAddressCommand.Execute(null);
            FocusActiveFileView();
            e.Handled = true;
        }
    }

    private async void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (Browser is not { } browser)
            return;
        if (e.Key == Key.Enter)
        {
            await browser.NavigateToAddressAsync(AddressBox.Text);
            if (!browser.IsAddressEditing)
                FileList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            browser.CancelEditAddressCommand.Execute(null);
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void OnSortByName(object? sender, RoutedEventArgs e) => SetSort(SortField.Name);
    private void OnSortByDate(object? sender, RoutedEventArgs e) => SetSort(SortField.LastWriteTime);
    private void OnSortBySize(object? sender, RoutedEventArgs e) => SetSort(SortField.Size);
    private void OnSortByKind(object? sender, RoutedEventArgs e) => SetSort(SortField.Kind);

    private void SetSort(SortField field)
    {
        if (Browser is { } browser)
            browser.SetSort(field, browser.CurrentSortDirection);
    }

    private void OnToggleSortDirection(object? sender, RoutedEventArgs e)
    {
        if (Browser is not { } browser)
            return;
        var next = browser.CurrentSortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;
        browser.SetSort(browser.CurrentSortField, next);
    }

    private void OnSecondaryToggleSortDirection(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        var browser = shell.SecondaryBrowser;
        var next = browser.CurrentSortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;
        browser.SetSort(browser.CurrentSortField, next);
    }

    // ---------- 详细信息视图选集同步（DataGrid.SelectedItems 只读） ----------
    private bool _syncingDetailsSelection;

    private void OnDetailsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDetailsSelection || Shell is not { } shell) return;
        _syncingDetailsSelection = true;
        try
        {
            var target = shell.Browser.SelectedItems;
            target.Clear();
            foreach (var item in FileDetails.SelectedItems)
                if (item is FileItemViewModel vm)
                    target.Add(vm);
        }
        finally { _syncingDetailsSelection = false; }
    }

    private void OnBrowserSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MirrorSelectionToDetails();

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shell?.Browser is not { } browser)
            return;
        if (e.PropertyName is nameof(BrowserViewModel.HasMultipleTabs))
            ApplyWindowChromeLayout();
        if (e.PropertyName is nameof(BrowserViewModel.CurrentPath))
        {
            var oldDepth = PathDepth(_lastBrowserPath);
            var newDepth = PathDepth(browser.CurrentPath);
            _fileContentSlideX = newDepth > oldDepth ? 14 : newDepth < oldDepth ? -14 : 8;
            _lastBrowserPath = browser.CurrentPath;
            _pendingFileContentAnimation = true;
        }
        if (e.PropertyName is nameof(BrowserViewModel.IsSearchMode))
        {
            _fileContentSlideX = 10;
            _pendingFileContentAnimation = true;
        }
        if (e.PropertyName is nameof(BrowserViewModel.IsLoading)
            && !browser.IsLoading && _pendingFileContentAnimation)
        {
            _pendingFileContentAnimation = false;
            AnimateIn(FileDropArea, _fileContentSlideX, 0);
        }
        if (e.PropertyName is nameof(BrowserViewModel.ViewMode))
            AnimateIn(FileDropArea, 0, 5);
        if (e.PropertyName is nameof(BrowserViewModel.IsDetailsView))
            MirrorSelectionToDetails();
        if (e.PropertyName is nameof(BrowserViewModel.IsAddressEditing) && browser.IsAddressEditing)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
        if (e.PropertyName is nameof(BrowserViewModel.IsSearchPanelOpen) && browser.IsSearchPanelOpen)
        {
            AnimateIn(SearchIslandPanel, 0, -10);
            Dispatcher.UIThread.Post(() =>
            {
                BrowserSearch.Focus();
                BrowserSearch.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }

    private void MirrorSelectionToDetails()
    {
        if (_syncingDetailsSelection || Shell is not { } shell || !shell.Browser.IsDetailsView) return;
        _syncingDetailsSelection = true;
        try
        {
            FileDetails.SelectedItems.Clear();
            foreach (var vm in shell.Browser.SelectedItems)
                FileDetails.SelectedItems.Add(vm);
        }
        finally { _syncingDetailsSelection = false; }
    }

    // ---------- 对照窗格详细信息：选集与排序独立于 A，但共用同一 Browser VM 管线 ----------
    private bool _syncingSecondaryDetailsSelection;

    private void OnSecondaryDetailsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSecondaryDetailsSelection || Shell is not { } shell) return;
        _syncingSecondaryDetailsSelection = true;
        try
        {
            var target = shell.SecondaryBrowser.SelectedItems;
            target.Clear();
            foreach (var item in SecondaryFileDetails.SelectedItems)
                if (item is FileItemViewModel vm)
                    target.Add(vm);
        }
        finally { _syncingSecondaryDetailsSelection = false; }
    }

    private void OnSecondarySelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MirrorSecondarySelectionToDetails();

    private void MirrorSecondarySelectionToDetails()
    {
        if (_syncingSecondaryDetailsSelection || Shell is not { } shell || !shell.SecondaryBrowser.IsDetailsView) return;
        _syncingSecondaryDetailsSelection = true;
        try
        {
            SecondaryFileDetails.SelectedItems.Clear();
            foreach (var vm in shell.SecondaryBrowser.SelectedItems)
                SecondaryFileDetails.SelectedItems.Add(vm);
        }
        finally { _syncingSecondaryDetailsSelection = false; }
    }

    private void OnSecondaryBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserViewModel.IsDetailsView) or nameof(BrowserViewModel.ViewMode))
            MirrorSecondarySelectionToDetails();
    }

    private void OnSecondaryDetailsSorting(object? sender, DataGridColumnEventArgs e)
    {
        var field = e.Column.SortMemberPath switch
        {
            "Name" => SortField.Name,
            "Size" => SortField.Size,
            "Kind" => SortField.Kind,
            "LastWriteTime" => SortField.LastWriteTime,
            _ => (SortField?)null,
        };
        if (field is { } f && Shell is { } shell)
            shell.SecondaryBrowser.ApplyColumnSort(f);
        e.Handled = true;
    }

    /// <summary>详细信息点列头：排序统一收回 VM 管线（自然序/文件夹在前），控件自排被 Handled 拦截。</summary>
    private void OnDetailsSorting(object? sender, DataGridColumnEventArgs e)
    {
        var field = e.Column.SortMemberPath switch
        {
            "Name" => SortField.Name,
            "Size" => SortField.Size,
            "Kind" => SortField.Kind,
            "LastWriteTime" => SortField.LastWriteTime,
            _ => (SortField?)null,
        };
        if (field is { } f && Shell is { } shell)
            shell.Browser.ApplyColumnSort(f);
        e.Handled = true;
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await shell.Browser.SearchRecursiveFromPanelCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            shell.Browser.IsSearchPanelOpen = false;
            FocusActiveFileView();
            e.Handled = true;
        }
    }

    private async void OnSecondarySearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await shell.SecondaryBrowser.SearchRecursiveFromPanelCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            shell.SecondaryBrowser.IsSearchPanelOpen = false;
            FocusActiveFileView();
            e.Handled = true;
        }
    }

    private void OnApplySecondarySearchFilterClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        shell.SecondaryBrowser.IsSearchPanelOpen = false;
        FocusActiveFileView();
    }

    // ---------- 对照窗格标签 ----------
    private async void OnSecondaryTabTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is { } shell && sender is Control { DataContext: BrowserTabViewModel tab })
        {
            shell.ActivateSecondaryPane();
            await shell.SecondaryBrowser.ActivateTabCommand.ExecuteAsync(tab);
        }
        e.Handled = true;
    }

    private async void OnSecondaryTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && sender is Control { DataContext: BrowserTabViewModel tab })
            await shell.SecondaryBrowser.CloseTabCommand.ExecuteAsync(tab);
        e.Handled = true;
    }

    private async void OnSecondaryDuplicateTabClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && sender is Control { Tag: BrowserTabViewModel tab })
            await shell.SecondaryBrowser.DuplicateTabCommand.ExecuteAsync(tab);
    }

    private async void OnSecondaryCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && sender is Control { Tag: BrowserTabViewModel tab })
            await shell.SecondaryBrowser.CloseTabCommand.ExecuteAsync(tab);
    }

    private void OnSecondarySearchScrimTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is { } shell)
            shell.SecondaryBrowser.IsSearchPanelOpen = false;
        e.Handled = true;
    }

    private void OnApplySearchFilterClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        shell.Browser.IsSearchPanelOpen = false;
        FocusActiveFileView();
    }

    private void OnSearchScrimTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is { } shell)
            shell.Browser.IsSearchPanelOpen = false;
        e.Handled = true;
    }

    private void OnFloatingPreviewClosed(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
            shell.IsPreviewPageOpen = false;
    }

    /// <summary>
    /// 浮窗被收成标题条后，换一个文件再点开必须重新展开内容。
    /// 只监听 IsPreviewPageOpen 不够：它一直是 true，属性没变化就不会有回调，
    /// 用户看到的就是“窗口在、内容不展开”（v0.32 反馈）。
    /// </summary>
    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shell is not { IsPreviewPageOpen: true } shell)
            return;
        if (e.PropertyName is not nameof(PreviewViewModel.CurrentItem))
            return;
        ExpandFloatingPreview();
    }

    /// <summary>
    /// 把浮窗恢复到“展开且开着”的状态。只写 <c>IsMinimized=false</c> 不够：
    /// MdFloatingWindow 的展开距离取自它自己记下的展开高度，而这个值在
    /// “先收起、再换文件”的时序里可能已经被抹掉（或控件当时还没量出高度），
    /// 结果就是属性是 false、窗口却仍停在标题条高度——正是 v0.32 反馈的现象。
    /// 所以这里连高度一起显式复位。
    /// </summary>
    private void ExpandFloatingPreview()
    {
        FloatingPreview.IsMinimized = false;
        FloatingPreview.IsOpen = true;
        LayoutFloatingPreview(Bounds.Size);
        FloatingPreview.Height = DesignedPreviewHeight(Bounds.Size);
    }

    /// <summary>浮窗展开时的设计高度：540 DIP，但不超过窗口留给它的空间。</summary>
    private static double DesignedPreviewHeight(Size size) =>
        Math.Max(360, Math.Min(540, size.Height - 96));

    private void FocusActiveFileView()
    {
        if (Shell is { ShowDualPane: true, IsSecondaryPaneActive: true } shell)
        {
            if (shell.SecondaryBrowser.IsDetailsView)
                SecondaryFileDetails.Focus();
            else if (shell.SecondaryBrowser.IsGridView)
                SecondaryFileGrid.Focus();
            else
                SecondaryFileList.Focus();
            return;
        }
        if (Shell?.Browser.IsDetailsView == true)
            FileDetails.Focus();
        else if (Shell?.Browser.IsGridView == true)
            FileGrid.Focus();
        else
            FileList.Focus();
    }

    // ---------- 菜单与工具栏 ----------
    private BrowserViewModel? Browser => Shell?.Browser;
    private BrowserViewModel? MenuBrowser => _menuBrowser ?? Browser;

    private async void OnMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (MenuBrowser?.SelectedItems.FirstOrDefault() is not { } item || Shell is not { } shell)
            return;
        if (item.IsFolder)
            await MenuBrowser.OpenItemAsync(item);
        else
            shell.OnFileOpened(item);
    }

    private async void OnMenuOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (MenuBrowser is not { } browser || Browser is not { } primary)
            return;
        var item = browser.SelectedItems.FirstOrDefault();
        if (ReferenceEquals(browser, primary))
            await primary.OpenItemInNewTabAsync(item);
        else if (item is { IsFolder: true })
            await primary.OpenInNewTabAsync(item.FullPath);
    }

    private async void OnMenuOpenExternal(object? sender, RoutedEventArgs e)
    {
        if (MenuBrowser is { } browser)
            await browser.OpenItemExternalAsync(browser.SelectedItems.FirstOrDefault());
    }

    private void OnMenuCopy(object? sender, RoutedEventArgs e) => MenuBrowser?.CopySelectionCommand.Execute(null);
    private void OnMenuCut(object? sender, RoutedEventArgs e) => MenuBrowser?.CutSelectionCommand.Execute(null);
    private void OnMenuPaste(object? sender, RoutedEventArgs e) => MenuBrowser?.PasteCommand.Execute(null);
    private void OnMenuRename(object? sender, RoutedEventArgs e) => MenuBrowser?.RenameCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnToolbarRename(object? sender, RoutedEventArgs e) => Browser?.RenameCommand.Execute(Browser?.SelectedItems.FirstOrDefault());
    private void OnMenuFavorite(object? sender, RoutedEventArgs e) => MenuBrowser?.ToggleFavoriteCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuCompress(object? sender, RoutedEventArgs e) => MenuBrowser?.CompressSelectionCommand.Execute(null);
    private void OnMenuOpenArchiveWorkbench(object? sender, RoutedEventArgs e) => MenuBrowser?.OpenArchiveWorkbenchCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuOpenDocumentWorkbench(object? sender, RoutedEventArgs e) => MenuBrowser?.OpenDocumentWorkbenchCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuExtract(object? sender, RoutedEventArgs e) => MenuBrowser?.ExtractSelectionCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuSelectionMode(object? sender, RoutedEventArgs e) => MenuBrowser?.EnterSelectionModeCommand.Execute(null);
    private void OnMenuBatchRename(object? sender, RoutedEventArgs e) => MenuBrowser?.BatchRenameSelectionCommand.Execute(null);
    private void OnMenuChecksums(object? sender, RoutedEventArgs e) => MenuBrowser?.ComputeChecksumsCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuProperties(object? sender, RoutedEventArgs e) => MenuBrowser?.ShowPropertiesCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuCopyPath(object? sender, RoutedEventArgs e) => MenuBrowser?.CopyPathCommand.Execute(null);
    private void OnMenuOpenContainingFolder(object? sender, RoutedEventArgs e) => MenuBrowser?.OpenContainingFolderCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuRevealInSystemFileManager(object? sender, RoutedEventArgs e) => MenuBrowser?.RevealInSystemFileManagerCommand.Execute(MenuBrowser?.SelectedItems.FirstOrDefault());
    private void OnMenuDelete(object? sender, RoutedEventArgs e) => MenuBrowser?.DeleteSelectionCommand.Execute(null);

    private void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is { } shell)
            shell.IsDrawerOpen = false;
    }

    private void OnTaskCenterScrimTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is { } shell)
            shell.IsTaskCenterOpen = false;
    }

    private void OnClosePreviewPane(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
            shell.ShowPreviewPane = false;
    }

    // ---------- 键盘 ----------
    private async void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell)
            return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (e.Key == Key.F1 && !shell.IsDialogOpen)
        {
            shell.ShowKeyboardShortcutsCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (shell.Page == ShellPage.Browser && !shell.IsDialogOpen && ctrl && e.Key == Key.L)
        {
            // Ctrl+L 编辑“活动窗格”的路径：B 活动时不能把输入框开在 A。
            var target = shell.ActiveBrowser;
            target.BeginEditAddressCommand.Execute(null);
            if (target.IsAddressEditing)
            {
                if (ReferenceEquals(target, shell.SecondaryBrowser))
                    SecondaryAddressBox.Focus();
                else
                    AddressBox.Focus();
            }
            e.Handled = true;
            return;
        }
        if (shell.Page == ShellPage.Browser && !shell.IsDialogOpen && ctrl && (e.Key == Key.F || e.Key == Key.K))
        {
            // Ctrl+F/K 打开“活动窗格”的搜索面板：B 活动时不该弹出覆盖整个窗口的 A 面板。
            var target = shell.ActiveBrowser;
            target.OpenSearchPanelCommand.Execute(null);
            if (ReferenceEquals(target, shell.SecondaryBrowser))
                SecondarySearch.Focus();
            else
                BrowserSearch.Focus();
            e.Handled = true;
            return;
        }
        if (shell.Page == ShellPage.Browser && !shell.IsDialogOpen && ctrl &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0 && e.Key == Key.D)
        {
            shell.ToggleDualPaneCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Source is TextBox)
        {
            if (e.Key == Key.Escape)
                Focus();
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                await shell.TryGoBackAsync();
                e.Handled = true;
                break;
            case Key.Left when (e.KeyModifiers & KeyModifiers.Alt) != 0:
                if (shell.Page == ShellPage.Browser)
                    shell.ActiveBrowser.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when (e.KeyModifiers & KeyModifiers.Alt) != 0:
                if (shell.Page == ShellPage.Browser)
                    shell.ActiveBrowser.GoForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when (e.KeyModifiers & KeyModifiers.Alt) != 0:
                if (shell.Page == ShellPage.Browser)
                    shell.ActiveBrowser.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
        }

        if (shell.Page != ShellPage.Browser)
            return;

        switch (e.Key)
        {
            case Key.Delete:
                shell.ActiveBrowser.DeleteSelectionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                shell.ActiveBrowser.RenameCommand.Execute(shell.ActiveBrowser.SelectedItems.FirstOrDefault());
                e.Handled = true;
                break;
            case Key.F5:
                shell.ActiveBrowser.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F6 when shell.ShowDualPane:
                if (shell.IsSecondaryPaneActive)
                    shell.ActivatePrimaryPane();
                else
                    shell.ActivateSecondaryPane();
                FocusActiveFileView();
                e.Handled = true;
                break;
            case Key.R when ctrl:
                shell.ActiveBrowser.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C when ctrl && (e.KeyModifiers & KeyModifiers.Shift) != 0:
                shell.ActiveBrowser.CopyPathCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C when ctrl:
                shell.ActiveBrowser.CopySelectionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.X when ctrl:
                shell.ActiveBrowser.CutSelectionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V when ctrl:
                shell.ActiveBrowser.PasteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A when ctrl:
                shell.ActiveBrowser.SelectAllCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T when ctrl && (e.KeyModifiers & KeyModifiers.Shift) != 0:
                shell.Browser.ReopenClosedTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T when ctrl:
                shell.Browser.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when ctrl:
                shell.Browser.CloseActiveTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab when ctrl:
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
                    shell.Browser.PreviousTabCommand.Execute(null);
                else
                    shell.Browser.NextTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                if (shell.ActiveBrowser.SelectedItems.FirstOrDefault() is { } item)
                {
                    if (item.IsFolder)
                        await shell.ActiveBrowser.OpenItemAsync(item);
                    else
                        await shell.ActiveBrowser.OpenItemExternalAsync(item);
                    e.Handled = true;
                }
                break;
        }
    }
}
