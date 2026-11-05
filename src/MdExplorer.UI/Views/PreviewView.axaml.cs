using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using MdExplorer.UI.Media;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class PreviewView : UserControl
{
    public static readonly StyledProperty<bool> IsMediaRendererActiveProperty =
        AvaloniaProperty.Register<PreviewView, bool>(nameof(IsMediaRendererActive));

    private PreviewViewModel? _vm;
    private bool _isAttached;

    static PreviewView()
    {
        IsMediaRendererActiveProperty.Changed.AddClassHandler<PreviewView>((view, _) => view.UpdateRendererHost());
    }

    public bool IsMediaRendererActive
    {
        get => GetValue(IsMediaRendererActiveProperty);
        set => SetValue(IsMediaRendererActiveProperty, value);
    }

    public PreviewView()
    {
        InitializeComponent();
        MediaRendererHost.InitializationErrorChanged += OnMediaRendererInitializationErrorChanged;
        UpdateRendererHost();
    }

    private void UpdateRendererHost()
    {
        var viewModel = _vm;
        var canHostRenderer = IsMediaRendererActive && _isAttached && viewModel is not null;
        MediaRendererHost.IsActive = canHostRenderer && viewModel!.ShowAvaloniaMediaRendererHost;

        var nativeRenderer = canHostRenderer && viewModel!.ShowNativeVideoSurface
            ? viewModel.VideoRenderer
            : null;
        if (nativeRenderer is null)
        {
            NativeVideoHost.Content = null;
        }
        else if (NativeVideoHost.Content is MediaSurfaceHost nativeHost)
        {
            if (!ReferenceEquals(nativeHost.Renderer, nativeRenderer))
                nativeHost.Renderer = nativeRenderer;
        }
        else
        {
            NativeVideoHost.Content = new MediaSurfaceHost { Renderer = nativeRenderer };
        }

        var frameSource = canHostRenderer && viewModel!.ShowFrameVideoSurface
            ? viewModel.VideoFrameSource
            : null;
        if (frameSource is null)
        {
            FrameVideoHost.Content = null;
        }
        else if (FrameVideoHost.Content is MediaFrameSurface frameHost)
        {
            if (!ReferenceEquals(frameHost.Source, frameSource))
                frameHost.Source = frameSource;
        }
        else
        {
            FrameVideoHost.Content = new MediaFrameSurface { Source = frameSource };
        }
    }

    private void OnMediaRendererInitializationErrorChanged(object? sender, EventArgs e) =>
        _vm?.ReportMediaRendererInitializationError(MediaRendererHost.InitializationError);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateRendererHost();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UpdateRendererHost();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ReportMediaRendererInitializationError(null);
        }
        _vm = DataContext as PreviewViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ReportMediaRendererInitializationError(MediaRendererHost.InitializationError);
            UpdateRendererHost();
            ApplySource();
        }
        else
        {
            UpdateRendererHost();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.TextContent) or nameof(PreviewViewModel.HighlightLanguage))
            ApplySource();
        if (e.PropertyName is nameof(PreviewViewModel.ShowAvaloniaMediaRendererHost)
            or nameof(PreviewViewModel.ShowNativeVideoSurface)
            or nameof(PreviewViewModel.VideoRenderer)
            or nameof(PreviewViewModel.ShowFrameVideoSurface)
            or nameof(PreviewViewModel.VideoFrameSource))
            UpdateRendererHost();
    }

    /// <summary>KeePass 分组/条目行没有独立的选中控件，点击即交给 VM 切换（同压缩包面包屑的既定做法）。</summary>
    private void OnKeePassGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeePassGroupRowModel group } && _vm is not null)
            _vm.SelectKeePassGroup(group);
        e.Handled = true;
    }

    private void OnKeePassEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeePassEntryRowModel entry } && _vm is not null)
            _vm.SelectKeePassEntry(entry);
        e.Handled = true;
    }

    /// <summary>把 VM 的源码文本/语种同步进只读编辑器（TextEditor 非绑定属性，代码后置是既定用法）。</summary>
    private void ApplySource()
    {
        if (_vm is null) return;
        CodeEditor.Text = _vm.TextContent ?? string.Empty;
        var language = _vm.HighlightLanguage;
        CodeEditor.SyntaxHighlighting = string.IsNullOrEmpty(language)
            ? null
            : HighlightingManager.Instance.GetDefinition(language);
    }
}
