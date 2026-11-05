using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Preview;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.30：内嵌媒体播放的端口契约。核心只依赖 IMediaPlaybackService，
/// 所以“没有后端”“只有音频后端”“切换文件要停播”这些行为都能在无头环境断言。
/// </summary>
public sealed class MediaPlaybackTests
{
    private static FileItem Item(string name, FileKind kind) => new(
        FileLocator.FromPath("/media/" + name),
        name,
        kind,
        4096,
        DateTimeOffset.UnixEpoch,
        IsHidden: false,
        IsSymlink: false,
        Path.GetExtension(name));

    private static PreviewViewModel CreatePreview(IMediaPlaybackService? media) => new(
        BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService()),
        () => new ProcessFileLauncher(),
        media);

    private static IMediaPlaybackService Media(bool audio = true, bool video = false) =>
        new FakeMediaPlaybackService(audio, video);

    [Fact]
    public async Task WithoutBallbackBackendUiSaysSoAndNeverPretendsToPlay()
    {
        var preview = CreatePreview(new UnsupportedMediaPlaybackService("桌面端暂无内嵌解码后端。"));
        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        Assert.True(preview.IsMedia);
        Assert.True(preview.IsAudioMedia);
        Assert.False(preview.CanPlayMediaInApp);
        Assert.Equal("none", preview.MediaBackendName);
        Assert.Contains("暂无内嵌解码后端", preview.MediaBackendDiagnostic, StringComparison.Ordinal);
        Assert.False(preview.IsMediaPlaying);
        // 兜底文案不再与媒体面板打架
        Assert.Null(preview.FailureText);
        Assert.False(preview.HasFailure);

        // 没有后端时命令是空操作，不许改动状态或抛异常
        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.False(preview.CanPlayMediaInApp);
        Assert.False(preview.IsMediaPlaying);
        Assert.Equal("此平台暂不支持内嵌播放", preview.MediaStateText);
    }

    private static PreviewViewModel CreatePreviewWithSettings(
        IMediaPlaybackService media, AppSettings settings) => new(
        BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService()),
        () => new ProcessFileLauncher(),
        media,
        () => settings);

    [Fact]
    public async Task VideoDefaultsToExternalPlayerUntilUserOptsIn()
    {
        var settings = new AppSettings { PlayVideoInline = false, MediaAutoPlay = false };
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: true);
        var preview = CreatePreviewWithSettings(media, settings);
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        // 默认不出画面：卡片给两个显式入口，而且不会留下一个“能播但只有声音”的假按钮。
        Assert.True(preview.ShowInlineVideoGate);
        Assert.False(preview.CanPlayMediaInApp);
        Assert.False(preview.ShowFrameVideoSurface);
        Assert.False(preview.HasMediaProbe);
        Assert.Equal("默认用系统播放器打开", preview.MediaStateText);

        // 用户显式选择内嵌后才出画面；这条对当前文件生效，换文件复位。
        preview.EnableInlineVideoCommand.Execute(null);
        Assert.False(preview.ShowInlineVideoGate);
        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowFrameVideoSurface);

        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));
        Assert.False(preview.PlayVideoInline);

        // 音频不受这条偏好影响。
        Assert.True(preview.CanPlayMediaInApp);
    }

    [Fact]
    public async Task SettingsPreferenceCanTurnVideoInlineOnAndAutoplayFollowsIt()
    {
        var settings = new AppSettings { PlayVideoInline = true, MediaAutoPlay = true };
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: true);
        var preview = CreatePreviewWithSettings(media, settings);
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.False(preview.ShowInlineVideoGate);
        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowFrameVideoSurface);
        Assert.Equal(1, media.OpenCount); // 自动播放只在前置条件都成立时才真的开流
        Assert.True(preview.IsMediaPlaying);
    }

    [Fact]
    public async Task AudioBackendPlaysTogglesPausesAndReportsTime()
    {
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: false);
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.Equal("可以播放", preview.MediaStateText);

        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.Equal(1, media.OpenCount);
        Assert.Equal("/media/song.mp3", media.OpenedPath);
        Assert.True(preview.IsMediaPlaying);
        Assert.Equal("正在播放", preview.MediaStateText);

        media.Position = TimeSpan.FromSeconds(75);
        media.Duration = TimeSpan.FromMinutes(3);
        await preview.SeekMediaForwardCommand.ExecuteAsync(null);
        Assert.Equal(TimeSpan.FromSeconds(85), media.Position);
        Assert.Equal("01:25", preview.MediaPositionText);
        Assert.Equal("03:00", preview.MediaDurationText);
        Assert.InRange(preview.MediaProgressPercent, 47.0, 47.3);

        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.False(preview.IsMediaPlaying);
        Assert.Equal(1, media.OpenCount); // 同一文件不重复打开
        Assert.Equal("已暂停", preview.MediaStateText);
    }

    [Fact]
    public async Task AudioOnlyBackendRefusesVideoAndLeavesSystemOpenAsTheWay()
    {
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: false);
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.IsVideoMedia);
        Assert.False(preview.CanPlayMediaInApp);
        Assert.Contains("视频", media.Capabilities.Diagnostic, StringComparison.Ordinal);

        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.Equal(0, media.OpenCount);
        Assert.False(preview.IsMediaPlaying);
    }

    [Fact]
    public async Task SwitchingPreviewStopsThePreviousTrackAndSeekClampsAtZero()
    {
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: true);
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));
        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.True(preview.IsMediaPlaying);

        await preview.LoadAsync(Item("cover.png", FileKind.Image));
        Assert.Equal(1, media.StopCount);
        Assert.Equal(MediaPlaybackState.Stopped, media.State);
        Assert.False(preview.IsMedia);
        Assert.False(preview.CanPlayMediaInApp);

        await preview.LoadAsync(Item("clip.mkv", FileKind.Video));
        Assert.True(preview.CanPlayMediaInApp);
        media.Position = TimeSpan.FromSeconds(3);
        await preview.SeekMediaBackwardCommand.ExecuteAsync(null);
        Assert.Equal(TimeSpan.Zero, media.Position);
    }

    [Fact]
    public async Task VolumeIsClampedToZeroHundred()
    {
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: false);
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("song.flac", FileKind.Audio));

        preview.MediaVolumePercent = 140;
        Assert.Equal(100, media.VolumePercent);
        preview.MediaVolumePercent = -18;
        Assert.Equal(0, media.VolumePercent);
        Assert.Equal(0, preview.MediaVolumePercent);
    }

    [Fact]
    public async Task ProbeReportsUnsupportedContainerWithoutPretendingToPlay()
    {
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: false)
        {
            Probe = new MediaProbeResult(
                false, false, false, "容器不在系统清单内",
                "Windows Media Foundation 对这些容器没有内置支持，可用系统应用打开。"),
        };
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("clip.avi", FileKind.Video));

        Assert.False(preview.CanPlayMediaInApp);
        Assert.True(preview.HasMediaProbe);
        Assert.Equal("容器不在系统清单内", preview.MediaProbeText);
        Assert.True(preview.HasMediaProbeReason);
        Assert.Contains("系统应用打开", preview.MediaProbeReasonText, StringComparison.Ordinal);
        Assert.Equal(1, media.ProbeCount);
    }

    [Fact]
    public async Task BackendWithoutProbeSaysCapabilityUnknownInsteadOfGuessing()
    {
        var preview = CreatePreview(new UnsupportedMediaPlaybackService("桌面端暂无内嵌解码后端。"));
        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        // 没有实现 IMediaProbe 的后端不许按扩展名猜，只说明“未知”
        Assert.Contains("未知", preview.MediaProbeText, StringComparison.Ordinal);
        Assert.False(preview.HasMediaProbeReason);
    }

    [Fact]
    public async Task RendererTextDistinguishesNativeSurfaceFromFramePushAndNone()
    {
        await using var frames = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: true);
        var framePreview = CreatePreview(frames);
        await framePreview.LoadAsync(Item("clip.mp4", FileKind.Video));
        Assert.Equal("画面：宿主帧呈现", framePreview.MediaRendererText);

        await using var none = new UnsupportedMediaPlaybackService("无后端");
        var nonePreview = CreatePreview(none);
        await nonePreview.LoadAsync(Item("clip.mp4", FileKind.Video));
        Assert.Contains("未接入", nonePreview.MediaRendererText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeSurfaceBackendPlaysVideoAndHandsTheRendererToTheHost()
    {
        var backend = new FakeNativeSurfaceBackend();
        var preview = CreatePreview(backend);
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowNativeVideoSurface);
        Assert.Same(backend, preview.VideoRenderer);
        Assert.Equal("画面：平台原生 Surface", preview.MediaRendererText);

        // 音频项不该出现视频画面宿主的占位
        await preview.LoadAsync(Item("song.m4a", FileKind.Audio));
        Assert.False(preview.ShowNativeVideoSurface);
    }

    [Fact]
    public async Task FramePushBackendHandsTheSinkToTheHostAndRendersPushedFrames()
    {
        var backend = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: true);
        var preview = CreatePreview(backend);
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowFrameVideoSurface);
        Assert.False(preview.ShowNativeVideoSurface);
        Assert.Same(backend, preview.VideoFrameSource);
        Assert.Null(preview.VideoRenderer); // 两种交付方式互斥，不许同时挂两套画面
        Assert.Equal("画面：宿主帧呈现", preview.MediaRendererText);

        var sink = new RecordingFrameSink();
        Assert.True(preview.VideoFrameSource!.AttachFrameSink(sink));
        Assert.Same(sink, backend.Sink);
        backend.Sink!.PresentFrame(new byte[4 * 2 * 2], 2, 2, 8);
        Assert.Equal(1, sink.FrameCount);
        Assert.Equal((2, 2), (sink.Width, sink.Height));

        preview.VideoFrameSource.DetachFrameSink();
        Assert.Null(backend.Sink);
    }

    [Fact]
    public async Task FramePushClaimWithoutAFrameSinkIsNotPlayable()
    {
        // 后端自称推帧却交不出帧呈现面：和“有声无画”一样，不许给播放按钮。
        var preview = CreatePreview(new FramePushWithoutSinkBackend());
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.False(preview.CanPlayMediaInApp);
        Assert.False(preview.ShowFrameVideoSurface);
        Assert.Null(preview.VideoFrameSource);
    }

    /// <summary>只记录收到了什么，不碰 UI 线程。</summary>
    private sealed class RecordingFrameSink : IMediaFrameSink
    {
        public int FrameCount { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Cleared { get; private set; }
        public void PresentFrame(ReadOnlySpan<byte> bgra8888, int width, int height, int strideBytes)
        {
            FrameCount++;
            Width = width;
            Height = height;
            Stride = strideBytes;
        }
        public int Stride { get; private set; }
        public void Clear() => Cleared = true;
    }

    private sealed class FramePushWithoutSinkBackend : IMediaPlaybackService
    {
        public MediaPlaybackCapabilities Capabilities { get; } =
            new(true, true, "fake.frame-push-no-sink", "声称推帧但没有帧呈现面。");
        public MediaRendererKind RendererKind => MediaRendererKind.FramePush;
        public MediaPlaybackState State => MediaPlaybackState.Idle;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public int VolumePercent => 100;
        public string? LastError => null;
        public event EventHandler? PlaybackChanged { add { } remove { } }
        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(int percent) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task VideoCapableBackendWithoutRendererStillRefusesVideo()
    {
        // 有 CanPlayVideo 但交付不了画面（RendererKind=None）时不许播，否则就是“有声无画”的假播放。
        var preview = CreatePreview(new VideoWithoutRendererBackend());
        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.False(preview.CanPlayMediaInApp);
        Assert.False(preview.ShowNativeVideoSurface);
        Assert.Null(preview.VideoRenderer);
        Assert.Contains("未接入", preview.MediaRendererText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeVetoesPlaybackEvenWhenBackendSaysItCould()
    {
        // 能力位说“能播”，但系统探测这个文件认不出来——按钮就必须消失，而不是点了才失败。
        var media = new FakeMediaPlaybackService(canPlayAudio: true, canPlayVideo: false)
        {
            Probe = new MediaProbeResult(
                false, false, false, "系统解封装器不识别该容器",
                "Android 的 MediaExtractor 只认 3gp/mp4/ts/mkv/webm。"),
        };
        var preview = CreatePreview(media);
        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        Assert.True(media.Capabilities.CanPlayAudio);
        Assert.False(preview.CanPlayMediaInApp);
        Assert.True(preview.HasMediaProbe);
        Assert.Equal("此平台暂不支持内嵌播放", preview.MediaStateText);

        await preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
        Assert.Equal(0, media.OpenCount);
    }

    private sealed class FakeNativeSurfaceBackend : IMediaPlaybackService, IMediaVideoRenderer
    {
        public MediaPlaybackCapabilities Capabilities { get; } =
            new(true, true, "fake.native", "假原生画面后端。");
        public MediaRendererKind RendererKind => MediaRendererKind.NativeSurface;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public int VolumePercent => 100;
        public string? LastError => null;
        public object? AttachedSurface { get; private set; }

        public event EventHandler? PlaybackChanged;

        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
        {
            State = MediaPlaybackState.Paused;
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        public Task PlayAsync(CancellationToken cancellationToken)
        {
            State = MediaPlaybackState.Playing;
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(int percent) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool AttachVideoSurface(object surface)
        {
            AttachedSurface = surface;
            return true;
        }

        public void DetachVideoSurface() => AttachedSurface = null;
    }

    private sealed class VideoWithoutRendererBackend : IMediaPlaybackService
    {
        public MediaPlaybackCapabilities Capabilities { get; } = new(true, true, "fake.lie", "声称能播视频但交不出画面。");
        public MediaRendererKind RendererKind => MediaRendererKind.None;
        public MediaPlaybackState State => MediaPlaybackState.Idle;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public int VolumePercent => 100;
        public string? LastError => null;
        public event EventHandler? PlaybackChanged { add { } remove { } }
        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(int percent) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMediaPlaybackService(bool canPlayAudio, bool canPlayVideo)
        : IMediaPlaybackService, IMediaProbe, IMediaVideoFrameSource
    {
        /// <summary>宿主交来的帧呈现面；推帧后端就是靠它把画面送进 Avalonia。</summary>
        public IMediaFrameSink? Sink { get; private set; }
        public bool AttachFrameSink(IMediaFrameSink sink)
        {
            Sink = sink;
            return true;
        }
        public void DetachFrameSink() => Sink = null;

        public MediaPlaybackCapabilities Capabilities { get; } = new(
            canPlayAudio,
            canPlayVideo,
            "fake.media",
            canPlayVideo
                ? "测试后端：音频与视频都走假实现。"
                : "测试后端：只支持音频，视频请用系统应用打开。");

        public MediaRendererKind RendererKind => canPlayVideo ? MediaRendererKind.FramePush : MediaRendererKind.None;

        /// <summary>为 null 时按能力位编一个结果；测试可显式指定“不支持”的预判。</summary>
        public MediaProbeResult? Probe { get; init; }
        public int ProbeCount { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            ProbeCount++;
            // 默认预判：后端认自己的能力位，具体文件不额外限制。
            return Task.FromResult(Probe ?? new MediaProbeResult(
                true, canPlayVideo, canPlayAudio, "假后端预判"));
        }

        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public int VolumePercent { get; private set; } = 100;
        public string? LastError { get; private set; }
        public int OpenCount { get; private set; }
        public int StopCount { get; private set; }
        public string? OpenedPath { get; private set; }

        public event EventHandler? PlaybackChanged;

        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
        {
            OpenCount++;
            OpenedPath = path;
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
            StopCount++;
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

        private void Raise() => PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }
}
