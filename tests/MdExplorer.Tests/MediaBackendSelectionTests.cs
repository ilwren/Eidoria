using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Preview;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.39：双内嵌媒体后端（平台栈 + 可选 libmpv）。核心只依赖端口，所以
/// 「偏好 → 裁决 → 回退与如实告知」「裁决层不吞掉可选画面端口」都能在无头环境断言。
/// </summary>
public sealed class MediaBackendSelectionTests
{
    private static AppSettings Settings(string preference = MediaBackendPreferenceNames.Auto) =>
        new() { MediaBackendPreference = preference };

    private static MediaBackendSelector Selector(
        IMediaPlaybackService? system,
        IMediaPlaybackService? libMpv,
        AppSettings? settings = null)
    {
        var current = settings ?? Settings();
        return new MediaBackendSelector(
            () => current,
            system is null ? null : () => system,
            libMpv is null ? null : () => libMpv);
    }

    [Fact]
    public void AutoPrefersLibMpvWhenItIsAvailable()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new FramePushFakeBackend("libmpv");

        var selector = Selector(system, mpv);

        Assert.Equal("libmpv", selector.Capabilities.BackendName);
        Assert.Equal(MediaRendererKind.FramePush, selector.RendererKind);
        Assert.Contains("使用：libmpv", selector.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("自动", selector.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoFallsBackToSystemStackAndSaysWhy()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        // 「注册了但用不了」：原生库没找到时 libmpv 后端正是这个形状（能力位为假）
        var mpv = new FakeBackend("libmpv", audio: false, video: false);

        var selector = Selector(system, mpv);

        Assert.Equal("MF", selector.Capabilities.BackendName);
        Assert.Equal(MediaRendererKind.FramePush, selector.RendererKind);
        // Auto 不是“回退”：它就是挑能用的那个。但“libmpv 为什么没被用上”必须写出来，
        // 否则选自动的用户永远不知道更流畅的那条为什么不生效。
        Assert.DoesNotContain("已回退", selector.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("libmpv 不可用", selector.SelectionSummary, StringComparison.Ordinal);
        // 结论与理由要能进诊断文本（预览卡直接显示它），不能只在测试里可见
        Assert.Contains("libmpv 不可用", selector.Capabilities.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPreferenceIgnoresAnAvailableLibMpv()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new FramePushFakeBackend("libmpv");

        var selector = Selector(system, mpv, Settings(MediaBackendPreferenceNames.SystemApi));

        Assert.Equal("MF", selector.Capabilities.BackendName);
        Assert.Equal(MediaRendererKind.FramePush, selector.RendererKind);
        Assert.Contains("只用系统解码栈", selector.SelectionSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("已回退", selector.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void LibMpvPreferenceWithoutNativeLibraryFallsBackAndAdmitsIt()
    {
        var system = new FakeBackend("GStreamer", video: true, renderer: MediaRendererKind.FramePush);

        // 这台机器上没有 libmpv：宿主根本没注册第二个工厂
        var selector = Selector(system, libMpv: null, Settings(MediaBackendPreferenceNames.LibMpv));

        Assert.Equal("GStreamer", selector.Capabilities.BackendName);
        Assert.Contains("已回退", selector.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("libmpv", selector.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoBackendAtAllReportsUnsupportedAndRefusesToOpen()
    {
        var selector = Selector(system: null, libMpv: null);

        Assert.False(selector.Capabilities.IsSupported);
        Assert.Equal("none", selector.Capabilities.BackendName);
        Assert.Equal(MediaRendererKind.None, selector.RendererKind);
        Assert.False(await selector.OpenAsync("/media/a.mp4", CancellationToken.None));
        Assert.Equal(MediaPlaybackState.Idle, selector.State);
    }

    [Fact]
    public async Task ProbeIsDelegatedAndALibMpvStyleBackendSaysItDoesNotPrecheck()
    {
        var system = new ProbingFakeBackend("MF", canPlay: false);
        var selector = Selector(system, libMpv: null, Settings(MediaBackendPreferenceNames.SystemApi));

        var probed = await selector.ProbeAsync("/media/a.mkv", CancellationToken.None);
        Assert.False(probed.CanPlay);
        Assert.Equal(1, system.ProbeCount);

        // 换成不实现 IMediaProbe 的后端：如实说“不预检”，并允许用户尝试
        var mpv = new FramePushFakeBackend("libmpv");
        var selector2 = Selector(system: null, libMpv: mpv);
        var unknown = await selector2.ProbeAsync("/media/a.mkv", CancellationToken.None);
        Assert.True(unknown.CanPlay);
        Assert.Contains("不做打开前预检", unknown.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingPreferenceDisposesTheOldBackendAndReDecides()
    {
        var settings = Settings();
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new FramePushFakeBackend("libmpv");
        var selector = Selector(system, mpv, settings);
        Assert.Equal("libmpv", selector.Capabilities.BackendName);

        settings.MediaBackendPreference = MediaBackendPreferenceNames.SystemApi;
        selector.Reset();

        Assert.False(selector.IsResolved);
        Assert.Equal("MF", selector.Capabilities.BackendName);
        Assert.True(mpv.Disposed);
        await selector.DisposeAsync();
    }

    [Fact]
    public void InvalidPersistedPreferenceNormalizesToAuto()
    {
        Assert.Equal(MediaBackendPreference.Auto, MediaBackendPreferenceNames.Parse("nonsense"));
        Assert.Equal(MediaBackendPreference.Auto, MediaBackendPreferenceNames.Parse(null));
        Assert.Equal(MediaBackendPreference.LibMpv, MediaBackendPreferenceNames.Parse("libmpv"));
        Assert.Equal("SystemApi",
            MediaBackendPreferenceNames.ToSettingValue(MediaBackendPreferenceNames.Parse("SystemApi")));
    }

    [Fact]
    public async Task SettingsFileWithAnUnknownBackendFallsBackToAuto()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eidoria-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                "{\"MediaBackendPreference\":\"something-else\"}");
            var store = await SettingsStore.LoadAsync(directory, CancellationToken.None);
            Assert.Equal("Auto", store.Settings.MediaBackendPreference);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── 裁决层不能吞掉可选的画面端口 ────────────────────────────────────────

    [Fact]
    public void SelectorExposesTheActiveBackendForOptionalPorts()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new FramePushFakeBackend("libmpv");
        var selector = Selector(system, mpv);

        // 裁决层自己装不出 IMediaVideoFrameSource：它必须把“真正在跑的那个”交出来，
        // 否则预览视图会以为这个后端不出画面。
        Assert.False(selector is IMediaVideoFrameSource);
        Assert.Same(mpv, selector.ActiveBackend);
        Assert.Equal(0, mpv.AttachCount);
        Assert.True(((IMediaVideoFrameSource)selector.ActiveBackend!).AttachFrameSink(new RecordingFrameSink()));
        Assert.Equal(1, mpv.AttachCount);
    }

    // ── 预览状态机：谁出画面 ────────────────────────────────────────────────

    private static FileItem Item(string name, FileKind kind) => new(
        FileLocator.FromPath("/media/" + name),
        name,
        kind,
        4096,
        DateTimeOffset.UnixEpoch,
        IsHidden: false,
        IsSymlink: false,
        Path.GetExtension(name));

    private static PreviewViewModel Preview(IMediaPlaybackService media, AppSettings settings) => new(
        BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService()),
        () => new ProcessFileLauncher(),
        media,
        () => settings);

    [Fact]
    public async Task MpvStyleFramePushBackendDrawsVideoInTheHostSurface()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new FramePushFakeBackend("libmpv");
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowFrameVideoSurface);
        Assert.False(preview.ShowNativeVideoSurface);
        Assert.Same(backend, preview.VideoFrameSource);
        Assert.Null(preview.VideoRenderer); // 两种交付方式互斥
        Assert.Contains("宿主帧呈现", preview.MediaRendererText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewStateUsesTheActiveBackendNotTheSelector()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new FramePushFakeBackend("libmpv");
        var preview = Preview(Selector(system, mpv, settings), settings);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        // 关键回归：预览拿到的是裁决层，但画面端口必须来自真正在跑的那个后端。
        Assert.Same(mpv, preview.VideoFrameSource);
        Assert.True(preview.ShowFrameVideoSurface);

        var sink = new RecordingFrameSink();
        Assert.True(preview.VideoFrameSource!.AttachFrameSink(sink));
        Assert.Same(sink, mpv.Sink);
        mpv.Sink!.PresentFrame(new byte[4 * 2 * 2], 2, 2, 8);
        Assert.Equal(1, sink.FrameCount);
    }

    [Fact]
    public async Task AudioDoesNotAskForAVideoSurface()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new FramePushFakeBackend("libmpv");
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.False(preview.ShowFrameVideoSurface);
        Assert.False(preview.ShowNativeVideoSurface);
    }

    [Fact]
    public async Task NonMediaSelectionNeverAsksForADecoder()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new FramePushFakeBackend("libmpv");
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("photo.png", FileKind.Image));

        Assert.False(preview.IsMedia);
        Assert.Null(preview.VideoFrameSource);
        Assert.Null(preview.VideoRenderer);
        Assert.False(preview.ShowFrameVideoSurface);
    }

    [Fact]
    public async Task VideoGateStaysClosedUntilTheUserAsksForInlineVideo()
    {
        var settings = new AppSettings { PlayVideoInline = false };
        var backend = new FramePushFakeBackend("libmpv");
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));
        Assert.True(preview.ShowInlineVideoGate);
        Assert.False(preview.ShowFrameVideoSurface);
        Assert.False(preview.CanPlayMediaInApp);

        preview.EnableInlineVideoCommand.Execute(null);
        Assert.True(preview.ShowFrameVideoSurface);
        Assert.True(preview.CanPlayMediaInApp);
    }

    // ── 测试替身 ────────────────────────────────────────────────────────────

    private class FakeBackend : IMediaPlaybackService
    {
        private readonly bool _audio;
        private readonly bool _video;
        private readonly MediaRendererKind _renderer;

        public FakeBackend(string name, bool audio = true, bool video = false,
            MediaRendererKind renderer = MediaRendererKind.None)
        {
            Name = name;
            _audio = audio;
            _video = video;
            _renderer = renderer;
        }

        public string Name { get; }
        public bool Disposed { get; private set; }

        public MediaPlaybackCapabilities Capabilities =>
            new(_audio, _video, Name, $"{Name} 后端诊断。");

        public MediaRendererKind RendererKind => _renderer;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public int VolumePercent { get; private set; } = 100;
        public string? LastError { get; private set; }
        public event EventHandler? PlaybackChanged;

        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(int percent) => VolumePercent = Math.Clamp(percent, 0, 100);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>libmpv 后端的形状：FramePush + 自己实现帧交付面（无原生控件）。</summary>
    private sealed class FramePushFakeBackend(string name, bool audio = true, bool video = true)
        : FakeBackend(name, audio, video, MediaRendererKind.FramePush), IMediaVideoFrameSource
    {
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public IMediaFrameSink? Sink { get; private set; }

        public bool AttachFrameSink(IMediaFrameSink sink)
        {
            AttachCount++;
            Sink = sink;
            return true;
        }

        public void DetachFrameSink()
        {
            DetachCount++;
            Sink = null;
        }
    }

    private sealed class RecordingFrameSink : IMediaFrameSink
    {
        public int FrameCount { get; private set; }
        public bool Cleared { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgra8888, int width, int height, int strideBytes) =>
            FrameCount++;

        public void Clear() => Cleared = true;
    }

    private sealed class ProbingFakeBackend(string name, bool canPlay)
        : FakeBackend(name, video: canPlay, renderer: MediaRendererKind.FramePush), IMediaProbe
    {
        public int ProbeCount { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult(new MediaProbeResult(
                canPlay, HasVideo: canPlay, HasAudio: true,
                canPlay ? "系统解码器可以处理该文件。" : "系统解码器不认这个容器。"));
        }
    }
}
