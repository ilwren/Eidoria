using System.Security.Cryptography;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 只读存储洞察实现：迭代深搜聚合容量，不穿目录符号链接；无权限子树跳过并标为部分结果。
/// 重复查找先以尺寸做两遍预筛，再对候选流式计算 SHA-256，避免无差别哈希整棵树。
/// </summary>
public sealed class StorageInsightsService : IStorageInsightsService
{
    public Task<StorageAnalysisResult> AnalyzeAsync(
        string rootPath,
        StorageAnalysisOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = NormalizeRoot(rootPath);
        var normalized = NormalizeOptions(options);
        return Task.Run(() => AnalyzeInternal(root, normalized, progress, cancellationToken), cancellationToken);
    }

    public async Task<StorageDuplicateResult> FindDuplicatesAsync(
        string rootPath,
        StorageAnalysisOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = NormalizeRoot(rootPath);
        var normalized = NormalizeOptions(options);

        var sizeCounts = new Dictionary<long, int>();
        var firstPass = await Task.Run(() => Traverse(
            root,
            normalized,
            progress,
            progressFraction: 0,
            item =>
            {
                if (!item.IsFolder && !item.IsSymlink && item.Length is > 0)
                {
                    var length = item.Length.Value;
                    sizeCounts[length] = sizeCounts.TryGetValue(length, out var count) ? count + 1 : 1;
                }

                return true;
            },
            cancellationToken), cancellationToken).ConfigureAwait(false);

        var candidateGroups = new Dictionary<long, List<FileItem>>();
        var candidateCount = 0;
        var candidateCapReached = false;
        var secondPass = await Task.Run(() => Traverse(
            root,
            normalized,
            progress,
            progressFraction: 0.25,
            item =>
            {
                if (item.IsFolder || item.IsSymlink || item.Length is not > 0)
                    return true;

                var length = item.Length.Value;
                if (!sizeCounts.TryGetValue(length, out var count) || count < 2)
                    return true;
                if (candidateCount >= normalized.MaxDuplicateCandidateFiles)
                {
                    candidateCapReached = true;
                    return false;
                }

                if (!candidateGroups.TryGetValue(length, out var group))
                {
                    group = [];
                    candidateGroups[length] = group;
                }

                group.Add(item);
                candidateCount++;
                return true;
            },
            cancellationToken), cancellationToken).ConfigureAwait(false);

        var hashedGroups = new Dictionary<(long Size, string Hash), List<FileItem>>();
        var processed = 0;
        var hashed = 0;
        var skipped = 0;
        var orderedCandidates = candidateGroups
            .OrderByDescending(pair => pair.Key * Math.Max(0, pair.Value.Count - 1))
            .ThenByDescending(pair => pair.Key)
            .ToArray();

        foreach (var (size, candidates) in orderedCandidates)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;

                FileItem? current;
                try
                {
                    current = LocalFileSystemService.StatPath(candidate.FullPath);
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                if (current is null || current.IsFolder || current.IsSymlink || current.Length != size)
                {
                    skipped++;
                    continue;
                }

                string? hash;
                try
                {
                    hash = await ComputeSha256Async(current.FullPath, size, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                if (hash is null)
                {
                    skipped++;
                    continue;
                }

                hashed++;
                var key = (size, hash);
                if (!hashedGroups.TryGetValue(key, out var group))
                {
                    group = [];
                    hashedGroups[key] = group;
                }

                group.Add(current);
                progress?.Report(new OperationProgress(
                    candidateCount == 0 ? 1 : 0.5 + 0.5 * processed / candidateCount,
                    current.Name,
                    processed,
                    candidateCount));
            }
        }

        var groups = hashedGroups
            .Where(pair => pair.Value.Count >= 2)
            .Select(pair =>
            {
                pair.Value.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, PathComparison));
                return new DuplicateFileGroup(pair.Key.Size, pair.Key.Hash, pair.Value.ToArray());
            })
            .OrderByDescending(group => group.ReclaimableBytes)
            .ThenByDescending(group => group.FileSize)
            .ThenBy(group => group.Files[0].FullPath, PathComparer)
            .ToArray();

        progress?.Report(new OperationProgress(1, string.Empty, processed, candidateCount));
        return new StorageDuplicateResult(
            root,
            groups,
            groups.Sum(group => group.ReclaimableBytes),
            candidateCount,
            hashed,
            skipped,
            firstPass.IsPartial || secondPass.IsPartial || skipped > 0,
            firstPass.WasTruncated || secondPass.WasTruncated || candidateCapReached);
    }

    private static StorageAnalysisResult AnalyzeInternal(
        string root,
        NormalizedOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        long fileCount = 0;
        long folderCount = 0;
        var kinds = new Dictionary<FileKind, KindAggregate>();
        var folders = new Dictionary<string, FolderAggregate>(PathComparer);
        var largest = new List<FileItem>(options.LargestFileCount);

        var outcome = Traverse(
            root,
            options,
            progress,
            progressFraction: 0,
            item =>
            {
                if (item.IsFolder)
                {
                    folderCount++;
                    GetFolderBucket(root, folders, item).FolderCount++;
                    return true;
                }

                var bytes = item.Length ?? 0;
                fileCount++;
                totalBytes += bytes;

                if (!kinds.TryGetValue(item.Kind, out var kind))
                {
                    kind = new KindAggregate();
                    kinds[item.Kind] = kind;
                }

                kind.TotalBytes += bytes;
                kind.FileCount++;

                var bucket = GetFolderBucket(root, folders, item);
                bucket.TotalBytes += bytes;
                bucket.FileCount++;

                AddLargest(largest, item, options.LargestFileCount);
                return true;
            },
            cancellationToken);

        largest.Sort((a, b) =>
        {
            var bySize = (b.Length ?? 0).CompareTo(a.Length ?? 0);
            return bySize != 0 ? bySize : string.Compare(a.FullPath, b.FullPath, PathComparison);
        });

        var kindUsages = kinds
            .Select(pair => new StorageKindUsage(pair.Key, pair.Value.TotalBytes, pair.Value.FileCount))
            .OrderByDescending(usage => usage.TotalBytes)
            .ThenBy(usage => usage.Kind)
            .ToArray();
        var topFolders = folders.Values
            .Select(folder => new StorageFolderUsage(
                folder.Path,
                folder.Name,
                folder.TotalBytes,
                folder.FileCount,
                folder.FolderCount,
                folder.IsRootFiles))
            .OrderByDescending(folder => folder.TotalBytes)
            .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .Take(options.TopFolderCount)
            .ToArray();

        progress?.Report(new OperationProgress(1, string.Empty, ClampToInt(outcome.Entries), ClampToInt(outcome.Entries)));
        return new StorageAnalysisResult(
            root,
            totalBytes,
            fileCount,
            folderCount,
            outcome.IsPartial,
            outcome.WasTruncated,
            kindUsages,
            topFolders,
            largest.ToArray());
    }

    private static ScanOutcome Traverse(
        string root,
        NormalizedOptions options,
        IProgress<OperationProgress>? progress,
        double progressFraction,
        Func<FileItem, bool> visit,
        CancellationToken cancellationToken)
    {
        var outcome = new ScanOutcome();
        var pending = new Stack<string>();
        pending.Push(root);
        var stop = false;

        while (pending.Count > 0 && !stop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                outcome.IsPartial = true;
                continue;
            }
            catch (IOException)
            {
                outcome.IsPartial = true;
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileItem? item;
                try
                {
                    item = LocalFileSystemService.StatPath(entry);
                }
                catch (IOException)
                {
                    outcome.IsPartial = true;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    outcome.IsPartial = true;
                    continue;
                }

                if (item is null)
                    continue;
                if (!options.IncludeHidden && item.IsHidden)
                    continue;

                outcome.Entries++;
                if (outcome.Entries > options.MaxEntries)
                {
                    outcome.WasTruncated = true;
                    stop = true;
                    break;
                }

                if (!visit(item))
                {
                    outcome.WasTruncated = true;
                    stop = true;
                    break;
                }

                if (item.IsFolder && !item.IsSymlink)
                    pending.Push(entry);
                if (outcome.Entries % 500 == 0)
                {
                    progress?.Report(new OperationProgress(
                        progressFraction,
                        item.Name,
                        ClampToInt(outcome.Entries),
                        0));
                }
            }
        }

        return outcome;
    }

    private static async Task<string?> ComputeSha256Async(
        string path, long expectedLength, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, useAsync: true);
        if (stream.Length != expectedLength)
            return null;

        var buffer = new byte[1 << 20];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            readTotal += read;
        }

        return readTotal == expectedLength
            ? Convert.ToHexStringLower(hasher.GetHashAndReset())
            : null;
    }

    private static FolderAggregate GetFolderBucket(
        string root, Dictionary<string, FolderAggregate> folders, FileItem item)
    {
        var relative = Path.GetRelativePath(root, item.FullPath);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separator < 0 && !item.IsFolder)
        {
            if (!folders.TryGetValue(root, out var rootBucket))
            {
                var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                rootBucket = new FolderAggregate(
                    root,
                    string.IsNullOrWhiteSpace(rootName) ? root : rootName,
                    isRootFiles: true);
                folders[root] = rootBucket;
            }

            return rootBucket;
        }

        var firstSegment = separator < 0 ? relative : relative[..separator];
        var bucketPath = Path.Combine(root, firstSegment);
        if (!folders.TryGetValue(bucketPath, out var bucket))
        {
            bucket = new FolderAggregate(bucketPath, firstSegment, isRootFiles: false);
            folders[bucketPath] = bucket;
        }

        return bucket;
    }

    private static void AddLargest(List<FileItem> largest, FileItem candidate, int limit)
    {
        if (limit <= 0)
            return;
        if (largest.Count < limit)
        {
            largest.Add(candidate);
            return;
        }

        var minIndex = 0;
        for (var i = 1; i < largest.Count; i++)
        {
            var current = largest[i].Length ?? 0;
            var min = largest[minIndex].Length ?? 0;
            if (current < min || (current == min && string.Compare(
                    largest[i].FullPath, largest[minIndex].FullPath, PathComparison) > 0))
                minIndex = i;
        }

        var candidateBytes = candidate.Length ?? 0;
        var minBytes = largest[minIndex].Length ?? 0;
        if (candidateBytes > minBytes || (candidateBytes == minBytes && string.Compare(
                candidate.FullPath, largest[minIndex].FullPath, PathComparison) < 0))
            largest[minIndex] = candidate;
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || FileLocator.IsStorageUri(rootPath))
            throw new DirectoryNotFoundException("存储洞察只支持现有的本地文件夹。");

        var full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("存储洞察只支持现有的本地文件夹。");
        return full;
    }

    private static NormalizedOptions NormalizeOptions(StorageAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new NormalizedOptions(
            options.IncludeHidden,
            Math.Clamp(options.LargestFileCount, 0, 100),
            Math.Clamp(options.TopFolderCount, 1, 50),
            Math.Clamp(options.MaxEntries, 1, 1_000_000),
            Math.Clamp(options.MaxDuplicateCandidateFiles, 1, 50_000));
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record NormalizedOptions(
        bool IncludeHidden,
        int LargestFileCount,
        int TopFolderCount,
        int MaxEntries,
        int MaxDuplicateCandidateFiles);

    private sealed class ScanOutcome
    {
        public bool IsPartial { get; set; }
        public bool WasTruncated { get; set; }
        public long Entries { get; set; }
    }

    private sealed class KindAggregate
    {
        public long TotalBytes { get; set; }
        public long FileCount { get; set; }
    }

    private sealed class FolderAggregate(string path, string name, bool isRootFiles)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public bool IsRootFiles { get; } = isRootFiles;
        public long TotalBytes { get; set; }
        public long FileCount { get; set; }
        public long FolderCount { get; set; }
    }
}
