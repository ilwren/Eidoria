using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 缩略图磁盘缓存：一张图 = 一个文件，键由「路径 + 档位 + 大小 + 修改时间」算出。
/// <para>
/// 为什么把大小与修改时间放进键里：目标是文件系统的现实——同一个文件可能被替换、
/// 被同步工具改写、被截图工具覆盖。键里带上这两个事实后，旧缓存自动变成未命中，
/// 不需要维护「失效队列」这种容易漏掉的状态。
/// </para>
/// <para>
/// 负缓存（<c>.fail</c> 标记）记的是「这个文件当前解不出来」：坏图、无权限、格式不支持。
/// 没有它的话，一屏 300 个条目里只要有几张坏图，每次翻页都会重试同样几次解码，
/// 还把并发额度占住。标记同样随键失效——文件变了就重新给机会。
/// </para>
/// <para>
/// 线程与容错：所有方法都允许后台线程调用；IO 异常一律吞掉并降级为未命中。
/// 缓存是加速器，不是事实来源。
/// </para>
/// </summary>
public sealed class FileThumbnailCache : IThumbnailCache
{
    /// <summary>默认上限 192 MB：一屏 300 张 192 px 缩略图约 6 MB，够存二十多屏。</summary>
    public const long DefaultQuotaBytes = 192L * 1024 * 1024;

    private const string ImageExtension = ".img";
    private const string FailureExtension = ".fail";

    /// <summary>清扫后保留的比例：一次别再涨到上限就扫，留出余量。</summary>
    private const double SweepTargetRatio = 0.75;

    /// <summary>内存里的负缓存上限：只用来省掉重复 stat，丢了也不影响正确性。</summary>
    private const int NegativeMemoryLimit = 8192;

    /// <summary>
    /// 失败标记的寿命（与 §6.6 的 24 h 一致）。为什么要过期：失败可能是暂时的
    /// （文件被占用、瞬时无权限、网络盘没挂上）。键里虽然有修改时间，但文件没改过时
    /// 这个键会一直命中——不设寿命，一张「当时打不开」的图就永远不出缩略图了。
    /// </summary>
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(24);

    private readonly string _directory;
    private readonly long _quotaBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _negative = new(StringComparer.Ordinal);
    private readonly object _negativeGate = new();
    private long _knownBytes = -1;
    private int _writesSinceSweep;

    public FileThumbnailCache(string directory, long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _quotaBytes = quotaBytes > 0 ? quotaBytes : DefaultQuotaBytes;
        TryCreateDirectory(_directory);
    }

    public async Task<ThumbnailLookup> LookupAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        var basePath = BasePathFor(request);
        if (basePath is null)
            return ThumbnailLookup.Miss;

        if (IsKnownFailure(basePath))
            return ThumbnailLookup.KnownFailure;

        var imagePath = FullPathFor(basePath) + ImageExtension;
        try
        {
            if (!File.Exists(imagePath))
                return ThumbnailLookup.Miss;
            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            // 空文件是写坏留下的残渣：当作未命中，让它重新生成。
            if (bytes.Length == 0)
                return ThumbnailLookup.Miss;
            return ThumbnailLookup.Hit(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ThumbnailLookup.Miss;
        }
    }

    public async Task StoreAsync(ThumbnailRequest request, byte[] encodedImage, CancellationToken cancellationToken)
    {
        if (encodedImage.Length == 0)
            return;
        var basePath = BasePathFor(request);
        if (basePath is null)
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var imagePath = FullPathFor(basePath) + ImageExtension;
            var parent = Path.GetDirectoryName(imagePath);
            if (parent is null || !TryCreateDirectory(parent))
                return;

            // 先写临时文件再改名：中途崩溃不会留下半张图被当成命中。
            var temp = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, encodedImage, cancellationToken).ConfigureAwait(false);
                File.Move(temp, imagePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TryDelete(temp);
                return;
            }

            RemoveNegative(basePath);
            TryDelete(FullPathFor(basePath) + FailureExtension);
            NoteWritten(encodedImage.LongLength);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task StoreFailureAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        var basePath = BasePathFor(request);
        if (basePath is null)
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var marker = FullPathFor(basePath) + FailureExtension;
            var parent = Path.GetDirectoryName(marker);
            if (parent is null || !TryCreateDirectory(parent))
                return;
            try
            {
                await File.WriteAllBytesAsync(marker, [], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return;
            }
            AddNegative(basePath, DateTime.UtcNow + FailureLifetime);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<ThumbnailCacheStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var entries = 0;
        var failures = 0;
        long bytes = 0;
        try
        {
            foreach (var file in EnumerateCacheFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name.EndsWith(FailureExtension, StringComparison.Ordinal))
                {
                    failures++;
                    continue;
                }
                entries++;
                bytes += SafeLength(file);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Task.FromResult(new ThumbnailCacheStats(entries, failures, bytes));
        }

        _knownBytes = bytes;
        return Task.FromResult(new ThumbnailCacheStats(entries, failures, bytes));
    }

    public Task<long> ClearAsync(CancellationToken cancellationToken)
    {
        long freed = 0;
        try
        {
            foreach (var file in EnumerateCacheFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                freed += SafeLength(file);
                TryDelete(file.FullName);
            }
            foreach (var dir in SafeEnumerateDirectories(_directory))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清不干净也只是占着磁盘：界面会显示真实剩余量。
        }

        lock (_negativeGate)
        {
            _negative.Clear();
        }
        _knownBytes = 0;
        return Task.FromResult(freed);
    }

    // ── 内部 ─────────────────────────────────────────────────────────────────

    private static string? BasePathFor(ThumbnailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            return null;

        // 键必须包含所有会影响像素结果的事实：档位、大小、修改时间。
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{request.SourcePath}|{(int)request.Tier}|{request.LastWriteTime.UtcTicks}|{request.Length}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant()[..32];
        return Path.Combine(hash[..2], hash);
    }

    private string FullPathFor(string basePath) => Path.Combine(_directory, basePath);

    private bool IsKnownFailure(string basePath)
    {
        lock (_negativeGate)
        {
            if (_negative.TryGetValue(basePath, out var knownExpiry))
            {
                if (DateTime.UtcNow < knownExpiry)
                    return true;
                // 内存里的记录也过期了：跟磁盘标记一样不再作数。
                _negative.Remove(basePath);
            }
        }

        var marker = FullPathFor(basePath) + FailureExtension;
        DateTime writtenAt;
        try
        {
            if (!File.Exists(marker))
                return false;
            writtenAt = File.GetLastWriteTimeUtc(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        if (writtenAt != DateTime.MinValue && DateTime.UtcNow - writtenAt > FailureLifetime)
        {
            // 过期标记：删掉并重新给这个文件一次机会。
            TryDelete(marker);
            return false;
        }

        AddNegative(basePath, writtenAt == DateTime.MinValue
            ? DateTime.UtcNow + FailureLifetime
            : writtenAt.ToUniversalTime() + FailureLifetime);
        return true;
    }

    private void AddNegative(string basePath, DateTime expiresAt)
    {
        lock (_negativeGate)
        {
            if (_negative.Count >= NegativeMemoryLimit)
                _negative.Clear();
            _negative[basePath] = expiresAt;
        }
    }

    private void RemoveNegative(string basePath)
    {
        lock (_negativeGate)
        {
            _negative.Remove(basePath);
        }
    }

    private void NoteWritten(long bytes)
    {
        _writesSinceSweep++;
        // 记账未知（本进程还没统计过）或即将超限时就地清扫：Sweep 会重新数一遍磁盘，
        // 所以这里不需要（也不该）先自己加一遍，免得两套数字不一致。
        if (_knownBytes < 0 || _knownBytes + bytes > _quotaBytes || _writesSinceSweep >= 64)
        {
            _writesSinceSweep = 0;
            Sweep();
            return;
        }
        _knownBytes += bytes;
    }

    /// <summary>按最后写入时间从旧到新删除，直到降到配额的四分之三以下。</summary>
    private void Sweep()
    {
        try
        {
            var files = EnumerateCacheFiles().ToList();
            long total = 0;
            foreach (var file in files)
                total += SafeLength(file);
            if (total <= _quotaBytes)
            {
                _knownBytes = total;
                return;
            }

            var target = (long)(_quotaBytes * SweepTargetRatio);
            foreach (var file in files.OrderBy(SafeLastWrite))
            {
                if (total <= target)
                    break;
                var length = SafeLength(file);
                if (TryDelete(file.FullName))
                    total -= length;
            }
            _knownBytes = total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _knownBytes = -1;
        }
    }

    private IEnumerable<FileInfo> EnumerateCacheFiles()
    {
        var root = new DirectoryInfo(_directory);
        if (!root.Exists)
            return [];
        return root.EnumerateFiles("*", SearchOption.AllDirectories);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    private static DateTime SafeLastWrite(FileInfo file)
    {
        try
        {
            return file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
