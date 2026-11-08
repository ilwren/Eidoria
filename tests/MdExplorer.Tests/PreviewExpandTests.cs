using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.60：预览能不能打开、画面能不能放大。
/// <para>
/// 用户回报「媒体不能随意展开预览」——在 Windows / 大窗口上，预览栏的可见性只看设置开关，
/// 用户把开关关过之后就再也没有入口打开；画面本身又被写死 280 DIP，放大不了。
/// 这里把两条链路都钉死：打开有显式命令（Ctrl+P / 溢出菜单），画面有放大命令且受宿主高度约束。
/// </para>
/// </summary>
public sealed class PreviewExpandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-preview-expand-").FullName;
    private readonly string _dataDir;
    private readonly string _startDir;

    public PreviewExpandTests()
    {
        _dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _startDir = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        File.WriteAllText(Path.Combine(_startDir, "clip.mp4"), "placeholder");
        File.WriteAllText(Path.Combine(_startDir, "note.txt"), "note");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ShellViewModel CreateShell() => AppHost.CreateShell(AppHost.Build(_dataDir, _startDir));

    [Fact]
    public async Task OpenPreviewCommandRestoresThePaneAfterTheSettingWasTurnedOff()
    {
        var shell = CreateShell();
        shell.SetViewportSize(1440, 900);
        await shell.OpenFolderAsync(_startDir);
        var file = Assert.Single(shell.Browser.Items, item => item.Name == "note.txt");

        // 用户曾经关过预览栏（设置里的开关被写成 false）。
        shell.ClosePreviewPane();
        Assert.False(shell.ShowPreviewPane);

        await shell.Preview.LoadAsync(file.Model);
        Assert.False(shell.ShowPreviewPaneEffective);

        // Ctrl+P 的入口：必须能把预览栏重新打开，而不是「设置关了就永远打不开」。
        shell.OpenPreviewPaneCommand.Execute(null);
        Assert.True(shell.ShowPreviewPaneNow);
        Assert.True(shell.ShowPreviewPaneEffective);
    }

    [Fact]
    public async Task OpenPreviewCommandOnNarrowViewportOpensTheFloatingPreview()
    {
        var shell = CreateShell();
        shell.SetViewportSize(390, 844);
        await shell.OpenFolderAsync(_startDir);

        shell.OpenPreviewPaneCommand.Execute(null);
        Assert.True(shell.IsPreviewPageOpen);
        Assert.False(shell.ShowPreviewPaneEffective);
    }

    [Fact]
    public async Task ClosingThePaneClearsTheManualRequestSoItDoesNotComeBack()
    {
        var shell = CreateShell();
        shell.SetViewportSize(1440, 900);
        await shell.OpenFolderAsync(_startDir);
        var file = Assert.Single(shell.Browser.Items, item => item.Name == "note.txt");
        await shell.Preview.LoadAsync(file.Model);

        shell.OpenPreviewPaneCommand.Execute(null);
        Assert.True(shell.ShowPreviewPaneEffective);

        shell.ClosePreviewPane();
        Assert.False(shell.ShowPreviewPaneEffective);
        Assert.False(shell.ShowPreviewPane);
    }

    [Fact]
    public async Task MediaSurfaceStartsAt280AndExpandsWithinTheHostBudget()
    {
        var preview = new PreviewViewModel(
            MdExplorer.UI.Preview.BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService()),
            () => new ProcessFileLauncher(),
            new FramePushMediaStub(),
            // 视频内嵌开关默认是开的（设置页默认值），否则 ShowFrameVideoSurface 恒为 false。
            () => new AppSettings { PlayVideoInline = true, MediaAutoPlay = false });

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));

        Assert.True(preview.CanExpandMedia);
        Assert.Equal(280d, preview.VideoSurfaceHeight);
        Assert.Equal("放大画面", preview.ExpandMediaLabel);

        // 预览栏只有 700 高：展开高度不能超过它减掉控制条的空间。
        preview.SetExpandedVideoBudget(700);
        preview.ToggleMediaExpandedCommand.Execute(null);
        Assert.True(preview.IsMediaExpanded);
        Assert.Equal("还原画面", preview.ExpandMediaLabel);
        Assert.InRange(preview.VideoSurfaceHeight, 280d, 580d);

        // 换文件必须自动还原：否则下一条内容会带着上一个文件的界面状态出现。
        await preview.LoadAsync(Item("note.txt", FileKind.Text));
        Assert.False(preview.IsMediaExpanded);
        Assert.False(preview.CanExpandMedia);
    }

    [Fact]
    public async Task FrameTelemetryIsReflectedInThePreviewDiagnostics()
    {
        var stub = new FramePushMediaStub();
        var preview = new PreviewViewModel(
            MdExplorer.UI.Preview.BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService()),
            () => new ProcessFileLauncher(),
            stub,
            () => new AppSettings { PlayVideoInline = true, MediaAutoPlay = false });

        // 没有选中媒体时不显示任何帧统计（不能凭空白写 0 fps）。
        Assert.False(preview.HasMediaFrameStats);

        await preview.LoadAsync(Item("clip.mp4", FileKind.Video));
        // 选中媒体但后端还没开始推帧：如实说「还没有画面」，而不是 0 fps。
        Assert.True(preview.HasMediaFrameStats);
        Assert.Contains("还没有画面", preview.MediaFrameStatsText, StringComparison.Ordinal);

        Assert.True(stub.AttachFrameSink(new NullSink()));
        stub.EmitFrame();
        stub.EmitFrame();
        // 账本是后端对象本身的状态，读的是最新值，不需要额外通知。
        Assert.Contains("帧统计", preview.MediaFrameStatsText, StringComparison.Ordinal);
        Assert.Contains("fps", preview.MediaFrameStatsText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 与磁盘上真实存在的文件一致（长度 + 时间戳）：混合检索会复核文件事实，
    /// 对不上的命中会被丢掉——测试项也必须「像真的」。
    /// </summary>
    private FileItem Item(string name, FileKind kind)
    {
        var path = Path.Combine(_startDir, name);
        if (!File.Exists(path))
            File.WriteAllText(path, name);
        var info = new FileInfo(path);
        return new FileItem(FileLocator.FromPath(path), name, kind, info.Length, info.LastWriteTime,
            IsHidden: false, IsSymlink: false, info.Extension);
    }

    private sealed class NullSink : IMediaFrameSink
    {
        public void PresentFrame(ReadOnlySpan<byte> bgra8888, int width, int height, int strideBytes) { }
        public void Clear() { }
    }

    /// <summary>推帧后端 + 帧统计：只发帧，不解码。</summary>
    private sealed class FramePushMediaStub : IMediaPlaybackService, IMediaProbe, IMediaVideoFrameSource, IMediaFrameTelemetry
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult(true, HasVideo: true, HasAudio: true, "假后端预判"));

        private IMediaFrameSink? _sink;
        private FramePacingStats _pacing = new(30d);

        public MediaPlaybackCapabilities Capabilities { get; } =
            new(CanPlayAudio: true, CanPlayVideo: true, "frame-stub", "测试用推帧后端");

        public MediaRendererKind RendererKind => MediaRendererKind.FramePush;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Paused;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromSeconds(60);
        public int VolumePercent => 100;
        public string? LastError => null;
        public event EventHandler? PlaybackChanged { add { } remove { } }

        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
        {
            State = MediaPlaybackState.Paused;
            return Task.FromResult(true);
        }

        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(int percent) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool AttachFrameSink(IMediaFrameSink sink)
        {
            _sink = sink;
            var pacing = new FramePacingStats(30d);
            pacing.MarkFirstFramePending(System.Diagnostics.Stopwatch.GetTimestamp());
            _pacing = pacing;
            return true;
        }

        public void DetachFrameSink() => _sink = null;

        public FramePacingStats FramePacing => _pacing;

        public void EmitFrame()
        {
            if (_sink is null)
                return;
            var pixels = new byte[4 * 2];
            _sink.PresentFrame(pixels, 1, 1, 4);
            _pacing.Record(System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }
}
