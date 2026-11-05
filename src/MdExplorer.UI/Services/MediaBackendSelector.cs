using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>
/// 内嵌媒体后端的裁决层：同一台机器上可能同时存在平台自带解码栈与可选的 libmpv，
/// 这里按用户偏好 + 实际可用性挑一个，并**把挑的结果和理由写进诊断文本**。
/// <para>
/// 三条原则：
/// ① 只用真的报出能力位的后端——能力位为 false 的一律不算可用（不出现“看得见播不响”）；
/// ② 偏好是用户的选择，不是承诺：显式选了 libmpv 但这台机器上加载不了时，回退到平台栈
///    并在诊断里说清「已回退」与原因，而不是静默换后端；
/// ③ 一次只持有后端一个实例，同一个后端不重复初始化（WinRT 播放器与 libmpv 都有代价）。
/// </para>
/// <para>
/// 本类不是线程安全的：全部调用都发生在 UI 线程（与端口契约一致）。
/// </para>
/// </summary>
public sealed class MediaBackendSelector
    : IMediaPlaybackService, IMediaProbe, IMediaPlaybackPoller, IMediaVideoControlSource, IMediaBackendSelector
{
    private enum BackendKind
    {
        System,
        LibMpv,
    }

    private readonly Func<AppSettings> _settings;
    private readonly Func<IMediaPlaybackService?>? _systemFactory;
    private readonly Func<IMediaPlaybackService?>? _libMpvFactory;
    private readonly List<IMediaPlaybackService> _discarded = [];

    private IMediaPlaybackService? _active;
    private BackendKind? _activeKind;
    private bool _resolved;
    private MediaBackendPreference _preference = MediaBackendPreference.Auto;
    private string _selectionSummary = "尚未选择内嵌媒体后端。";
    private string _selectionNote = string.Empty;
    private bool _disposed;

    public MediaBackendSelector(
        Func<AppSettings> settings,
        Func<IMediaPlaybackService?>? systemFactory,
        Func<IMediaPlaybackService?>? libMpvFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _systemFactory = systemFactory;
        _libMpvFactory = libMpvFactory;
    }

    /// <summary>后端已（重新）裁决；设置页据此刷新状态文本。</summary>
    public event EventHandler? BackendResolved;

    /// <summary>当前生效的偏好（已解析；无法识别的值按 Auto 处理）。</summary>
    public MediaBackendPreference Preference => _resolved ? _preference : ParsePreference();

    /// <summary>当前真正在用的后端名；没有可用后端时为 <c>none</c>。</summary>
    public string ActiveBackendName => _active?.Capabilities.BackendName ?? "none";

    /// <summary>裁决结论 + 逐条理由（设置页与预览卡都用它，不猜）。</summary>
    public string SelectionSummary => _selectionSummary;

    /// <summary>是否已经裁决过（设置页在没打开过媒体时也能如实说“还没决定”）。</summary>
    public bool IsResolved => _resolved;

    /// <summary>真正在跑的后端（用于探测它实现了哪些可选画面端口）。</summary>
    public IMediaPlaybackService? ActiveBackend
    {
        get
        {
            EnsureResolved();
            return _active;
        }
    }

    /// <summary>
    /// 偏好变更后调用：停掉并释放当前后端，下次选媒体时重新裁决。
    /// 正在播的内容会被停止——切换后端不可能保持播放状态，这一点不装作可以。
    /// </summary>
    public void Reset()
    {
        StopAndDispose();
        _active = null;
        _activeKind = null;
        _resolved = false;
        _selectionSummary = "已切换媒体后端偏好，下次打开媒体时重新裁决。";
        _selectionNote = string.Empty;
        BackendResolved?.Invoke(this, EventArgs.Empty);
    }

    // ── IMediaPlaybackService ────────────────────────────────────────────────

    public MediaPlaybackCapabilities Capabilities
    {
        get
        {
            EnsureResolved();
            if (_active is null)
                return new MediaPlaybackCapabilities(false, false, "none", _selectionSummary);
            var active = _active.Capabilities;
            return string.IsNullOrEmpty(_selectionNote)
                ? active
                : active with { Diagnostic = active.Diagnostic + " " + _selectionNote };
        }
    }

    public MediaRendererKind RendererKind
    {
        get
        {
            EnsureResolved();
            return _active?.RendererKind ?? MediaRendererKind.None;
        }
    }

    public MediaPlaybackState State => _active?.State ?? MediaPlaybackState.Idle;
    public TimeSpan Position => _active?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _active?.Duration ?? TimeSpan.Zero;
    public int VolumePercent => _active?.VolumePercent ?? 100;
    public string? LastError => _active?.LastError;

    public event EventHandler? PlaybackChanged;

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        EnsureResolved();
        return _active is null ? Task.FromResult(false) : _active.OpenAsync(path, cancellationToken);
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        EnsureResolved();
        return _active?.PlayAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        EnsureResolved();
        return _active?.PauseAsync() ?? Task.CompletedTask;
    }

    public Task StopAsync()
    {
        EnsureResolved();
        return _active?.StopAsync() ?? Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        EnsureResolved();
        return _active?.SeekAsync(position) ?? Task.CompletedTask;
    }

    public void SetVolume(int percent)
    {
        EnsureResolved();
        _active?.SetVolume(percent);
    }

    // ── IMediaProbe ──────────────────────────────────────────────────────────

    /// <summary>
    /// 代理到当前后端。libmpv 不做打开前预检（它几乎什么容器都收，真正的结论只有播了才知道），
    /// 这里如实回一句「不预检」，而不是编一个「应该能播」。
    /// </summary>
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        EnsureResolved();
        if (_active is IMediaProbe probe)
            return await probe.ProbeAsync(path, cancellationToken).ConfigureAwait(true);
        return new MediaProbeResult(
            true, false, false,
            $"{( _active?.Capabilities.BackendName ?? "无后端")} 不做打开前预检：能否播放以实际打开结果为准。");
    }

    // ── IMediaPlaybackPoller ─────────────────────────────────────────────────

    public void PollPosition() => (_active as IMediaPlaybackPoller)?.PollPosition();

    // ── IMediaVideoControlSource ─────────────────────────────────────────────

    public MediaVideoControl CreateVideoControl(bool includeVideoSurface)
    {
        EnsureResolved();
        if (_active is IMediaVideoControlSource source)
            return source.CreateVideoControl(includeVideoSurface);
        return new MediaVideoControl(null,
            $"{_active?.Capabilities.BackendName ?? "无后端"} 不使用宿主控件交付画面。");
    }

    public void ReleaseVideoControl() => (_active as IMediaVideoControlSource)?.ReleaseVideoControl();

    // ── 裁决 ─────────────────────────────────────────────────────────────────

    private MediaBackendPreference ParsePreference() =>
        MediaBackendPreferenceNames.Parse(_settings().MediaBackendPreference);

    private void EnsureResolved()
    {
        // 裁决只做一次：失败（一个可用后端都没有）也算裁决过，否则每次读能力位都会重新建实例。
        if (_disposed || _resolved)
            return;

        _preference = ParsePreference();
        // 自动与「优先 libmpv」同一顺序：libmpv 若可用就是更流畅的那条；
        // 「只用系统栈」把系统放前面，失败再退回 libmpv（并写明回退）。
        var order = _preference == MediaBackendPreference.SystemApi
            ? new[] { BackendKind.System, BackendKind.LibMpv }
            : new[] { BackendKind.LibMpv, BackendKind.System };

        var notes = new List<string>();
        // 与 notes 分开：只有“这个平台确实注册了它、但它用不了”才值得在 Auto 模式下说出来。
        // 「本平台没有 libmpv 后端」对 Android 用户是常态，不该当成结论的一部分。
        var unusable = new List<string>();
        foreach (var kind in order)
        {
            var factory = kind == BackendKind.LibMpv ? _libMpvFactory : _systemFactory;
            var label = kind == BackendKind.LibMpv ? "libmpv" : "系统解码栈";
            if (factory is null)
            {
                notes.Add($"本平台没有{label}后端。");
                continue;
            }

            IMediaPlaybackService? created;
            try
            {
                created = factory();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var why = $"{label} 初始化失败：{ex.Message}";
                notes.Add(why);
                unusable.Add(why);
                continue;
            }

            if (created is null)
            {
                var why = $"{label} 未创建实例。";
                notes.Add(why);
                unusable.Add(why);
                continue;
            }

            if (created.Capabilities.IsSupported)
            {
                _active = created;
                _activeKind = kind;
                break;
            }

            // 不可用的候选留着统一释放：它的 Dispose 也要在 UI 线程上按顺序做。
            _discarded.Add(created);
            var reason = $"{created.Capabilities.BackendName} 不可用：{created.Capabilities.Diagnostic}";
            notes.Add(reason);
            unusable.Add(reason);
        }

        if (_active is null)
        {
            _resolved = true;
            _selectionSummary = notes.Count == 0
                ? "没有可用的内嵌媒体后端。"
                : "没有可用的内嵌媒体后端：" + string.Join(" ", notes);
            _selectionNote = string.Empty;
            BackendResolved?.Invoke(this, EventArgs.Empty);
            return;
        }

        _active.PlaybackChanged += OnActivePlaybackChanged;
        var preferenceText = _preference switch
        {
            MediaBackendPreference.SystemApi => "只用系统解码栈",
            MediaBackendPreference.LibMpv => "优先 libmpv",
            _ => "自动",
        };
        var picked = _active.Capabilities.BackendName;
        // Auto 的语义就是“挑一个能用的”，用不上另一条不算“回退”；但“libmpv 没加载上”这件事
        // 仍然要写出来——否则选“自动”的用户永远不知道更流畅的那条为什么不生效。
        var preferredKind = _preference == MediaBackendPreference.SystemApi ? BackendKind.System : BackendKind.LibMpv;
        var explicitChoice = _preference != MediaBackendPreference.Auto;
        var fellBack = explicitChoice && _activeKind != preferredKind;
        var expected = preferredKind == BackendKind.System ? "系统解码栈" : "libmpv";
        var note = fellBack
            ? $"偏好的{expected}不可用，已回退到{picked}。" + (notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty)
            : (explicitChoice || unusable.Count == 0) ? string.Empty : string.Join(" ", unusable);

        _selectionNote = note;
        _selectionSummary = $"偏好：{preferenceText} · 使用：{picked}" + (note.Length > 0 ? " · " + note : string.Empty);
        _resolved = true;
        BackendResolved?.Invoke(this, EventArgs.Empty);
    }

    private void OnActivePlaybackChanged(object? sender, EventArgs e) => PlaybackChanged?.Invoke(this, e);

    private void StopAndDispose()
    {
        if (_active is { } active)
        {
            active.PlaybackChanged -= OnActivePlaybackChanged;
            try { active.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { /* 释放失败不该把切换偏好这条路堵死 */ }
        }

        foreach (var discarded in _discarded)
        {
            try { discarded.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { }
        }
        _discarded.Clear();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        StopAndDispose();
        _active = null;
        _activeKind = null;
        _resolved = false;
        return ValueTask.CompletedTask;
    }
}
