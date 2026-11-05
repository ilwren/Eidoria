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
public sealed class FileNameIndexService : IFileNameIndexService
{
    private const string IndexFileName = "filename-index-v1.json";
    private const int MaxEntries = 500_000;

    private readonly string _dataDirectory;
    private readonly string _indexPath;
    private readonly ISettingsStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watcherGate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private IReadOnlyList<string> _watchedRoots = [];
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
            await WriteSnapshotAsync(built, cancellationToken).ConfigureAwait(false);
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
            if (query.Length > 0 && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
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
            if (_watchedRoots.SequenceEqual(roots, PathComparer) && _watchers.Count == roots.Count)
                return;

            DisposeWatchersLocked();
            try
            {
                foreach (var root in roots)
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += OnIndexRootChanged;
                    watcher.Deleted += OnIndexRootChanged;
                    watcher.Renamed += OnIndexRootRenamed;
                    watcher.Error += OnIndexWatcherError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }

                _watchedRoots = roots;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _invalidated = true;
                DisposeWatchersLocked();
            }
        }
    }

    private void OnIndexRootChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSameOrUnder(e.FullPath, _dataDirectory))
            InvalidateFromWatcher();
    }

    private void OnIndexRootRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsSameOrUnder(e.FullPath, _dataDirectory) && !IsSameOrUnder(e.OldFullPath, _dataDirectory))
            InvalidateFromWatcher();
    }

    private void OnIndexWatcherError(object sender, ErrorEventArgs e) => InvalidateFromWatcher();

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

    private void DisposeWatchersLocked()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
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
