namespace MdExplorer.Infrastructure;

/// <summary>
/// 索引根的变更聚合器（v0.56）：把 <see cref="FileSystemWatcher"/> 的事件攒成一个**去重路径集合**，
/// 静默一小段时间后一次性交给回调。两个索引（文件名 / 全文）共用它，行为才一致。
/// <para>
/// 为什么要攒：一次「另存为」在 Windows 上能连出 3–6 个事件（临时名写入、改名、属性变更），
/// 照着一个事件跑一次增量，等于把同一批 I/O 做很多遍；安静 700 ms 再处理，通常只剩最终状态。
/// </para>
/// <para>
/// 溢出与错误一律回调 <c>onOverflow</c> 让上层自己决定（文件名索引会退回「标脏 + 实时扫描」，
/// 全文索引会让用户重建）——这里不猜，也不静默丢变更：丢变更比多做一次重建更糟。
/// </para>
/// </summary>
internal sealed class IndexRootWatcher : IDisposable
{
    /// <summary>一次最多攒这么多路径；再多说明是批量操作（解压、git checkout），交给全量重建更划算。</summary>
    internal const int MaxPendingPaths = 4096;

    /// <summary>默认静默窗口：够把一次保存/改名产生的连环事件收成一批，又不会让用户觉得索引没跟上。</summary>
    internal static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(700);

    private readonly IReadOnlyList<string> _roots;
    private readonly TimeSpan _quietPeriod;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _flush;
    private readonly Action _onOverflow;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pending = new(PathComparer);
    private Timer? _timer;
    private bool _flushing;
    private bool _started;
    private bool _disposed;

    public IndexRootWatcher(
        IReadOnlyList<string> roots,
        Func<IReadOnlyList<string>, CancellationToken, Task> flush,
        Action onOverflow,
        TimeSpan? quietPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(flush);
        ArgumentNullException.ThrowIfNull(onOverflow);
        _roots = roots;
        _flush = flush;
        _onOverflow = onOverflow;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>监视器是否真的建起来了（平台不支持 / 权限不足时会立刻变成 false）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _started && !_disposed && _watchers.Count > 0;
        }
    }

    /// <summary>还没交给回调的路径条数（测试与状态文案用）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>开始监听。任何一个根都建不起监视器时立刻上报溢出，由上层回退。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
            _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        try
        {
            foreach (var root in _roots)
            {
                // 文件内容/大小的变更也要盯：索引里存着 mtime 与 size（大小/时间筛选依赖它们）。
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        watcher.Dispose();
                        continue;
                    }

                    _watchers.Add(watcher);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or PlatformNotSupportedException)
        {
            // 平台不支持或权限不足：当作「看不住这个根」，让上层回退到全量校准。
            _onOverflow();
            Stop();
        }
    }

    public void Stop()
    {
        List<FileSystemWatcher> watchers;
        Timer? timer;
        lock (_gate)
        {
            _started = false;
            watchers = [.. _watchers];
            _watchers.Clear();
            timer = _timer;
            _timer = null;
            _pending.Clear();
        }

        foreach (var watcher in watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        timer?.Dispose();
    }

    /// <summary>
    /// 立刻处理待办变更（静默窗口未到也处理）。测试用它替代等待；
    /// 应用退出前的收尾也可以调一次，避免刚写的文件没进索引。
    /// </summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            if (_flushing)
                return Task.CompletedTask; // 正在跑的那一轮末尾会把新来的再跑一遍
            _flushing = true;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        return FlushLoopAsync();
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (true)
            {
                string[] batch;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                        break;
                    batch = [.. _pending];
                    _pending.Clear();
                }

                try
                {
                    await _flush(batch, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 增量失败不能装作成功：上层据此退回全量重建。
                    _onOverflow();
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
                _flushing = false;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // 改名 = 老路径消失 + 新路径出现，两边都要处理，否则旧条目会赖在快照里。
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出：系统已经丢了事件，这里再攒也没有意义，直接交给上层。
        _onOverflow();
        Stop();
    }

    private void Enqueue(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var overflow = false;
        lock (_gate)
        {
            if (_disposed || !_started)
                return;
            _pending.Add(path);
            if (_pending.Count > MaxPendingPaths)
            {
                _pending.Clear();
                overflow = true;
            }
            else
            {
                _timer?.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
            }
        }

        if (overflow)
        {
            _onOverflow();
            Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
    }
}
