using System.Text.Json;
using System.Text.Json.Serialization;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>
/// Android 应用内回收站。Android 没有可供普通应用统一浏览/还原的系统回收站；
/// 本服务把本地与 SAF 条目先复制（本地同卷时优先移动）到应用数据目录，再删源，
/// 以配额与保留期约束占用。桌面仍使用系统回收站实现，不经此类。
/// </summary>
public sealed class AppTrashService : ITrashService
{
    public const long DefaultQuotaBytes = 1_000_000_000;
    public const int DefaultRetentionDays = 30;

    private readonly string _trashRoot;
    private readonly DelegatingFileSystemService _fileSystem;
    private long _quotaBytes;
    private int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppTrashService(
        string dataDirectory,
        DelegatingFileSystemService fileSystem,
        long quotaBytes = DefaultQuotaBytes,
        int retentionDays = DefaultRetentionDays,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (quotaBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(quotaBytes), "回收站配额必须为正数。");
        if (retentionDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "回收站保留期必须为正数。");

        _trashRoot = Path.Combine(Path.GetFullPath(dataDirectory), "app-trash-v1");
        _fileSystem = fileSystem;
        _quotaBytes = quotaBytes;
        _retentionDays = retentionDays;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool CanMoveToTrash => true;
    public bool CanBrowseTrash => true;

    /// <summary>当前配额（字节）。设置页调整后立即作用于后续的准入判断与占用百分比。</summary>
    public long QuotaBytes
    {
        get => Interlocked.Read(ref _quotaBytes);
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "回收站配额必须为正数。");
            Interlocked.Exchange(ref _quotaBytes, value);
        }
    }

    /// <summary>当前保留天数。调整后下一次清理即按新窗口裁掉过期条目。</summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "回收站保留期必须为正数。");
            Volatile.Write(ref _retentionDays, value);
        }
    }

    private string FilesDir => Path.Combine(_trashRoot, "files");
    private string InfoDir => Path.Combine(_trashRoot, "info");

    public async Task MoveToTrashAsync(string path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await MoveToTrashLockedAsync(path, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TrashedItem>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ListLocked(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<TrashStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return GetStatsLocked(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(string trashItemId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RestoreLockedAsync(trashItemId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task DeletePermanentlyAsync(string trashItemId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { DeletePermanentlyLocked(trashItemId, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task EmptyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(FilesDir).ToArray())
                    DeleteLocalPath(entry);
                foreach (var entry in Directory.EnumerateFileSystemEntries(InfoDir).ToArray())
                    DeleteLocalPath(entry);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task MoveToTrashLockedAsync(string path, CancellationToken cancellationToken)
    {
        EnsureDirectories();
        if (IsInsideAppTrash(path))
            throw new IOException("项目已经位于应用内回收站中。");

        var source = await _fileSystem.StatAsync(path, cancellationToken).ConfigureAwait(false)
                     ?? throw new FileNotFoundException("要移入回收站的项目不存在或授权已失效。", path);
        var parent = _fileSystem.GetParentPath(path)
                     ?? throw new IOException("无法确定原文件夹，授权根本身不能移入应用内回收站。");

        PurgeExpiredLocked(cancellationToken);
        var stats = GetStatsLocked(cancellationToken, purgeExpired: false);
        var estimate = await MeasureSourceAsync(path, source, cancellationToken).ConfigureAwait(false);
        if (estimate is { } estimatedBytes && stats.UsedBytes + estimatedBytes > QuotaBytes)
            throw new IOException($"应用内回收站空间不足（上限 {Formatting.Bytes(QuotaBytes)}），已保留原项目。");

        var id = Guid.NewGuid().ToString("N");
        var payloadPath = PayloadPath(id);
        var stagingPath = payloadPath + ".part";
        var infoPath = InfoPath(id);
        var metadata = new AppTrashEntryMetadata(
            id,
            path,
            parent,
            source.Name,
            _clock(),
            source.IsFolder,
            estimate ?? 0);

        // 先提交元数据再搬 payload：崩溃时至多留下无 payload 的信息文件，List 会忽略；
        // 源删除尚未开始时可安全回滚副本；删除一旦开始便可能只删掉一半，此时必须保留 payload。
        await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);

        var sourceRemoved = false;
        var sourceDeleteStarted = false;
        var localMoved = false;
        var committed = false;
        try
        {
            if (!FileLocator.IsStorageUri(path))
            {
                localMoved = await TryMoveLocalAsync(path, payloadPath, source.IsFolder, cancellationToken).ConfigureAwait(false);
                sourceRemoved = localMoved;
            }

            if (!localMoved)
            {
                await _fileSystem.CopyItemAsync(path, FilesDir, Path.GetFileName(stagingPath), cancellationToken).ConfigureAwait(false);
                await MoveLocalPayloadAsync(stagingPath, payloadPath, cancellationToken).ConfigureAwait(false);
            }

            var actualBytes = ComputeLocalPayloadSize(payloadPath) ?? metadata.SizeBytes;
            if (stats.UsedBytes + actualBytes > QuotaBytes)
            {
                if (localMoved)
                {
                    // 配额拒绝发生在本地原子移动之后：先把 payload 原路搬回再报错。
                    await MoveLocalPayloadBackAsync(payloadPath, path, metadata, cancellationToken).ConfigureAwait(false);
                    sourceRemoved = false;
                    localMoved = false;
                }
                throw new IOException($"应用内回收站空间不足（上限 {Formatting.Bytes(QuotaBytes)}），已保留原项目。");
            }

            if (actualBytes != metadata.SizeBytes)
            {
                metadata = metadata with { SizeBytes = actualBytes };
                await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
            }

            if (!sourceRemoved)
            {
                sourceDeleteStarted = true;
                await _fileSystem.DeleteKeyAsync(path, cancellationToken).ConfigureAwait(false);
                sourceRemoved = true;
            }
            committed = true;
        }
        catch
        {
            if (!committed && !sourceRemoved && !sourceDeleteStarted)
            {
                TryDeleteLocalPath(stagingPath);
                TryDeleteLocalPath(payloadPath);
                TryDeleteLocalPath(infoPath);
            }
            else if (!committed && localMoved)
            {
                // 本地移动已发生但后续提交失败：尽力搬回；若搬回也失败，保留信息+payload 供还原。
                try
                {
                    await MoveLocalPayloadBackAsync(payloadPath, path, metadata, CancellationToken.None).ConfigureAwait(false);
                    TryDeleteLocalPath(infoPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 保留可恢复的回收站条目，不在此吞掉原始异常。
                }
            }
            throw;
        }
    }

    private async Task RestoreLockedAsync(string trashItemId, CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var id = ValidateId(trashItemId);
        var metadata = ReadMetadata(id)
                       ?? throw new IOException("回收站条目不存在或信息已损坏。");
        var payloadPath = PayloadPath(id);
        if (!File.Exists(payloadPath) && !Directory.Exists(payloadPath))
            throw new IOException("回收站中的文件已丢失，无法还原。");

        if (FileLocator.IsStorageUri(metadata.OriginalParentPath))
        {
            if (!await _fileSystem.EnsureFolderRegisteredAsync(metadata.OriginalParentPath, cancellationToken).ConfigureAwait(false))
                throw new IOException("原文件夹不可用，可能需要重新授权；项目仍保留在回收站中。");
        }
        else
        {
            Directory.CreateDirectory(metadata.OriginalParentPath);
        }

        if (await _fileSystem.ChildExistsAsync(metadata.OriginalParentPath, metadata.OriginalName).ConfigureAwait(false))
            throw new IOException("原位置已有同名项目，未覆盖；项目仍保留在回收站中。");

        if (!FileLocator.IsStorageUri(metadata.OriginalPath))
        {
            var moved = await TryMoveLocalAsync(payloadPath, metadata.OriginalPath, metadata.IsFolder, cancellationToken).ConfigureAwait(false);
            if (moved)
            {
                TryDeleteLocalPath(InfoPath(id));
                return;
            }
        }

        string? producedKey = null;
        try
        {
            producedKey = await _fileSystem.CopyItemAsync(
                payloadPath, metadata.OriginalParentPath, metadata.OriginalName, cancellationToken).ConfigureAwait(false);
            if (!await _fileSystem.ChildExistsAsync(metadata.OriginalParentPath, metadata.OriginalName).ConfigureAwait(false))
                throw new IOException("还原未产生目标项目，已保留回收站副本。");
            if (!metadata.IsFolder)
            {
                var restored = await _fileSystem.StatAsync(producedKey, cancellationToken).ConfigureAwait(false);
                if (restored?.Length is { } length && length != metadata.SizeBytes)
                    throw new IOException("还原后的文件大小不一致，已保留回收站副本。");
            }
        }
        catch
        {
            // 只清理本次复制可能产生的同名半成品；进入复制前已确认目标不存在。
            try
            {
                if (await _fileSystem.ChildKeyAsync(metadata.OriginalParentPath, metadata.OriginalName).ConfigureAwait(false) is { } partial)
                    await _fileSystem.DeleteKeyAsync(partial, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
            }
            throw;
        }

        await Task.Run(() => DeleteLocalPath(payloadPath), cancellationToken).ConfigureAwait(false);
        TryDeleteLocalPath(InfoPath(id));
    }

    private void DeletePermanentlyLocked(string trashItemId, CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var id = ValidateId(trashItemId);
        cancellationToken.ThrowIfCancellationRequested();
        var payloadPath = PayloadPath(id);
        var infoPath = InfoPath(id);
        if (!File.Exists(payloadPath) && !Directory.Exists(payloadPath) && !File.Exists(infoPath))
            throw new IOException("回收站条目不存在。");

        DeleteLocalPath(payloadPath);
        TryDeleteLocalPath(infoPath);
    }

    private List<TrashedItem> ListLocked(CancellationToken cancellationToken)
    {
        EnsureDirectories();
        PurgeExpiredLocked(cancellationToken);
        var items = new List<TrashedItem>();
        foreach (var metadata in ReadAllMetadataLocked())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloadPath = PayloadPath(metadata.Id);
            if (!File.Exists(payloadPath) && !Directory.Exists(payloadPath))
                continue;
            items.Add(new TrashedItem(
                metadata.Id,
                metadata.OriginalName,
                metadata.OriginalPath,
                metadata.DeletedAt,
                metadata.IsFolder,
                metadata.SizeBytes));
        }
        items.Sort((a, b) => b.DeletedAt.CompareTo(a.DeletedAt));
        return items;
    }

    private TrashStats GetStatsLocked(CancellationToken cancellationToken, bool purgeExpired = true)
    {
        EnsureDirectories();
        if (purgeExpired)
            PurgeExpiredLocked(cancellationToken);
        var items = ListLocked(cancellationToken);
        return new TrashStats(
            items.Sum(item => item.SizeBytes ?? 0),
            items.Count,
            QuotaBytes,
            RetentionDays);
    }

    private void PurgeExpiredLocked(CancellationToken cancellationToken)
    {
        var cutoff = _clock().AddDays(-RetentionDays);
        foreach (var metadata in ReadAllMetadataLocked())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.DeletedAt > cutoff)
                continue;
            TryDeleteLocalPath(PayloadPath(metadata.Id));
            TryDeleteLocalPath(InfoPath(metadata.Id));
        }
    }

    private List<AppTrashEntryMetadata> ReadAllMetadataLocked()
    {
        var result = new List<AppTrashEntryMetadata>();
        if (!Directory.Exists(InfoDir)) return result;
        foreach (var infoFile in Directory.EnumerateFiles(InfoDir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(infoFile);
            if (!IsSafeId(id)) continue;
            if (ReadMetadata(id) is { } metadata)
                result.Add(metadata);
        }
        return result;
    }

    private AppTrashEntryMetadata? ReadMetadata(string id)
    {
        var infoPath = InfoPath(id);
        if (!File.Exists(infoPath)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize(
                File.ReadAllText(infoPath),
                AppTrashJsonContext.Default.AppTrashEntryMetadata);
            return metadata is not null && string.Equals(metadata.Id, id, StringComparison.Ordinal)
                ? metadata
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteMetadataAsync(AppTrashEntryMetadata metadata, CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var infoPath = InfoPath(metadata.Id);
        var tempPath = infoPath + ".tmp";
        var json = JsonSerializer.Serialize(metadata, AppTrashJsonContext.Default.AppTrashEntryMetadata);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, infoPath, overwrite: true);
    }

    private async Task<long?> MeasureSourceAsync(string path, FileItem source, CancellationToken cancellationToken)
    {
        if (!source.IsFolder)
            return source.Length;
        if (!FileLocator.IsStorageUri(path))
            return await Task.Run(() => ComputeLocalPayloadSize(path), cancellationToken).ConfigureAwait(false);

        long total = 0;
        var complete = true;
        var visited = new HashSet<string>(StringComparer.Ordinal) { path };
        var pending = new Stack<string>();
        pending.Push(path);
        var visitedEntries = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            await foreach (var child in _fileSystem.ListAsync(folder, includeHidden: true, cancellationToken).ConfigureAwait(false))
            {
                if (++visitedEntries > 250_000)
                    return null;
                if (child.IsFolder)
                {
                    if (visited.Add(child.FullPath))
                        pending.Push(child.FullPath);
                }
                else if (child.Length is { } length)
                {
                    total = checked(total + length);
                }
                else
                {
                    complete = false;
                }
            }
        }
        return complete ? total : null;
    }

    private static Task<bool> TryMoveLocalAsync(
        string sourcePath, string destinationPath, bool isFolder, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (isFolder) Directory.Move(sourcePath, destinationPath);
                else File.Move(sourcePath, destinationPath);
                return true;
            }
            catch (IOException) when (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                return false;
            }
            catch (UnauthorizedAccessException) when (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                return false;
            }
        }, cancellationToken);

    private static Task MoveLocalPayloadAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destinationPath);
            else File.Move(sourcePath, destinationPath);
        }, cancellationToken);

    private static Task MoveLocalPayloadBackAsync(
        string payloadPath, string originalPath, AppTrashEntryMetadata metadata, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(metadata.OriginalParentPath);
            if (metadata.IsFolder) Directory.Move(payloadPath, originalPath);
            else File.Move(payloadPath, originalPath);
        }, cancellationToken);

    private bool IsInsideAppTrash(string path)
    {
        if (FileLocator.IsStorageUri(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_trashRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(full, root, comparison) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InfoDir);
    }

    private string PayloadPath(string id) => Path.Combine(FilesDir, ValidateId(id));
    private string InfoPath(string id) => Path.Combine(InfoDir, ValidateId(id) + ".json");

    private static string ValidateId(string id)
    {
        if (!IsSafeId(id))
            throw new ArgumentException("回收站条目标识无效。", nameof(id));
        return id;
    }

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_') &&
        !id.Contains("..", StringComparison.Ordinal);

    private static long? ComputeLocalPayloadSize(string payload)
    {
        try
        {
            if (File.Exists(payload))
                return new FileInfo(payload).Length;
            if (!Directory.Exists(payload))
                return null;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                try { total = checked(total + new FileInfo(file).Length); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (OverflowException) { return long.MaxValue; }
            }
            return total;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void DeleteLocalPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteLocalPath(string path)
    {
        try { DeleteLocalPath(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record AppTrashEntryMetadata(
    string Id,
    string OriginalPath,
    string OriginalParentPath,
    string OriginalName,
    DateTimeOffset DeletedAt,
    bool IsFolder,
    long SizeBytes);

[JsonSerializable(typeof(AppTrashEntryMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppTrashJsonContext : JsonSerializerContext { }
