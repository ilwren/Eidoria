using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Screenshots;

/// <summary>
/// 截图用的假播放后端：真实解码依赖平台能力（Windows Media Foundation / Android MediaPlayer），
/// 无头 CI 里没有，所以只模拟“已打开、已播放、时长 3:12、位置 0:42”这一个可截图状态。
/// </summary>
internal sealed class StubMediaPlayback : IMediaPlaybackService, IMediaProbe
{
    public MediaPlaybackCapabilities Capabilities { get; } = new(
        CanPlayAudio: true,
        CanPlayVideo: true,
        BackendName: "截图假后端（StubMediaPlayback）",
        Diagnostic: "截图环境没有真实解码器；真机由平台后端播放。");

    // 无头环境没有原生画面宿主，截图里视频项保持“不可内嵌播放”，不显示空的画面框。
    public MediaRendererKind RendererKind => MediaRendererKind.None;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public TimeSpan Position { get; private set; } = TimeSpan.FromSeconds(42);
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(192);
    public int VolumePercent { get; private set; } = 80;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        State = MediaPlaybackState.Paused;
        Raise();
        return Task.FromResult(true);
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        State = MediaPlaybackState.Playing;
        Raise();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        State = MediaPlaybackState.Paused;
        Raise();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = MediaPlaybackState.Stopped;
        Position = TimeSpan.Zero;
        Raise();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        Raise();
        return Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        Raise();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 截图环境要展示“探测说不支持”的那一屏，所以这里给视频一个真实的拒绝理由：
    /// 无头进程没有 Android 的 TextureView，也没有 Windows 的 HWND 宿主。
    /// </summary>
    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var isVideo = Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v";
        return Task.FromResult(isVideo
            ? new MediaProbeResult(
                false, true, true, "视频 · H264 + AAC · 1280×720",
                "这个进程没有原生画面宿主（无头截图环境），真机由平台画面承载；此处只展示探测与拒绝文案。")
            : new MediaProbeResult(true, false, true, "音频 · M4A", null));
    }

    private void Raise() => PlaybackChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 推帧后端的假实现（<see cref="MediaRendererKind.FramePush"/>）：不依赖任何解码器，
/// 宿主把帧呈现面交过来时就推一张程序生成的 640×360 画面。
/// 无头环境里没有 appink/GStreamer，这张假帧就是“推帧路线能出画面”的证据。
/// </summary>
internal sealed class FramePushVideoStub : IMediaPlaybackService, IMediaProbe, IMediaVideoFrameSource
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 360;

    /// <summary>程序生成的 BGRA 画面：青绿渐变 + 底部深色条，肉眼可辨“这是渲染出来的帧”。</summary>
    private static readonly byte[] Frame = BuildFrame();

    private IMediaFrameSink? _sink;

    public MediaPlaybackCapabilities Capabilities { get; } = new(
        CanPlayAudio: true,
        CanPlayVideo: true,
        BackendName: "推帧假后端（FramePushVideoStub）",
        Diagnostic: "截图环境用生成的帧代替解码器输出；真机由 Linux appsink 或 Windows 帧服务推同样的 BGRA 帧。");

    public MediaRendererKind RendererKind => MediaRendererKind.FramePush;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public TimeSpan Position { get; private set; } = TimeSpan.FromSeconds(12);
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(96);
    public int VolumePercent { get; private set; } = 80;
    public string? LastError => null;

    public event EventHandler? PlaybackChanged;

    public bool AttachFrameSink(IMediaFrameSink sink)
    {
        _sink = sink;
        sink.PresentFrame(Frame, FrameWidth, FrameHeight, FrameWidth * 4);
        return true;
    }

    public void DetachFrameSink() => _sink = null;

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        State = MediaPlaybackState.Paused;
        Raise();
        return Task.FromResult(true);
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        State = MediaPlaybackState.Playing;
        _sink?.PresentFrame(Frame, FrameWidth, FrameHeight, FrameWidth * 4);
        Raise();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        State = MediaPlaybackState.Paused;
        Raise();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = MediaPlaybackState.Stopped;
        Position = TimeSpan.Zero;
        Raise();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        Raise();
        return Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        Raise();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>推帧后端的探测：这个容器它接得住，画面走帧呈现面。</summary>
    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(new MediaProbeResult(true, true, true, "视频 · H264 + AAC · 1280×720"));

    private void Raise() => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private static byte[] BuildFrame()
    {
        var pixels = new byte[FrameWidth * FrameHeight * 4];
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                var offset = (y * FrameWidth + x) * 4;
                var band = y > FrameHeight - 48;
                pixels[offset] = band ? (byte)32 : (byte)(40 + 180 * x / FrameWidth);      // B
                pixels[offset + 1] = band ? (byte)32 : (byte)(150 - 60 * y / FrameHeight);  // G
                pixels[offset + 2] = band ? (byte)40 : (byte)(70 + 40 * y / FrameHeight);   // R
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }
}
