using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Desktop;

/// <summary>
/// libmpv 后端（桌面端可选路径）。解码、音视频同步、音频输出全部交给用户自己放的 LGPL-only libmpv，
/// 画面由它自带的软件渲染器解码成 BGRA，再按帧推给宿主（<see cref="IMediaFrameSink"/>）。
/// <para>
/// 为什么再养一条后端：系统栈在 Windows 上没有能直接交给外部窗口的画面 API，
/// 现有实现只能靠 <c>MediaComposition</c> 反复取缩略帧（约 12 fps，卡顿可见）；
/// libmpv 是真正的连续解码 + 音视频同步，推帧的帧率由播放时钟决定。
/// 两条路径都保留、由用户选择，谁更稳用真机结果决定去留（见 docs/DEVELOPMENT.md D-03）。
/// </para>
/// <para>
/// 为什么用推帧而不是"交一个控件给宿主"：包装层的 <c>LibMpv.Avalonia</c> 依赖 <c>ReactiveUI.Avalonia</c>，
/// 与 §3.2 的禁用清单（<c>ReactiveUI.*</c>）直接冲突，而且它的签名在 Linux 上过不了 NuGet 校验（NU3012）。
/// 只引用不含 UI 的 <c>HanumanInstitute.LibMpv</c> 核心包 + 自己驱动 <c>SoftwareRender</c>，
/// 既守住了依赖边界，又复用了已经跑通的 <see cref="IMediaFrameSink"/> 通道（Linux GStreamer 同一条路）。
/// </para>
/// <para>
/// 打包边界：本仓**不携带也不下载** libmpv 二进制，程序不联网；用户把审核过的构建放到程序目录
/// 或用环境变量指路即可（<see cref="MpvNativeLibrary.RootVariable"/>）。许可与构建要求见 docs/LIBMPV-SPIKE.md。
/// </para>
/// <para>
/// 线程约定：端口方法只在 UI 线程调用；<c>mpv_render_context_render</c> 固定在本类自己的渲染线程上调用
/// （libmpv 要求同一线程、且不要在更新回调里直接渲染），更新回调只负责按一下信号量。
/// </para>
/// </summary>
internal sealed class MpvMediaPlaybackService : IMediaPlaybackService, IMediaPlaybackPoller, IMediaVideoFrameSource
{
    /// <summary>渲染上限：预览卡只有两三百像素高，按 960×540 封顶即可，避免为预览做 1080p 的 CPU 转换。</summary>
    private const int MaxRenderWidth = 960;
    private const int MaxRenderHeight = 540;

    private readonly MpvNativeLibraryStatus _library = MpvNativeLibrary.Status;
    private readonly AutoResetEvent _frameSignal = new(false);

    private MpvContext? _context;
    private Thread? _renderThread;
    private volatile bool _renderStop;
    private IMediaFrameSink? _sink;

    private byte[]? _frameBuffer;
    private GCHandle _frameHandle;
    private int _renderWidth = MaxRenderWidth;
    private int _renderHeight = MaxRenderHeight;
    private int _desiredWidth = MaxRenderWidth;
    private int _desiredHeight = MaxRenderHeight;
    private bool _sizeKnown;

    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private TimeSpan _position;
    private TimeSpan _duration;
    private int _volume = 100;
    private DateTime? _openSince;
    private bool _disposed;

    /// <summary>打开后多久还没拿到时长就算失败——不给用户一个永远“正在打开…”的卡片。</summary>
    private static readonly TimeSpan OpenWatchdog = TimeSpan.FromSeconds(5);

    public MediaPlaybackCapabilities Capabilities => _library.IsAvailable
        ? new MediaPlaybackCapabilities(true, true, "libmpv",
            "libmpv 负责解码、音视频同步与音频输出，画面经软件渲染转成 BGRA 推给宿主；" +
            $"原生库来自用户自行放置的 LGPL-only 构建{(_library.LibraryPath is null ? "（系统库路径）" : string.Empty)}。" +
            "目标是连续流畅播放；能否解码某个文件取决于该构建带的 FFmpeg 组件。")
        : new MediaPlaybackCapabilities(false, false, "libmpv", _library.Detail);

    public MediaRendererKind RendererKind => _library.IsAvailable
        ? MediaRendererKind.FramePush
        : MediaRendererKind.None;

    public MediaPlaybackState State => _state;
    public TimeSpan Position => ReadPropertyTime(static context => context.TimePos.Get(), _position);
    public TimeSpan Duration => ReadPropertyTime(static context => context.Duration.Get(), _duration);
    public int VolumePercent => _volume;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    // ── 帧交付 ───────────────────────────────────────────────────────────────

    public bool AttachFrameSink(IMediaFrameSink sink)
    {
        if (!_library.IsAvailable || _disposed)
            return false;
        _sink = sink;
        EnsureRenderThread();
        // 让已经开始播的画面立刻出现在新宿主上，而不是等下一次解码更新。
        _frameSignal.Set();
        return true;
    }

    public void DetachFrameSink()
    {
        _sink = null;
        _sizeKnown = false;
    }

    // ── 播放控制 ─────────────────────────────────────────────────────────────

    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_library.IsAvailable)
        {
            LastError = _library.Detail;
            _state = MediaPlaybackState.Failed;
            RaiseChanged();
            return false;
        }

        var context = EnsureContext();
        if (context is null)
            return false;

        try
        {
            _state = MediaPlaybackState.Opening;
            LastError = null;
            _position = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            _openSince = DateTime.UtcNow;
            _sizeKnown = false;
            RaiseChanged();

            await context.LoadFile(path).InvokeAsync().ConfigureAwait(true);
            context.Volume.Set(_volume);
            // 端口语义是“打开但不自动播放”；要不要立刻出声由 PlayAsync 决定。
            context.Pause.Set(true);
            _state = MediaPlaybackState.Paused;
            RaiseChanged();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"libmpv 打不开该文件：{ex.Message}";
            _state = MediaPlaybackState.Failed;
            _openSince = null;
            RaiseChanged();
            return false;
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = Context();
        if (context is null)
            return Task.CompletedTask;

        try
        {
            context.Pause.Set(false);
            if (_state is MediaPlaybackState.Paused or MediaPlaybackState.Stopped
                or MediaPlaybackState.Ended or MediaPlaybackState.Opening)
            {
                _state = MediaPlaybackState.Playing;
            }
            LastError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"libmpv 播放失败：{ex.Message}";
            _state = MediaPlaybackState.Failed;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        var context = Context();
        if (context is null)
            return Task.CompletedTask;

        try
        {
            context.Pause.Set(true);
            if (_state == MediaPlaybackState.Playing)
                _state = MediaPlaybackState.Paused;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"libmpv 暂停失败：{ex.Message}";
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var context = Context();
        if (context is not null)
        {
            try
            {
                await context.Stop().InvokeAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastError = $"libmpv 停止失败：{ex.Message}";
            }
        }

        _position = TimeSpan.Zero;
        _openSince = null;
        _state = MediaPlaybackState.Stopped;
        if (_sink is { } sink)
        {
            try
            {
                sink.Clear();
            }
            catch (Exception)
            {
                // 画面清不掉也只是留了最后一帧，不该影响停止。
            }
        }
        RaiseChanged();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var context = Context();
        if (context is null)
            return;

        var seconds = Math.Max(0, position.TotalSeconds);
        try
        {
            await context.Seek(seconds, SeekOption.Absolute).InvokeAsync().ConfigureAwait(true);
            _position = position;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"libmpv 跳转失败：{ex.Message}";
        }
        RaiseChanged();
    }

    public void SetVolume(int percent)
    {
        _volume = Math.Clamp(percent, 0, 100);
        var context = Context();
        if (context is null)
            return;
        try
        {
            context.Volume.Set(_volume);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"libmpv 音量设置失败：{ex.Message}";
        }
    }

    // ── 状态刷新（宿主定时器驱动） ───────────────────────────────────────────

    /// <summary>
    /// 把 libmpv 的真实状态拉回来。包装层的事件循环在自己的线程上，宿主这边用 500 ms 的
    /// 属性轮询更简单也更稳：读几个标量属性，代价可以忽略。
    /// </summary>
    public void PollPosition()
    {
        var context = Context();
        if (context is null)
            return;

        var previousState = _state;
        var previousPosition = _position;
        var previousDuration = _duration;

        _position = ReadPropertyTime(static c => c.TimePos.Get(), _position);
        _duration = ReadPropertyTime(static c => c.Duration.Get(), _duration);

        var paused = true;
        var eof = false;
        var idle = true;
        try
        {
            paused = context.Pause.Get() ?? true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try
        {
            eof = context.EofReached.Get() ?? false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try
        {
            idle = context.IdleActive.Get() ?? true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        if (eof)
        {
            _state = MediaPlaybackState.Ended;
            _openSince = null;
        }
        else if (idle && _duration <= TimeSpan.Zero)
        {
            // 还没真正载入文件。正在打开时交给看门狗收尾（否则用户在 500 ms 内会看到
            // “正在播放 → 已停止 → 正在播放”的跳动）；已经播过又回到空闲，就是真的停了。
            if (_openSince is { } openedAt)
            {
                if (DateTime.UtcNow - openedAt > OpenWatchdog)
                {
                    _openSince = null;
                    LastError = "libmpv 没能打开这个文件（容器或编码可能不被这个原生构建支持）。";
                    _state = MediaPlaybackState.Failed;
                }
            }
            else if (_state is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
            {
                _state = MediaPlaybackState.Stopped;
            }
        }
        else
        {
            _state = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
            _openSince = null;
        }

        if (!_sizeKnown)
            UpdateRenderSize(context);

        if (_state != previousState
            || (_position - previousPosition).Duration() > TimeSpan.FromMilliseconds(250)
            || (_duration - previousDuration).Duration() > TimeSpan.FromMilliseconds(250))
        {
            RaiseChanged();
        }
    }

    // ── 释放 ─────────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        StopRenderThread();

        var context = _context;
        _context = null;
        if (context is not null)
        {
            try
            {
                context.Dispose();
            }
            catch (Exception)
            {
                // 释放失败只能说“尽力了”：不能让关闭预览把外壳拖崩。
            }
        }

        _sink = null;
        return ValueTask.CompletedTask;
    }

    // ── 上下文与渲染线程 ─────────────────────────────────────────────────────

    /// <summary><c>vo=libmpv</c> 必须写在 initialize 之前，否则渲染上下文建不起来。</summary>
    private sealed class SoftwareOutputContext : MpvContext
    {
        protected override void OnPreInitialize() => SetOptionString("vo", "libmpv");
    }

    private MpvContext? EnsureContext()
    {
        if (_disposed)
            return null;
        if (_context is not null)
            return _context;

        MpvContext? created = null;
        try
        {
            created = new SoftwareOutputContext();
            created.StartSoftwareRendering(OnFrameRequested);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 原生库存在但初始化失败（版本不匹配、缺依赖 DLL、AOT 下包装层不可用……）：
            // 如实记下来并降级，别在预览这条路上把进程带走。
            LastError = $"libmpv 初始化失败：{ex.Message}";
            try
            {
                created?.Dispose();
            }
            catch (Exception)
            {
            }
            return null;
        }

        _context = created;
        EnsureRenderThread();
        return _context;
    }

    private MpvContext? Context() => _context;

    private void EnsureRenderThread()
    {
        if (_renderThread is not null || _disposed)
            return;
        _renderStop = false;
        var thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "libmpv-render",
        };
        _renderThread = thread;
        thread.Start();
    }

    private void StopRenderThread()
    {
        var thread = _renderThread;
        _renderThread = null;
        if (thread is null)
            return;
        _renderStop = true;
        _frameSignal.Set();
        try
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // 线程已经没了或不允许等待：都只是释放阶段的噪音。
        }
        FreeFrameBuffer();
    }

    /// <summary>mpv 的更新回调（来自 mpv 内部线程）：只按一下信号量，绝不在回调里渲染。</summary>
    private void OnFrameRequested() => _frameSignal.Set();

    private void RenderLoop()
    {
        while (!_renderStop)
        {
            _frameSignal.WaitOne(100);
            if (_renderStop)
                return;
            PumpFrame();
        }
    }

    private void PumpFrame()
    {
        var context = _context;
        var sink = _sink;
        if (context is null || sink is null || _disposed)
            return;

        try
        {
            EnsureFrameBuffer(_desiredWidth, _desiredHeight);
            if (_frameBuffer is null)
                return;
            context.SoftwareRender(_renderWidth, _renderHeight, _frameHandle.AddrOfPinnedObject(), "bgra");
            sink.PresentFrame(_frameBuffer, _renderWidth, _renderHeight, _renderWidth * 4);
        }
        catch (Exception)
        {
            // 单帧渲染失败不该让整段播放失败：丢掉这一帧，等下一次更新回调。
        }
    }

    private void EnsureFrameBuffer(int width, int height)
    {
        if (_frameBuffer is not null && _renderWidth == width && _renderHeight == height)
            return;

        FreeFrameBuffer();
        _renderWidth = Math.Max(2, width);
        _renderHeight = Math.Max(2, height);
        _frameBuffer = new byte[_renderWidth * _renderHeight * 4];
        _frameHandle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
    }

    private void FreeFrameBuffer()
    {
        if (_frameHandle.IsAllocated)
            _frameHandle.Free();
        _frameBuffer = null;
    }

    /// <summary>
    /// 按视频尺寸定渲染缓冲：预览卡只有两三百像素高，按 960×540 封顶即可。
    /// 尺寸取视频自身的宽高比，避免依赖软件渲染器的缩放策略。
    /// </summary>
    private void UpdateRenderSize(MpvContext context)
    {
        try
        {
            var width = context.Width.Get();
            var height = context.Height.Get();
            if (width is not > 0 || height is not > 0)
                return;

            var scale = Math.Min(1d, Math.Min((double)MaxRenderWidth / width.Value, (double)MaxRenderHeight / height.Value));
            var targetWidth = Math.Max(2, (int)Math.Round(width.Value * scale / 2) * 2);
            var targetHeight = Math.Max(2, (int)Math.Round(height.Value * scale / 2) * 2);
            _desiredWidth = targetWidth;
            _desiredHeight = targetHeight;
            _sizeKnown = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 属性暂时读不到（还没起播）：下一轮轮询再试。
        }
    }

    // ── 内部工具 ─────────────────────────────────────────────────────────────

    private TimeSpan ReadPropertyTime(Func<MpvContext, double?> read, TimeSpan fallback)
    {
        // 读一个标量属性是一次同步 FFI 调用，开销可忽略；异常时退回上一次已知值，绝不让 UI 卡住。
        var context = Context();
        if (context is null)
            return fallback;
        try
        {
            return ReadSeconds(read(context), fallback);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return fallback;
        }
    }

    private static TimeSpan ReadSeconds(double? seconds, TimeSpan fallback)
    {
        if (seconds is not { } value || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return fallback;
        return TimeSpan.FromSeconds(value);
    }

    private void RaiseChanged() => PlaybackChanged?.Invoke(this, EventArgs.Empty);
}
