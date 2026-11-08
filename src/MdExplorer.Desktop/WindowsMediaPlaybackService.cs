#if WINDOWS
using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using MdExplorer.Platform.Abstractions;
// 平台抽象里也有一个 MediaPlaybackState；不整体 using Windows.Media.Playback，避免同名类型二义。
using WinRtMediaPlayer = Windows.Media.Playback.MediaPlayer;
using WinRtAudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory;
using WinRtState = Windows.Media.Playback.MediaPlaybackState;
using WinRtBuffer = Windows.Storage.Streams.Buffer;

namespace MdExplorer.Desktop;

/// <summary>
/// Windows 系统解码：直接使用 Media Foundation 之上的 WinRT <see cref="WinRtMediaPlayer"/> 播音频，
/// 视频画面走「宿主帧呈现」——用 <see cref="MediaComposition"/> 按播放位置取缩略帧，
/// 解成 BGRA 交给 <see cref="IMediaFrameSink"/>，由 Avalonia 自己画。
/// <para>
/// 为什么不把画面交给 <see cref="WinRtMediaPlayer"/>：它没有把视频画进外部 HWND/位图的 API，
/// 帧服务模式（<c>IsVideoFrameServerEnabled</c>）只能拷贝到 D3D Surface，需要自带 D3D 互操作组件；
/// 而 <c>IMFMediaEngine</c> 要手写 COM vtable。两者都要在真机上验证才知道对不对，
/// 取帧循环以 30 fps 为上限，并按播放器时钟采样，实际帧率仍受系统解码与宿主绘制速度限制。
/// </para>
/// </summary>
internal sealed class WindowsMediaPlaybackService
    : IMediaPlaybackService, IMediaProbe, IMediaPlaybackPoller, IMediaVideoFrameSource, IMediaFrameTelemetry
{
    /// <summary>取帧目标尺寸：预览窗格够用，同时让每次取帧的解码开销可控。</summary>
    private const int FrameWidth = 480;
    private const int FrameHeight = 270;

    /// <summary>
    /// 按最多 30 fps 采样；真实帧率由系统解码耗时和 UI 绘制速度共同决定。
    /// v0.46 从 60 降到 30：每次取帧都是一次真正的解码，60 fps 在预览窗格里换来的
    /// 观感提升很有限，CPU 却翻倍（用户反馈占用偏高）；30 fps 仍能跟上绝大多数视频。
    /// </summary>
    private const int TargetFrameRate = 30;
    private static readonly TimeSpan FramePeriod = TimeSpan.FromSeconds(1d / TargetFrameRate);
    private static readonly TimeSpan MinimumFrameAdvance = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 取帧要「往前提一点」：一次解码 + 一次界面绘制的耗时都要几十毫秒，
    /// 若按「此刻的播放位置」取帧，画出来时声音已经走过去了，观感就是口型慢半拍。
    /// 往前 60 ms 取，正好抵掉这段流水线延迟。
    /// </summary>
    private static readonly TimeSpan FrameLead = TimeSpan.FromMilliseconds(60);

    private readonly WinRtMediaPlayer? _player;
    private readonly object _gate = new();
    private readonly object _framePumpGate = new();
    private readonly SemaphoreSlim _compositionGate = new(1, 1);
    private string? _path;
    private bool _ready;
    private bool _disposed;

    private MediaComposition? _composition;
    private string? _compositionPath;

    private IMediaFrameSink? _frameSink;
    private CancellationTokenSource? _frameCts;
    // v0.60：帧节奏账本。用户说「卡」时，这里给出帧率 / 间隔分位数 / 掉帧 / 首帧时延，
    // 不再靠主观描述。只在取帧线程与 Attach 时读写，用 Interlocked 交换引用即可。
    private MdExplorer.Domain.FramePacingStats _pacing = new(TargetFrameRate);
    private Thread? _frameThread;
    // v0.55：上一轮取帧线程还在收尾时又有人要启动（挂帧呈现面 / 暂停恢复），
    // 记下来由它在 finally 里接力。此前这种启动直接被忽略，而收尾那一轮又判定「sink 已换」
    // 不重启——于是取帧线程从此消失，画面一直黑到用户去动一下界面。
    private bool _pumpRestartPending;
    // WinRT 的 Buffer 是原生分配，按帧新建同样是把内存分配打成碎片；这里复用一块。
    // 只在取帧线程上读写（GrabFrameAsync 由该线程同步等待）。
    private WinRtBuffer? _frameBuffer;

    public WindowsMediaPlaybackService()
    {
        try
        {
            _player = new WinRtMediaPlayer();
            Player.AudioCategory = WinRtAudioCategory.Media;
            Player.MediaOpened += (_, _) =>
            {
                _ready = true;
                State = MediaPlaybackState.Paused;
                RaiseChanged();
            };
            Player.MediaEnded += (_, _) =>
            {
                State = MediaPlaybackState.Ended;
                StopFramePump();
                RaiseChanged();
            };
            Player.MediaFailed += (_, args) =>
            {
                LastError = $"Media Foundation 打不开该文件：{args.ErrorMessage}";
                State = MediaPlaybackState.Failed;
                StopFramePump();
                RaiseChanged();
            };
            Player.PlaybackSession.PlaybackStateChanged += (_, _) =>
            {
                if (Player.PlaybackSession.PlaybackState is WinRtState.Playing)
                {
                    State = MediaPlaybackState.Playing;
                    StartFramePump();
                }
                else if (State == MediaPlaybackState.Playing)
                {
                    State = MediaPlaybackState.Paused;
                    StopFramePump();
                }
                RaiseChanged();
            };
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            Unavailable = ex.Message;
        }
    }

    /// <summary>构造期失败原因（非 null 表示 WinRT 媒体栈在本机不可用）。</summary>
    public string? Unavailable { get; }

    public MediaPlaybackCapabilities Capabilities => Unavailable is null
        ? new(true, true, "Windows Media Foundation",
            $"音频由系统 Media Foundation 解码；视频画面由 MediaComposition 取帧，采样目标最高 {TargetFrameRate} fps（实际帧率受解码速度影响），" +
            "完整播放请用系统应用打开。")
        : new(false, false, "none", $"Windows 媒体栈不可用：{Unavailable}");

    public MediaRendererKind RendererKind =>
        Unavailable is null ? MediaRendererKind.FramePush : MediaRendererKind.None;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public TimeSpan Position => _ready ? Player.PlaybackSession.Position : TimeSpan.Zero;
    public TimeSpan Duration => _ready ? Player.PlaybackSession.NaturalDuration : TimeSpan.Zero;

    /// <summary>构造期失败后 <see cref="_player"/> 为 null，所有入口都先看 <see cref="Unavailable"/>。</summary>
    private WinRtMediaPlayer Player => _player!;
    public int VolumePercent { get; private set; } = 100;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (_disposed || _player is null || Unavailable is not null)
        {
            LastError = Capabilities.Diagnostic;
            State = MediaPlaybackState.Failed;
            RaiseChanged();
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        StopFramePump();
        try
        {
            lock (_gate)
            {
                Player.Source = MediaSourceFromPath(path);
                _path = path;
                _ready = false;
                // 换文件必须丢掉上一个文件的取帧组合：留着它会在播新音频时把旧视频的帧推上去。
                _composition = null;
                _compositionPath = null;
                LastError = null;
                State = MediaPlaybackState.Opening;
            }

            // 有视频轨的文件先把取帧用的组合建好；建不出来就只当音频播（并如实记录原因）。
            if (LooksLikeVideo(path))
            {
                try
                {
                    await EnsureCompositionAsync(path).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsPlatformFailure(ex))
                {
                    LastError = $"视频画面不可用：{ex.Message}";
                }
            }

            RaiseChanged();
            return true;
        }
        catch (Exception ex) when (IsPlatformFailure(ex) || ex is UriFormatException)
        {
            LastError = $"无法打开：{ex.Message}";
            State = MediaPlaybackState.Failed;
            RaiseChanged();
            return false;
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _path is null)
            return Task.CompletedTask;
        Player.Play();
        State = MediaPlaybackState.Playing;
        StartFramePump();
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (_disposed || _path is null)
            return Task.CompletedTask;
        Player.Pause();
        State = MediaPlaybackState.Paused;
        StopFramePump();
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_disposed || _path is null)
            return Task.CompletedTask;
        StopFramePump();
        Player.Pause();
        Player.PlaybackSession.Position = TimeSpan.Zero;
        State = MediaPlaybackState.Stopped;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        if (_disposed || !_ready)
            return Task.CompletedTask;
        var duration = Duration;
        var target = position < TimeSpan.Zero ? TimeSpan.Zero
            : duration > TimeSpan.Zero && position > duration ? duration : position;
        Player.PlaybackSession.Position = target;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        if (_disposed)
            return;
        try { Player.Volume = VolumePercent / 100.0; }
        catch (Exception ex) when (IsPlatformFailure(ex)) { }
        RaiseChanged();
    }

    /// <summary>MediaPlayer 的位置变化事件频率不可控，由宿主定时器统一拉取。</summary>
    public void PollPosition()
    {
        if (_ready)
            RaiseChanged();
    }

    // ---------- 帧推送（IMediaVideoFrameSource） ----------

    /// <summary>宿主交出帧呈现面：已有文件时立刻开始取帧，否则等下一次播放。</summary>
    public bool AttachFrameSink(IMediaFrameSink sink)
    {
        if (_disposed || Unavailable is not null || sink is null)
            return false;
        _frameSink = sink;
        // 重新挂接 = 新的一段观看：首帧时延从此刻算起，旧样本不再代表这段表现。
        var pacing = new MdExplorer.Domain.FramePacingStats(TargetFrameRate);
        pacing.MarkFirstFramePending(Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _pacing, pacing);
        StartFramePump();
        return true;
    }

    /// <summary>取帧账本（v0.60）。UI 只读，不参与排序或播放控制。</summary>
    public MdExplorer.Domain.FramePacingStats FramePacing => Volatile.Read(ref _pacing);

    public void DetachFrameSink()
    {
        StopFramePump();
        _frameSink = null;
    }

    private void StartFramePump()
    {
        lock (_framePumpGate)
        {
            if (_disposed || _frameSink is null || _composition is null)
                return;
            if (_frameThread is not null)
            {
                // 线程还活着说明泵在工作，不用别人接力；只有它已经被取消、正在收尾时才记一笔。
                if (_frameCts is { IsCancellationRequested: true })
                    _pumpRestartPending = true;
                return;
            }
            var sink = _frameSink;
            _pumpRestartPending = false;

            var cts = new CancellationTokenSource();
            var thread = new Thread(() => PumpFrames(sink, cts))
            {
                IsBackground = true,
                Name = "MdExplorer Windows frames",
            };
            _frameCts = cts;
            _frameThread = thread;
            thread.Start();
        }
    }

    private void StopFramePump()
    {
        CancellationTokenSource? cts;
        Thread? thread;
        lock (_framePumpGate)
        {
            cts = _frameCts;
            thread = _frameThread;
            if (cts is null)
                return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // WinRT 解码调用可能暂时不响应取消。保留线程句柄直到 PumpFrames finally 收尾，
        // 防止快速暂停/继续时启动第二个取帧线程并争抢 CPU / 乱序绘帧。
        // v0.55：这里常被 UI 线程调用（暂停/停止/切文件），等待上限从 500 ms 收到 200 ms——
        // 一帧还没解完就得等半秒，用户按一下暂停界面就僵一下。等不到也没关系：
        // 旧线程收尾时看到 _pumpRestartPending 会自己接力。
        try { thread?.Join(200); }
        catch (ThreadStateException) { }
    }

    /// <summary>
    /// 取帧循环：把播放器当前位置当作时间轴，向 <see cref="MediaComposition"/> 要一帧缩略图。
    /// 位置几乎没变就跳过，避免同一帧重复解码；任何平台异常都只让这一帧失败，不终止播放。
    /// </summary>
    private void PumpFrames(IMediaFrameSink sink, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var lastPosition = TimeSpan.MinValue;
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (State is MediaPlaybackState.Playing or MediaPlaybackState.Paused or MediaPlaybackState.Opening)
                    {
                        var position = Player.PlaybackSession.Position;
                        if (lastPosition == TimeSpan.MinValue ||
                            Math.Abs((position - lastPosition).Ticks) >= MinimumFrameAdvance.Ticks)
                        {
                            var frame = GrabFrameAsync(position + FrameLead, token).GetAwaiter().GetResult();
                            if (frame is { } raw && !token.IsCancellationRequested)
                            {
                                sink.PresentFrame(raw.Pixels, raw.Width, raw.Height, raw.Stride);
                                // 记在「真正交给宿主」之后：用户看到的节奏是呈现节奏，不是解码节奏。
                                FramePacing.Record(Stopwatch.GetTimestamp());
                                lastPosition = position;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (IsPlatformFailure(ex))
                {
                    LastError = $"取帧失败：{ex.Message}";
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                // 一次解码 + 一次呈现超过目标周期，说明「跟不上」的原因在解码侧，而不是主循环空转。
                if (elapsed > FramePeriod)
                    FramePacing.RecordOverrun(elapsed.TotalMilliseconds);
                var wait = FramePeriod - elapsed;
                if (wait <= TimeSpan.Zero)
                    continue;
                try
                {
                    if (token.WaitHandle.WaitOne(wait))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        finally
        {
            var restart = false;
            lock (_framePumpGate)
            {
                if (ReferenceEquals(_frameCts, cts))
                {
                    restart = !_disposed && _frameSink is not null &&
                        (_pumpRestartPending ||
                         (cts.IsCancellationRequested && State == MediaPlaybackState.Playing &&
                          ReferenceEquals(_frameSink, sink)));
                    _pumpRestartPending = false;
                    _frameCts = null;
                    _frameThread = null;
                }
            }
            cts.Dispose();
            if (restart)
                StartFramePump();
        }
    }

    /// <summary>取某一时刻的画面。返回 null 表示这一帧取不到（不影响音频继续播）。</summary>
    private async Task<RawFrame?> GrabFrameAsync(TimeSpan position, CancellationToken token)
    {
        var composition = _composition;
        if (composition is null)
            return null;

        var duration = SafeDuration(composition);
        if (duration > TimeSpan.Zero)
        {
            // 片尾取帧会失败，留一点余量。
            var limit = duration - TimeSpan.FromMilliseconds(80);
            if (position > limit) position = limit;
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        using var stream = await composition
            .GetThumbnailAsync(position, FrameWidth, FrameHeight, VideoFramePrecision.NearestFrame)
            .AsTask(token)
            .ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
        using var bitmap = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
            .AsTask(token)
            .ConfigureAwait(false);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
            return null;

        var tight = width * 4 * height;
        var buffer = _frameBuffer;
        if (buffer is null || buffer.Capacity < (uint)tight)
        {
            buffer = new WinRtBuffer((uint)tight);
            _frameBuffer = buffer;
        }
        buffer.Length = (uint)tight;
        bitmap.CopyToBuffer(buffer);
        Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out var copy);
        if (copy is null || copy.Length < tight)
            return null;

        // SoftwareBitmap 一般是紧凑的，但只要解码器补过行尾，就按实际长度反推行距，
        // 免得把一行像素错位地画出来。
        var stride = copy.Length % height == 0 && copy.Length / height >= width * 4
            ? copy.Length / height
            : width * 4;
        return new RawFrame(copy, width, height, stride);
    }

    private static TimeSpan SafeDuration(MediaComposition composition)
    {
        try { return composition.Duration; }
        catch (Exception ex) when (IsPlatformFailure(ex)) { return TimeSpan.Zero; }
    }

    private async Task<MediaComposition> EnsureCompositionAsync(string path)
    {
        if (_composition is not null && string.Equals(_compositionPath, path, StringComparison.Ordinal))
            return _composition;

        await _compositionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_composition is not null && string.Equals(_compositionPath, path, StringComparison.Ordinal))
                return _composition;

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(true);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask().ConfigureAwait(true);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            _composition = composition;
            _compositionPath = path;
            // 宿主的帧呈现面可能比组合先就绪（AttachFrameSink 时 _composition 还是 null），
            // 这里补一次起泵，否则第一帧要等到用户按播放才出现。
            StartFramePump();
            return composition;
        }
        finally
        {
            _compositionGate.Release();
        }
    }

    /// <summary>
    /// 预判：视频容器会真的建一次取帧组合——系统打不开轨道就如实说“不能内嵌播”，
    /// 而不是等用户按下播放才发现只有声音。
    /// </summary>
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var videoContainers = new[] { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv" };
        var audioContainers = new[] { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma" };

        if (videoContainers.Contains(extension))
        {
            if (Unavailable is not null)
                return new MediaProbeResult(false, true, true, "含视频轨", Capabilities.Diagnostic);
            try
            {
                await EnsureCompositionAsync(path).ConfigureAwait(true);
                return new MediaProbeResult(
                    true, true, true,
                    $"视频 · 系统解码 · 取帧目标最高 {TargetFrameRate} fps",
                    "画面按播放器时间轴由 MediaComposition 取出后交给 Avalonia 绘制，采样间隔覆盖 30 fps 视频；" +
                    "实际帧率仍随解码与绘制负载变化；" +
                    "HEVC 需系统扩展、DTS/AC-3 默认缺失，完整播放请用系统应用打开。");
            }
            catch (Exception ex) when (IsPlatformFailure(ex))
            {
                return new MediaProbeResult(
                    false, true, true, "含视频轨（系统解码器打不开）",
                    $"Media Foundation 无法为该文件建立取帧组合：{ex.Message}");
            }
        }

        if (audioContainers.Contains(extension))
            return new MediaProbeResult(
                true, false, true, $"音频 · {extension.TrimStart('.').ToUpperInvariant()}（系统容器清单内）",
                null);

        return new MediaProbeResult(
            false, false, false, "容器不在系统清单内",
            "Windows Media Foundation 对这些容器没有内置支持，可用系统应用打开。");
    }

    /// <summary>
    /// MediaPlayer 的 Source 只接受流或 URI。Windows 路径里的反斜杠会让 <see cref="Uri"/> 直接抛
    /// <see cref="UriFormatException"/>（`new Uri(@"C:\a.mp4")` 不是合法 URI），所以统一先转成
    /// file URI；空格与中文由 <see cref="Uri"/> 自己转义，UNC 路径按 `file://server/share` 处理。
    /// </summary>
    private static global::Windows.Media.Core.MediaSource MediaSourceFromPath(string path) =>
        global::Windows.Media.Core.MediaSource.CreateFromUri(LocalFileUri(path));

    private static Uri LocalFileUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("//", StringComparison.Ordinal)
            ? new Uri("file:" + normalized)
            : new Uri("file:///" + normalized.TrimStart('/'));
    }

    private static bool LooksLikeVideo(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".mkv" or ".webm" or ".avi" or ".wmv";

    private void RaiseChanged() => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsPlatformFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException
            or TypeLoadException
            or DllNotFoundException
            or EntryPointNotFoundException
            or NotSupportedException
            or InvalidOperationException
            or UnauthorizedAccessException
            or FileNotFoundException
            or IOException
            or ArgumentException;

    /// <summary>一帧紧凑 BGRA 画面。</summary>
    private readonly record struct RawFrame(byte[] Pixels, int Width, int Height, int Stride);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        StopFramePump();
        _composition = null;
        _compositionPath = null;
        if (_player is { } player)
        {
            try { player.Dispose(); }
            catch (Exception ex) when (IsPlatformFailure(ex)) { }
        }
        return ValueTask.CompletedTask;
    }
}
#endif
