namespace MdExplorer.Platform.Abstractions;

/// <summary>轻编辑第一切片明确支持的 Unicode 编码。旧代码页不猜测解码，以免静默损坏原文。</summary>
public enum TextFileEncodingKind
{
    Utf8,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,
}

/// <summary>
/// 一次完整读入的文本快照。Sha256 是保存时判断外部修改的唯一依据；
/// Length/LastWriteTime 只用于呈现和快速诊断，不参与覆盖裁决。
/// </summary>
public sealed record TextFileSnapshot(
    string Path,
    string Text,
    string Sha256,
    long Length,
    DateTimeOffset LastWriteTime,
    TextFileEncodingKind Encoding,
    bool HasBom,
    string NewLine,
    bool IsReadOnly,
    long MaxBytes);

/// <summary>文件在编辑期间已被其他程序修改；调用方必须让用户选择重载或显式覆盖。</summary>
public sealed class TextFileChangedException(string path)
    : IOException($"文件已被其他程序修改：{path}")
{
    public string FilePath { get; } = path;
}

/// <summary>
/// 小文本文件的完整读取与安全写回端口。实现必须保留原编码、BOM 与主导换行，
/// 并在覆盖前复核快照哈希；不得为了保存而静默吞掉外部修改。
/// </summary>
public interface ITextFileService
{
    Task<TextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken);

    /// <param name="expected">打开文件时取得的快照；其 MaxBytes 同时约束编码后的新内容。</param>
    /// <param name="overwriteChanged">仅在用户已看到外部修改冲突并明确选择覆盖时为 true。</param>
    Task<TextFileSnapshot> SaveAsync(
        TextFileSnapshot expected,
        string text,
        bool overwriteChanged,
        CancellationToken cancellationToken);
}
