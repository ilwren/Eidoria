using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 本地文件名索引：显式根、全量快照、紧凑 JSON 持久化（源生成序列化，AOT 安全）。
/// 索引只保存定位与展示所需的轻量元数据；命中返回前以真实文件复核，删除/改名后的旧条目不会出现。
/// 构建不穿目录符号链接；无权限子树跳过并把快照标为部分结果。
/// </summary>
public sealed class FileNameIndexService : IFileNameIndexService, IDisposable
{
    private const string IndexFileName = "filename-index-v1.json";
    private const int MaxEntries = 500_000;

    private readonly string _dataDirectory;
    private readonly string _indexPath;
    private readonly ISettingsStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watcherGate = new();
    private IndexRootWatcher? _watcher;
    private IReadOnlyList<string> _watchedRoots = [];
    // v0.56：增量落盘的闸门。持续写入的文件（下载中、日志）每 700 ms 就会攒出一批变更，
    // 每批都重写整份 JSON 是纯写盘——内存里的快照一直是新的，磁盘最多落后几秒。
    private readonly object _snapshotWriteGate = new();
    private DateTimeOffset _lastSnapshotWrite = DateTimeOffset.MinValue;
    private bool _snapshotWritePending;
    private Timer? _snapshotFlushTimer;
    private static readonly TimeSpan MinSnapshotWriteInterval = TimeSpan.FromSeconds(3);
    private FileNameIndexSnapshot? _snapshot;
    private bool _loaded;
    private volatile bool _invalidated;

    public FileNameIndexService(string dataDirectory, ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        Directory.CreateDirectory(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _indexPath = Path.Combine(dataDirectory, IndexFileName);
        _settingsStore = settingsStore;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<FileNameIndexStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        UpdateWatchers(snapshot);
        return BuildStats(snapshot);
    }

    public async Task<FileNameIndexStats> RebuildAsync(
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var roots = ConfiguredRoots();
            if (roots.Count == 0)
            {
                DeleteIndexFile();
                _snapshot = null;
                _loaded = true;
                _invalidated = false;
                DisposeWatchers();
                progress?.Report(new OperationProgress(1, string.Empty, 0, 0));
                return BuildStats(null);
            }

            _invalidated = false;
            var built = await Task.Run(
                () => BuildSnapshot(roots, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            // v0.49：内容与上次完全一致时不重写快照。用户点两次「重建」、或目录没变却触发重建时，
            // 旧实现会把整份 JSON（上万条）重写一遍——纯写盘、零收益。
            var previous = _snapshot ?? await ReadSnapshotFromDiskAsync(cancellationToken).ConfigureAwait(false);
            if (!SnapshotsEquivalent(previous, built))
                await WriteSnapshotAsync(built, cancellationToken).ConfigureAwait(false);
            _lastSnapshotWrite = DateTimeOffset.UtcNow;
            lock (_snapshotWriteGate)
                _snapshotWritePending = false;
            _snapshot = built;
            _loaded = true;
            UpdateWatchers(built);
            progress?.Report(new OperationProgress(1, string.Empty, built.Entries.Count, built.Entries.Count));
            return BuildStats(built);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 待写的快照不能再落地：否则刚删掉的索引文件会被几秒后的补写又造出来。
            lock (_snapshotWriteGate)
            {
                _snapshotFlushTimer?.Dispose();
                _snapshotFlushTimer = null;
                _snapshotWritePending = false;
            }

            DeleteIndexFile();
            _snapshot = null;
            _loaded = true;
            _invalidated = false;
            DisposeWatchers();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CoversAsync(string rootPath, CancellationToken cancellationToken)
    {
        if (!_settingsStore.Settings.UseSearchIndex || TryNormalizeLocalPath(rootPath) is not { } normalized)
            return false;

        // 先用轻量根配置判覆盖，未命中时不加载整份快照，浏览器在普通目录搜索不付索引读取成本。
        var roots = ConfiguredRoots();
        if (!roots.Any(root => IsSameOrUnder(normalized, root)))
            return false;

        var snapshot = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        UpdateWatchers(snapshot);
        return snapshot is not null && !_invalidated && RootsEqual(snapshot.Roots, roots);
    }

    public async IAsyncEnumerable<FileItem> SearchAsync(
        SearchSpec spec, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_settingsStore.Settings.UseSearchIndex)
            yield break;

        var snapshot = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        UpdateWatchers(snapshot);
        if (snapshot is null || _invalidated || !RootsEqual(snapshot.Roots, ConfiguredRoots()))
            yield break;

        var matches = await Task.Run(
            () => FilterSnapshot(snapshot, spec, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileItem? current;
            try
            {
                current = LocalFileSystemService.StatPath(match.Path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (current is not null)
                yield return current;
        }
    }

    private async Task<FileNameIndexSnapshot?> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return _snapshot;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return _snapshot;

            try
            {
                if (File.Exists(_indexPath))
                {
                    await using var stream = new FileStream(
                        _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    var loaded = await JsonSerializer.DeserializeAsync(
                        stream, FileNameIndexJsonContext.Default.FileNameIndexSnapshot, cancellationToken)
                        .ConfigureAwait(false);
                    if (loaded is not null && loaded.Version == 1)
                    {
                        loaded.Roots ??= [];
                        loaded.Entries ??= [];
                        _snapshot = loaded;
                    }
                }
            }
            catch (JsonException)
            {
                _snapshot = null; // 损坏时回落实时扫描；下一次重建会原子覆盖
            }
            catch (IOException)
            {
                _snapshot = null;
            }
            catch (UnauthorizedAccessException)
            {
                _snapshot = null;
            }

            _loaded = true;
            UpdateWatchers(_snapshot);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 直接读盘上的快照（调用方必须已持有 <see cref="_gate"/>——<see cref="EnsureLoadedAsync"/> 会再抢一次锁）。
    /// 只用于重建前的「要不要写盘」比较；读不出来就当没有旧快照。
    /// </summary>
    private async Task<FileNameIndexSnapshot?> ReadSnapshotFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_indexPath))
                return null;
            await using var stream = new FileStream(
                _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var loaded = await JsonSerializer.DeserializeAsync(
                stream, FileNameIndexJsonContext.Default.FileNameIndexSnapshot, cancellationToken)
                .ConfigureAwait(false);
            if (loaded is null || loaded.Version != 1)
                return null;
            loaded.Roots ??= [];
            loaded.Entries ??= [];
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private FileNameIndexStats BuildStats(FileNameIndexSnapshot? snapshot)
    {
        var roots = ConfiguredRoots();
        long bytes = 0;
        try
        {
            bytes = new FileInfo(_indexPath) is { Exists: true } info ? info.Length : 0;
        }
        catch (IOException)
        {
            bytes = 0;
        }
        catch (UnauthorizedAccessException)
        {
            bytes = 0;
        }

        return new FileNameIndexStats(
            roots.Count,
            snapshot?.Entries.Count ?? 0,
            bytes,
            snapshot?.BuiltAtUtc,
            snapshot is not null && !_invalidated && RootsEqual(snapshot.Roots, roots),
            snapshot?.IsPartial ?? false);
    }

    private IReadOnlyList<string> ConfiguredRoots()
    {
        var configured = _settingsStore.Settings.SearchIndexRootPaths ?? [];
        var normalized = new List<string>();
        foreach (var path in configured.ToArray())
        {
            if (TryNormalizeLocalPath(path) is { } root &&
                !normalized.Contains(root, PathComparer))
                normalized.Add(root);
        }

        // 父根已覆盖子根：保留最短根，避免同一路径在多个根下重复入索引。
        normalized.Sort((a, b) => a.Length.CompareTo(b.Length));
        var roots = new List<string>();
        foreach (var candidate in normalized)
        {
            if (!roots.Any(existing => IsSameOrUnder(candidate, existing)))
                roots.Add(candidate);
        }

        return roots;
    }

    private static FileNameIndexSnapshot BuildSnapshot(
        IReadOnlyList<string> roots,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, FileNameIndexEntry>(PathComparer);
        var partial = false;
        var reachedCap = false;

        for (var rootIndex = 0; rootIndex < roots.Count && !reachedCap; rootIndex++)
        {
            var root = roots[rootIndex];
            if (!Directory.Exists(root))
            {
                partial = true;
                continue;
            }

            var pending = new Stack<(string Path, bool HiddenAncestor)>();
            pending.Push((root, false));
            while (pending.Count > 0 && !reachedCap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, hiddenAncestor) = pending.Pop();
                List<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(current).ToList();
                }
                catch (UnauthorizedAccessException)
                {
                    partial = true;
                    continue;
                }
                catch (IOException)
                {
                    partial = true;
                    continue;
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileItem? item;
                    try
                    {
                        item = LocalFileSystemService.StatPath(child);
                    }
                    catch (IOException)
                    {
                        partial = true;
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        partial = true;
                        continue;
                    }

                    if (item is null)
                        continue;

                    var fullPath = item.Locator.Path;
                    if (entries.TryAdd(fullPath, new FileNameIndexEntry
                        {
                            Path = fullPath,
                            Name = item.Name,
                            Kind = (int)item.Kind,
                            Length = item.Length,
                            LastWriteUtcTicks = item.LastWriteTime.UtcTicks,
                            IsHidden = item.IsHidden,
                            HasHiddenAncestor = hiddenAncestor,
                            IsSymlink = item.IsSymlink,
                            Extension = item.Extension,
                        }))
                    {
                        if (entries.Count >= MaxEntries)
                        {
                            partial = true;
                            reachedCap = true;
                        }
                        else if (entries.Count % 500 == 0)
                        {
                            progress?.Report(new OperationProgress(
                                roots.Count == 0 ? 0 : (double)rootIndex / roots.Count,
                                item.Name,
                                entries.Count,
                                0));
                        }
                    }

                    if (item.IsFolder && !item.IsSymlink)
                        pending.Push((child, hiddenAncestor || item.IsHidden));
                    if (reachedCap)
                        break;
                }
            }

            progress?.Report(new OperationProgress(
                roots.Count == 0 ? 1 : (double)(rootIndex + 1) / roots.Count,
                Path.GetFileName(root),
                entries.Count,
                0));
        }

        var snapshot = new FileNameIndexSnapshot
        {
            Version = 1,
            BuiltAtUtc = DateTimeOffset.UtcNow,
            IsPartial = partial,
            Roots = roots.ToList(),
            Entries = entries.Values.ToList(),
        };
        snapshot.Entries.Sort((a, b) => string.Compare(a.Path, b.Path, PathComparison));
        return snapshot;
    }

    private static List<FileNameIndexEntry> FilterSnapshot(
        FileNameIndexSnapshot snapshot, SearchSpec spec, CancellationToken cancellationToken)
    {
        var query = spec.Query?.Trim() ?? string.Empty;
        var hasKinds = spec.Kinds is { Count: > 0 };
        if ((query.Length == 0 && !hasKinds) || spec.MaxResults <= 0)
            return [];

        string? rootPrefix = null;
        if (!string.IsNullOrWhiteSpace(spec.RootPath))
        {
            if (TryNormalizeLocalPath(spec.RootPath) is not { } root)
                return [];
            rootPrefix = WithTrailingSeparator(root);
        }

        var filter = spec.Filter;
        var matches = new List<FileNameIndexEntry>();
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rootPrefix is not null && !entry.Path.StartsWith(rootPrefix, PathComparison))
                continue;
            if (!spec.IncludeHidden && (entry.IsHidden || entry.HasHiddenAncestor))
                continue;
            if (hasKinds && !spec.Kinds!.Contains((FileKind)entry.Kind))
                continue;
            // v0.52：查询里的空格是 AND（每个词都要出现），不能再按整串子串匹配。
            if (query.Length > 0 && !SearchRanking.Matches(entry.Name, query))
                continue;
            // v0.54：大小 / 修改时间筛选。放在 MaxResults 截断之前，否则先截断再筛会出现「结果莫名其妙变少」。
            if (filter is { IsEmpty: false } &&
                !filter.Matches(entry.Length, new DateTimeOffset(entry.LastWriteUtcTicks, TimeSpan.Zero)))
                continue;
            matches.Add(entry);
        }

        matches.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Path, b.Path, PathComparison);
        });
        if (matches.Count > spec.MaxResults)
            matches.RemoveRange(spec.MaxResults, matches.Count - spec.MaxResults);
        return matches;
    }

    /// <summary>
    /// 两份快照在「索引内容」上是否等价（v0.49）：忽略构建时间，只看根配置、是否部分、以及每条路径的
    /// 全部可见事实。等价就说明重写这份 JSON 没有任何收益，直接跳过写盘。
    /// </summary>
    internal static bool SnapshotsEquivalent(FileNameIndexSnapshot? previous, FileNameIndexSnapshot next)
    {
        if (previous is null || previous.IsPartial != next.IsPartial)
            return false;
        if (!RootsEqual(previous.Roots, next.Roots))
            return false;
        if (previous.Entries.Count != next.Entries.Count)
            return false;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var index = new Dictionary<string, FileNameIndexEntry>(previous.Entries.Count, comparer);
        foreach (var entry in previous.Entries)
            index[entry.Path] = entry;

        foreach (var entry in next.Entries)
        {
            if (!index.TryGetValue(entry.Path, out var old))
                return false;
            if (old.Name != entry.Name || old.Kind != entry.Kind || old.Length != entry.Length ||
                old.LastWriteUtcTicks != entry.LastWriteUtcTicks || old.IsHidden != entry.IsHidden ||
                old.HasHiddenAncestor != entry.HasHiddenAncestor || old.IsSymlink != entry.IsSymlink ||
                old.Extension != entry.Extension)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 把监视器攒下的一批路径增量应用到快照上（v0.56）：先删掉这些路径（及其子树）的旧条目，
    /// 再按当前磁盘状态补回。成功时快照**仍然有效**（<c>IsCurrent</c> 保持为真），浏览器不必回落实时扫描。
    /// <para>
    /// 增量只处理「变化的那一撮」：一次保存、一次改名、删掉一个文件，代价与索引规模无关。
    /// 批量操作（解压、git checkout）由 <see cref="IndexRootWatcher"/> 判定溢出后交回全量校准。
    /// </para>
    /// </summary>
    internal async Task<IncrementalApplyResult> ApplyIncrementalAsync(
        IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)
    {
        if (changedPaths.Count == 0)
            return IncrementalApplyResult.NothingToDo;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _snapshot;
            if (snapshot is null || _invalidated)
                return IncrementalApplyResult.Unavailable;
            var roots = ConfiguredRoots();
            if (!RootsEqual(snapshot.Roots, roots))
                return IncrementalApplyResult.Unavailable;

            // 先把「与我们无关」的路径挑掉再动快照：这批全在根外的话，一次复制都不用做。
            var applicable = new List<string>(changedPaths.Count);
            foreach (var raw in changedPaths)
            {
                if (TryNormalizeLocalPath(raw) is not { } path)
                    continue;
                // 索引文件自己落在某个根里时不参与（应用数据目录可以被用户加进索引根）。
                if (IsSameOrUnder(path, _dataDirectory))
                    continue;
                if (!roots.Any(root => IsSameOrUnder(path, root)))
                    continue;
                applicable.Add(path);
            }

            if (applicable.Count == 0)
                return IncrementalApplyResult.NothingToDo;

            var entries = new Dictionary<string, FileNameIndexEntry>(
                snapshot.Entries.Count + applicable.Count, PathComparer);
            foreach (var entry in snapshot.Entries)
                entries[entry.Path] = entry;

            var changed = false;
            foreach (var path in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changed |= RemoveSubtree(entries, path);
                changed |= AddCurrentState(entries, path, roots);
            }

            if (!changed)
                return IncrementalApplyResult.NothingToDo;

            // 只写真正变了的内容；写盘本身按 MinSnapshotWriteInterval 节流。
            snapshot.Entries = [.. entries.Values];
            snapshot.Entries.Sort((a, b) => string.Compare(a.Path, b.Path, PathComparison));
            snapshot.BuiltAtUtc = DateTimeOffset.UtcNow;
            if (snapshot.Entries.Count >= MaxEntries)
                snapshot.IsPartial = true;
            await PersistSnapshotIfDueAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return IncrementalApplyResult.Updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return IncrementalApplyResult.Unavailable;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 增量落盘：距上次写盘不足 <see cref="MinSnapshotWriteInterval"/> 就记一笔待写、稍后补上。
    /// 内存里的快照始终是最新的，节流只影响「重启后看到的是哪一版」。
    /// </summary>
    private async Task PersistSnapshotIfDueAsync(FileNameIndexSnapshot snapshot, CancellationToken cancellationToken)
    {
        lock (_snapshotWriteGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSnapshotWrite < MinSnapshotWriteInterval)
            {
                ScheduleSnapshotFlushLocked();
                return;
            }

            // 先占住时间戳再写：并发的第二批不会跟着再写一遍。
            _lastSnapshotWrite = now;
            _snapshotWritePending = true;
        }

        try
        {
            await WriteSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_snapshotWriteGate)
                _snapshotWritePending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 写不进去就留一笔待写：内存里的索引照常可用，稍后再试。
            lock (_snapshotWriteGate)
                ScheduleSnapshotFlushLocked();
        }
    }

    private void ScheduleSnapshotFlushLocked()
    {
        _snapshotWritePending = true;
        if (_snapshotFlushTimer is not null)
            return;
        _snapshotFlushTimer = new Timer(
            _ => _ = FlushPendingSnapshotAsync(), null, MinSnapshotWriteInterval, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushPendingSnapshotAsync()
    {
        lock (_snapshotWriteGate)
        {
            _snapshotFlushTimer?.Dispose();
            _snapshotFlushTimer = null;
            if (!_snapshotWritePending)
                return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_snapshot is not { } snapshot || _invalidated)
            {
                // 没东西可写（被清空/已标脏）：别留一个永远不再触发的「待写」标记。
                lock (_snapshotWriteGate)
                    _snapshotWritePending = false;
                return;
            }

            await WriteSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            lock (_snapshotWriteGate)
            {
                _lastSnapshotWrite = DateTimeOffset.UtcNow;
                _snapshotWritePending = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 写不进去就留到下一次：索引在内存里仍然可用。
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool RemoveSubtree(Dictionary<string, FileNameIndexEntry> entries, string path)
    {
        var removed = entries.Remove(path);
        var prefix = WithTrailingSeparator(path);
        List<string>? doomed = null;
        foreach (var key in entries.Keys)
        {
            if (key.StartsWith(prefix, PathComparison))
                (doomed ??= []).Add(key);
        }

        if (doomed is null)
            return removed;
        foreach (var key in doomed)
            entries.Remove(key);
        return true;
    }

    /// <summary>按当前磁盘状态把这条路径（是目录就连子树）补回快照；路径已不存在就什么都不做。</summary>
    private static bool AddCurrentState(
        Dictionary<string, FileNameIndexEntry> entries, string path, IReadOnlyList<string> roots)
    {
        FileItem? item;
        try
        {
            item = LocalFileSystemService.StatPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        if (item is null)
            return false;

        var hiddenAncestor = HasHiddenAncestor(path, roots);
        var changed = AddEntry(entries, item, hiddenAncestor);
        if (!item.IsFolder || item.IsSymlink)
            return changed;

        // 整体挪进来的目录只会来一条事件：子树得自己补，否则要等用户手动重建。
        var pending = new Stack<(string Path, bool HiddenAncestor)>();
        pending.Push((path, hiddenAncestor));
        while (pending.Count > 0)
        {
            var (current, hidden) = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (entries.Count >= MaxEntries)
                    return true; // 撞到条数上界：剩下的等下次重建，先如实返回
                FileItem? childItem;
                try
                {
                    childItem = LocalFileSystemService.StatPath(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (childItem is null)
                    continue;
                changed |= AddEntry(entries, childItem, hidden);
                if (childItem.IsFolder && !childItem.IsSymlink)
                    pending.Push((child, hidden || childItem.IsHidden));
            }
        }

        return changed;
    }

    /// <summary>写入一条索引条目；返回「与旧值不同」（相等就不算变更，避免白写一次快照）。</summary>
    private static bool AddEntry(
        Dictionary<string, FileNameIndexEntry> entries, FileItem item, bool hiddenAncestor)
    {
        var fullPath = item.Locator.Path;
        var exists = entries.TryGetValue(fullPath, out var existing);
        if (!exists && entries.Count >= MaxEntries)
            return false;
        if (exists && existing!.Name == item.Name && existing.Kind == (int)item.Kind &&
            existing.Length == item.Length && existing.LastWriteUtcTicks == item.LastWriteTime.UtcTicks &&
            existing.IsHidden == item.IsHidden && existing.HasHiddenAncestor == hiddenAncestor &&
            existing.IsSymlink == item.IsSymlink && existing.Extension == item.Extension)
        {
            return false;
        }

        entries[fullPath] = new FileNameIndexEntry
        {
            Path = fullPath,
            Name = item.Name,
            Kind = (int)item.Kind,
            Length = item.Length,
            LastWriteUtcTicks = item.LastWriteTime.UtcTicks,
            IsHidden = item.IsHidden,
            HasHiddenAncestor = hiddenAncestor,
            IsSymlink = item.IsSymlink,
            Extension = item.Extension,
        };
        return true;
    }

    /// <summary>从索引根往下走到这条路径，路上有没有隐藏目录（与全量构建时的判定口径一致）。</summary>
    private static bool HasHiddenAncestor(string path, IReadOnlyList<string> roots)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            if (roots.Any(root => string.Equals(root, parent, PathComparison)))
                return false;
            try
            {
                var info = new DirectoryInfo(parent);
                if (!info.Exists)
                    return false;
                if ((info.Attributes & FileAttributes.Hidden) != 0 ||
                    (info.Name.StartsWith('.') && info.Name.Length > 1))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }

            parent = Path.GetDirectoryName(parent);
        }

        return false;
    }

    private async Task WriteSnapshotAsync(FileNameIndexSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temp = _indexPath + ".tmp";
        await using (var stream = new FileStream(
                         temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream, snapshot, FileNameIndexJsonContext.Default.FileNameIndexSnapshot, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _indexPath, overwrite: true);
    }

    /// <summary>
    /// 运行期变更失效（增量更新留待后续版本）：索引根内一旦发生增删/改名，快照立即标为需重建，
    /// 浏览器随之回落实时扫描，避免新建文件被旧快照漏掉。应用数据目录自身的写入不触发失效。
    /// </summary>
    private void UpdateWatchers(FileNameIndexSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            DisposeWatchers();
            return;
        }

        var roots = ConfiguredRoots();
        if (!RootsEqual(snapshot.Roots, roots) || roots.Any(root => !Directory.Exists(root)))
        {
            _invalidated = true;
            DisposeWatchers();
            return;
        }

        if (_invalidated)
        {
            DisposeWatchers();
            return;
        }

        lock (_watcherGate)
        {
            if (_watchedRoots.SequenceEqual(roots, PathComparer) && _watcher is not null)
                return;

            DisposeWatchersLocked();
            var watcher = new IndexRootWatcher(roots, ApplyWatchedChangesAsync, InvalidateFromWatcher);
            watcher.Start();
            if (_invalidated)
            {
                // 建监视器时就发现看不住（平台不支持 / 权限不足）：聚合器已经回调过溢出。
                watcher.Dispose();
                return;
            }

            _watcher = watcher;
            _watchedRoots = roots;
        }
    }

    /// <summary>
    /// 监视器攒好的一批变更（v0.56）。增量成功就继续用这份快照；
    /// 失败（没有快照、根变了、写盘出错）就退回「标脏 + 实时扫描」的老路，不假装索引还是新的。
    /// </summary>
    private async Task ApplyWatchedChangesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var result = await ApplyIncrementalAsync(paths, cancellationToken).ConfigureAwait(false);
        // 「这批没带来变化」不是失败（重复事件很常见）；只有「根本应用不了」才退回标脏。
        if (result == IncrementalApplyResult.Unavailable && _snapshot is not null)
            InvalidateFromWatcher();
    }

    private void InvalidateFromWatcher()
    {
        _invalidated = true;
        DisposeWatchers();
    }

    private void DisposeWatchers()
    {
        lock (_watcherGate)
            DisposeWatchersLocked();
    }

    /// <summary>停掉目录监听（应用退出 / 测试收尾用）。不释放信号量：还在跑的查询要能收尾。</summary>
    public void Dispose()
    {
        lock (_watcherGate)
            DisposeWatchersLocked();
        lock (_snapshotWriteGate)
        {
            _snapshotFlushTimer?.Dispose();
            _snapshotFlushTimer = null;
        }
    }

    private void DisposeWatchersLocked()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedRoots = [];
    }

    private void DeleteIndexFile()
    {
        try
        {
            if (File.Exists(_indexPath))
                File.Delete(_indexPath);
        }
        catch (IOException)
        {
            // 清除失败时内存快照仍失效；文件会在下一次重建时被原子替换。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool RootsEqual(IReadOnlyList<string> snapshotRoots, IReadOnlyList<string> configuredRoots)
    {
        var snapshot = new HashSet<string>(PathComparer);
        foreach (var root in snapshotRoots)
        {
            if (TryNormalizeLocalPath(root) is { } normalized)
                snapshot.Add(normalized);
        }

        return snapshot.SetEquals(configuredRoots);
    }

    private static string? TryNormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal))
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            while (full.Length > root.Length &&
                   (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
                full = full[..^1];
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, PathComparison) ||
        path.StartsWith(WithTrailingSeparator(root), PathComparison);

    private static string WithTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

/// <summary>索引优先、扫描回落的搜索组合：浏览器与主页沿用同一 ISearchService 契约。</summary>
public sealed class IndexedSearchService : ISearchService
{
    private readonly IFileNameIndexService _index;
    private readonly ISearchService _fallback;
    private readonly ISettingsStore _settingsStore;

    public IndexedSearchService(
        IFileNameIndexService index, ISearchService fallback, ISettingsStore settingsStore)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public async IAsyncEnumerable<FileItem> SearchByNameAsync(
        SearchSpec spec, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_settingsStore.Settings.UseSearchIndex &&
            await _index.CoversAsync(spec.RootPath, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var item in _index.SearchAsync(spec, cancellationToken).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        await foreach (var item in _fallback.SearchByNameAsync(spec, cancellationToken).ConfigureAwait(false))
            yield return item;
    }
}

/// <summary>增量应用的结果（v0.56）：调用方要能分清「没有变化」与「应用不了」。</summary>
internal enum IncrementalApplyResult
{
    /// <summary>已经按当前磁盘状态更新了快照。</summary>
    Updated,

    /// <summary>这批路径没有带来任何变化（重复事件、索引本就一致）。</summary>
    NothingToDo,

    /// <summary>没有可用快照 / 根配置变了 / 写盘失败：只能靠全量重建。</summary>
    Unavailable,
}

internal sealed class FileNameIndexSnapshot
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("builtAtUtc")] public DateTimeOffset BuiltAtUtc { get; set; }
    [JsonPropertyName("partial")] public bool IsPartial { get; set; }
    [JsonPropertyName("roots")] public List<string> Roots { get; set; } = [];
    [JsonPropertyName("entries")] public List<FileNameIndexEntry> Entries { get; set; } = [];
}

internal sealed class FileNameIndexEntry
{
    [JsonPropertyName("p")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("k")] public int Kind { get; set; }
    [JsonPropertyName("l")] public long? Length { get; set; }
    [JsonPropertyName("t")] public long LastWriteUtcTicks { get; set; }
    [JsonPropertyName("h")] public bool IsHidden { get; set; }
    [JsonPropertyName("a")] public bool HasHiddenAncestor { get; set; }
    [JsonPropertyName("s")] public bool IsSymlink { get; set; }
    [JsonPropertyName("e")] public string? Extension { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FileNameIndexSnapshot))]
internal sealed partial class FileNameIndexJsonContext : JsonSerializerContext { }
