#if !WINDOWS
using System.Runtime.InteropServices;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Desktop;

/// <summary>
/// Linux 系统解码：直接驱动系统里的 GStreamer（playbin 会自动挑 ffmpeg/vaapi/alsa 组件）。
/// 选择它的理由：GStreamer 是发行版自带的 LGPL 媒体栈，不用往仓库里塞 FFmpeg 二进制，
/// 也不用自己维护原生构建链；代价是「装了哪些插件」由发行版决定，所以我们只报事实：
/// 库缺失就能力位 false，绝不给一个看得见却不出声的播放器。
/// <para>
/// 视频画面走 <c>appsink</c> 取帧再推给宿主（<see cref="MediaRendererKind.FramePush"/>）：
/// Wayland 下拿不到可交给解码器的窗口 id，原生画面宿主这条路走不通。取帧线程把 BGRA 帧
/// 拷进托管数组后交给 <see cref="IMediaFrameSink"/>，由 Avalonia 自己画。
/// </para>
/// </summary>
internal sealed class GStreamerMediaPlaybackService
    : IMediaPlaybackService, IMediaPlaybackPoller, IMediaProbe, IMediaVideoFrameSource
{
    private const string Library = "libgstreamer-1.0.so.0";
    private const string AppLibrary = "libgstapp-1.0.so.0";
    private const string VideoSinkName = "mdexplorer-vsink";
    private const int MapFlagRead = 1;

    // GstState / GstFormat / GstSeekFlags 的数值来自 gst/gstelement.h 与 gst/gstformat.h。
    private const int StateNull = 1;
    private const int StatePaused = 3;
    private const int StatePlaying = 4;
    private const int FormatTime = 3;
    private const int SeekFlagFlush = 1;
    private const int SeekFlagAccurate = 2;

    private static readonly object InitGate = new();
    private static bool _initialized;
    private static string? _initFailure;
    private static bool _videoAvailable;
    private static string? _videoUnavailableReason;

    private readonly object _gate = new();
    private IntPtr _pipeline;
    private bool _prepared;
    private bool _ended;
    private bool _disposed;

    // 视频：取帧线程（appsink 在播放态才由 playbin 收进自己的 bin，所以由线程自己去找）。
    private IMediaFrameSink? _frameSink;
    private CancellationTokenSource? _framePumpCts;
    private Thread? _framePump;

    public GStreamerMediaPlaybackService()
    {
        EnsureInitialized();
    }

    public MediaPlaybackCapabilities Capabilities => _initFailure is null
        ? new(true, _videoAvailable, "GStreamer（系统媒体栈）",
            _videoAvailable
                ? "音视频都由发行版自带的 GStreamer 解码；视频帧经 appsink 推给宿主（Wayland 无窗口 id 也能出画面）。"
                : $"音频由发行版自带的 GStreamer 解码；{_videoUnavailableReason}视频画面交系统应用打开。")
        : new(false, false, "none", $"GStreamer 不可用：{_initFailure}");

    /// <summary>视频帧交付方式：装齐 appsink/videoconvert 才是 <see cref="MediaRendererKind.FramePush"/>。</summary>
    public MediaRendererKind RendererKind =>
        _videoAvailable ? MediaRendererKind.FramePush : MediaRendererKind.None;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public int VolumePercent { get; private set; } = 100;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    public TimeSpan Position
    {
        get
        {
            if (!_prepared || _pipeline == IntPtr.Zero)
                return TimeSpan.Zero;
            return QueryPosition(_pipeline) ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (!_prepared || _pipeline == IntPtr.Zero)
                return TimeSpan.Zero;
            return QueryDuration(_pipeline) ?? TimeSpan.Zero;
        }
    }

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (_disposed || _initFailure is not null)
        {
            LastError = Capabilities.Diagnostic;
            State = MediaPlaybackState.Failed;
            RaiseChanged();
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            try
            {
                DestroyPipeline();
                var uri = new Uri(path).AbsoluteUri.Replace("\"", "%22", StringComparison.Ordinal);
                // 只要 appsink 可用就给 playbin 一个视频 sink：音频文件根本不会走到它，
                // 视频文件则把每帧交给我们推给宿主。sync=true 让画面跟着音频时钟走。
                var videoSink = _videoAvailable
                    ? $" video-sink=\"appsink name={VideoSinkName} caps=video/x-raw,format=BGRA sync=true max-buffers=2 drop=true\""
                    : string.Empty;
                var launch = $"playbin uri=\"{uri}\"{videoSink}";
                _pipeline = gst_parse_launch(launch, out var error);
                if (_pipeline == IntPtr.Zero)
                {
                    var message = error == IntPtr.Zero ? "无法创建 playbin" : "gst_parse_launch 失败";
                    LastError = $"GStreamer 无法为该文件建立播放管线：{message}";
                    State = MediaPlaybackState.Failed;
                    RaiseChanged();
                    return Task.FromResult(false);
                }

                ApplyVolume();
                var change = gst_element_set_state(_pipeline, StatePaused);
                // ASYNC 是正常返回：真正的失败要等 get_state 或后续 poll 才知道。
                if (change == 0)
                {
                    LastError = "GStreamer 拒绝了暂停态切换（可能缺少解码插件）。";
                    State = MediaPlaybackState.Failed;
                    DestroyPipeline();
                    RaiseChanged();
                    return Task.FromResult(false);
                }

                _prepared = true;
                _ended = false;
                LastError = null;
                State = MediaPlaybackState.Paused;
                StartFramePumpLocked();
            }
            catch (Exception ex) when (IsPlatformFailure(ex) || ex is UriFormatException)
            {
                LastError = $"无法打开：{ex.Message}";
                State = MediaPlaybackState.Failed;
                DestroyPipeline();
            }
        }

        RaiseChanged();
        return Task.FromResult(State == MediaPlaybackState.Paused);
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_prepared || _pipeline == IntPtr.Zero || _ended)
                return Task.CompletedTask;
            if (gst_element_set_state(_pipeline, StatePlaying) == 0)
            {
                LastError = "GStreamer 无法进入播放态（缺少解码插件或设备被占用）。";
                State = MediaPlaybackState.Failed;
                RaiseChanged();
                return Task.CompletedTask;
            }
            State = MediaPlaybackState.Playing;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_gate)
        {
            if (!_prepared || _pipeline == IntPtr.Zero)
                return Task.CompletedTask;
            gst_element_set_state(_pipeline, StatePaused);
            State = MediaPlaybackState.Paused;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_prepared || _pipeline == IntPtr.Zero)
                return Task.CompletedTask;
            StopFramePumpLocked();
            gst_element_set_state(_pipeline, StateNull);
            _prepared = false;
            State = MediaPlaybackState.Stopped;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        lock (_gate)
        {
            if (!_prepared || _pipeline == IntPtr.Zero)
                return Task.CompletedTask;
            var target = position < TimeSpan.Zero ? 0 : (long)position.TotalMilliseconds * 1_000_000L;
            var duration = QueryDuration(_pipeline);
            if (duration is { } total)
            {
                var limit = total.Ticks * 100L;
                if (target > limit) target = limit;
            }
            gst_element_seek_simple(_pipeline, FormatTime, SeekFlagFlush | SeekFlagAccurate, target);
            _ended = false;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            if (_prepared && _pipeline != IntPtr.Zero)
                ApplyVolume();
        }
        RaiseChanged();
    }

    /// <summary>由宿主定时器调用：GStreamer 的状态靠查询推进，没有托管事件。</summary>
    public void PollPosition()
    {
        if (!_prepared || _pipeline == IntPtr.Zero)
            return;
        var duration = QueryDuration(_pipeline);
        var position = QueryPosition(_pipeline);
        if (State == MediaPlaybackState.Playing && duration is { } total && position is { } current &&
            total > TimeSpan.Zero && current >= total - TimeSpan.FromMilliseconds(250))
        {
            _ended = true;
            State = MediaPlaybackState.Ended;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyVolume()
    {
        try
        {
            gst_util_set_object_arg(_pipeline, "volume", (VolumePercent / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            // 音量设置失败不影响播放本身，只影响音量。
        }
    }

    private void DestroyPipeline()
    {
        // 先让取帧线程停下来再拆管线：它在 appsink 上等样本，管线被拆掉后再取就是野指针。
        StopFramePumpLocked();
        if (_pipeline == IntPtr.Zero)
            return;
        try
        {
            gst_element_set_state(_pipeline, StateNull);
            gst_object_unref(_pipeline);
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
        }
        _pipeline = IntPtr.Zero;
        _prepared = false;
    }

    // ---------- 帧推送（IMediaVideoFrameSource） ----------

    /// <summary>宿主交出帧呈现面：下次打开文件后取帧线程就开始把画面推过去。</summary>
    public bool AttachFrameSink(IMediaFrameSink sink)
    {
        if (!_videoAvailable || sink is null)
            return false;
        lock (_gate)
        {
            _frameSink = sink;
            StartFramePumpLocked();
        }
        return true;
    }

    public void DetachFrameSink()
    {
        lock (_gate)
        {
            StopFramePumpLocked();
            _frameSink = null;
        }
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private void StartFramePumpLocked()
    {
        if (!_videoAvailable || _frameSink is null || _pipeline == IntPtr.Zero || _framePump is not null)
            return;
        var sink = _frameSink;
        var pipeline = _pipeline;
        _framePumpCts = new CancellationTokenSource();
        var token = _framePumpCts.Token;
        _framePump = new Thread(() => PumpFrames(pipeline, sink, token))
        {
            IsBackground = true,
            Name = "MdExplorer GStreamer frames",
        };
        _framePump.Start();
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private void StopFramePumpLocked()
    {
        var cts = _framePumpCts;
        var thread = _framePump;
        _framePumpCts = null;
        _framePump = null;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        // 取帧线程只做非阻塞推送，不会卡在这里；给足 500ms 兜底，绝不把 UI 线程锁死。
        try { thread?.Join(500); }
        catch (ThreadStateException) { }
        cts.Dispose();
    }

    /// <summary>
    /// 取帧循环：appsink 按播放时钟交付样本，这里只做「读 caps → 拷成紧凑 BGRA → 交给宿主」。
    /// 任何原生调用失败都直接退出，不再推帧——画面停住比崩溃或绿屏诚实。
    /// </summary>
    private void PumpFrames(IntPtr pipeline, IMediaFrameSink sink, CancellationToken token)
    {
        var appsink = IntPtr.Zero;
        try
        {
            // playbin 要到播放态才把 video-sink 收进自己的 bin，所以这里等它就位（最多 5 秒）。
            for (var attempt = 0; attempt < 100 && appsink == IntPtr.Zero && !token.IsCancellationRequested; attempt++)
            {
                appsink = gst_bin_get_by_name(pipeline, VideoSinkName);
                if (appsink == IntPtr.Zero)
                    Thread.Sleep(50);
            }
            if (appsink == IntPtr.Zero)
            {
                LastError = "GStreamer 没有把 appsink 挂进播放管线，视频画面未启动。";
                return;
            }

            while (!token.IsCancellationRequested)
            {
                var sample = gst_app_sink_try_pull_sample(appsink, 200_000_000UL); // 200ms
                if (sample == IntPtr.Zero)
                    continue;
                try
                {
                    PublishSample(sample, sink);
                }
                finally
                {
                    gst_sample_unref(sample);
                }
            }
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            LastError = $"视频帧推送已停止：{ex.Message}";
        }
        finally
        {
            if (appsink != IntPtr.Zero)
            {
                try { gst_object_unref(appsink); }
                catch (Exception ex) when (IsPlatformFailure(ex)) { }
            }
        }
    }

    private static void PublishSample(IntPtr sample, IMediaFrameSink sink)
    {
        var caps = gst_sample_get_caps(sample);
        if (caps == IntPtr.Zero)
            return;
        var structure = gst_caps_get_structure(caps, 0);
        if (structure == IntPtr.Zero)
            return;
        if (gst_structure_get_int(structure, "width", out var width) == 0 ||
            gst_structure_get_int(structure, "height", out var height) == 0)
            return;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            return;

        var buffer = gst_sample_get_buffer(sample);
        if (buffer == IntPtr.Zero)
            return;

        // GstMapInfo 的结构体大小随版本变化，这里用一块足够大的原生内存而不是托管结构体：
        // 只有头三个字段的偏移是稳定的（memory/flags/data/size/maxsize）。
        var info = Marshal.AllocHGlobal(128);
        try
        {
            if (gst_buffer_map(buffer, info, MapFlagRead) == 0)
                return;
            try
            {
                var data = Marshal.ReadIntPtr(info, 16);
                var size = Marshal.ReadInt64(info, 24);
                var rowBytes = width * 4;
                if (data == IntPtr.Zero || size < (long)rowBytes * height)
                    return;
                var stride = (int)(size / height);
                if (stride < rowBytes)
                    stride = rowBytes;

                var frame = new byte[rowBytes * height];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(data, y * stride), frame, y * rowBytes, rowBytes);
                sink.PresentFrame(frame, width, height, rowBytes);
            }
            finally
            {
                gst_buffer_unmap(buffer, info);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    private static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized || _initFailure is not null)
                return;
            try
            {
                gst_init(IntPtr.Zero, IntPtr.Zero);
                _initialized = true;
                DetectVideoSupport();
            }
            catch (Exception ex) when (IsPlatformFailure(ex))
            {
                _initFailure = ex is DllNotFoundException
                    ? "系统未安装 GStreamer（libgstreamer-1.0.so.0）"
                    : ex.Message;
            }
        }
    }

    /// <summary>
    /// 视频能不能出画面，取决于发行版装没装 appsink / videoconvert 与 libgstapp。
    /// 这里逐项核对；缺哪一项都只是“视频交系统应用打开”，不影响音频。
    /// </summary>
    private static void DetectVideoSupport()
    {
        try
        {
            var appsink = gst_element_factory_make("appsink", IntPtr.Zero);
            var convert = gst_element_factory_make("videoconvert", IntPtr.Zero);
            if (appsink != IntPtr.Zero)
                gst_object_unref(appsink);
            if (convert != IntPtr.Zero)
                gst_object_unref(convert);

            if (appsink == IntPtr.Zero)
            {
                _videoUnavailableReason = "系统缺少 appsink 插件（gst-plugins-base）；";
                return;
            }
            if (convert == IntPtr.Zero)
            {
                _videoUnavailableReason = "系统缺少 videoconvert 插件；";
                return;
            }
            if (!NativeLibrary.TryLoad(AppLibrary, out var handle) ||
                !NativeLibrary.TryGetExport(handle, "gst_app_sink_try_pull_sample", out _))
            {
                _videoUnavailableReason = "系统缺少 libgstapp-1.0（gst-plugins-base）；";
                return;
            }
            _videoAvailable = true;
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            _videoUnavailableReason = $"GStreamer 视频插件不可用：{ex.Message}；";
        }
    }

    /// <summary>
    /// 预判只看容器清单：真正的解码器由 GStreamer 的插件决定，逐个文件试开会带来可见延迟。
    /// 没有 appsink 时视频如实报“不能内嵌播”，而不是给一个只会出声的播放按钮。
    /// </summary>
    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var video = extension is ".mp4" or ".m4v" or ".mkv" or ".webm" or ".mov" or ".avi" or ".ts" or ".ogv" or ".flv";
        var audio = extension is ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".opus" or ".oga";

        if (video)
        {
            var label = extension.TrimStart('.').ToUpperInvariant();
            return Task.FromResult(_videoAvailable
                ? new MediaProbeResult(true, true, true, $"视频 · {label}（GStreamer 解码 → 帧推送）")
                : new MediaProbeResult(
                    false, true, true, $"视频 · {label}",
                    $"{_videoUnavailableReason}视频画面交系统应用打开；音频仍可播。"));
        }

        if (audio)
        {
            var label = extension.TrimStart('.').ToUpperInvariant();
            return Task.FromResult(_initFailure is null
                ? new MediaProbeResult(true, false, true, $"音频 · {label}（GStreamer 解码）")
                : new MediaProbeResult(false, false, true, $"音频 · {label}", _initFailure));
        }

        return Task.FromResult(new MediaProbeResult(
            false, false, false, "容器不在系统清单内",
            "GStreamer 对这个容器没有已安装的解码插件，可用系统应用打开。"));
    }

    private static TimeSpan? QueryPosition(IntPtr pipeline) =>
        Query(pipeline, gst_element_query_position);

    private static TimeSpan? QueryDuration(IntPtr pipeline) =>
        Query(pipeline, gst_element_query_duration);

    private static TimeSpan? Query(IntPtr pipeline, QueryDelegate query)
    {
        try
        {
            if (query(pipeline, FormatTime, out var value) == 0 || value < 0)
                return null;
            return TimeSpan.FromTicks(value / 100); // GStreamer 的单位是纳秒
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            return null;
        }
    }

    private static bool IsPlatformFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        lock (_gate)
        {
            DestroyPipeline();
        }
        return ValueTask.CompletedTask;
    }

    private delegate int QueryDelegate(IntPtr element, int format, out long value);

    [DllImport(Library)]
    private static extern void gst_init(IntPtr argc, IntPtr argv);

    [DllImport(Library)]
    private static extern IntPtr gst_parse_launch(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pipeline, out IntPtr error);

    [DllImport(Library)]
    private static extern int gst_element_set_state(IntPtr element, int state);

    [DllImport(Library)]
    private static extern int gst_element_query_position(IntPtr element, int format, out long position);

    [DllImport(Library)]
    private static extern int gst_element_query_duration(IntPtr element, int format, out long duration);

    [DllImport(Library)]
    private static extern int gst_element_seek_simple(IntPtr element, int format, int flags, long position);

    [DllImport(Library)]
    private static extern void gst_util_set_object_arg(
        IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library)]
    private static extern void gst_object_unref(IntPtr obj);

    [DllImport(Library)]
    private static extern IntPtr gst_element_factory_make(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string factory, IntPtr name);

    [DllImport(Library)]
    private static extern IntPtr gst_bin_get_by_name(
        IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library)]
    private static extern IntPtr gst_sample_get_caps(IntPtr sample);

    [DllImport(Library)]
    private static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    [DllImport(Library)]
    private static extern int gst_structure_get_int(
        IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);

    [DllImport(Library)]
    private static extern IntPtr gst_sample_get_buffer(IntPtr sample);

    [DllImport(Library)]
    private static extern int gst_buffer_map(IntPtr buffer, IntPtr info, int flags);

    [DllImport(Library)]
    private static extern void gst_buffer_unmap(IntPtr buffer, IntPtr info);

    [DllImport(Library)]
    private static extern void gst_sample_unref(IntPtr sample);

    [DllImport(AppLibrary)]
    private static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appsink, ulong timeoutNanoseconds);
}
#endif
