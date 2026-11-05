using System.Security.Cryptography;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 本地小文本的安全读写：严格 Unicode 解码、SHA-256 外部修改复核、同目录临时文件原子替换。
/// 存储 URI 不在此实现中伪装成本地路径；SAF 写回需要单独的委托实现与真机语义验证。
/// </summary>
public sealed class LocalTextFileService : ITextFileService
{
    public async Task<TextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var fullPath = NormalizeLocalPath(path);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var info = RequireFile(fullPath);
        if (info.Length > maxBytes)
            throw new InvalidDataException($"文件为 {info.Length:N0} 字节，超过轻编辑的 {maxBytes:N0} 字节上限。");

        var bytes = await ReadAllBytesAsync(fullPath, maxBytes, cancellationToken).ConfigureAwait(false);
        var decoded = TextFileCodec.Decode(bytes);
        info.Refresh();
        return new TextFileSnapshot(
            fullPath,
            decoded.Text,
            TextFileCodec.Hash(bytes),
            bytes.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            decoded.Kind,
            decoded.HasBom,
            TextFileCodec.DetectNewLine(decoded.Text),
            info.IsReadOnly,
            maxBytes);
    }

    public async Task<TextFileSnapshot> SaveAsync(
        TextFileSnapshot expected,
        string text,
        bool overwriteChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(text);
        var fullPath = NormalizeLocalPath(expected.Path);
        var info = RequireFile(fullPath);
        if (info.IsReadOnly)
            throw new UnauthorizedAccessException($"文件为只读：{fullPath}");

        var encoded = TextFileCodec.Encode(text, expected);
        if (encoded.Length > expected.MaxBytes)
            throw new InvalidDataException($"编辑后内容为 {encoded.Length:N0} 字节，超过轻编辑的 {expected.MaxBytes:N0} 字节上限。");

        await EnsureUnchangedAsync(fullPath, expected, overwriteChanged, cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("无法确定文件所在目录。");
        var tempPath = Path.Combine(directory, $".eidoria-save-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            CopyUnixMode(fullPath, tempPath);

            // 临时文件已完整落盘后再复核一次，缩小“检查通过后又被外部改写”的竞态窗口。
            await EnsureUnchangedAsync(fullPath, expected, overwriteChanged, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, null);
                else
                    File.Move(tempPath, fullPath);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
                // 保存结果以目标文件为准；临时文件清理失败不覆盖主错误。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return await ReadAsync(fullPath, expected.MaxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeLocalPath(string path)
    {
        if (FileLocator.IsStorageUri(path))
            throw new NotSupportedException("授权文件夹中的文件暂不支持在 Eidoria 内写回。");
        return Path.GetFullPath(path);
    }

    private static FileInfo RequireFile(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("文件不存在。", fullPath);
        if (info.LinkTarget is not null)
            throw new NotSupportedException("符号链接暂不支持在 Eidoria 内编辑，以免原子替换误伤链接本身。");
        return info;
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new InvalidDataException($"文件超过轻编辑的 {maxBytes:N0} 字节上限。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static async Task EnsureUnchangedAsync(
        string path,
        TextFileSnapshot expected,
        bool overwriteChanged,
        CancellationToken cancellationToken)
    {
        if (overwriteChanged)
            return;
        var current = await ComputeFingerprintAsync(path, cancellationToken).ConfigureAwait(false);
        if (current.Length != expected.Length || !string.Equals(current.Sha256, expected.Sha256, StringComparison.Ordinal))
            throw new TextFileChangedException(path);
    }

    private static async Task<(string Sha256, long Length)> ComputeFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }


    private static void CopyUnixMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 某些文件系统不暴露 Unix mode；内容替换仍可继续，权限保持新文件默认值。
        }
    }
}
