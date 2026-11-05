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
/// 「偏好 → 裁决 → 回退与如实告知」「控件交付与释放」「非媒体不建解码器」都能在无头环境断言。
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
        var mpv = new HostedFakeBackend("libmpv", video: true);

        var selector = Selector(system, mpv);

        Assert.Equal("libmpv", selector.Capabilities.BackendName);
        Assert.Equal(MediaRendererKind.HostedControl, selector.RendererKind);
        Assert.Contains("使用：libmpv", selector.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("自动", selector.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoFallsBackToSystemStackAndSaysWhy()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new HostedFakeBackend("libmpv", video: false, audio: false);

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
        var mpv = new HostedFakeBackend("libmpv", video: true);

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
        var hosted = new HostedFakeBackend("libmpv", video: true);
        var selector2 = Selector(system: null, libMpv: hosted);
        var unknown = await selector2.ProbeAsync("/media/a.mkv", CancellationToken.None);
        Assert.True(unknown.CanPlay);
        Assert.Contains("不做打开前预检", unknown.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedControlIsDelegatedAndReleased()
    {
        var mpv = new HostedFakeBackend("libmpv", video: true);
        var selector = Selector(system: null, libMpv: mpv);

        var session = selector.CreateVideoControl(includeVideoSurface: true);
        Assert.True(session.IsAvailable);
        Assert.Equal(1, mpv.CreateCount);
        Assert.True(mpv.LastIncludeVideoSurface ?? false);

        // 音频：仍然要建上下文，但不要 GPU 表面
        mpv.LastIncludeVideoSurface = null;
        var audioSession = selector.CreateVideoControl(includeVideoSurface: false);
        Assert.True(audioSession.IsAvailable);
        Assert.False(mpv.LastIncludeVideoSurface ?? true);

        selector.ReleaseVideoControl();
        Assert.Equal(1, mpv.ReleaseCount);
    }

    [Fact]
    public void BackendWithoutHostedControlExplainsItselfInsteadOfReturningAControl()
    {
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var selector = Selector(system, libMpv: null, Settings(MediaBackendPreferenceNames.SystemApi));

        var session = selector.CreateVideoControl(includeVideoSurface: true);
        Assert.False(session.IsAvailable);
        Assert.Contains("不使用宿主控件", session.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingPreferenceDisposesTheOldBackendAndReDecides()
    {
        var settings = Settings();
        var system = new FakeBackend("MF", video: true, renderer: MediaRendererKind.FramePush);
        var mpv = new HostedFakeBackend("libmpv", video: true);
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
    public async Task HostedControlBackendDrawsVideoInItsOwnControl()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new HostedFakeBackend("libmpv", video: true);
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.True(preview.ShowHostedMediaHost);
        Assert.True(preview.ShowHostedVideoSurface);
        Assert.Equal(280d, preview.HostedSurfaceHeight);
        Assert.False(preview.ShowFrameVideoSurface);
        Assert.False(preview.ShowNativeVideoSurface);
        Assert.NotNull(preview.VideoControlSource);
        Assert.NotNull(preview.MediaControlHostSource);
        Assert.Contains("libmpv", preview.MediaRendererText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioNeedsTheHostButNotAVideoSurface()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new HostedFakeBackend("libmpv", video: true);
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("song.mp3", FileKind.Audio));

        Assert.True(preview.CanPlayMediaInApp);
        Assert.False(preview.ShowHostedVideoSurface);
        // 控件宿主仍然要交给视图：音频也得先有 libmpv 上下文（没有上下文就没有解码器）
        Assert.NotNull(preview.MediaControlHostSource);
        Assert.True(preview.ShowHostedMediaHost);
        // 不占版面，但**不许**用 IsVisible 收起——那会把它摘出视觉树，声音也就没了
        Assert.Equal(0d, preview.HostedSurfaceHeight);
        Assert.False(preview.HostedVideoWantsSurface);
    }

    [Fact]
    public async Task NonMediaSelectionNeverAsksForADecoder()
    {
        var settings = new AppSettings { PlayVideoInline = true };
        var backend = new HostedFakeBackend("libmpv", video: true);
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("photo.png", FileKind.Image));

        Assert.False(preview.IsMedia);
        Assert.Null(preview.MediaControlHostSource);
        Assert.False(preview.ShowHostedMediaHost);
        Assert.False(preview.ShowHostedVideoSurface);
    }

    [Fact]
    public async Task VideoGateStaysClosedUntilTheUserAsksForInlineVideo()
    {
        var settings = new AppSettings { PlayVideoInline = false };
        var backend = new HostedFakeBackend("libmpv", video: true);
        var preview = Preview(backend, settings);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));
        Assert.True(preview.ShowInlineVideoGate);
        Assert.False(preview.ShowHostedVideoSurface);
        Assert.False(preview.CanPlayMediaInApp);

        preview.EnableInlineVideoCommand.Execute(null);
        Assert.True(preview.ShowHostedVideoSurface);
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

    private sealed class HostedFakeBackend(string name, bool video, bool audio = true)
        : FakeBackend(name, audio, video, MediaRendererKind.HostedControl), IMediaVideoControlSource
    {
        public int CreateCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public bool? LastIncludeVideoSurface { get; set; }

        public MediaVideoControl CreateVideoControl(bool includeVideoSurface)
        {
            CreateCount++;
            LastIncludeVideoSurface = includeVideoSurface;
            // 单元测试不初始化 Avalonia：这里只要“非 null 控件”这个事实，
            // 真控件由 MediaVideoControlHost 在视图层承接。
            return new MediaVideoControl(new object());
        }

        public void ReleaseVideoControl() => ReleaseCount++;
    }
}
