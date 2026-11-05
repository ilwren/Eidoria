using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdExplorer.UI.Preview;
using DocxViewerControl = OfficeViewer.Avalonia.Docx.DocxViewer;
using PdfViewerControl = OfficeViewer.Avalonia.Pdf.PdfViewer;
using PptxViewerControl = OfficeViewer.Avalonia.Pptx.PptxViewer;
using XlsxViewerControl = OfficeViewer.Avalonia.Xlsx.XlsxViewer;

namespace MdExplorer.UI.Views;

/// <summary>
/// OfficeViewer.Avalonia 的唯一宿主适配层：按格式只实例化一个第三方控件，
/// 同步页数/工作表状态，并在窄视口把其固定纸面宽度整体缩放到可用宽度。
/// 控件内部仍负责连续滚动、Ctrl+滚轮及触摸缩放，离开视觉树时释放文档资源。
/// </summary>
public sealed partial class OfficeDocumentViewer : UserControl
{
    public static readonly StyledProperty<OfficePreviewDocument?> DocumentProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, OfficePreviewDocument?>(nameof(Document));

    public static readonly StyledProperty<int> SelectedSheetIndexProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, int>(
            nameof(SelectedSheetIndex), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> PageCountProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, int>(
            nameof(PageCount), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> SheetCountProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, int>(
            nameof(SheetCount), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>直接 PDF 控件已明确报错或超时仍未给出页数时置位，宿主据此切到 PDFium。</summary>
    public static readonly StyledProperty<bool> HasLoadErrorProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, bool>(
            nameof(HasLoadError), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> LoadErrorProperty =
        AvaloniaProperty.Register<OfficeDocumentViewer, string?>(
            nameof(LoadError), defaultBindingMode: BindingMode.TwoWay);

    private readonly ScaleTransform _scaleTransform = new();
    private Control? _viewer;
    private OfficePreviewFormat? _format;
    private bool _syncingSheetIndex;
    private bool _isAttached;
    private bool _releaseScheduled;
    private CancellationTokenSource? _pdfLoadMonitorCancellation;

    public OfficeDocumentViewer()
    {
        InitializeComponent();
        ScaleHost.LayoutTransform = _scaleTransform;
    }

    public OfficePreviewDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public int SelectedSheetIndex
    {
        get => GetValue(SelectedSheetIndexProperty);
        set => SetValue(SelectedSheetIndexProperty, value);
    }

    public int PageCount
    {
        get => GetValue(PageCountProperty);
        set => SetValue(PageCountProperty, value);
    }

    public int SheetCount
    {
        get => GetValue(SheetCountProperty);
        set => SetValue(SheetCountProperty, value);
    }

    public bool HasLoadError
    {
        get => GetValue(HasLoadErrorProperty);
        set => SetValue(HasLoadErrorProperty, value);
    }

    public string? LoadError
    {
        get => GetValue(LoadErrorProperty);
        set => SetValue(LoadErrorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            ReplaceViewer(change.GetNewValue<OfficePreviewDocument?>());
        }
        else if (change.Property == SelectedSheetIndexProperty && !_syncingSheetIndex &&
                 _viewer is XlsxViewerControl xlsx)
        {
            _syncingSheetIndex = true;
            try
            {
                xlsx.SetCurrentValue(
                    XlsxViewerControl.SelectedSheetIndexProperty,
                    change.GetNewValue<int>());
            }
            finally
            {
                _syncingSheetIndex = false;
            }
            UpdateScale();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScale();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        if (_viewer is null && Document is not null)
            ReplaceViewer(Document);
        else
            UpdateScale();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        var viewer = _viewer;
        if (viewer is not null && !_releaseScheduled)
        {
            _releaseScheduled = true;
            // 关窗/切页的当前帧可能仍在呈现第三方控件树；等这一轮布局/渲染工作
            // 落定后再拆控件。若控件在此之前重新挂载，则保留原实例直接续用。
            Dispatcher.UIThread.Post(() =>
            {
                _releaseScheduled = false;
                if (!_isAttached && ReferenceEquals(_viewer, viewer))
                    ReleaseViewer();
            }, DispatcherPriority.Background);
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void ReplaceViewer(OfficePreviewDocument? document)
    {
        ReleaseViewer();
        SetCurrentValue(PageCountProperty, 0);
        SetCurrentValue(SheetCountProperty, 0);
        SetCurrentValue(HasLoadErrorProperty, false);
        SetCurrentValue(LoadErrorProperty, null);
        if (document is null)
            return;

        _format = document.Format;
        Control viewer = document.Format switch
        {
            OfficePreviewFormat.Docx => new DocxViewerControl(),
            OfficePreviewFormat.Pptx => new PptxViewerControl(),
            OfficePreviewFormat.Xlsx => new XlsxViewerControl(),
            _ => new PdfViewerControl(),
        };
        viewer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        viewer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        viewer.PropertyChanged += OnViewerPropertyChanged;
        _viewer = viewer;
        ScaleHost.Child = viewer;
        UpdateScale();

        // 先订阅计数再赋字节，确保第三方控件异步完成时宿主状态不丢第一帧结果。
        switch (viewer)
        {
            case DocxViewerControl docx:
                docx.Document = document.Data;
                break;
            case PptxViewerControl pptx:
                pptx.Document = document.Data;
                break;
            case XlsxViewerControl xlsx:
                xlsx.SelectedSheetIndex = SelectedSheetIndex;
                xlsx.Document = document.Data;
                break;
            case PdfViewerControl pdf:
                pdf.Document = document.Data;
                StartPdfLoadMonitor(pdf);
                break;
        }
    }

    private void ReleaseViewer()
    {
        CancelPdfLoadMonitor();
        if (_viewer is null)
            return;
        _viewer.PropertyChanged -= OnViewerPropertyChanged;
        if (ReferenceEquals(ScaleHost.Child, _viewer))
            ScaleHost.Child = null;
        (_viewer as IDisposable)?.Dispose();
        _viewer = null;
        _format = null;
    }

    private void OnViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _viewer))
            return;

        if (sender is DocxViewerControl && e.Property == DocxViewerControl.PageCountProperty)
            SetCurrentValue(PageCountProperty, e.GetNewValue<int>());
        else if (sender is PptxViewerControl && e.Property == PptxViewerControl.PageCountProperty)
            SetCurrentValue(PageCountProperty, e.GetNewValue<int>());
        else if (sender is PdfViewerControl && e.Property == PdfViewerControl.PageCountProperty)
        {
            var pageCount = e.GetNewValue<int>();
            SetCurrentValue(PageCountProperty, pageCount);
            if (pageCount > 0)
            {
                CancelPdfLoadMonitor();
                SetCurrentValue(HasLoadErrorProperty, false);
                SetCurrentValue(LoadErrorProperty, null);
            }
        }
        else if (sender is XlsxViewerControl && e.Property == XlsxViewerControl.SheetCountProperty)
            SetCurrentValue(SheetCountProperty, e.GetNewValue<int>());
        else if (sender is XlsxViewerControl && e.Property == XlsxViewerControl.SelectedSheetIndexProperty &&
                 !_syncingSheetIndex)
        {
            _syncingSheetIndex = true;
            try
            {
                SetCurrentValue(SelectedSheetIndexProperty, e.GetNewValue<int>());
            }
            finally
            {
                _syncingSheetIndex = false;
            }
            UpdateScale();
        }
    }

    private void StartPdfLoadMonitor(PdfViewerControl pdf)
    {
        CancelPdfLoadMonitor();
        var cancellation = new CancellationTokenSource();
        _pdfLoadMonitorCancellation = cancellation;
        _ = MonitorPdfLoadAsync(pdf, cancellation);
    }

    private async Task MonitorPdfLoadAsync(PdfViewerControl pdf, CancellationTokenSource cancellation)
    {
        try
        {
            // 上游没有错误状态属性：成功以 PageCount>0 为准，失败则识别其错误横幅，
            // 最长等 10 秒。这样宿主不必等用户看见红字才知道应切到 PDFium。
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_viewer, pdf))
                    return;
                if (pdf.PageCount > 0)
                {
                    SetCurrentValue(HasLoadErrorProperty, false);
                    SetCurrentValue(LoadErrorProperty, null);
                    return;
                }

                if (attempt % 5 != 4)
                    continue;
                var error = pdf.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(text => text.Text)
                    .FirstOrDefault(text =>
                        !string.IsNullOrWhiteSpace(text) &&
                        text.StartsWith("无法加载 PDF", StringComparison.Ordinal));
                if (error is not null)
                {
                    SetCurrentValue(LoadErrorProperty, error);
                    SetCurrentValue(HasLoadErrorProperty, true);
                    return;
                }
            }

            if (ReferenceEquals(_viewer, pdf) && pdf.PageCount <= 0)
            {
                SetCurrentValue(LoadErrorProperty, "直接 PDF 控件在 10 秒内没有返回页数。");
                SetCurrentValue(HasLoadErrorProperty, true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 文档已切换或控件已释放，旧监控自然退出。
        }
        finally
        {
            if (ReferenceEquals(_pdfLoadMonitorCancellation, cancellation))
                _pdfLoadMonitorCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelPdfLoadMonitor()
    {
        var cancellation = _pdfLoadMonitorCancellation;
        _pdfLoadMonitorCancellation = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private double GetNominalWidth(OfficePreviewFormat format)
    {
        if (format == OfficePreviewFormat.Xlsx &&
            Document?.Facts.SheetNominalWidths is { } widths &&
            SelectedSheetIndex >= 0 && SelectedSheetIndex < widths.Count &&
            widths[SelectedSheetIndex] > 0)
            return widths[SelectedSheetIndex];

        return format switch
        {
            OfficePreviewFormat.Docx => 920d,
            OfficePreviewFormat.Pptx => 1380d,
            OfficePreviewFormat.Xlsx => 900d,
            _ => 760d,
        };
    }

    private void UpdateScale()
    {
        if (_viewer is null || _format is not { } format)
            return;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        // 第三方控件没有 FitWidth/Zoom 绑定：给它一个等于纸面/活动工作表逻辑宽度的
        // 设计视口，再由布局变换等比落回真实视口，Compact 下第一屏仍能看到完整页宽。
        // XLSX 小表允许适度放大，避免在宽工作台里只剩中央一条难以辨认的网格。
        var nominalWidth = GetNominalWidth(format);
        var isSpreadsheet = format == OfficePreviewFormat.Xlsx;
        var designWidth = isSpreadsheet ? nominalWidth : Math.Max(width, nominalWidth);
        var maximumScale = isSpreadsheet ? 1.75 : 1d;
        var scale = Math.Clamp(width / designWidth, 0.1, maximumScale);
        _viewer.Width = designWidth;
        _viewer.Height = height / scale;
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
    }
}
