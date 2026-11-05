using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Preview;

namespace MdExplorer.UI.ViewModels;

/// <summary>
/// 预览状态机：图片位图 / 文本（含代码、数据、未知二进制 Hex 摘要）/ ZIP 条目 / 纯元数据卡。
/// 内容生产已插件化（P0，docs/PLUGIN-SYSTEM.md §6）：经 <see cref="PreviewProviderRegistry"/>
/// 按优先级择一提供者，本类只管呈现状态与代次防串。每次 Load 先递增代次作废上一轮。
/// </summary>
public sealed partial class PreviewViewModel(
    PreviewProviderRegistry previews,
    Func<IFileLauncherService> launcherRefresh,
    IMediaPlaybackService? mediaPlayback = null,
    Func<AppSettings>? settings = null) : ObservableObject
{
    private readonly IMediaPlaybackService? _media = mediaPlayback;
    private readonly Func<AppSettings>? _settings = settings;
    private string _mediaProbeText = string.Empty;
    private string? _mediaProbeReason;
    // 探测说“系统接不了”时，能力位再好看也不能播——否则按钮按下去只会失败。
    private bool _mediaProbeAllowsPlayback = true;
    private Avalonia.Threading.DispatcherTimer? _mediaTimer;
    private string? _mediaPath;
    private bool _mediaSubscribed;
    private int _generation;
    private int _pdfRenderGeneration;
    private byte[]? _pdfData;
    private bool _switchingPdfEngine;

    [ObservableProperty] private bool _hasItem;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private string? _markdownText;
    [ObservableProperty] private string? _highlightLanguage;
    [ObservableProperty] private string? _previewEncoding;
    [ObservableProperty] private string? _failureText;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canOpen;
    /// <summary>预览区页签：0=内容，1=属性。切换文件时回到内容，文件夹则直接落到属性。</summary>
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _pdfPageIndex;
    [ObservableProperty] private int _pdfPageCount;
    [ObservableProperty] private OfficePreviewDocument? _officeDocument;
    [ObservableProperty] private int _officePageCount;
    [ObservableProperty] private int _officeSheetCount;
    [ObservableProperty] private int _officeSheetIndex;
    [ObservableProperty] private bool _officeViewerLoadFailed;

    // ── KeePass（KDBX 只读） ────────────────────────────────────────────────
    // 密文留在 _keepassData 里等用户输密码；解锁成功后立刻丢掉，绝不留到下一次。
    private byte[]? _keepassData;
    private List<KeePassEntryNode> _allKeePassEntries = [];
    [ObservableProperty] private bool _needsKeePassPassword;
    [ObservableProperty] private string _keePassPassword = string.Empty;
    [ObservableProperty] private string? _keePassErrorText;
    [ObservableProperty] private bool _hasKeePass;
    [ObservableProperty] private bool _showKeePassSecrets;
    [ObservableProperty] private string _keePassSummaryText = string.Empty;
    [ObservableProperty] private KeePassGroupRowModel? _selectedKeePassGroup;
    [ObservableProperty] private KeePassEntryRowModel? _selectedKeePassEntry;

    /// <summary>分组（按层级扁平，Depth 决定缩进）。</summary>
    public ObservableCollection<KeePassGroupRowModel> KeePassGroups { get; } = [];
    /// <summary>当前分组（含子分组）下的条目。</summary>
    public ObservableCollection<KeePassEntryRowModel> KeePassEntries { get; } = [];
    /// <summary>选中条目的字段；受保护字段默认遮罩。</summary>
    public ObservableCollection<KeePassFieldRowModel> KeePassFields { get; } = [];
    public bool IsKeePassLocked => NeedsKeePassPassword && !HasKeePass;
    public string SelectedKeePassGroupName => SelectedKeePassGroup?.Name ?? string.Empty;
    public bool HasKeePassError => !string.IsNullOrEmpty(KeePassErrorText);

    partial void OnNeedsKeePassPasswordChanged(bool value) => OnPropertyChanged(nameof(IsKeePassLocked));
    partial void OnHasKeePassChanged(bool value) => OnPropertyChanged(nameof(IsKeePassLocked));
    partial void OnKeePassErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasKeePassError));
    partial void OnShowKeePassSecretsChanged(bool value) => RefreshKeePassFields();
    partial void OnSelectedKeePassEntryChanged(KeePassEntryRowModel? value) => RefreshKeePassFields();
    partial void OnSelectedKeePassGroupChanged(KeePassGroupRowModel? value)
    {
        OnPropertyChanged(nameof(SelectedKeePassGroupName));
        RefreshKeePassEntries();
    }

    /// <summary>选中分组（含子分组）后重建条目列表；由视图的行点击处理器调用。</summary>
    public void SelectKeePassGroup(KeePassGroupRowModel group)
    {
        if (group is null)
            return;
        SelectedKeePassGroup = group;
        OnPropertyChanged(nameof(SelectedKeePassGroupName));
    }

    /// <summary>选中条目后重建字段表。</summary>
    public void SelectKeePassEntry(KeePassEntryRowModel entry) => SelectedKeePassEntry = entry;

    /// <summary>用面板上输入的主密码解锁；失败只给脱敏文案，原样保留密文等待重试。</summary>
    [RelayCommand]
    private async Task UnlockKeePassAsync()
    {
        if (_keepassData is not { } data || IsLoading)
            return;
        var password = KeePassPassword;
        KeePassErrorText = null;
        IsLoading = true;
        try
        {
            var database = await Task.Run(() => KeePassReader.Read(data, password), CancellationToken.None)
                .ConfigureAwait(true);
            KeePassPassword = string.Empty;
            _keepassData = null;
            ApplyKeePass(database);
        }
        catch (Exception ex) when (ex is KeePassUnlockException or InvalidDataException or NotSupportedException
            or IOException or ArgumentException)
        {
            KeePassErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>锁定：清掉已解出的内容与内存里的密文，再回到「等待主密码」。</summary>
    [RelayCommand]
    private async Task LockKeePassAsync()
    {
        ClearKeePassContent();
        HasKeePass = false;
        _keepassData = null;
        KeePassPassword = string.Empty;
        await LoadAsync(CurrentItem).ConfigureAwait(true);
    }

    private void ApplyKeePass(KeePassDatabase database)
    {
        ClearKeePassContent();
        _allKeePassEntries = [.. database.Entries];
        foreach (var group in Flatten(database.Root, 0))
            KeePassGroups.Add(group);
        SelectedKeePassGroup = KeePassGroups.Count > 0 ? KeePassGroups[0] : null;
        RefreshKeePassEntries();

        HasKeePass = true;
        NeedsKeePassPassword = false;
        KeePassSummaryText =
            $"{KeePassGroups.Count:N0} 个分组 · {_allKeePassEntries.Count:N0} 个条目 · " +
            $"KDBX {database.FileVersion >> 16}.{database.FileVersion & 0xFFFF}" +
            (database.Truncated ? "（超出上限，已截断）" : string.Empty);
        MetadataRows.Add(new MetadataRowModel("密码库", database.DatabaseName ?? "（未命名）"));
        MetadataRows.Add(new MetadataRowModel("条目", $"{_allKeePassEntries.Count:N0} 个（只读）"));
    }

    private static IEnumerable<KeePassGroupRowModel> Flatten(KeePassGroupNode node, int depth)
    {
        yield return new KeePassGroupRowModel(node.Path, node.Name, node.EntryCount, depth);
        foreach (var child in node.Children)
            foreach (var row in Flatten(child, depth + 1))
                yield return row;
    }

    private void RefreshKeePassEntries()
    {
        KeePassEntries.Clear();
        var filter = SelectedKeePassGroup?.Path;
        foreach (var entry in _allKeePassEntries)
        {
            if (filter is not null &&
                !string.Equals(entry.GroupPath, filter, StringComparison.Ordinal) &&
                !entry.GroupPath.StartsWith(filter + " / ", StringComparison.Ordinal))
                continue;
            KeePassEntries.Add(new KeePassEntryRowModel(entry));
        }

        SelectedKeePassEntry = KeePassEntries.Count > 0 ? KeePassEntries[0] : null;
    }

    private void RefreshKeePassFields()
    {
        KeePassFields.Clear();
        if (SelectedKeePassEntry?.Entry is not { } entry)
            return;
        foreach (var field in entry.Fields)
            KeePassFields.Add(new KeePassFieldRowModel(
                field.Key,
                field.IsProtected && !ShowKeePassSecrets ? "••••••••" : field.Value,
                field.IsProtected));
    }

    private void ClearKeePassContent()
    {
        _allKeePassEntries = [];
        KeePassGroups.Clear();
        KeePassEntries.Clear();
        KeePassFields.Clear();
        SelectedKeePassGroup = null;
        SelectedKeePassEntry = null;
        KeePassSummaryText = string.Empty;
        KeePassErrorText = null;
        ShowKeePassSecrets = false;
    }

    /// <summary>
    /// 关闭预览时把重量级负载（位图、PDF 字节、第三方直接控件）放掉了，内容需要重载。
    /// 文本类内容只占几百 KB，不必跟着一起丢。
    /// </summary>
    public bool IsPayloadReleased { get; private set; }

    public bool HasImage => Image is not null;
    public bool HasText => TextContent is not null;
    public bool HasPreviewEncoding => !string.IsNullOrWhiteSpace(PreviewEncoding);
    public string PreviewEncodingText => HasPreviewEncoding ? $"编码 · {PreviewEncoding}" : string.Empty;
    public bool HasMarkdown => MarkdownText is not null;
    /// <summary>代码呈现（编辑器：行号/高亮）——HighlightLanguage 非空即进入，包括 ""（无高亮定义）。</summary>
    public bool ShowSource => HighlightLanguage is not null;
    public bool ShowPlainText => TextContent is not null && HighlightLanguage is null;
    public bool HasArchive => ArchiveEntries.Count > 0;
    /// <summary>轻编辑/PDF 文档可从只读预览进入独立工作台。</summary>
    public bool CanOpenDocumentWorkbench =>
        CurrentItem is { } item && DocumentWorkbenchViewModel.CanOpen(item);
    /// <summary>仅本地 ZIP 家族文件可进入工作台；SAF 定位串不伪装成本地路径。</summary>
    public bool CanOpenArchiveWorkbench =>
        CurrentItem is { IsFolder: false, Kind: FileKind.Archive } item &&
        !FileLocator.IsStorageUri(item.FullPath);
    public bool HasFailure => FailureText is not null;

    // ---------- 内嵌音视频（v0.30 第一切片） ----------
    /// <summary>当前选中项是音频或视频。</summary>
    public bool IsMedia => CurrentItem is { IsFolder: false, Kind: FileKind.Audio or FileKind.Video };
    public bool IsAudioMedia => CurrentItem is { IsFolder: false, Kind: FileKind.Audio };
    public bool IsVideoMedia => CurrentItem is { IsFolder: false, Kind: FileKind.Video };
    /// <summary>
    /// 当前后端能否在应用内播放这个媒体。音频只看 CanPlayAudio；
    /// 视频还要求后端真的能交付画面（<see cref="MediaRendererKind.None"/> 时不能播，
    /// 否则会出现“有声音没画面”的假播放）。
    /// </summary>
    public bool CanPlayMediaInApp =>
        _media is { } media && IsMedia && _mediaProbeAllowsPlayback &&
        (IsAudioMedia ? media.Capabilities.CanPlayAudio : PlayVideoInline && MediaBackendCanShowVideo);

    /// <summary>
    /// 后端是否真能交付画面（不看用户偏好）：推帧后端必须真的实现帧呈现面，
    /// 否则按下去只有声音没画面。
    /// </summary>
    private bool MediaBackendCanShowVideo =>
        _media is { } media && media.Capabilities.CanPlayVideo &&
        media.RendererKind != MediaRendererKind.None &&
        (media.RendererKind != MediaRendererKind.FramePush || media is IMediaVideoFrameSource);

    /// <summary>
    /// 视频是否在应用内嵌面板里出画面。默认关（逐帧抓取可能花屏/音画不同步），
    /// 用户可以在预览卡里对当前文件显式打开；没有注入设置时保持打开（单元测试直连 VM）。
    /// </summary>
    public bool PlayVideoInline => ForceInlineVideo || (_settings?.Invoke().PlayVideoInline ?? true);

    /// <summary>视频默认外挂时，卡片给出「用系统播放器打开 / 仍要内嵌观看」两个显式入口。</summary>
    public bool ShowInlineVideoGate =>
        IsVideoMedia && !PlayVideoInline && _mediaProbeAllowsPlayback && MediaBackendCanShowVideo;

    /// <summary>是否显示媒体后端名称与诊断长文（设置项，默认显示）。</summary>
    public bool ShowMediaDiagnostics => _settings?.Invoke().ShowMediaDiagnostics ?? true;
    /// <summary>后端需要宿主的原生画面窗口（Android TextureView / Windows HWND）。</summary>
    public IMediaVideoRenderer? VideoRenderer => _media as IMediaVideoRenderer;
    public bool ShowNativeVideoSurface =>
        IsVideoMedia && CanPlayMediaInApp && PlayVideoInline &&
        _media?.RendererKind == MediaRendererKind.NativeSurface;
    /// <summary>后端只推帧（Linux Wayland 的 appsink 路线）；画面由 Avalonia 自己画。</summary>
    public IMediaVideoFrameSource? VideoFrameSource => _media as IMediaVideoFrameSource;
    public bool ShowFrameVideoSurface =>
        IsVideoMedia && CanPlayMediaInApp && PlayVideoInline &&
        _media?.RendererKind == MediaRendererKind.FramePush && VideoFrameSource is not null;
    /// <summary>对当前文件显式改用内嵌画面（风险提示就在卡片上，用户自己选的）。</summary>
    [ObservableProperty] private bool _forceInlineVideo;

    /// <summary>对当前文件打开内嵌画面；换文件时随 <see cref="LoadAsync"/> 复位。</summary>
    [RelayCommand]
    private void EnableInlineVideo() => ForceInlineVideo = true;

    partial void OnForceInlineVideoChanged(bool value) =>
        OnPropertyChanged(nameof(PlayVideoInline));

    /// <summary>设置变化后刷新依赖偏好的可见性（Shell 在写设置后调用）。</summary>
    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(PlayVideoInline));
        OnPropertyChanged(nameof(ShowMediaDiagnostics));
        RaiseMediaState();
    }

    public bool IsMediaPlaying => _media?.State == MediaPlaybackState.Playing;
    public string MediaBackendName => _media?.Capabilities.BackendName ?? "none";
    /// <summary>系统解码器对这个文件的预判（事实来自平台，不是按扩展名猜）。</summary>
    public string MediaProbeText => _mediaProbeText;
    public bool HasMediaProbe =>
        IsMedia && !ShowInlineVideoGate && !string.IsNullOrWhiteSpace(_mediaProbeText) && !CanPlayMediaInApp;
    public string MediaProbeReasonText => _mediaProbeReason ?? string.Empty;
    public bool HasMediaProbeReason => IsMedia && !string.IsNullOrWhiteSpace(_mediaProbeReason);
    /// <summary>画面交付方式：纯音频后端或未接视频时为“无画面输出”。</summary>
    public string MediaRendererText => _media?.RendererKind switch
    {
        MediaRendererKind.NativeSurface => "画面：平台原生 Surface",
        MediaRendererKind.FramePush => "画面：宿主帧呈现",
        _ => "画面：未接入（用系统应用打开）",
    };
    public string MediaBackendDiagnostic => _media?.Capabilities.Diagnostic ?? "未配置播放后端。";
    public string MediaStateText => _media?.State switch
    {
        MediaPlaybackState.Opening => "正在打开…",
        MediaPlaybackState.Playing => "正在播放",
        MediaPlaybackState.Paused => "已暂停",
        MediaPlaybackState.Stopped => "已停止",
        MediaPlaybackState.Ended => "播放结束",
        MediaPlaybackState.Failed => _media.LastError ?? "播放失败",
        _ => ShowInlineVideoGate ? "默认用系统播放器打开"
            : CanPlayMediaInApp ? "可以播放" : "此平台暂不支持内嵌播放",
    };
    public string MediaPositionText => FormatTime(_media?.Position ?? TimeSpan.Zero);
    public string MediaDurationText => FormatTime(_media?.Duration ?? TimeSpan.Zero);
    public double MediaProgressPercent
    {
        get
        {
            if (_media is not { Duration.TotalMilliseconds: > 0 } media)
                return 0;
            return Math.Clamp(media.Position.TotalMilliseconds * 100 / media.Duration.TotalMilliseconds, 0, 100);
        }
    }
    public int MediaVolumePercent
    {
        get => _media?.VolumePercent ?? 100;
        set
        {
            _media?.SetVolume(value);
            OnPropertyChanged();
        }
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";

    [RelayCommand]
    private async Task ToggleMediaPlaybackAsync()
    {
        if (_media is not { } media || !CanPlayMediaInApp)
            return;
        if (CurrentItem is not { } item)
            return;
        if (_mediaPath is null || !string.Equals(_mediaPath, item.FullPath, StringComparison.Ordinal))
        {
            if (!await media.OpenAsync(item.FullPath, CancellationToken.None).ConfigureAwait(true))
            {
                RaiseMediaState();
                return;
            }
            _mediaPath = item.FullPath;
        }

        if (media.State == MediaPlaybackState.Playing)
            await media.PauseAsync().ConfigureAwait(true);
        else
            await media.PlayAsync(CancellationToken.None).ConfigureAwait(true);
        RaiseMediaState();
        UpdateMediaTimer();
    }

    [RelayCommand]
    private async Task StopMediaPlaybackAsync()
    {
        if (_media is not { } media)
            return;
        await media.StopAsync().ConfigureAwait(true);
        RaiseMediaState();
        UpdateMediaTimer();
    }

    [RelayCommand]
    private async Task SeekMediaForwardAsync()
    {
        if (_media is not { } media)
            return;
        await media.SeekAsync(media.Position + TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        RaiseMediaState();
    }

    [RelayCommand]
    private async Task SeekMediaBackwardAsync()
    {
        if (_media is not { } media)
            return;
        var target = media.Position - TimeSpan.FromSeconds(10);
        await media.SeekAsync(target < TimeSpan.Zero ? TimeSpan.Zero : target).ConfigureAwait(true);
        RaiseMediaState();
    }

    /// <summary>
    /// 打开前先问平台「认不认这个文件」。只有实现了 <see cref="IMediaProbe"/> 的后端才答得上来，
    /// 答不上来就明说未知——绝不用扩展名猜出一个“应该能播”。
    /// </summary>
    private async Task ProbeMediaAsync(FileItem item)
    {
        _mediaProbeText = string.Empty;
        _mediaProbeReason = null;
        _mediaProbeAllowsPlayback = true;
        if (_media is IMediaProbe probe)
        {
            try
            {
                var result = await probe.ProbeAsync(item.FullPath, CancellationToken.None).ConfigureAwait(true);
                _mediaProbeText = result.Summary;
                _mediaProbeReason = result.Reason;
                _mediaProbeAllowsPlayback = result.CanPlay;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                _mediaProbeText = "系统解码器探测失败";
                _mediaProbeReason = ex.Message;
            }
        }
        else if (_media is not null)
        {
            _mediaProbeText = "系统解码器能力未知（尝试播放后才知道）";
        }
        OnPropertyChanged(nameof(CanPlayMediaInApp));
        OnPropertyChanged(nameof(ShowNativeVideoSurface));
        OnPropertyChanged(nameof(ShowFrameVideoSurface));
        OnPropertyChanged(nameof(MediaProbeText));
        OnPropertyChanged(nameof(HasMediaProbe));
        OnPropertyChanged(nameof(MediaProbeReasonText));
        OnPropertyChanged(nameof(HasMediaProbeReason));
    }

    private void RaiseMediaState()
    {
        OnPropertyChanged(nameof(IsMedia));
        OnPropertyChanged(nameof(IsAudioMedia));
        OnPropertyChanged(nameof(IsVideoMedia));
        OnPropertyChanged(nameof(CanPlayMediaInApp));
        OnPropertyChanged(nameof(ShowNativeVideoSurface));
        OnPropertyChanged(nameof(ShowFrameVideoSurface));
        OnPropertyChanged(nameof(VideoRenderer));
        OnPropertyChanged(nameof(VideoFrameSource));
        OnPropertyChanged(nameof(IsMediaPlaying));
        OnPropertyChanged(nameof(MediaStateText));
        OnPropertyChanged(nameof(MediaPositionText));
        OnPropertyChanged(nameof(MediaDurationText));
        OnPropertyChanged(nameof(MediaProgressPercent));
        OnPropertyChanged(nameof(MediaVolumePercent));
        OnPropertyChanged(nameof(MediaBackendName));
        OnPropertyChanged(nameof(MediaBackendDiagnostic));
        OnPropertyChanged(nameof(MediaRendererText));
        OnPropertyChanged(nameof(MediaProbeText));
        OnPropertyChanged(nameof(HasMediaProbe));
        OnPropertyChanged(nameof(MediaProbeReasonText));
        OnPropertyChanged(nameof(HasMediaProbeReason));
        OnPropertyChanged(nameof(PlayVideoInline));
        OnPropertyChanged(nameof(ShowInlineVideoGate));
        OnPropertyChanged(nameof(ShowMediaDiagnostics));
        OnPropertyChanged(nameof(MediaStateText));
    }

    private void SubscribeMedia()
    {
        if (_media is null || _mediaSubscribed)
            return;
        _media.PlaybackChanged += OnMediaPlaybackChanged;
        _mediaSubscribed = true;
    }

    private void OnMediaPlaybackChanged(object? sender, EventArgs e)
    {
        // 后端可能从任意线程回调：有 Avalonia 应用时切回 UI 线程，测试环境直接刷新。
        if (global::Avalonia.Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RaiseMediaState);
            return;
        }
        RaiseMediaState();
    }

    /// <summary>播放中每 500 ms 刷新一次进度；没有 Avalonia 消息泵（普通单元测试）时不建定时器。</summary>
    private void UpdateMediaTimer()
    {
        if (_media is null || global::Avalonia.Application.Current is null)
            return;
        if (IsMediaPlaying)
        {
            if (_mediaTimer is not null)
                return;
            _mediaTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _mediaTimer.Tick += (_, _) =>
            {
                if (_media is IMediaPlaybackPoller poll)
                    poll.PollPosition();
                RaiseMediaState();
            };
            _mediaTimer.Start();
            return;
        }
        StopMediaTimer();
    }

    private void StopMediaTimer()
    {
        if (_mediaTimer is null)
            return;
        _mediaTimer.Stop();
        _mediaTimer = null;
    }

    private async Task StopMediaIfAnyAsync()
    {
        StopMediaTimer();
        _mediaPath = null;
        if (_media is { } media && media.State is MediaPlaybackState.Playing or MediaPlaybackState.Paused
            or MediaPlaybackState.Opening or MediaPlaybackState.Stopped)
        {
            await media.StopAsync().ConfigureAwait(true);
        }
    }
    public bool HasPdf => PdfPageCount > 0;
    public bool HasOfficeDocument => OfficeDocument is not null;
    public bool IsOfficeExcel => OfficeDocument?.Format == OfficePreviewFormat.Xlsx;
    public bool IsDirectPdf => OfficeDocument?.Format == OfficePreviewFormat.Pdf;
    public PdfViewEngine PdfEngine => IsDirectPdf ? PdfViewEngine.Direct : PdfViewEngine.Raster;
    public bool CanUseRasterPdf =>
        !OperatingSystem.IsAndroid() || OperatingSystem.IsAndroidVersionAtLeast(31);
    public bool CanSwitchToRasterPdf => IsDirectPdf && _pdfData is not null && CanUseRasterPdf;
    public bool CanPreviousOfficeSheet => IsOfficeExcel && OfficeSheetIndex > 0;
    public bool CanNextOfficeSheet => IsOfficeExcel && OfficeSheetIndex < OfficeSheetCount - 1;
    public string OfficeStatusText
    {
        get
        {
            if (OfficeDocument is not { } document)
                return string.Empty;
            return document.Format switch
            {
                OfficePreviewFormat.Docx => OfficePageCount > 0
                    ? $"Word · 共 {OfficePageCount:N0} 页 · 只读兼容预览"
                    : $"Word · {document.Facts.Summary} · 正在排版",
                OfficePreviewFormat.Pptx => $"PowerPoint · 共 {(OfficePageCount > 0 ? OfficePageCount : document.Facts.SlideCount ?? 0):N0} 张 · 只读兼容预览",
                OfficePreviewFormat.Xlsx => OfficeSheetCount > 0
                    ? $"Excel · 工作表 {OfficeSheetIndex + 1:N0}/{OfficeSheetCount:N0} · {CurrentOfficeSheetName}"
                    : $"Excel · {document.Facts.Summary} · 正在读取",
                _ => OfficePageCount > 0
                    ? $"PDF 直接预览 · 共 {OfficePageCount:N0} 页"
                    : "PDF 直接预览 · 正在读取",
            };
        }
    }
    private string CurrentOfficeSheetName =>
        OfficeDocument is { } document && OfficeSheetIndex >= 0 && OfficeSheetIndex < document.Facts.SheetNames.Count
            ? document.Facts.SheetNames[OfficeSheetIndex]
            : string.Empty;
    /// <summary>内容页下方的图标事实条只承载数值；带字段名的完整事实仍在属性页。</summary>
    public bool HasQuickFacts => CurrentItem is { IsFolder: false };
    public string QuickSizeText => CurrentItem is { IsFolder: false } item ? Formatting.Bytes(item.Length) : string.Empty;
    public string QuickModifiedText => CurrentItem is { } item
        ? Formatting.DateTime(item.LastWriteTime, DateTimeOffset.Now)
        : string.Empty;
    public bool HasImageDimensions => HasImage && !HasPdf && Image is { PixelSize.Width: > 0, PixelSize.Height: > 0 };
    public string ImageDimensionsText => HasImageDimensions ? $"{Image!.PixelSize.Width}×{Image.PixelSize.Height}" : string.Empty;
    public string PdfPageText => HasPdf ? $"第 {PdfPageIndex + 1} / {PdfPageCount} 页" : string.Empty;
    public bool CanPreviousPdfPage => HasPdf && PdfPageIndex > 0;
    public bool CanNextPdfPage => HasPdf && PdfPageIndex < PdfPageCount - 1;

    public ObservableCollection<MetadataRowModel> MetadataRows { get; } = [];
    public ObservableCollection<ArchiveEntryRowModel> ArchiveEntries { get; } = [];

    public FileItem? CurrentItem { get; private set; }

    /// <summary>请求 Shell 打开当前本地压缩包的工作台。</summary>
    public event EventHandler<string>? ArchiveWorkbenchRequested;

    /// <summary>请求 Shell 打开当前文本/PDF 文档工作台。</summary>
    public event EventHandler<FileItem>? DocumentWorkbenchRequested;

    partial void OnImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasImageDimensions));
        OnPropertyChanged(nameof(ImageDimensionsText));
    }

    private void RaiseQuickFacts()
    {
        OnPropertyChanged(nameof(HasQuickFacts));
        OnPropertyChanged(nameof(QuickSizeText));
        OnPropertyChanged(nameof(QuickModifiedText));
        OnPropertyChanged(nameof(HasImageDimensions));
        OnPropertyChanged(nameof(ImageDimensionsText));
    }
    partial void OnTextContentChanged(string? value)
    {
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(ShowPlainText));
    }

    partial void OnMarkdownTextChanged(string? value) => OnPropertyChanged(nameof(HasMarkdown));

    partial void OnPreviewEncodingChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPreviewEncoding));
        OnPropertyChanged(nameof(PreviewEncodingText));
    }

    partial void OnHighlightLanguageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowSource));
        OnPropertyChanged(nameof(ShowPlainText));
    }
    partial void OnFailureTextChanged(string? value) => OnPropertyChanged(nameof(HasFailure));

    partial void OnPdfPageIndexChanged(int value) => RaisePdfState();

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
        if (value && IsDirectPdf && CanUseRasterPdf)
            _ = SwitchToRasterPdfAsync();
    }

    private void RaiseOfficeState()
    {
        OnPropertyChanged(nameof(HasOfficeDocument));
        OnPropertyChanged(nameof(IsOfficeExcel));
        OnPropertyChanged(nameof(IsDirectPdf));
        OnPropertyChanged(nameof(PdfEngine));
        OnPropertyChanged(nameof(CanSwitchToRasterPdf));
        OnPropertyChanged(nameof(CanPreviousOfficeSheet));
        OnPropertyChanged(nameof(CanNextOfficeSheet));
        OnPropertyChanged(nameof(OfficeStatusText));
    }

    private void RaisePdfState()
    {
        OnPropertyChanged(nameof(HasPdf));
        OnPropertyChanged(nameof(PdfPageText));
        OnPropertyChanged(nameof(CanPreviousPdfPage));
        OnPropertyChanged(nameof(CanNextPdfPage));
        OnPropertyChanged(nameof(HasImageDimensions));
        OnPropertyChanged(nameof(ImageDimensionsText));
    }


    /// <summary>
    /// 关闭预览（Compact 的浮窗关闭、Expanded+ 的预览栏关闭）时调用：直接控件是按页建控件树的，
    /// 100+ 页 PDF 会把提交内存顶到 ~1 GB 且关闭后不回落；位图与 PDF 字节同样要显式放掉。
    /// 文本类内容保留，重开预览不必重新解码。
    /// </summary>
    public void ReleaseHeavyContent()
    {
        if (!HasItem)
            return;
        var hadHeavyPayload = Image is not null || OfficeDocument is not null || _pdfData is not null ||
            HasKeePass || _keepassData is not null;
        // 抬代次：已经在跑的加载/渲染回来后自己作废，不会把内容又塞回来。
        _generation++;
        _switchingPdfEngine = false;
        Image = null;
        _pdfData = null;
        PdfPageCount = 0;
        PdfPageIndex = 0;
        OfficeDocument = null;
        OfficeViewerLoadFailed = false;
        OfficePageCount = 0;
        OfficeSheetCount = 0;
        OfficeSheetIndex = 0;
        // 密码库属于敏感内容：关闭预览就连同密文、主密码、解出的条目一起放掉。
        ClearKeePassContent();
        _keepassData = null;
        KeePassPassword = string.Empty;
        HasKeePass = false;
        NeedsKeePassPassword = false;
        IsPayloadReleased = hadHeavyPayload;
    }

    /// <summary>旧位图可能还在这一帧的绘制队列里：等这一轮渲染落地后再释放。</summary>
    partial void OnImageChanging(Bitmap? oldValue, Bitmap? newValue)
    {
        if (oldValue is null || ReferenceEquals(oldValue, newValue))
            return;
        if (global::Avalonia.Application.Current is null)
        {
            oldValue.Dispose();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            oldValue.Dispose, Avalonia.Threading.DispatcherPriority.Background);
    }

    public async Task LoadAsync(FileItem? item)
    {
        var generation = ++_generation;
        await StopMediaIfAnyAsync().ConfigureAwait(true);
        // 停媒体是异步的：等它返回时可能已经有更新的一次 LoadAsync 接管了预览。
        // 旧代次若继续往下写状态，就会把新选中的文件清成空白（v0.32 反馈的浮窗空内容）。
        if (generation != _generation)
            return;
        SubscribeMedia();
        CurrentItem = item;
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CanOpenArchiveWorkbench));
        OnPropertyChanged(nameof(CanOpenDocumentWorkbench));
        RaiseQuickFacts();
        ForceInlineVideo = false;
        Image = null;
        TextContent = null;
        MarkdownText = null;
        HighlightLanguage = null;
        PreviewEncoding = null;
        FailureText = null;
        _pdfData = null;
        _switchingPdfEngine = false;
        PdfPageIndex = 0;
        PdfPageCount = 0;
        OfficeDocument = null;
        OfficeViewerLoadFailed = false;
        OfficePageCount = 0;
        OfficeSheetCount = 0;
        OfficeSheetIndex = 0;
        ClearKeePassContent();
        _keepassData = null;
        KeePassPassword = string.Empty;
        HasKeePass = false;
        NeedsKeePassPassword = false;
        ArchiveEntries.Clear();
        MetadataRows.Clear();
        OnPropertyChanged(nameof(HasArchive));

        if (item is null)
        {
            HasItem = false;
            CanOpen = false;
            Title = string.Empty;
            Subtitle = string.Empty;
            IsLoading = false;
            return;
        }

        HasItem = true;
        IsPayloadReleased = false;
        CanOpen = !item.IsFolder;
        RaiseMediaState();
        if (IsMedia)
        {
            await ProbeMediaAsync(item).ConfigureAwait(true);
            if (generation != _generation)
                return;
            // 自动播放只在设置里显式打开时生效；视频还必须内嵌出画面，
            // 否则用户听到声音却看不到画面，比不播更糟。
            if ((_settings?.Invoke().MediaAutoPlay ?? false) && CanPlayMediaInApp)
            {
                await ToggleMediaPlaybackAsync().ConfigureAwait(true);
                if (generation != _generation)
                    return;
            }
        }
        SelectedTabIndex = item.IsFolder ? 1 : 0;
        Title = item.Name;
        Subtitle = FileKindClassifier.Describe(item);
        BuildMetadata(item);
        if (item.IsFolder)
        {
            IsLoading = false;
            return;
        }

        IsLoading = true;
        try
        {
            var provider = previews.Resolve(item);
            if (provider is null)
            {
                FailureText = "此类型暂不支持内置预览，可先用系统应用打开。";
                return;
            }

            var content = await provider.LoadAsync(item, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation) return;
            Image = content.Image;
            TextContent = content.Text;
            MarkdownText = content.Markdown;
            HighlightLanguage = content.HighlightLanguage;
            PreviewEncoding = content.TextEncoding;
            if (content.Entries is not null)
            {
                foreach (var entry in content.Entries)
                    ArchiveEntries.Add(entry);
                OnPropertyChanged(nameof(HasArchive));
            }
            if (content.Pdf is { } pdf)
            {
                _pdfData = pdf.Data;
                PdfPageCount = pdf.PageCount;
                PdfPageIndex = 0;
                Image = pdf.FirstPage;
                MetadataRows.Add(new MetadataRowModel("页数", $"{pdf.PageCount:N0} 页"));
            }
            if (content.Office is { } office)
            {
                if (office.Format == OfficePreviewFormat.Pdf)
                    _pdfData = office.Data;
                OfficeDocument = office;
                OfficeSheetIndex = 0;
                OfficeSheetCount = office.Facts.SheetNames.Count;
                OfficePageCount = office.Facts.SlideCount ?? 0;
                MetadataRows.Add(new MetadataRowModel("结构", office.Facts.Summary));
                MetadataRows.Add(new MetadataRowModel("渲染", OfficeDocumentLoader.EngineDescription));
                if (office.Facts.SheetNames.Count > 0)
                    MetadataRows.Add(new MetadataRowModel("工作表", string.Join("、", office.Facts.SheetNames)));
            }
            if (content.KeePassUnlock is { } unlock)
            {
                _keepassData = unlock.Data;
                NeedsKeePassPassword = true;
                MetadataRows.Add(new MetadataRowModel("密码库", "KeePass KDBX · 只读解锁"));
            }
            // 媒体项的可用性/失败原因由媒体面板逐条说明；再叠一句兜底文案只会自相矛盾。
            FailureText = IsMedia ? null : content.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if (generation == _generation)
                FailureText = $"预览失败：{ex.Message}";
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PreviousPdfPageAsync() => await GoToPdfPageAsync(PdfPageIndex - 1).ConfigureAwait(true);

    [RelayCommand]
    private async Task NextPdfPageAsync() => await GoToPdfPageAsync(PdfPageIndex + 1).ConfigureAwait(true);

    [RelayCommand]
    private async Task FirstPdfPageAsync() => await GoToPdfPageAsync(0).ConfigureAwait(true);

    [RelayCommand]
    private async Task LastPdfPageAsync() => await GoToPdfPageAsync(PdfPageCount - 1).ConfigureAwait(true);

    [RelayCommand]
    private void PreviousOfficeSheet() => OfficeSheetIndex = Math.Max(0, OfficeSheetIndex - 1);

    [RelayCommand]
    private void NextOfficeSheet() =>
        OfficeSheetIndex = Math.Min(Math.Max(0, OfficeSheetCount - 1), OfficeSheetIndex + 1);

    [RelayCommand]
    private async Task UseRasterPdfAsync() => await SwitchToRasterPdfAsync().ConfigureAwait(true);

    private async Task SwitchToRasterPdfAsync()
    {
        if (_switchingPdfEngine || !IsDirectPdf || _pdfData is null || !CanUseRasterPdf)
            return;

        _switchingPdfEngine = true;
        var generation = _generation;
        var renderGeneration = ++_pdfRenderGeneration;
        var data = _pdfData;
        OfficeDocument = null;
        OfficePageCount = 0;
        OfficeViewerLoadFailed = false;
        FailureText = null;
        IsLoading = true;
        try
        {
            var pdf = await PdfPreviewRenderer.LoadAsync(data, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation || renderGeneration != _pdfRenderGeneration)
            {
                pdf.FirstPage.Dispose();
                return;
            }

            PdfPageCount = pdf.PageCount;
            PdfPageIndex = 0;
            Image = pdf.FirstPage;
            for (var i = MetadataRows.Count - 1; i >= 0; i--)
            {
                if (MetadataRows[i].Key is "页数" or "结构" or "渲染")
                    MetadataRows.RemoveAt(i);
            }
            MetadataRows.Add(new MetadataRowModel("页数", $"{pdf.PageCount:N0} 页"));
            MetadataRows.Add(new MetadataRowModel("渲染", "PDFtoImage/PDFium（MIT）· 光栅兼容视图"));
            RaiseOfficeState();
            RaisePdfState();
        }
        catch (Exception ex)
        {
            if (generation == _generation && renderGeneration == _pdfRenderGeneration)
                FailureText = $"直接 PDF 预览不可用，兼容视图也无法读取：{ex.Message}";
        }
        finally
        {
            if (generation == _generation && renderGeneration == _pdfRenderGeneration)
                IsLoading = false;
            _switchingPdfEngine = false;
        }
    }

    private async Task GoToPdfPageAsync(int pageIndex)
    {
        if (_pdfData is null || PdfPageCount <= 0)
            return;
        var target = Math.Clamp(pageIndex, 0, PdfPageCount - 1);
        if (target == PdfPageIndex && Image is not null)
            return;

        var documentGeneration = _generation;
        var renderGeneration = ++_pdfRenderGeneration;
        PdfPageIndex = target;
        FailureText = null;
        IsLoading = true;
        try
        {
            var bitmap = await PdfPreviewRenderer
                .RenderPageAsync(_pdfData, target, CancellationToken.None)
                .ConfigureAwait(true);
            if (documentGeneration != _generation || renderGeneration != _pdfRenderGeneration)
            {
                bitmap.Dispose();
                return;
            }

            // 旧位图由 OnImageChanging 在这一帧绘制后释放，这里不再重复 Dispose。
            Image = bitmap;
        }
        catch (Exception ex)
        {
            if (documentGeneration == _generation && renderGeneration == _pdfRenderGeneration)
                FailureText = $"无法渲染此 PDF 页面：{ex.Message}";
        }
        finally
        {
            if (documentGeneration == _generation && renderGeneration == _pdfRenderGeneration)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenDocumentWorkbench()
    {
        if (CanOpenDocumentWorkbench && CurrentItem is { } item)
            DocumentWorkbenchRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void OpenArchiveWorkbench()
    {
        if (CanOpenArchiveWorkbench && CurrentItem is { } item)
            ArchiveWorkbenchRequested?.Invoke(this, item.FullPath);
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (CurrentItem is null) return;
        // Android 上启动器在 MainView 挂载后替换，故每次取最新实例
        await launcherRefresh().OpenAsync(CurrentItem.FullPath, CancellationToken.None).ConfigureAwait(true);
    }

    private void BuildMetadata(FileItem item)
    {
        MetadataRows.Add(new MetadataRowModel("类型", FileKindClassifier.Describe(item)));
        if (!item.IsFolder)
            MetadataRows.Add(new MetadataRowModel("大小", $"{Formatting.Bytes(item.Length)}（{item.Length:N0} 字节）"));
        MetadataRows.Add(new MetadataRowModel("修改时间", Formatting.FullDateTime(item.LastWriteTime)));
        MetadataRows.Add(new MetadataRowModel("位置", Path.GetDirectoryName(item.FullPath) ?? item.FullPath));
        if (item.IsSymlink)
            MetadataRows.Add(new MetadataRowModel("链接", "符号链接"));
        if (item.IsHidden)
            MetadataRows.Add(new MetadataRowModel("可见性", "隐藏"));
    }
}
