using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 没有内嵌解码后端时的实现（桌面端的当前默认）。它必须把自己说清楚：
/// 能力位全为 false，所有动作都是空操作，打开即失败并给出可执行建议，
/// 绝不让 UI 出现“看起来能播但没声音”的状态。
/// </summary>
public sealed class UnsupportedMediaPlaybackService(string diagnostic) : IMediaPlaybackService
{
    public MediaPlaybackCapabilities Capabilities { get; } =
        new(false, false, "none", diagnostic);

    public MediaRendererKind RendererKind => MediaRendererKind.None;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan Duration => TimeSpan.Zero;
    public int VolumePercent { get; private set; } = 100;
    public string? LastError { get; private set; }
    public event EventHandler? PlaybackChanged;

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        LastError = diagnostic;
        State = MediaPlaybackState.Failed;
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(false);
    }

    public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
    public void SetVolume(int percent) => VolumePercent = Math.Clamp(percent, 0, 100);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
