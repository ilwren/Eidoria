using Android.Media;
using Android.Views;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Android;

/// <summary>
/// Android 内嵌播放：用系统 <see cref="MediaPlayer"/> 同时负责解封装、解码与音画同步。
/// <para>
/// 音频不需要任何宿主帮助；视频画面由宿主提供 <see cref="TextureView"/> 的 <see cref="Surface"/>
/// （见 <see cref="AndroidVideoSurfaceAttachment"/>），这里只负责在画面就绪时 <c>SetSurface</c>。
/// 也就是说：这一端不解析容器、不解码、不做同步，全部交给 MediaPlayer——
/// 换来的是零原生依赖，代价是容器/编码覆盖由设备决定（AVI/FLV/WMV 系统不认）。
/// </para>
/// </summary>
public sealed class AndroidMediaPlaybackService : IMediaPlaybackService, IMediaPlaybackPoller, IMediaProbe, IMediaVideoRenderer
{
    private readonly MediaPlayer _player = new();
    private readonly object _gate = new();
    private string? _path;
    private Surface? _surface;
    private bool _videoAttached;
    private bool _prepared;
    private bool _disposed;

    public AndroidMediaPlaybackService()
    {
        _player.Completion += (_, _) =>
        {
            State = MediaPlaybackState.Ended;
            RaiseChanged();
        };
        _player.Error += (_, args) =>
        {
            LastError = $"Android MediaPlayer 报错：{args.What}";
            State = MediaPlaybackState.Failed;
            RaiseChanged();
        };
    }

    public MediaPlaybackCapabilities Capabilities { get; } = new(
        CanPlayAudio: true,
        CanPlayVideo: true,
        BackendName: "Android MediaPlayer",
        Diagnostic: "音频与视频都由设备自带的 MediaPlayer 解码，画面画在预览卡里的 TextureView 上；"
                    + "系统不认的容器（AVI/FLV/WMV）仍交系统应用打开。");

    /// <summary>画面交给宿主托管的原生窗口，后端不产出帧。</summary>
    public MediaRendererKind RendererKind => MediaRendererKind.NativeSurface;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public TimeSpan Position => _prepared ? TimeSpan.FromMilliseconds(_player.CurrentPosition) : TimeSpan.Zero;
    public TimeSpan Duration => _prepared ? TimeSpan.FromMilliseconds(_player.Duration) : TimeSpan.Zero;
    public int VolumePercent { get; private set; } = 100;
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackChanged;

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (_disposed)
            return Task.FromResult(false);

        lock (_gate)
        {
            try
            {
                _player.Reset();
                _prepared = false;
                _path = path;
                _player.SetDataSource(path);
                _player.SetVolume(VolumePercent / 100f, VolumePercent / 100f);
                // 画面可能先于文件就绪（TextureView 先建好），也可能晚到；两边都尝试接一次。
                if (_surface is not null)
                    _player.SetSurface(_surface);
                _player.Prepare();
                _prepared = true;
                LastError = null;
                State = MediaPlaybackState.Paused;
                RaiseChanged();
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalStateException or ArgumentException)
            {
                LastError = $"无法打开音频：{ex.Message}";
                State = MediaPlaybackState.Failed;
                RaiseChanged();
                return Task.FromResult(false);
            }
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_prepared)
                return Task.CompletedTask;
            _player.Start();
            State = MediaPlaybackState.Playing;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_gate)
        {
            if (!_prepared)
                return Task.CompletedTask;
            if (_player.IsPlaying)
                _player.Pause();
            State = MediaPlaybackState.Paused;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_prepared)
                return Task.CompletedTask;
            _player.Pause();
            _player.SeekTo(0);
            State = MediaPlaybackState.Stopped;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        lock (_gate)
        {
            if (!_prepared)
                return Task.CompletedTask;
            var milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, _player.Duration));
            _player.SeekTo(milliseconds);
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            if (_prepared)
                _player.SetVolume(VolumePercent / 100f, VolumePercent / 100f);
        }
        RaiseChanged();
    }

    /// <summary>
    /// 用系统 MediaExtractor/MediaCodec 预判这个文件能不能被设备解码。
    /// 事实来源是设备本身：容器不认就明说不认，没有对应解码器也明说，
    /// 而不是按扩展名猜（AVI/FLV/WMV 这类容器系统本就不支持）。
    /// </summary>
    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaExtractor? extractor = null;
        try
        {
            extractor = new MediaExtractor();
            extractor.SetDataSource(path);
            var videoMime = default(string);
            var audioMime = default(string);
            string? size = null;
            for (var i = 0; i < extractor.TrackCount; i++)
            {
                using var format = extractor.GetTrackFormat(i);
                var mime = format.GetString(MediaFormat.KeyMime);
                if (mime is null)
                    continue;
                if (mime.StartsWith("video/", StringComparison.Ordinal))
                {
                    if (videoMime is null)
                    {
                        videoMime = mime;
                        if (format.ContainsKey(MediaFormat.KeyWidth) && format.ContainsKey(MediaFormat.KeyHeight))
                            size = $"{format.GetInteger(MediaFormat.KeyWidth)}×{format.GetInteger(MediaFormat.KeyHeight)}";
                    }
                }
                else if (mime.StartsWith("audio/", StringComparison.Ordinal))
                {
                    audioMime ??= mime;
                }
            }

            if (videoMime is null && audioMime is null)
                return Task.FromResult(new MediaProbeResult(
                    false, false, false, "系统解封装器没有找到音视频轨道",
                    "文件可能是纯字幕/纯数据容器，或已损坏。"));

            if (videoMime is not null)
            {
                var videoDecodable = HasDecoder(videoMime);
                var audioDecodable = audioMime is null || HasDecoder(audioMime);
                var track = size is null ? Describe(videoMime, audioMime) : $"{Describe(videoMime, audioMime)} · {size}";
                if (!videoDecodable || !audioDecodable)
                    return Task.FromResult(new MediaProbeResult(
                        false, true, audioMime is not null, $"系统解码器缺少对应组件 · {track}",
                        "设备未提供该编码的解码器；可用系统应用打开，或安装带软件解码器的播放器。"));
                return Task.FromResult(new MediaProbeResult(
                    true, true, audioMime is not null, $"视频 · {track}",
                    size is null ? null : "画面将由设备解码后画在预览卡内；拖动进度或切后台时画面可能短暂黑屏。"));
            }

            if (!HasDecoder(audioMime!))
                return Task.FromResult(new MediaProbeResult(
                    false, false, true, $"系统解码器缺少 {audioMime}", "设备未提供该音频编码的解码器。"));

            return Task.FromResult(new MediaProbeResult(
                true, false, true, $"音频 · {Describe(null, audioMime)}", null));
        }
        catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalArgumentException or Java.Lang.RuntimeException)
        {
            return Task.FromResult(new MediaProbeResult(
                false, false, false, "系统解封装器不识别该容器",
                $"Android 的 MediaExtractor 只认 3gp/mp4/ts/mkv/webm，AVI/FLV/WMV 等容器没有系统支持（{ex.Message}）。"));
        }
        finally
        {
            extractor?.Release();
            extractor?.Dispose();
        }
    }

    private static bool HasDecoder(string mime)
    {
        try
        {
            using var codec = MediaCodec.CreateDecoderByType(mime);
            return codec is not null;
        }
        catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalArgumentException)
        {
            return false;
        }
    }

    private static string Describe(string? videoMime, string? audioMime)
    {
        var parts = new List<string>(2);
        if (videoMime is not null) parts.Add(Short(videoMime));
        if (audioMime is not null) parts.Add(Short(audioMime));
        return string.Join(" + ", parts);

        static string Short(string mime) => mime.StartsWith("video/", StringComparison.Ordinal)
            ? mime[6..].ToUpperInvariant()
            : mime.StartsWith("audio/", StringComparison.Ordinal) ? mime[6..].ToUpperInvariant() : mime;
    }

    /// <summary>由宿主定时器调用：把播放进度推给 UI（Android MediaPlayer 没有位置回调）。</summary>
    public void PollPosition()
    {
        if (_prepared)
            RaiseChanged();
    }

    private void RaiseChanged() => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 宿主把 TextureView 的 Surface 交过来。允许在 <c>Prepare</c> 之前到达（暂存，打开时再设），
    /// 也允许在播放中到达（切清晰度、旋转屏幕后重建画面）。
    /// </summary>
    public bool AttachVideoSurface(object surface)
    {
        if (surface is not Surface androidSurface)
            return false;
        lock (_gate)
        {
            if (_disposed)
                return false;
            _surface = androidSurface;
            _videoAttached = true;
            if (_prepared)
            {
                try { _player.SetSurface(androidSurface); }
                catch (Java.Lang.IllegalStateException)
                {
                    // 播放器正在切换状态；保留下一次 Open/SetSurface 的机会，不把异常丢给 UI 线程。
                }
            }
        }
        RaiseChanged();
        return true;
    }

    public void DetachVideoSurface()
    {
        lock (_gate)
        {
            if (!_videoAttached)
                return;
            _videoAttached = false;
            _surface = null;
            if (_prepared && !_disposed)
            {
                try { _player.SetSurface(null); }
                catch (Java.Lang.IllegalStateException)
                {
                }
            }
        }
        RaiseChanged();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                _player.Reset();
                _player.Release();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // 释放期间的 IllegalState 不应阻止宿主退出
            }
            finally
            {
                _player.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }
}
