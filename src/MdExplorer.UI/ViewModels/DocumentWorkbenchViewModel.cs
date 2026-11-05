using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Preview;
using PDFtoImage;
using PDFtoImage.Exceptions;
using Formatting = MdExplorer.Domain.Formatting;

namespace MdExplorer.UI.ViewModels;

/// <summary>
/// 文档工作台：文本/代码/Markdown 的显式轻编辑与 PDF 阅读共用一个全页宿主。
/// 预览仍默认只读；只有进入编辑态后才允许改文本，保存走 Shell 的串行任务中心。
/// </summary>
public sealed partial class DocumentWorkbenchViewModel(
    IFileSystemService fileSystem,
    ITextFileService textFiles,
    IDialogs dialogs,
    Func<IFileLauncherService> launcherRefresh) : ObservableObject
{
    public const long MaxTextBytes = 2L * 1024 * 1024;
    private const int MaxCachedPdfThumbnails = 36;
    private const int MaxCachedContinuousPdfPages = 10;
    private const long MaxContinuousPdfCacheBytes = 96L * 1024 * 1024;
    private static readonly int[] ZoomLevels = [50, 75, 100, 125, 150, 200, 250, 300];
    private static readonly HashSet<string> TextualDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".csv", ".tsv", ".reg",
    };

    private int _loadGeneration;
    private int _pdfRenderGeneration;
    private int _pdfVisualGeneration;
    private TextFileSnapshot? _textSnapshot;
    private string _originalText = string.Empty;
    private byte[]? _pdfData;
    private string? _pdfPassword;
    private bool _pdfPasswordKnown;
    private bool _pdfDocumentReady;
    private long _thumbnailAccessTick;
    private long _readerAccessTick;

    [ObservableProperty] private bool _hasDocument;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _failureText;
    [ObservableProperty] private string _statusText = "尚未打开文档";
    [ObservableProperty] private bool _isCompactLayout;

    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _hasExternalConflict;
    [ObservableProperty] private string? _highlightLanguage;
    [ObservableProperty] private int _textViewIndex;
    [ObservableProperty] private string _changeSummaryText = "尚未修改";
    [ObservableProperty] private string _diffPreviewText = string.Empty;

    [ObservableProperty] private int _pdfPageIndex;
    [ObservableProperty] private int _pdfPageCount;
    [ObservableProperty] private string _pdfPageInput = "1";
    [ObservableProperty] private Bitmap? _pdfImage;
    [ObservableProperty] private int _pdfReadingModeIndex;
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private string _pdfPasswordInput = string.Empty;
    [ObservableProperty] private bool _needsPdfPassword;

    [ObservableProperty] private OfficePreviewDocument? _officeDocument;
    [ObservableProperty] private int _officePageCount;
    [ObservableProperty] private int _officeSheetCount;
    [ObservableProperty] private int _officeSheetIndex;
    [ObservableProperty] private bool _officeViewerLoadFailed;

    public FileItem? CurrentItem { get; private set; }
    public ObservableCollection<PdfPageViewModel> PdfPages { get; } = [];

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? DocumentSaved;

    public bool IsPdfDocument => CurrentItem is { } item && FileKindClassifier.IsPdf(item);
    public bool IsTextDocument => CurrentItem is { } item && IsSupportedText(item);
    public bool IsOfficeDocument => CurrentItem is { } item && FileKindClassifier.IsOfficeDocument(item);
    public bool UseOfficeViewer => OfficeDocument is not null;
    /// <summary>工作面在 Office 预检失败时也必须可见，否则只能看到空白页与无处落点的 FailureText。</summary>
    public bool ShowOfficeWorkbench => IsOfficeDocument || IsDirectPdf;
    /// <summary>v0.24 起 PDF 默认由直接控件呈现；PDFium 页面位图只作为兼容工作面。</summary>
    public bool IsDirectPdf => OfficeDocument?.Format == OfficePreviewFormat.Pdf;
    public PdfViewEngine PdfEngine =>
        !IsPdfDocument ? PdfViewEngine.Direct : IsDirectPdf ? PdfViewEngine.Direct : PdfViewEngine.Raster;
    public bool PdfRasterAvailable =>
        !OperatingSystem.IsAndroid() || OperatingSystem.IsAndroidVersionAtLeast(31);
    public bool CanSwitchToRasterPdf => IsDirectPdf && _pdfData is not null && PdfRasterAvailable;
    public bool CanSwitchToDirectPdf =>
        IsPdfDocument && !IsDirectPdf && _pdfData is not null && !_pdfPasswordKnown &&
        _pdfData.LongLength <= OfficeDocumentLoader.DirectPdfWorkbenchMaxBytes &&
        OfficeDocumentLoader.IsDirectPdfCandidate(_pdfData);
    public bool ShowPdfWorkbench => IsPdfDocument && !IsDirectPdf;
    public bool IsOfficeExcel => OfficeDocument?.Format == OfficePreviewFormat.Xlsx;
    public bool CanPreviousOfficeSheet => IsOfficeExcel && OfficeSheetIndex > 0;
    public bool CanNextOfficeSheet => IsOfficeExcel && OfficeSheetIndex < OfficeSheetCount - 1;
    public string CurrentOfficeSheetName =>
        OfficeDocument is { } document && OfficeSheetIndex >= 0 && OfficeSheetIndex < document.Facts.SheetNames.Count
            ? document.Facts.SheetNames[OfficeSheetIndex]
            : string.Empty;
    public string OfficeCountText
    {
        get
        {
            if (OfficeDocument is not { } document)
                return string.Empty;
            return document.Format switch
            {
                OfficePreviewFormat.Docx => OfficePageCount > 0 ? $"{OfficePageCount:N0} 页" : document.Facts.Summary,
                OfficePreviewFormat.Pptx => $"{(OfficePageCount > 0 ? OfficePageCount : document.Facts.SlideCount ?? 0):N0} 张幻灯片",
                OfficePreviewFormat.Xlsx => OfficeSheetCount > 0
                    ? $"{OfficeSheetIndex + 1:N0}/{OfficeSheetCount:N0} · {CurrentOfficeSheetName}"
                    : document.Facts.Summary,
                _ => OfficePageCount > 0 ? $"{OfficePageCount:N0} 页 · PDF 直接预览" : "PDF 直接预览",
            };
        }
    }
    public bool HasFailure => FailureText is not null;
    public bool IsMarkdown => CurrentItem is { } item &&
                              (string.Equals(item.Extension, ".md", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.Extension, ".markdown", StringComparison.OrdinalIgnoreCase));
    public bool IsSourceView => !IsMarkdown || TextViewIndex == 0;
    public bool IsRenderedView => IsMarkdown && TextViewIndex == 1;
    public string MarkdownText => EditorText;

    public bool CanEditDocument =>
        IsTextDocument && _textSnapshot is not null && !_textSnapshot.IsReadOnly && !IsLoading && !IsSaving;
    public bool IsReadOnlyText => IsTextDocument && _textSnapshot is { IsReadOnly: true };
    public bool IsDirty => IsTextDocument && _textSnapshot is not null &&
                           !string.Equals(Canonical(EditorText), Canonical(_originalText), StringComparison.Ordinal);
    public bool NeedsCloseConfirmation => IsDirty || IsSaving;
    public bool CanSave => IsEditing && IsDirty && CanEditDocument && EstimatedEditorBytes <= MaxTextBytes;
    public bool CanFormat => IsEditing && CanEditDocument && CurrentItem is { } item && IsFormattable(item);
    public int CharacterCount => EditorText.Length;
    public int LineCount => EditorText.Length == 0 ? 1 : EditorText.Count(c => c == '\n') + 1;
    public long EstimatedEditorBytes => EstimateBytes(EditorText);
    public string EncodingText => _textSnapshot is null
        ? string.Empty
        : $"{EncodingName(_textSnapshot.Encoding)}{(_textSnapshot.HasBom ? " · BOM" : " · 无 BOM")}";
    public string LineEndingText => _textSnapshot?.NewLine switch
    {
        "\r\n" => "CRLF",
        "\r" => "CR",
        "\n" => "LF",
        _ => string.Empty,
    };
    public string TextFactsText => _textSnapshot is null
        ? string.Empty
        : IsCompactLayout
            ? $"{EncodingName(_textSnapshot.Encoding)} · {LineCount:N0} 行 · {Formatting.Bytes(EstimatedEditorBytes)}"
            : $"{EncodingText} · {LineEndingText} · {LineCount:N0} 行 · {CharacterCount:N0} 字符 · {Formatting.Bytes(EstimatedEditorBytes)}";
    public string DirtyText => IsDirty ? "已修改" : IsEditing ? "正在编辑" : "只读预览";

    public bool HasPdf => IsPdfDocument && PdfPageCount > 0;
    public bool IsContinuousPdfMode => PdfReadingModeIndex == 1;
    public bool IsSinglePagePdfMode => !IsContinuousPdfMode;
    public string PdfReadingModeText => IsContinuousPdfMode ? "连续" : "单页";
    public string PdfPageText => HasPdf ? $"第 {PdfPageIndex + 1} / {PdfPageCount} 页" : string.Empty;
    public string PdfPageCountText => HasPdf ? $"/ {PdfPageCount:N0}" : string.Empty;
    public bool CanPreviousPdfPage => HasPdf && PdfPageIndex > 0;
    public bool CanNextPdfPage => HasPdf && PdfPageIndex < PdfPageCount - 1;
    public double PdfDisplayWidth => (IsCompactLayout ? 340 : 780) * ZoomPercent / 100.0;
    public string ZoomText => $"{ZoomPercent}%";
    public string RotationText => $"{RotationDegrees}°";

    public static bool CanOpen(FileItem item)
    {
        if (item.IsFolder)
            return false;
        if (FileKindClassifier.IsPdf(item) || FileKindClassifier.IsOfficeDocument(item))
            return true;
        // 授权夹文本已可读写：写回走就地覆盖 + 写后复核，不再按路径形态拒绝进入工作台。
        return IsSupportedText(item);
    }

    public static bool IsSupportedText(FileItem item)
    {
        if (item.IsFolder)
            return false;
        if (item.Kind is FileKind.Text or FileKind.Code)
            return true;
        var extension = item.Extension ?? Path.GetExtension(item.Name);
        return item.Kind == FileKind.Data && TextualDataExtensions.Contains(extension);
    }

    partial void OnFailureTextChanged(string? value) => OnPropertyChanged(nameof(HasFailure));

    partial void OnIsCompactLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(PdfDisplayWidth));
        OnPropertyChanged(nameof(TextFactsText));
        UpdatePdfReaderLayout();
    }

    partial void OnPdfReadingModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsContinuousPdfMode));
        OnPropertyChanged(nameof(IsSinglePagePdfMode));
        OnPropertyChanged(nameof(PdfReadingModeText));
        if (!_pdfDocumentReady)
            return;

        if (IsContinuousPdfMode)
        {
            StatusText = IsCompactLayout ? "PDF 连续阅读" : "PDF 连续阅读 · PDFium 光栅兼容视图";
            EnsureContinuousNeighborhood();
        }
        else
        {
            StatusText = IsCompactLayout ? "PDF 只读阅读" : "PDF 只读阅读 · PDFium 光栅兼容视图";
            _ = RenderCurrentPdfPageAsync();
        }
    }

    partial void OnEditorTextChanged(string value)
    {
        OnPropertyChanged(nameof(MarkdownText));
        RefreshTextDerived();
    }

    partial void OnIsEditingChanged(bool value) => RefreshTextDerived();

    partial void OnTextViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSourceView));
        OnPropertyChanged(nameof(IsRenderedView));
    }

    partial void OnIsLoadingChanged(bool value) => RefreshTextDerived();

    partial void OnIsSavingChanged(bool value) => RefreshTextDerived();

    partial void OnPdfPageIndexChanged(int value)
    {
        PdfPageInput = (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        RaisePdfState();
        foreach (var page in PdfPages)
            page.IsCurrent = page.Index == value;
        if (!_pdfDocumentReady)
            return;
        if (IsContinuousPdfMode)
        {
            if (value >= 0 && value < PdfPages.Count)
                PdfPages[value].ReaderAccessTick = ++_readerAccessTick;
            TrimContinuousPageCache();
        }
        else
        {
            _ = RenderCurrentPdfPageAsync();
        }
    }

    partial void OnPdfPageCountChanged(int value) => RaisePdfState();

    partial void OnOfficeDocumentChanged(OfficePreviewDocument? value) => RaiseOfficeState();

    partial void OnOfficePageCountChanged(int value) => RaiseOfficeState();

    partial void OnOfficeSheetCountChanged(int value)
    {
        if (value > 0 && OfficeSheetIndex >= value)
            OfficeSheetIndex = value - 1;
        RaiseOfficeState();
    }

    partial void OnOfficeSheetIndexChanged(int value) => RaiseOfficeState();

    partial void OnOfficeViewerLoadFailedChanged(bool value)
    {
        if (value && IsDirectPdf)
            _ = SwitchToRasterPdfAsync();
    }

    private void RaiseOfficeState()
    {
        OnPropertyChanged(nameof(UseOfficeViewer));
        OnPropertyChanged(nameof(ShowOfficeWorkbench));
        OnPropertyChanged(nameof(IsDirectPdf));
        OnPropertyChanged(nameof(PdfEngine));
        OnPropertyChanged(nameof(CanSwitchToRasterPdf));
        OnPropertyChanged(nameof(CanSwitchToDirectPdf));
        OnPropertyChanged(nameof(ShowPdfWorkbench));
        OnPropertyChanged(nameof(IsOfficeExcel));
        OnPropertyChanged(nameof(CanPreviousOfficeSheet));
        OnPropertyChanged(nameof(CanNextOfficeSheet));
        OnPropertyChanged(nameof(CurrentOfficeSheetName));
        OnPropertyChanged(nameof(OfficeCountText));
    }

    partial void OnZoomPercentChanged(int value)
    {
        OnPropertyChanged(nameof(PdfDisplayWidth));
        OnPropertyChanged(nameof(ZoomText));
        InvalidateContinuousPageImages();
        if (!_pdfDocumentReady)
            return;
        if (IsContinuousPdfMode)
            EnsureContinuousNeighborhood();
        else
            _ = RenderCurrentPdfPageAsync();
    }

    partial void OnRotationDegreesChanged(int value)
    {
        OnPropertyChanged(nameof(RotationText));
        InvalidateContinuousPageImages();
        if (!_pdfDocumentReady)
            return;
        if (IsContinuousPdfMode)
            EnsureContinuousNeighborhood();
        else
            _ = RenderCurrentPdfPageAsync();
    }

    public bool IsSameDocument(string path) =>
        HasDocument && string.Equals(FullPath, path, StringComparison.Ordinal);

    public async Task LoadAsync(FileItem item)
    {
        var generation = ++_loadGeneration;
        ClearPdfResources(clearBytes: true);
        ClearOfficeDocument();
        _textSnapshot = null;
        _originalText = string.Empty;
        CurrentItem = item;
        HasDocument = true;
        Title = item.Name;
        FullPath = item.FullPath;
        Subtitle = FileKindClassifier.Describe(item);
        FailureText = null;
        HasExternalConflict = false;
        IsEditing = false;
        TextViewIndex = 0;
        HighlightLanguage = null;
        EditorText = string.Empty;
        DiffPreviewText = string.Empty;
        ChangeSummaryText = "尚未修改";
        StatusText = "正在读取…";
        IsLoading = true;
        RaiseDocumentKindState();

        try
        {
            if (IsPdfDocument)
            {
                await LoadPdfDocumentAsync(item, password: null, generation).ConfigureAwait(true);
            }
            else if (IsOfficeDocument)
            {
                await LoadOfficeDocumentAsync(item, generation).ConfigureAwait(true);
            }
            else if (IsTextDocument)
            {
                var snapshot = await textFiles.ReadAsync(item.FullPath, MaxTextBytes, CancellationToken.None)
                    .ConfigureAwait(true);
                if (generation != _loadGeneration)
                    return;
                _textSnapshot = snapshot;
                _originalText = snapshot.Text;
                HighlightLanguage = SourceLanguages.ForExtension(item.Extension) ?? string.Empty;
                EditorText = snapshot.Text;
                StatusText = snapshot.IsReadOnly ? "文件为只读，只能预览" : "只读预览";
            }
            else
            {
                FailureText = "此类型暂不支持文档工作台。";
                StatusText = "无法打开";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if (generation == _loadGeneration)
            {
                FailureText = ex.Message;
                StatusText = "读取失败";
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
                RaiseDocumentKindState();
                RefreshTextDerived();
            }
        }
    }

    public void Clear()
    {
        _loadGeneration++;
        ClearPdfResources(clearBytes: true);
        ClearOfficeDocument();
        _textSnapshot = null;
        _originalText = string.Empty;
        CurrentItem = null;
        HasDocument = false;
        Title = string.Empty;
        FullPath = string.Empty;
        Subtitle = string.Empty;
        EditorText = string.Empty;
        IsEditing = false;
        HasExternalConflict = false;
        FailureText = null;
        StatusText = "尚未打开文档";
        DiffPreviewText = string.Empty;
        RaiseDocumentKindState();
        RefreshTextDerived();
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty)
            return true;
        return await dialogs.ConfirmAsync(
            "放弃未保存更改？",
            $"「{Title}」还有未保存的更改。返回后这些更改不会写入磁盘。",
            "放弃更改",
            destructive: true).ConfigureAwait(true);
    }

    /// <summary>桌面窗口关闭前的统一守卫；正在保存时不允许以放弃更改的方式截断任务。</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (IsSaving)
        {
            dialogs.Notify("文档正在保存，请等待任务完成后再关闭");
            return false;
        }
        if (!IsDirty)
            return true;
        return await dialogs.ConfirmAsync(
            "关闭前放弃未保存更改？",
            $"「{Title}」还有未保存的更改。关闭窗口后这些更改将丢失且不会写入磁盘。",
            "放弃并关闭",
            destructive: true).ConfigureAwait(true);
    }

    public void SetEditorTextFromView(string text)
    {
        if (!string.Equals(EditorText, text, StringComparison.Ordinal))
            EditorText = text;
    }

    [RelayCommand]
    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void BeginEdit()
    {
        if (!CanEditDocument)
            return;
        IsEditing = true;
        StatusText = IsCompactLayout ? "正在编辑" : "正在编辑 · 保存将进入任务中心";
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
            return;
        EditorText = _originalText;
        HasExternalConflict = false;
        IsEditing = false;
        StatusText = "只读预览";
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveInternalAsync(overwriteChanged: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task OverwriteAsync() => await SaveInternalAsync(overwriteChanged: true).ConfigureAwait(true);

    private async Task SaveInternalAsync(bool overwriteChanged)
    {
        if (_textSnapshot is not { } snapshot || !IsEditing || !IsDirty || IsSaving)
            return;
        if (EstimatedEditorBytes > MaxTextBytes)
        {
            StatusText = $"内容超过 {Formatting.Bytes(MaxTextBytes)}，不能用轻编辑保存";
            return;
        }

        var generation = _loadGeneration;
        var textToSave = EditorText;
        TextFileSnapshot? saved = null;
        var conflict = false;
        IsSaving = true;
        try
        {
            var outcome = await dialogs.RunOperationAsync($"正在保存「{Title}」…", async (progress, ct) =>
            {
                progress.Report(new OperationProgress(0.2, Title, 0, 1));
                try
                {
                    saved = await textFiles.SaveAsync(snapshot, textToSave, overwriteChanged, ct).ConfigureAwait(true);
                    progress.Report(new OperationProgress(1, Title, 1, 1));
                    return new OperationOutcome(1, [], [FullPath]);
                }
                catch (TextFileChangedException ex)
                {
                    conflict = true;
                    return new OperationOutcome(0, [new OperationError(Title, ex.Message, FullPath)], []);
                }
                catch (OperationCanceledException)
                {
                    return new OperationOutcome(0, [], [], Cancelled: true);
                }
            }).ConfigureAwait(true);

            if (generation != _loadGeneration)
                return;
            if (saved is not null && outcome.Success)
            {
                _textSnapshot = saved;
                _originalText = saved.Text;
                HasExternalConflict = false;
                StatusText = $"已保存 · {DateTime.Now:HH:mm:ss}";
                RefreshTextDerived();
                DocumentSaved?.Invoke(this, FullPath);
            }
            else if (conflict)
            {
                HasExternalConflict = true;
                StatusText = "磁盘版本已变化；请选择重新加载，或明确以编辑器内容覆盖";
            }
            else if (!outcome.Cancelled)
            {
                StatusText = "保存失败，详情见任务中心";
            }
        }
        finally
        {
            if (generation == _loadGeneration)
                IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (CurrentItem is not { } item)
            return;
        if (IsDirty && !HasExternalConflict && !await ConfirmDiscardAsync().ConfigureAwait(true))
            return;
        var wasEditing = IsEditing;
        await LoadAsync(item).ConfigureAwait(true);
        if (wasEditing && CanEditDocument)
            BeginEdit();
    }

    [RelayCommand]
    private void Format()
    {
        if (!CanFormat || CurrentItem is not { } item)
            return;
        try
        {
            EditorText = IsJson(item) ? FormatJson(EditorText) : FormatXml(EditorText);
            StatusText = "已格式化，尚未保存";
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException)
        {
            StatusText = $"无法格式化：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenExternalAsync()
    {
        if (CurrentItem is { } item)
            await launcherRefresh().OpenAsync(item.FullPath, CancellationToken.None).ConfigureAwait(true);
    }

    // ---------- Office ----------

    private async Task LoadOfficeDocumentAsync(FileItem item, int generation)
    {
        var document = await OfficeDocumentLoader.LoadAsync(
            fileSystem, item, OfficeDocumentLoader.WorkbenchMaxBytes, CancellationToken.None).ConfigureAwait(true);
        if (generation != _loadGeneration)
            return;

        OfficeDocument = document;
        OfficeSheetIndex = 0;
        OfficeSheetCount = document.Facts.SheetNames.Count;
        OfficePageCount = document.Facts.SlideCount ?? 0;
        StatusText = document.Format switch
        {
            OfficePreviewFormat.Docx => IsCompactLayout
                ? "Word 只读预览"
                : $"Word 只读预览 · {document.Facts.Summary}",
            OfficePreviewFormat.Pptx => IsCompactLayout
                ? "PowerPoint 只读预览"
                : $"PowerPoint 只读预览 · {document.Facts.Summary}",
            OfficePreviewFormat.Xlsx => IsCompactLayout
                ? "Excel 只读预览"
                : $"Excel 只读预览 · {document.Facts.Summary}",
            _ => "只读预览",
        };
        RaiseOfficeState();
    }

    [RelayCommand]
    private void PreviousOfficeSheet() => OfficeSheetIndex = Math.Max(0, OfficeSheetIndex - 1);

    [RelayCommand]
    private void NextOfficeSheet() =>
        OfficeSheetIndex = Math.Min(Math.Max(0, OfficeSheetCount - 1), OfficeSheetIndex + 1);

    private void ClearOfficeDocument()
    {
        OfficeDocument = null;
        OfficePageCount = 0;
        OfficeSheetCount = 0;
        OfficeSheetIndex = 0;
        OfficeViewerLoadFailed = false;
    }

    private bool TryActivateDirectPdf()
    {
        if (_pdfData is null || _pdfPasswordKnown)
            return false;

        // 页数闸门：直接控件按页排控件树，100+ 页会把提交内存顶到上 GB 且关闭后不回落。
        if (!AllowsDirectPdfControl(_pdfData))
            return false;

        OfficePreviewDocument? document;
        try
        {
            if (!OfficeDocumentLoader.TryCreateDirectPdf(
                    _pdfData, OfficeDocumentLoader.DirectPdfWorkbenchMaxBytes, out document))
                return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        OfficeViewerLoadFailed = false;
        OfficeDocument = document;
        OfficePageCount = 0;
        OfficeSheetCount = 0;
        OfficeSheetIndex = 0;
        NeedsPdfPassword = false;
        FailureText = null;
        StatusText = IsCompactLayout
            ? "PDF 直接预览"
            : "PDF 直接预览 · OfficeViewer Avalonia 原生控件";
        RaiseOfficeState();
        return true;
    }

    /// <summary>
    /// 数得出页数就按上限判断；数不出来（损坏、加密、本机没有 PDFium）时保守放行，
    /// 让直接控件自己报错并由 <see cref="OfficeViewerLoadFailed"/> 回落到 PDFium。
    /// </summary>
    private static bool AllowsDirectPdfControl(byte[] data)
    {
        try
        {
            var pageCount = PdfPreviewRenderer.CountPages(data);
            return OfficeDocumentLoader.CanUseDirectPdf(
                pageCount, data.LongLength, OfficeDocumentLoader.DirectPdfWorkbenchMaxBytes);
        }
        catch (Exception ex) when (ex is PdfException or PdfPasswordProtectedException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or InvalidDataException or IOException)
        {
            return true;
        }
    }

    [RelayCommand]
    private void UseDirectPdf()
    {
        if (!CanSwitchToDirectPdf)
            return;
        ClearPdfResources(clearBytes: false);
        if (TryActivateDirectPdf())
            return;
        FailureText = "此 PDF 不适合直接预览控件；请继续使用 PDFium 兼容视图或系统应用。";
        StatusText = "PDF 直接预览不可用";
    }

    [RelayCommand]
    private async Task UseRasterPdfAsync() => await SwitchToRasterPdfAsync().ConfigureAwait(true);

    private async Task SwitchToRasterPdfAsync()
    {
        if (CurrentItem is not { } item || !IsDirectPdf || _pdfData is null)
            return;
        if (!PdfRasterAvailable)
        {
            FailureText = "直接 PDF 控件无法读取此文件；当前 Android 版本没有 PDFium 兼容视图，可先用系统应用打开。";
            StatusText = "PDF 不可用";
            return;
        }

        var generation = _loadGeneration;
        ClearOfficeDocument();
        FailureText = null;
        IsLoading = true;
        try
        {
            await LoadPdfDocumentAsync(item, _pdfPassword, generation, forceRaster: true).ConfigureAwait(true);
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
        RaiseOfficeState();
    }

    // ---------- PDF ----------

    private async Task LoadPdfDocumentAsync(
        FileItem item, string? password, int generation, bool forceRaster = false)
    {
        NeedsPdfPassword = false;
        if (!string.IsNullOrEmpty(password))
            _pdfPasswordKnown = true;
        var rasterUnavailableOnPlatform =
            OperatingSystem.IsAndroid() && !OperatingSystem.IsAndroidVersionAtLeast(31);
        var limit = rasterUnavailableOnPlatform
            ? OfficeDocumentLoader.DirectPdfWorkbenchMaxBytes
            : (OperatingSystem.IsAndroid() ? 64L : 128L) * 1024 * 1024;
        if (item.Length is { } knownLength && knownLength > limit)
        {
            FailureText = $"此 PDF 为 {Formatting.Bytes(item.Length)}，超过内置阅读的 {Formatting.Bytes(limit)} 安全上限。";
            StatusText = "PDF 过大";
            return;
        }

        if (_pdfData is null)
        {
            await using var source = await fileSystem.OpenReadAsync(item.FullPath, CancellationToken.None)
                .ConfigureAwait(true);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(true)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    FailureText = $"此 PDF 超过内置阅读的 {Formatting.Bytes(limit)} 安全上限。";
                    StatusText = "PDF 过大";
                    return;
                }
                await memory.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(true);
            }
            _pdfData = memory.ToArray();
        }

        // 工作台同样先交给直接 PDF 控件；它失败时 OfficeDocumentViewer 会回告页数错误，
        // 再由 SwitchToRasterPdfAsync 强制进入 PDFium。加密、密码与超直接上限则在此预分流。
        if (!forceRaster && string.IsNullOrEmpty(password) && TryActivateDirectPdf())
            return;

        if (rasterUnavailableOnPlatform)
        {
            FailureText = "此 PDF 无法由直接预览控件打开；PDFium 兼容视图需要 Android 12（API 31）或更高版本。";
            StatusText = "PDF 不可用";
            return;
        }

        try
        {
            _pdfDocumentReady = false;
            var document = await PdfPreviewRenderer.LoadAsync(
                    _pdfData, CancellationToken.None, password, RenderWidthForZoom, Rotation)
                .ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                document.FirstPage.Dispose();
                return;
            }

            _pdfPassword = password;
            PdfPageCount = document.PageCount;
            PdfPages.Clear();
            for (var i = 0; i < document.PageCount; i++)
            {
                var aspect = 0.707;
                if (document.PageSizes is { } sizes && i < sizes.Count && sizes[i].Height > 0)
                    aspect = Math.Clamp(sizes[i].Width / sizes[i].Height, 0.2, 5);
                PdfPages.Add(new PdfPageViewModel(i, aspect));
            }
            UpdatePdfReaderLayout();
            SetPdfImage(document.FirstPage);
            PdfPageIndex = 0;
            if (PdfPages.Count > 0)
                PdfPages[0].IsCurrent = true;
            _pdfDocumentReady = true;
            StatusText = IsContinuousPdfMode
                ? (IsCompactLayout ? "PDF 连续阅读" : "PDF 连续阅读 · PDFium 光栅兼容视图")
                : (IsCompactLayout ? "PDF 只读阅读" : "PDF 只读阅读 · PDFium 光栅兼容视图");
            RaisePdfState();
            RaiseOfficeState();
            if (IsContinuousPdfMode)
                EnsureContinuousNeighborhood();
        }
        catch (PdfPasswordProtectedException)
        {
            if (generation == _loadGeneration)
            {
                _pdfPasswordKnown = true;
                NeedsPdfPassword = true;
                FailureText = null;
                StatusText = "需要 PDF 密码";
                RaiseOfficeState();
            }
        }
        catch (PdfException ex)
        {
            if (generation == _loadGeneration)
            {
                FailureText = $"无法读取此 PDF：{ex.Message}";
                StatusText = "PDF 读取失败";
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (generation == _loadGeneration && (forceRaster || !TryActivateDirectPdf()))
            {
                FailureText = "直接 PDF 控件与 PDFium 兼容视图都不可用；可先用系统应用打开。";
                StatusText = "PDF 不可用";
            }
        }
    }

    [RelayCommand]
    private async Task UnlockPdfAsync()
    {
        if (CurrentItem is not { } item || !IsPdfDocument || IsLoading)
            return;
        var password = PdfPasswordInput;
        PdfPasswordInput = string.Empty;
        IsLoading = true;
        FailureText = null;
        try
        {
            await LoadPdfDocumentAsync(item, password, _loadGeneration).ConfigureAwait(true);
            if (NeedsPdfPassword)
                StatusText = "密码不正确或仍无法解锁";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PreviousPdfPage() => PdfPageIndex = Math.Max(0, PdfPageIndex - 1);

    [RelayCommand]
    private void NextPdfPage() => PdfPageIndex = Math.Min(Math.Max(0, PdfPageCount - 1), PdfPageIndex + 1);

    [RelayCommand]
    private void FirstPdfPage() => PdfPageIndex = 0;

    [RelayCommand]
    private void LastPdfPage() => PdfPageIndex = Math.Max(0, PdfPageCount - 1);

    [RelayCommand]
    private void UseSinglePagePdfMode() => PdfReadingModeIndex = 0;

    [RelayCommand]
    private void UseContinuousPdfMode() => PdfReadingModeIndex = 1;

    [RelayCommand]
    private void GoToPdfPageInput()
    {
        if (int.TryParse(PdfPageInput, out var oneBased))
            PdfPageIndex = Math.Clamp(oneBased - 1, 0, Math.Max(0, PdfPageCount - 1));
        PdfPageInput = (PdfPageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void ZoomOut() => SetZoom(next: false);

    [RelayCommand]
    private void ZoomIn() => SetZoom(next: true);

    [RelayCommand]
    private void RotateLeft() => RotationDegrees = (RotationDegrees + 270) % 360;

    [RelayCommand]
    private void RotateRight() => RotationDegrees = (RotationDegrees + 90) % 360;

    private void SetZoom(bool next)
    {
        var index = Array.IndexOf(ZoomLevels, ZoomPercent);
        if (index < 0)
            index = 2;
        ZoomPercent = ZoomLevels[Math.Clamp(index + (next ? 1 : -1), 0, ZoomLevels.Length - 1)];
    }

    private int RenderWidthForZoom => PdfPreviewRenderer.DefaultRenderWidth * ZoomPercent / 100;

    private PdfRotation Rotation => RotationDegrees switch
    {
        90 => PdfRotation.Rotate90,
        180 => PdfRotation.Rotate180,
        270 => PdfRotation.Rotate270,
        _ => PdfRotation.Rotate0,
    };

    private async Task RenderCurrentPdfPageAsync()
    {
        if (_pdfData is null || PdfPageCount <= 0)
            return;
        var loadGeneration = _loadGeneration;
        var renderGeneration = ++_pdfRenderGeneration;
        var target = Math.Clamp(PdfPageIndex, 0, PdfPageCount - 1);
        IsLoading = true;
        try
        {
            var bitmap = await PdfPreviewRenderer.RenderPageAsync(
                    _pdfData, target, CancellationToken.None, _pdfPassword, RenderWidthForZoom, Rotation)
                .ConfigureAwait(true);
            if (loadGeneration != _loadGeneration || renderGeneration != _pdfRenderGeneration)
            {
                bitmap.Dispose();
                return;
            }
            SetPdfImage(bitmap);
            FailureText = null;
        }
        catch (Exception ex) when (ex is PdfException or IOException or InvalidDataException or NotSupportedException)
        {
            if (loadGeneration == _loadGeneration && renderGeneration == _pdfRenderGeneration)
                FailureText = $"无法渲染此 PDF 页面：{ex.Message}";
        }
        finally
        {
            if (loadGeneration == _loadGeneration && renderGeneration == _pdfRenderGeneration)
                IsLoading = false;
        }
    }

    public async Task EnsureThumbnailAsync(PdfPageViewModel page)
    {
        if (_pdfData is null || page.Thumbnail is not null || page.IsThumbnailLoading)
            return;
        var generation = _loadGeneration;
        page.IsThumbnailLoading = true;
        page.LastAccessTick = ++_thumbnailAccessTick;
        try
        {
            var bitmap = await PdfPreviewRenderer.RenderPageAsync(
                    _pdfData, page.Index, CancellationToken.None, _pdfPassword, 180, PdfRotation.Rotate0)
                .ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                bitmap.Dispose();
                return;
            }
            page.Thumbnail = bitmap;
            TrimThumbnailCache();
        }
        catch (Exception ex) when (ex is PdfException or IOException or InvalidDataException)
        {
            // 缩略图失败不遮蔽正文页；页码纸形占位仍可导航。
        }
        finally
        {
            if (generation == _loadGeneration)
                page.IsThumbnailLoading = false;
        }
    }

    public async Task EnsurePageImageAsync(PdfPageViewModel page)
    {
        if (_pdfData is null || !_pdfDocumentReady || page.PageImage is not null ||
            page.IsPageLoading || page.HasPageLoadError)
            return;

        var loadGeneration = _loadGeneration;
        var visualGeneration = _pdfVisualGeneration;
        page.IsPageLoading = true;
        page.ReaderAccessTick = ++_readerAccessTick;
        try
        {
            var bitmap = await PdfPreviewRenderer.RenderPageAsync(
                    _pdfData, page.Index, CancellationToken.None, _pdfPassword, RenderWidthForZoom, Rotation)
                .ConfigureAwait(true);
            if (loadGeneration != _loadGeneration || visualGeneration != _pdfVisualGeneration)
            {
                bitmap.Dispose();
                return;
            }

            page.SetPageImage(bitmap);
            TrimContinuousPageCache();
        }
        catch (Exception ex) when (ex is PdfException or IOException or InvalidDataException or NotSupportedException)
        {
            if (loadGeneration == _loadGeneration && visualGeneration == _pdfVisualGeneration)
                page.HasPageLoadError = true;
        }
        finally
        {
            if (loadGeneration == _loadGeneration && visualGeneration == _pdfVisualGeneration)
                page.IsPageLoading = false;
        }
    }

    private void EnsureContinuousNeighborhood()
    {
        if (!_pdfDocumentReady || PdfPages.Count == 0)
            return;
        var current = Math.Clamp(PdfPageIndex, 0, PdfPages.Count - 1);
        _ = EnsurePageImageAsync(PdfPages[current]);
        if (current + 1 < PdfPages.Count)
            _ = EnsurePageImageAsync(PdfPages[current + 1]);
        if (current > 0)
            _ = EnsurePageImageAsync(PdfPages[current - 1]);
    }

    private void UpdatePdfReaderLayout()
    {
        var sideways = RotationDegrees is 90 or 270;
        foreach (var page in PdfPages)
            page.UpdateReaderLayout(PdfDisplayWidth, sideways);
    }

    private void InvalidateContinuousPageImages()
    {
        _pdfVisualGeneration++;
        foreach (var page in PdfPages)
        {
            page.SetPageImage(null);
            page.IsPageLoading = false;
            page.HasPageLoadError = false;
        }
        UpdatePdfReaderLayout();
    }

    private void TrimContinuousPageCache()
    {
        var loaded = PdfPages.Where(page => page.PageImage is not null).ToList();
        while (loaded.Count > 2)
        {
            var estimatedBytes = loaded.Sum(page => page.PageImage is { } bitmap
                ? (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4
                : 0);
            if (loaded.Count <= MaxCachedContinuousPdfPages && estimatedBytes <= MaxContinuousPdfCacheBytes)
                break;

            var victim = loaded
                .Where(page => page.Index != PdfPageIndex)
                .OrderByDescending(page => Math.Abs(page.Index - PdfPageIndex))
                .ThenBy(page => page.ReaderAccessTick)
                .FirstOrDefault();
            if (victim is null)
                break;
            victim.SetPageImage(null);
            loaded.Remove(victim);
        }
    }

    private void TrimThumbnailCache()
    {
        var loaded = PdfPages.Where(page => page.Thumbnail is not null).ToList();
        if (loaded.Count <= MaxCachedPdfThumbnails)
            return;
        foreach (var page in loaded
                     .Where(page => page.Index != PdfPageIndex)
                     .OrderByDescending(page => Math.Abs(page.Index - PdfPageIndex))
                     .ThenBy(page => page.LastAccessTick))
        {
            if (loaded.Count <= MaxCachedPdfThumbnails)
                break;
            page.Thumbnail?.Dispose();
            page.Thumbnail = null;
            loaded.Remove(page);
        }
    }

    private void SetPdfImage(Bitmap? next)
    {
        var previous = PdfImage;
        PdfImage = next;
        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
    }

    private void ClearPdfResources(bool clearBytes)
    {
        _pdfRenderGeneration++;
        _pdfVisualGeneration++;
        _pdfDocumentReady = false;
        SetPdfImage(null);
        foreach (var page in PdfPages)
        {
            page.SetPageImage(null);
            page.IsPageLoading = false;
            page.HasPageLoadError = false;
            page.Thumbnail?.Dispose();
            page.Thumbnail = null;
        }
        PdfPages.Clear();
        PdfPageCount = 0;
        PdfPageIndex = 0;
        PdfPageInput = "1";
        ZoomPercent = 100;
        RotationDegrees = 0;
        NeedsPdfPassword = false;
        PdfPasswordInput = string.Empty;
        if (clearBytes)
        {
            _pdfData = null;
            _pdfPassword = null;
            _pdfPasswordKnown = false;
        }
        RaisePdfState();
    }

    private void RaisePdfState()
    {
        OnPropertyChanged(nameof(HasPdf));
        OnPropertyChanged(nameof(PdfPageText));
        OnPropertyChanged(nameof(PdfPageCountText));
        OnPropertyChanged(nameof(CanPreviousPdfPage));
        OnPropertyChanged(nameof(CanNextPdfPage));
        OnPropertyChanged(nameof(PdfDisplayWidth));
        OnPropertyChanged(nameof(IsContinuousPdfMode));
        OnPropertyChanged(nameof(IsSinglePagePdfMode));
        OnPropertyChanged(nameof(PdfReadingModeText));
    }

    // ---------- 文本派生状态与格式化 ----------

    private void RaiseDocumentKindState()
    {
        OnPropertyChanged(nameof(IsPdfDocument));
        OnPropertyChanged(nameof(IsTextDocument));
        OnPropertyChanged(nameof(IsOfficeDocument));
        OnPropertyChanged(nameof(UseOfficeViewer));
        OnPropertyChanged(nameof(ShowOfficeWorkbench));
        OnPropertyChanged(nameof(IsDirectPdf));
        OnPropertyChanged(nameof(PdfEngine));
        OnPropertyChanged(nameof(CanSwitchToRasterPdf));
        OnPropertyChanged(nameof(CanSwitchToDirectPdf));
        OnPropertyChanged(nameof(ShowPdfWorkbench));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(IsSourceView));
        OnPropertyChanged(nameof(IsRenderedView));
        OnPropertyChanged(nameof(HasPdf));
    }

    private void RefreshTextDerived()
    {
        OnPropertyChanged(nameof(CanEditDocument));
        OnPropertyChanged(nameof(IsReadOnlyText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(NeedsCloseConfirmation));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanFormat));
        OnPropertyChanged(nameof(CharacterCount));
        OnPropertyChanged(nameof(LineCount));
        OnPropertyChanged(nameof(EstimatedEditorBytes));
        OnPropertyChanged(nameof(EncodingText));
        OnPropertyChanged(nameof(LineEndingText));
        OnPropertyChanged(nameof(TextFactsText));
        OnPropertyChanged(nameof(DirtyText));

        DiffPreviewText = string.Empty;
        if (!IsDirty)
        {
            ChangeSummaryText = "尚未修改";
            return;
        }

        var before = Canonical(_originalText).Split('\n');
        var after = Canonical(EditorText).Split('\n');
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
               before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        var removed = before.Skip(prefix).Take(before.Length - prefix - suffix).ToArray();
        var added = after.Skip(prefix).Take(after.Length - prefix - suffix).ToArray();
        ChangeSummaryText = $"已修改 · +{added.Length:N0} 行 / -{removed.Length:N0} 行 · 首处在第 {prefix + 1:N0} 行";
        var preview = new List<string>();
        foreach (var line in removed.Take(6))
            preview.Add("- " + Truncate(line));
        foreach (var line in added.Take(6))
            preview.Add("+ " + Truncate(line));
        if (removed.Length > 6 || added.Length > 6)
            preview.Add("… 其余变更未在摘要中展开");
        DiffPreviewText = string.Join("\n", preview);
    }

    private static string Truncate(string line) => line.Length <= 120 ? line : line[..117] + "…";

    private static string Canonical(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private long EstimateBytes(string text)
    {
        if (_textSnapshot is null)
            return Encoding.UTF8.GetByteCount(text);
        var encoding = _textSnapshot.Encoding switch
        {
            TextFileEncodingKind.Utf16Le => Encoding.Unicode,
            TextFileEncodingKind.Utf16Be => Encoding.BigEndianUnicode,
            TextFileEncodingKind.Utf32Le => Encoding.UTF32,
            TextFileEncodingKind.Utf32Be => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
            _ => Encoding.UTF8,
        };
        var bomBytes = _textSnapshot.HasBom
            ? _textSnapshot.Encoding switch
            {
                TextFileEncodingKind.Utf8 => 3,
                TextFileEncodingKind.Utf16Le or TextFileEncodingKind.Utf16Be => 2,
                _ => 4,
            }
            : 0;
        return encoding.GetByteCount(Canonical(text).Replace("\n", _textSnapshot.NewLine, StringComparison.Ordinal)) + bomBytes;
    }

    private static string EncodingName(TextFileEncodingKind kind) => kind switch
    {
        TextFileEncodingKind.Utf16Le => "UTF-16 LE",
        TextFileEncodingKind.Utf16Be => "UTF-16 BE",
        TextFileEncodingKind.Utf32Le => "UTF-32 LE",
        TextFileEncodingKind.Utf32Be => "UTF-32 BE",
        _ => "UTF-8",
    };

    private static bool IsFormattable(FileItem item) => IsJson(item) || IsXml(item);

    private static bool IsJson(FileItem item) =>
        string.Equals(item.Extension, ".json", StringComparison.OrdinalIgnoreCase);

    private static bool IsXml(FileItem item) =>
        item.Extension?.ToLowerInvariant() is ".xml" or ".xaml" or ".axaml" or ".csproj" or ".props" or ".targets" or ".svg";

    private static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            document.RootElement.WriteTo(writer);
        // 编辑器内部统一使用 LF；落盘时再由快照恢复原文件的主导换行，避免 Windows 格式化混入 CRLF。
        return Canonical(Encoding.UTF8.GetString(stream.ToArray())) + "\n";
    }

    private static string FormatXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        };
        using var stringReader = new StringReader(text);
        using var reader = XmlReader.Create(stringReader, settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var declaration = document.Declaration?.ToString();
        var body = Canonical(document.ToString(SaveOptions.None));
        return (string.IsNullOrEmpty(declaration) ? body : declaration + "\n" + body) + "\n";
    }
}

/// <summary>PDF 工作台的单页导航模型；缩略图与连续阅读位图都只在页面进入视口时按需生成。</summary>
public sealed partial class PdfPageViewModel(int index, double aspectRatio) : ObservableObject
{
    public int Index { get; } = index;
    public int PageNumber => Index + 1;
    public string PageText => PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string PageLabel => $"第 {PageText} 页";
    public double AspectRatio { get; } = aspectRatio;
    public double PaperHeight => Math.Clamp(76 / AspectRatio, 42, 150);
    internal long LastAccessTick { get; set; }
    internal long ReaderAccessTick { get; set; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private Bitmap? _pageImage;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isThumbnailLoading;
    [ObservableProperty] private bool _isPageLoading;
    [ObservableProperty] private bool _hasPageLoadError;
    [ObservableProperty] private double _readerWidth = 780;
    [ObservableProperty] private bool _isReaderSideways;

    public bool HasThumbnail => Thumbnail is not null;
    public bool HasPageImage => PageImage is not null;
    public double ReaderHeight => ReaderWidth * (IsReaderSideways ? AspectRatio : 1 / AspectRatio);

    public void UpdateReaderLayout(double readerWidth, bool sideways)
    {
        ReaderWidth = Math.Max(120, readerWidth);
        IsReaderSideways = sideways;
        OnPropertyChanged(nameof(ReaderHeight));
    }

    public void SetPageImage(Bitmap? next)
    {
        var previous = PageImage;
        PageImage = next;
        if (next is not null)
            HasPageLoadError = false;
        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
    }

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    partial void OnPageImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPageImage));

    partial void OnReaderWidthChanged(double value) => OnPropertyChanged(nameof(ReaderHeight));

    partial void OnIsReaderSidewaysChanged(bool value) => OnPropertyChanged(nameof(ReaderHeight));
}
