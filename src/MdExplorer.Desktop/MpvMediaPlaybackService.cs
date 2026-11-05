using Avalonia;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Avalonia;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Desktop;

/// <summary>
/// libmpv 后端（桌面端可选路径）。它把解码、渲染、音频输出全部交给用户自己放的 LGPL-only libmpv，
/// 宿主只做三件事：提供一个 Avalonia 控件当画面宿主、转发播放命令、按需读属性回报状态。
/// <para>
/// 为什么再养一条后端：系统栈在 Windows 上没有能直接交给外部窗口的画面 API，
/// 现有实现只能靠 <c>MediaComposition</c> 反复取缩略帧（约 12 fps，卡顿可见）；
/// libmpv 的 GL 渲染器是连续呈现，观感与系统播放器接近。两条路径都保留、由用户选择，
/// 谁更稳就以真机结果决定去留（见 docs/DEVELOPMENT.md D-03）。
/// </para>
/// <para>
/// 打包边界：本仓**不携带也不下载** libmpv 二进制，程序不联网；用户把审核过的构建放到程序目录
/// 或用环境变量指路即可（<see cref="MpvNativeLibrary.RootVariable"/>）。许可与构建要求见 docs/LIBMPV-SPIKE.md。
/// </para>
/// <para>
/// 线程约定与端口一致：所有成员只在 UI 线程调用。
/// </para>
/// </summary>
internal sealed class MpvMediaPlaybackService : IMediaPlaybackService, IMediaPlaybackPoller, IMediaVideoControlSource
{
    /// <summary>画面渲染路线覆盖：<c>opengl</c>（默认）、<c>software</c>、<c>native</c>（仅 Windows 子窗口）。</summary>
    private const string RendererVariable = "MDEXPLORER_LIBMPV_RENDERER";

    /// <summary>打开后多久还没拿到时长就算失败——不给用户一个永远“正在打开…”的卡片。</summary>
    private static readonly TimeSpan OpenWatchdog = TimeSpan.FromSeconds(5);

    private readonly MpvNativeLibraryStatus _library = MpvNativeLibrary.Status;
    private readonly object _gate = new();

    private MpvView? _view;
    private MpvContext? _context;
    private string? _pendingPath;
    private string? _lastPath;
    private TimeSpan _resumePosition;
    private bool _pendingAutoPlay;
    private DateTime? _pendingOpenSince;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private TimeSpan _position;
    private TimeSpan _duration;
    private int _volume = 100;
    private bool _disposed;

    public MediaPlaybackCapabilities Capabilities => _library.IsAvailable
        ? new MediaPlaybackCapabilities(true, true, "libmpv",
            $"libmpv 负责解码与画面输出（{RendererName(PreferredRenderer())}），音频走系统默认输出设备；" +
            $"原生库来自用户自行放置的 LGPL-only 构建{(_library.LibraryPath is null ? "（系统库路径）" : string.Empty)}。" +
            "目标是连续流畅播放；能否解码某个文件取决于该构建带的 FFmpeg 组件。")
        : new MediaPlaybackCapabilities(false, false, "libmpv", _library.Detail);

    public MediaRendererKind RendererKind => _library.IsAvailable
        ? MediaRendererKind.HostedControl
        : MediaRendererKind.None;

    public MediaPlaybackState State => _state;
    public TimeSpan Position => ReadPropertyTime(static context => context.TimePos.Get(), _position);
    public TimeSpan Duration => ReadPropertyTime(static context => context.Duration.Get(), _duration);
    public int VolumePercent => _volume;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    // ── 画面控件（后端自带） ────────────────────────────────────────────────

    /// <summary>
    /// 交出 libmpv 的画面控件。包装层的每个渲染视图都在自己的字段里创建 mpv 上下文，
    /// 所以“控件存在”与“能播放”是同一件事：控件必须先挂进视觉树，我们才拿得到上下文。
    /// 音频（或未放行视频画面）时用软件渲染器，避免为一个没有画面的文件去开 GL 表面。
    /// </summary>
    public MediaVideoControl CreateVideoControl(bool includeVideoSurface)
    {
        if (!_library.IsAvailable)
            return new MediaVideoControl(null, _library.Detail);
        if (_disposed)
            return new MediaVideoControl(null, "libmpv 后端已释放。");

        // 宿主重建画面时先收旧控件：一个后端同时只有一份上下文，不接受两个控件抢同一个播放器。
        if (_view is not null)
            ReleaseVideoControl();

        try
        {
            var renderer = includeVideoSurface ? PreferredRenderer() : VideoRenderer.Software;
            var view = new MpvView { Renderer = renderer };
            view.PropertyChanged += OnViewPropertyChanged;
            _view = view;
            TryBindContext(view);
            return new MediaVideoControl(view);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _view = null;
            LastError = $"libmpv 画面控件创建失败：{ex.Message}";
            return new MediaVideoControl(null, LastError);
        }
    }

    public void ReleaseVideoControl()
    {
        var view = _view;
        _view = null;
        if (view is not null)
            view.PropertyChanged -= OnViewPropertyChanged;

        MpvContext? context;
        lock (_gate)
        {
            context = _context;
            _context = null;
        }

        // 画面控件被卸载（预览栏收起、切到别的目的地）时不留一个“已停止”的假状态：
        // 记下正在播的文件，控件重新挂回来时按原样续上（用户只是把预览收起来，不是要停播）。
        var resumePath = _state is MediaPlaybackState.Playing or MediaPlaybackState.Paused
            or MediaPlaybackState.Opening ? _lastPath : null;
        var resumePlaying = _state == MediaPlaybackState.Playing;
        var resumeAt = _position;

        _resumePosition = resumePath is null ? TimeSpan.Zero : resumeAt;
        _position = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _pendingPath = resumePath;
        _pendingAutoPlay = resumePlaying;
        _pendingOpenSince = resumePath is null ? null : DateTime.UtcNow;

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

        _state = resumePath is null ? MediaPlaybackState.Stopped : MediaPlaybackState.Opening;
        RaiseChanged();
    }

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != MpvView.MpvContextProperty)
            return;
        TryBindContext(sender as MpvView ?? _view);
    }

    private void TryBindContext(MpvView? view)
    {
        var context = view?.MpvContext;
        if (context is null)
            return;
        lock (_gate)
        {
            if (ReferenceEquals(_context, context))
                return;
            _context = context;
        }

        try
        {
            context.Volume.Set(_volume);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 音量设不上不该阻断打开；诊断里已有后端名，播放本身会继续。
        }

        TryStartPending();
    }

    /// <summary>
    /// 上下文一到位就把“等着播的那个文件”打开。控件刚从属性变更里拿到的上下文与
    /// 500 ms 轮询里补取到的上下文都走这里，两条路都试，谁先到谁开播——
    /// 只依赖属性变更事件会漏掉“控件挂上来时上下文还没建好”的那种时序。
    /// </summary>
    private void TryStartPending()
    {
        if (_pendingPath is not { } path || CurrentContext() is null)
            return;

        _pendingPath = null;
        var startAt = _resumePosition;
        _resumePosition = TimeSpan.Zero;
        _ = OpenCoreAsync(path, startAt);
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

        _resumePosition = TimeSpan.Zero;
        var context = CurrentContext();
        if (context is null)
        {
            // 宿主还没把画面控件挂进视觉树（自动播放、或预览栏刚展开）：记下意图，控件一到位就打开。
            _pendingPath = path;
            _pendingOpenSince = DateTime.UtcNow;
            _state = MediaPlaybackState.Opening;
            LastError = null;
            RaiseChanged();
            return true;
        }

        return await OpenCoreAsync(path).ConfigureAwait(true);
    }

    private async Task<bool> OpenCoreAsync(string path, TimeSpan startAt = default)
    {
        var context = CurrentContext();
        if (context is null)
            return false;

        try
        {
            _state = MediaPlaybackState.Opening;
            LastError = null;
            _position = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            _pendingOpenSince = DateTime.UtcNow;
            _lastPath = path;
            RaiseChanged();

            await context.LoadFile(path).InvokeAsync().ConfigureAwait(true);
            context.Volume.Set(_volume);
            if (startAt > TimeSpan.Zero)
            {
                // 画面控件被宿主摘掉又挂回来时从原位置续播，而不是把用户丢回片头。
                await context.Seek(startAt.TotalSeconds, SeekOption.Absolute).InvokeAsync().ConfigureAwait(true);
                _position = startAt;
            }
            // 端口语义是“打开但不自动播放”；要不要立刻出声由 PlayAsync 决定。
            context.Pause.Set(!_pendingAutoPlay);
            _pendingAutoPlay = false;
            _state = _state == MediaPlaybackState.Failed ? _state : MediaPlaybackState.Paused;
            RaiseChanged();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"libmpv 打不开该文件：{ex.Message}";
            _state = MediaPlaybackState.Failed;
            _pendingOpenSince = null;
            RaiseChanged();
            return false;
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = CurrentContext();
        if (context is null)
        {
            _pendingAutoPlay = true;
            if (_state is MediaPlaybackState.Paused or MediaPlaybackState.Stopped or MediaPlaybackState.Ended)
                _state = MediaPlaybackState.Opening;
            RaiseChanged();
            return Task.CompletedTask;
        }

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
        var context = CurrentContext();
        if (context is null)
        {
            _pendingAutoPlay = false;
            return Task.CompletedTask;
        }

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
        _pendingAutoPlay = false;
        _pendingPath = null;
        _lastPath = null;
        _resumePosition = TimeSpan.Zero;
        var context = CurrentContext();
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
        _state = MediaPlaybackState.Stopped;
        _pendingOpenSince = null;
        RaiseChanged();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var context = CurrentContext();
        if (context is null)
            return;

        var seconds = Math.Max(0, position.TotalSeconds);
        try
        {
            await context.Seek(seconds, SeekOption.Absolute).InvokeAsync().ConfigureAwait(true);
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
        var context = CurrentContext();
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
    /// 属性轮询更简单也更稳：读四个标量属性，代价可以忽略。
    /// </summary>
    public void PollPosition()
    {
        // 控件可能是在轮询间隔里才把上下文建好的：每次轮询都补一次“该开播了”。
        TryStartPending();

        var context = CurrentContext();
        if (context is null)
        {
            if (_pendingPath is not null && _pendingOpenSince is { } since)
            {
                if (DateTime.UtcNow - since > OpenWatchdog)
                {
                    _pendingPath = null;
                    _pendingOpenSince = null;
                    LastError = "libmpv 画面控件尚未就绪，无法开始播放。";
                    _state = MediaPlaybackState.Failed;
                    RaiseChanged();
                }
                // 还不到看门狗时限：继续等宿主把控件挂上来。
            }
            return;
        }

        var previousState = _state;
        var previousPosition = _position;
        var previousDuration = _duration;

        _position = ReadPropertyTime(static c => c.TimePos.Get(), _position);
        _duration = ReadPropertyTime(static c => c.Duration.Get(), _duration);

        bool paused = true, eof = false, idle = true;
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
            _pendingOpenSince = null;
        }
        else if (idle && _duration <= TimeSpan.Zero)
        {
            // 还没真正载入文件。正在打开时保持原状态由看门狗收尾（否则用户在 500 ms 内会看到
            // “正在播放 → 已停止 → 正在播放”的跳动）；已经播过又回到空闲，就是真的停了。
            if (_pendingOpenSince is { } openedAt)
            {
                if (DateTime.UtcNow - openedAt > OpenWatchdog)
                {
                    _pendingOpenSince = null;
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
            _pendingOpenSince = null;
        }

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
        ReleaseVideoControl();
        return ValueTask.CompletedTask;
    }

    // ── 内部工具 ─────────────────────────────────────────────────────────────

    private MpvContext? CurrentContext()
    {
        lock (_gate)
        {
            if (_context is not null)
                return _context;
        }

        // 控件已创建但还没初始化完：顺手取一次，免得依赖属性变更事件的时序。
        var view = _view;
        if (view?.MpvContext is { } context)
        {
            lock (_gate)
            {
                _context ??= context;
                return _context;
            }
        }
        return null;
    }

    private TimeSpan ReadPropertyTime(Func<MpvContext, double?> read, TimeSpan fallback)
    {
        // 读一个标量属性是一次同步 FFI 调用，开销可忽略；异常时退回上一次已知值，绝不让 UI 卡住。
        var context = CurrentContext();
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

    private static VideoRenderer PreferredRenderer()
    {
        var raw = Environment.GetEnvironmentVariable(RendererVariable)?.Trim().ToLowerInvariant();
        return raw switch
        {
            "software" or "sw" => VideoRenderer.Software,
            // 原生子窗口只在 Windows 有意义（D3D 互操作），Linux 上会退化，别当默认。
            "native" when OperatingSystem.IsWindows() => VideoRenderer.Native,
            "opengl" or "gl" => VideoRenderer.OpenGl,
            _ => VideoRenderer.OpenGl,
        };
    }

    private static string RendererName(VideoRenderer renderer) => renderer switch
    {
        VideoRenderer.Software => "软件渲染",
        VideoRenderer.Native => "原生子窗口",
        _ => "OpenGL 渲染",
    };
}
