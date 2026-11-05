using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Desktop;

/// <summary>
/// Optional desktop libmpv backend. It uses the wrapper's Avalonia renderer directly rather than
/// copying decoded frames through the UI thread. Native libraries are deliberately not bundled;
/// the selected build is loaded from MDEXPLORER_LIBMPV_ROOT or the process/system search path.
/// </summary>
internal sealed class LibMpvMediaPlaybackService : IMediaPlaybackService, IMediaPlaybackPoller
{
    private static readonly BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

    private readonly string? _nativeLibraryPath;
    private readonly string? _initializationFailure;
    private MpvContext? _context;
    private MpvContext? _loadedContext;
    private string? _path;
    private int _generation;
    private bool _pendingPlay;
    private bool _disposed;
    private int _volumePercent = 100;
    private TimeSpan _positionAnchor;
    private long _playStartedTimestamp;

    public LibMpvMediaPlaybackService()
    {
        ConfigureNativeSearchPath();
        var probe = ProbeNativeLibrary();
        _nativeLibraryPath = probe.Path;
        _initializationFailure = probe.Error;
    }

    public static void ConfigureNativeSearchPath()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("MDEXPLORER_LIBMPV_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Directory.Exists(configuredRoot))
            return;

        var root = Path.GetFullPath(configuredRoot);
        // Windows resolves libmpv's dependent DLLs (FFmpeg, zlib, etc.) through PATH even when
        // the main DLL was found by an absolute path. Restrict this to the explicit opt-in folder.
        if (OperatingSystem.IsWindows())
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var alreadyPresent = path.Split(Path.PathSeparator)
                .Any(entry => string.Equals(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
                Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + path);
        }

        MpvApi.RootPath = root;
    }

    public MediaPlaybackCapabilities Capabilities => _initializationFailure is null
        ? new(true, true, "libmpv（直接 renderer）",
            $"libmpv 原生库已载入（{_nativeLibraryPath}）；视频由 Avalonia renderer 直接呈现，不经逐帧截图。" +
            "解码器覆盖、硬件加速与播放效果由所提供的原生构建决定；本应用不携带或静默替换该运行库。")
        : new(false, false, "libmpv（未就绪）",
            $"libmpv 不可用：{_initializationFailure} 未自动回退到 Windows 系统后端；请安装并审核 LGPL-only libmpv 与依赖，" +
            "再把 MDEXPLORER_LIBMPV_ROOT 指向其原生库目录。可在设置中改回系统 API。即使托管代码已编译，也不代表原生播放运行库已安装。");

    public MediaRendererKind RendererKind => _initializationFailure is null
        ? MediaRendererKind.AvaloniaControl
        : MediaRendererKind.None;

    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;

    public TimeSpan Position
    {
        get
        {
            if (_context is { } context && ReferenceEquals(_loadedContext, context) &&
                TryReadNumericProperty(context, "TimePos", out var nativeSeconds) && nativeSeconds >= 0)
                return SafeTimeSpan(nativeSeconds);

            return State == MediaPlaybackState.Playing && _playStartedTimestamp != 0
                ? _positionAnchor + Stopwatch.GetElapsedTime(_playStartedTimestamp)
                : _positionAnchor;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_context is { } context && ReferenceEquals(_loadedContext, context) &&
                TryReadNumericProperty(context, "Duration", out var nativeSeconds) && nativeSeconds > 0)
                return SafeTimeSpan(nativeSeconds);
            return TimeSpan.Zero;
        }
    }

    public int VolumePercent => _volumePercent;
    public string? LastError { get; private set; }
    public event EventHandler? PlaybackChanged;

    /// <summary>Called by the Avalonia host control when its MpvView creates a native context.</summary>
    public void AttachContext(MpvContext context)
    {
        if (_disposed || _initializationFailure is not null || ReferenceEquals(_context, context))
            return;

        if (_context is { } previousContext)
            DetachContext(previousContext);

        var resume = _pendingPlay || State == MediaPlaybackState.Playing;
        _context = context;
        ApplyVolume(context);
        if (_path is null || State is MediaPlaybackState.Stopped or MediaPlaybackState.Ended or MediaPlaybackState.Failed)
        {
            RaiseChanged();
            return;
        }

        var generation = ++_generation;
        var startPosition = _positionAnchor;
        State = MediaPlaybackState.Opening;
        _ = LoadIntoContextAsync(context, _path, generation, resume, startPosition, CancellationToken.None);
    }

    /// <summary>Called when the renderer control leaves the visual tree or replaces its context.</summary>
    public void DetachContext(MpvContext? context)
    {
        if (context is null || !ReferenceEquals(_context, context))
            return;

        var resumeOnAttach = _pendingPlay || State == MediaPlaybackState.Playing;
        if (ReferenceEquals(_loadedContext, context))
        {
            _positionAnchor = Position;
            _playStartedTimestamp = 0;
            try { context.Pause.Set(true); }
            catch (Exception ex) { LastError = $"libmpv 暂停时出错：{ex.GetBaseException().Message}"; }
        }

        _generation++;
        _context = null;
        _loadedContext = null;
        _pendingPlay = resumeOnAttach;
        if (State is MediaPlaybackState.Playing or MediaPlaybackState.Opening)
            State = MediaPlaybackState.Paused;
        RaiseChanged();
    }

    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _initializationFailure is not null)
            return FailOpen(Capabilities.Diagnostic);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return FailOpen("libmpv 此处只接受存在的本地文件路径；当前媒体路径不可直接访问。");

        _path = Path.GetFullPath(path);
        _generation++;
        _pendingPlay = false;
        _positionAnchor = TimeSpan.Zero;
        _playStartedTimestamp = 0;
        LastError = null;

        if (_context is not { } context)
        {
            // The view can still be constructing its renderer. Keep the request and complete it
            // when MpvView reports its context instead of failing a valid first click.
            _loadedContext = null;
            State = MediaPlaybackState.Opening;
            RaiseChanged();
            return true;
        }

        return await LoadIntoContextAsync(
            context, _path, _generation, resume: false, startPosition: TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _path is null || _initializationFailure is not null)
            return;

        if (_context is not { } context)
        {
            _pendingPlay = true;
            State = MediaPlaybackState.Opening;
            RaiseChanged();
            return;
        }

        if (!ReferenceEquals(_loadedContext, context))
        {
            _pendingPlay = true;
            if (State == MediaPlaybackState.Opening)
            {
                // An attach/open request may already be loading this file. Let that operation observe
                // _pendingPlay instead of issuing a second loadfile command against the same context.
                RaiseChanged();
                return;
            }

            State = MediaPlaybackState.Opening;
            var generation = ++_generation;
            await LoadIntoContextAsync(context, _path, generation, resume: true, _positionAnchor, cancellationToken)
                .ConfigureAwait(true);
            return;
        }

        try
        {
            context.Pause.Set(false);
            _pendingPlay = false;
            _playStartedTimestamp = Stopwatch.GetTimestamp();
            State = MediaPlaybackState.Playing;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"libmpv 无法开始播放：{ex.GetBaseException().Message}";
            State = MediaPlaybackState.Failed;
        }
        RaiseChanged();
    }

    public Task PauseAsync()
    {
        _pendingPlay = false;
        if (_context is { } context && ReferenceEquals(_loadedContext, context))
        {
            _positionAnchor = Position;
            _playStartedTimestamp = 0;
            try
            {
                context.Pause.Set(true);
                State = MediaPlaybackState.Paused;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"libmpv 无法暂停：{ex.GetBaseException().Message}";
                State = MediaPlaybackState.Failed;
            }
        }
        else if (State == MediaPlaybackState.Opening)
        {
            State = MediaPlaybackState.Paused;
        }

        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _pendingPlay = false;
        _generation++;
        _positionAnchor = TimeSpan.Zero;
        _playStartedTimestamp = 0;
        LastError = null;
        var context = _context;
        _loadedContext = null;

        if (context is not null)
        {
            try { await context.Stop().InvokeAsync().ConfigureAwait(true); }
            catch (Exception ex) { LastError = $"libmpv 停止时出错：{ex.GetBaseException().Message}"; }
        }

        State = MediaPlaybackState.Stopped;
        RaiseChanged();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_disposed || _path is null)
            return;

        var duration = Duration;
        var target = position < TimeSpan.Zero ? TimeSpan.Zero
            : duration > TimeSpan.Zero && position > duration ? duration
            : position;
        var current = Position;
        _positionAnchor = target;
        if (_playStartedTimestamp != 0)
            _playStartedTimestamp = Stopwatch.GetTimestamp();

        if (_context is { } context && ReferenceEquals(_loadedContext, context))
        {
            try
            {
                var delta = target - current;
                await context.Seek(delta.TotalSeconds, SeekOption.Relative).InvokeAsync().ConfigureAwait(true);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"libmpv 定位失败：{ex.GetBaseException().Message}";
            }
        }

        RaiseChanged();
    }

    public void SetVolume(int percent)
    {
        _volumePercent = Math.Clamp(percent, 0, 100);
        if (_context is { } context && !TrySetNumericProperty(context, "Volume", _volumePercent))
            LastError = "当前 libmpv wrapper 未能写入 volume 属性；其他播放控制仍可使用。";
        RaiseChanged();
    }

    /// <summary>The UI polls this when mpv does not provide a wrapper-side position event.</summary>
    public void PollPosition()
    {
        var duration = Duration;
        if (State == MediaPlaybackState.Playing && duration > TimeSpan.Zero &&
            Position >= duration - TimeSpan.FromMilliseconds(100))
        {
            State = MediaPlaybackState.Ended;
            _positionAnchor = duration;
            _playStartedTimestamp = 0;
            _pendingPlay = false;
        }
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _pendingPlay = false;
        var context = _context;
        _context = null;
        _loadedContext = null;
        _path = null;
        if (context is not null)
        {
            try { await context.Stop().InvokeAsync().ConfigureAwait(true); }
            catch (Exception) { }
        }
        State = MediaPlaybackState.Idle;
        PlaybackChanged = null;
    }

    private async Task<bool> LoadIntoContextAsync(
        MpvContext context,
        string path,
        int generation,
        bool resume,
        TimeSpan startPosition,
        CancellationToken cancellationToken)
    {
        try
        {
            State = MediaPlaybackState.Opening;
            LastError = null;
            RaiseChanged();
            context.Pause.Set(true);
            await context.LoadFile(path).InvokeAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation || !ReferenceEquals(_context, context))
                return false;

            ApplyVolume(context);
            if (startPosition > TimeSpan.Zero)
                await context.Seek(startPosition.TotalSeconds, SeekOption.Relative).InvokeAsync().ConfigureAwait(true);
            context.Pause.Set(true);
            _loadedContext = context;
            _positionAnchor = startPosition;
            _playStartedTimestamp = 0;

            var shouldPlay = resume || _pendingPlay;
            _pendingPlay = false;
            if (shouldPlay)
            {
                context.Pause.Set(false);
                _playStartedTimestamp = Stopwatch.GetTimestamp();
                State = MediaPlaybackState.Playing;
            }
            else
            {
                State = MediaPlaybackState.Paused;
            }

            LastError = null;
            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation && ReferenceEquals(_context, context))
            {
                _loadedContext = null;
                _pendingPlay = false;
                LastError = $"libmpv 打开媒体失败：{ex.GetBaseException().Message}";
                State = MediaPlaybackState.Failed;
                RaiseChanged();
            }
            return false;
        }
    }

    private bool FailOpen(string error)
    {
        LastError = error;
        State = MediaPlaybackState.Failed;
        RaiseChanged();
        return false;
    }

    private void ApplyVolume(MpvContext context)
    {
        if (!TrySetNumericProperty(context, "Volume", _volumePercent))
            LastError = "当前 libmpv wrapper 未能写入 volume 属性；请检查 wrapper 与原生库版本是否匹配。";
    }

    private void RaiseChanged() => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private static (string? Path, string? Error) ProbeNativeLibrary()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["libmpv-2.dll"]
            : OperatingSystem.IsLinux()
                ? ["libmpv.so.2", "libmpv.so"]
                : OperatingSystem.IsMacOS()
                    ? ["libmpv.2.dylib", "libmpv.dylib"]
                    : [];
        if (names.Length == 0)
            return (null, "当前桌面平台没有受支持的 libmpv 库名。");

        var configuredRoot = Environment.GetEnvironmentVariable("MDEXPLORER_LIBMPV_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) && !Directory.Exists(configuredRoot))
            return (null, $"MDEXPLORER_LIBMPV_ROOT 指向不存在的目录：{configuredRoot}");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var root = Path.GetFullPath(configuredRoot);
            candidates.AddRange(names.Select(name => Path.Combine(root, name)));
        }
        else
        {
            candidates.AddRange(names.Select(name => Path.Combine(AppContext.BaseDirectory, name)));
            candidates.AddRange(names);
        }

        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && !File.Exists(candidate))
                continue;
            try
            {
                var handle = NativeLibrary.Load(candidate);
                NativeLibrary.Free(handle);
                return (candidate, null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or
                                       FileNotFoundException or FileLoadException or EntryPointNotFoundException)
            {
                lastFailure = ex;
            }
        }

        var searched = string.IsNullOrWhiteSpace(configuredRoot)
            ? "应用目录与系统原生库搜索路径"
            : $"{Path.GetFullPath(configuredRoot)}（显式指定目录内的 libmpv 及其依赖）";
        return (null,
            $"在{searched}中未能加载 {string.Join(" / ", names)}" +
            (lastFailure is null ? "。" : $"：{lastFailure.GetBaseException().Message}"));
    }

    private static bool TryReadNumericProperty(MpvContext context, string name, out double value)
    {
        value = 0;
        try
        {
            var property = context.GetType().GetProperty(name, PublicInstance);
            var wrapper = property?.GetValue(context);
            if (wrapper is null)
                return false;

            var getter = wrapper.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(method => method.Name == "Get" && method.GetParameters().Length == 0);
            var raw = getter?.Invoke(wrapper, null) ?? wrapper;
            if (raw is null)
                return false;

            var seconds = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
                return false;
            value = seconds;
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or
                                   FormatException or OverflowException or ArgumentException or
                                   AmbiguousMatchException or MemberAccessException)
        {
            return false;
        }
    }

    private static bool TrySetNumericProperty(MpvContext context, string name, double value)
    {
        try
        {
            var property = context.GetType().GetProperty(name, PublicInstance);
            var wrapper = property?.GetValue(context);
            if (wrapper is null)
                return false;

            foreach (var setter in wrapper.GetType().GetMethods(PublicInstance)
                         .Where(method => method.Name == "Set" && method.GetParameters().Length == 1))
            {
                var targetType = setter.GetParameters()[0].ParameterType;
                var numericType = Nullable.GetUnderlyingType(targetType) ?? targetType;
                var converted = Convert.ChangeType(value, numericType, CultureInfo.InvariantCulture);
                var result = setter.Invoke(wrapper, [converted]);
                if (result is Task task)
                    task.GetAwaiter().GetResult();
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or
                                   FormatException or OverflowException or ArgumentException or
                                   AmbiguousMatchException or MemberAccessException)
        {
            return false;
        }
    }

    private static TimeSpan SafeTimeSpan(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.Zero;
        try { return TimeSpan.FromSeconds(seconds); }
        catch (OverflowException) { return TimeSpan.Zero; }
    }
}
