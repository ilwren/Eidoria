namespace MdExplorer.Platform.Abstractions;

public enum MediaPlaybackState
{
    Idle,
    Opening,
    Playing,
    Paused,
    Stopped,
    Ended,
    Failed,
}

/// <summary>
/// 视频画面的交付方式。它决定 UI 用哪种宿主：Avalonia 内的帧呈现器、
/// 还是平台原生画面（Surface/HWND）。两者不能互相冒充。
/// </summary>
public enum MediaRendererKind
{
    /// <summary>没有画面输出（纯音频后端，或该平台尚未接入视频）。</summary>
    None,

    /// <summary>后端自己把画面画到平台原生 Surface/HWND，宿主只提供句柄。</summary>
    NativeSurface,

    /// <summary>后端把解码后的帧推给宿主，由宿主在 Avalonia 内呈现。</summary>
    FramePush,
}

/// <summary>后端自述能力。UI 只按这里说的显隐控件，不猜平台。</summary>
public sealed record MediaPlaybackCapabilities(
    bool CanPlayAudio,
    bool CanPlayVideo,
    string BackendName,
    string Diagnostic)
{
    public bool IsSupported => CanPlayAudio || CanPlayVideo;
}

/// <summary>
/// 系统解码器对单个文件的预判。它只承诺「系统这一侧愿不愿意接」，
/// 不等于一定能完整播完——所以它永远带一句人话解释。
/// </summary>
public sealed record MediaProbeResult(
    bool CanPlay,
    bool HasVideo,
    bool HasAudio,
    string Summary,
    string? Reason = null);

/// <summary>
/// 可选能力：能在打开前判断「系统解码器认不认这个文件」的后端实现它。
/// 系统解码器的覆盖是平台/发行版/设备相关的，所以这里只报事实，不猜。
/// </summary>
public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// 内嵌音视频播放端口（平台无关）。实现负责解码与输出；
/// 调用方不得假定任何实现是线程安全的，全部调用都在 UI 线程发起。
/// </summary>
public interface IMediaPlaybackService : IAsyncDisposable
{
    MediaPlaybackCapabilities Capabilities { get; }

    /// <summary>画面交付方式；纯音频或未接入视频时为 <see cref="MediaRendererKind.None"/>。</summary>
    MediaRendererKind RendererKind { get; }
    MediaPlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    int VolumePercent { get; }

    /// <summary>最近一次失败的用户可读原因；成功打开或停止后清空。</summary>
    string? LastError { get; }

    /// <summary>状态、位置或时长变化。可能来自后台线程，订阅方自行切回 UI 线程。</summary>
    event EventHandler? PlaybackChanged;

    /// <summary>打开媒体但不自动播放；返回是否成功。</summary>
    Task<bool> OpenAsync(string path, CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);

    /// <summary>0–100，实现自行夹取。</summary>
    void SetVolume(int percent);
}

/// <summary>
/// 需要宿主提供原生画面窗口的后端实现它（<see cref="MediaRendererKind.NativeSurface"/>）。
/// <para>
/// <paramref name="surface"/> 是平台对象，由宿主侧的桥接交出：Android 为
/// <c>Android.Views.Surface</c>（来自 TextureView 的 SurfaceTexture），Windows 为 HWND。
/// 跨平台端口刻意只用 <see cref="object"/>，避免抽象层反向依赖平台类型；
/// 实现必须校验类型，不是自己认识的对象就返回 false，而不是抛异常。
/// </para>
/// </summary>
public interface IMediaVideoRenderer
{
    /// <summary>宿主画面就绪。返回是否被后端接受（例如播放器还没 Prepare 时也应接受并暂存）。</summary>
    bool AttachVideoSurface(object surface);

    /// <summary>宿主画面销毁或控件脱离视觉树。</summary>
    void DetachVideoSurface();
}

/// <summary>
/// 宿主提供的帧呈现面（<see cref="MediaRendererKind.FramePush"/>）。后端把解码后的画面按视频帧率推过来，
/// 由宿主在 Avalonia 内绘制——Wayland 下拿不到窗口 id，只能用这条路线。
/// <para>
/// 帧数据是 BGRA8888、未预乘。实现必须允许从解码线程调用：拷贝进自己的缓冲区后立刻返回，
/// 绝不阻塞解码线程。解码器给的缓冲区常常按对齐补行尾，所以行距单独给出。
/// </para>
/// </summary>
public interface IMediaFrameSink
{
    /// <summary>推送一帧画面；尺寸从 0 变为有效值即表示画面开始。</summary>
    /// <param name="bgra8888">帧缓冲起始位置。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="strideBytes">每行字节数（含解码器的行尾对齐填充）。</param>
    void PresentFrame(ReadOnlySpan<byte> bgra8888, int width, int height, int strideBytes);

    /// <summary>清空画面（停止、切换文件或解码失败）。</summary>
    void Clear();
}

/// <summary>
/// 只能把帧推给宿主的后端实现它（Linux GStreamer appsink、Windows MediaComposition 取帧）。
/// 与 <see cref="IMediaVideoRenderer"/> 并列：二者互斥，实现只能选一种，否则“画面在哪”说不清。
/// </summary>
public interface IMediaVideoFrameSource
{
    /// <summary>宿主帧呈现面就绪；实现应在此之后开始推帧。返回是否接受。</summary>
    bool AttachFrameSink(IMediaFrameSink sink);

    /// <summary>宿主帧呈现面销毁或控件脱离视觉树。</summary>
    void DetachFrameSink();
}

/// <summary>
/// 没有位置回调的后端（如 Android MediaPlayer）实现它，宿主定时器调用即可把进度推给 UI。
/// 支持回调的后端不必实现。
/// </summary>
public interface IMediaPlaybackPoller
{
    void PollPosition();
}
