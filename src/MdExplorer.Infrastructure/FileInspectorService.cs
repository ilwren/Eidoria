using System.Security.Cryptography;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>本地文件检视：子树大小统计（迭代深搜、不穿目录符号链接）与流式校验和。</summary>
public sealed class FileInspectorService : IFileInspectorService
{
    public Task<FolderSizeResult> GetFolderSizeAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => ComputeFolderSize(path, cancellationToken), cancellationToken);

    private static FolderSizeResult ComputeFolderSize(string root, CancellationToken ct)
    {
        long bytes = 0, files = 0, folders = 0;
        var partial = false;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(entry); }
                    catch (IOException) { partial = true; continue; }
                    catch (UnauthorizedAccessException) { partial = true; continue; }

                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        folders++;
                        // 与搜索一致：不穿越目录符号链接，避免环路与重复计数
                        if ((attrs & FileAttributes.ReparsePoint) == 0)
                            pending.Push(entry);
                    }
                    else
                    {
                        files++;
                        try { bytes += new FileInfo(entry).Length; }
                        catch (IOException) { partial = true; }
                        catch (UnauthorizedAccessException) { partial = true; }
                    }
                }
            }
            catch (UnauthorizedAccessException) { partial = true; }
            catch (IOException) { partial = true; }
        }
        return new FolderSizeResult(bytes, files, folders, partial);
    }

    public async Task<string> ComputeHashAsync(
        string path, string algorithm, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(ParseAlgorithm(algorithm));
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, useAsync: true);
        var total = Math.Max(1, stream.Length);
        var buffer = new byte[1 << 20];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            readTotal += read;
            progress?.Report((double)readTotal / total);
        }
        progress?.Report(1);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static HashAlgorithmName ParseAlgorithm(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "SHA256" or "SHA-256" => HashAlgorithmName.SHA256,
        "MD5" => HashAlgorithmName.MD5,
        "SHA1" or "SHA-1" => HashAlgorithmName.SHA1,
        "SHA512" or "SHA-512" => HashAlgorithmName.SHA512,
        _ => throw new ArgumentException($"不支持的摘要算法：{algorithm}", nameof(algorithm)),
    };
}

/// <summary>
/// 基于 FileSystemWatcher 的文件夹监视：事件防抖 350ms 后合并为一次回调；
/// Watcher 内部错误时回调一次（触发全量刷新）并尽力重建。Dispose 释放。
/// </summary>
public sealed class FolderWatchService : IFolderWatchService
{
    public IDisposable Watch(string folderPath, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new Subscription(folderPath, changed);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _changed;
        private readonly Timer _debounce;
        private FileSystemWatcher? _watcher;
        private int _disposed;

        public Subscription(string folderPath, Action changed)
        {
            _changed = changed;
            _debounce = new Timer(_ => SafeInvoke(), null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                _watcher = CreateWatcher(folderPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher = null; // 监视不可用：静默降级（用户仍可手动刷新）
            }
        }

        private FileSystemWatcher CreateWatcher(string folderPath)
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = false, // 浏览器只关心当前层；递归监视在移动端过于昂贵
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void OnEvent(object sender, FileSystemEventArgs e) =>
            _debounce.Change(350, Timeout.Infinite);

        private void OnError(object sender, ErrorEventArgs e)
        {
            // 缓冲区溢出等：无法增量跟踪，触发一次全量刷新后尝试重建监视器
            SafeInvoke();
            if (Volatile.Read(ref _disposed) != 0 || _watcher is null) return;
            try
            {
                var path = _watcher.Path;
                _watcher.Dispose();
                _watcher = CreateWatcher(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher = null;
            }
        }

        private void SafeInvoke()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _changed(); }
            catch (Exception) { /* 回调异常不进监视线程 */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _debounce.Dispose();
            _watcher?.Dispose();
        }
    }
}
