using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 授权夹（SAF / 存储 URI）中的文本读写。与本地实现的差别必须说清：
/// 存储提供者没有同目录临时文件 + 原子替换，也没有 Unix mode 与只读位，
/// 因此写回只能「先复核、再就地覆盖、写完再读回复核」，失败时尽力恢复原内容，
/// 并把结果如实报给调用方，绝不宣称原子。
/// </summary>
public sealed class StorageTextFileService(IFileSystemService fileSystem) : ITextFileService
{
    public async Task<TextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!FileLocator.IsStorageUri(path))
            throw new NotSupportedException("本地路径请使用本地文本服务。");

        var item = await fileSystem.StatAsync(path, cancellationToken).ConfigureAwait(false)
                   ?? throw new FileNotFoundException("文件不可用。", path);
        if (item.IsFolder)
            throw new InvalidDataException("这是文件夹，不能作为文本打开。");

        var bytes = await ReadAllAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        var decoded = TextFileCodec.Decode(bytes);
        var lastWrite = item.LastWriteTime;
        return new TextFileSnapshot(
            path,
            decoded.Text,
            TextFileCodec.Hash(bytes),
            bytes.Length,
            lastWrite,
            decoded.Kind,
            decoded.HasBom,
            TextFileCodec.DetectNewLine(decoded.Text),
            IsReadOnly: false,
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
        if (!FileLocator.IsStorageUri(expected.Path))
            throw new NotSupportedException("本地路径请使用本地文本服务。");

        var encoded = TextFileCodec.Encode(text, expected);
        if (encoded.Length > expected.MaxBytes)
            throw new InvalidDataException(
                $"编辑后内容为 {encoded.Length:N0} 字节，超过轻编辑的 {expected.MaxBytes:N0} 字节上限。");

        // 覆盖前的原内容：写坏了靠它恢复（大小与上限同阶，最多一份 2 MiB 量级的副本）。
        var originalBytes = await ReadAllAsync(expected.Path, expected.MaxBytes, cancellationToken).ConfigureAwait(false);
        if (!overwriteChanged && !string.Equals(TextFileCodec.Hash(originalBytes), expected.Sha256, StringComparison.Ordinal))
            throw new TextFileChangedException(expected.Path);

        await WriteAllAsync(expected.Path, encoded, cancellationToken).ConfigureAwait(false);

        var writtenBytes = await ReadAllAsync(expected.Path, Math.Max(expected.MaxBytes, encoded.Length), cancellationToken)
            .ConfigureAwait(false);
        var writtenHash = TextFileCodec.Hash(writtenBytes);
        if (!string.Equals(writtenHash, TextFileCodec.Hash(encoded), StringComparison.Ordinal))
        {
            // 写后读回不一致：先尝试把原内容写回去，再把「无法验证」如实抛出。
            var restored = await TryRestoreAsync(expected.Path, originalBytes, cancellationToken).ConfigureAwait(false);
            throw new IOException(restored
                ? "授权文件夹中的写入无法通过复核（提供者未按预期提交内容），已尝试恢复原内容，请刷新后确认。"
                : "授权文件夹中的写入无法通过复核，且恢复原内容失败；请立即用其它方式检查该文件。");
        }

        return await ReadAsync(expected.Path, expected.MaxBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadAllAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await TextFileCodec.ReadAllBytesAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAllAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRestoreAsync(string path, byte[] originalBytes, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAllAsync(path, originalBytes, cancellationToken).ConfigureAwait(false);
            var check = await ReadAllAsync(path, Math.Max(originalBytes.Length, 1), cancellationToken).ConfigureAwait(false);
            return string.Equals(TextFileCodec.Hash(check), TextFileCodec.Hash(originalBytes), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>按定位串把读写分流给本地或授权夹实现；调用方不需要判断路径形态。</summary>
public sealed class DelegatingTextFileService(ITextFileService local, ITextFileService storage) : ITextFileService
{
    public Task<TextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) =>
        FileLocator.IsStorageUri(path)
            ? storage.ReadAsync(path, maxBytes, cancellationToken)
            : local.ReadAsync(path, maxBytes, cancellationToken);

    public Task<TextFileSnapshot> SaveAsync(
        TextFileSnapshot expected,
        string text,
        bool overwriteChanged,
        CancellationToken cancellationToken) =>
        FileLocator.IsStorageUri(expected.Path)
            ? storage.SaveAsync(expected, text, overwriteChanged, cancellationToken)
            : local.SaveAsync(expected, text, overwriteChanged, cancellationToken);
}
