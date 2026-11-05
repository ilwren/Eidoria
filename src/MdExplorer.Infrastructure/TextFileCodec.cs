using System.Security.Cryptography;
using System.Text;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 轻编辑的编码/BOM/换行与指纹规则。本地写回（原子替换）与授权夹写回（就地覆盖 + 复核）
/// 必须共用同一套编解码，否则同一份文件在两个入口会得到不同的字节。
/// </summary>
internal static class TextFileCodec
{
    public static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static (string Text, TextFileEncodingKind Kind, bool HasBom) Decode(byte[] bytes)
    {
        var (kind, bomLength) = DetectEncoding(bytes);
        string text;
        try
        {
            text = EncodingFor(kind).GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("文件不是受支持的 UTF-8、UTF-16 或 UTF-32 文本。", ex);
        }

        if (text.Contains('\0'))
            throw new InvalidDataException("文件包含 NUL 字节，按二进制文件处理，不进入轻编辑。");
        return (text, kind, bomLength > 0);
    }

    public static byte[] Encode(string text, TextFileSnapshot snapshot)
    {
        var normalized = NormalizeNewLines(text, snapshot.NewLine);
        var encoding = EncodingFor(snapshot.Encoding);
        var body = encoding.GetBytes(normalized);
        if (!snapshot.HasBom)
            return body;
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
            return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    public static string DetectNewLine(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        if (crlf > 0 && crlf >= lf && crlf >= cr)
            return "\r\n";
        return cr > lf ? "\r" : "\n";
    }

    /// <summary>有界读取：超过上限立即失败，避免把超大文件读进内存。</summary>
    public static async Task<byte[]> ReadAllBytesAsync(
        Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
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

    private static (TextFileEncodingKind Kind, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (TextFileEncodingKind.Utf32Le, 4);
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (TextFileEncodingKind.Utf32Be, 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (TextFileEncodingKind.Utf8, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (TextFileEncodingKind.Utf16Le, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (TextFileEncodingKind.Utf16Be, 2);
        return (TextFileEncodingKind.Utf8, 0);
    }

    private static Encoding EncodingFor(TextFileEncodingKind kind) => kind switch
    {
        TextFileEncodingKind.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        TextFileEncodingKind.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        TextFileEncodingKind.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
        TextFileEncodingKind.Utf32Le => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
        TextFileEncodingKind.Utf32Be => new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string NormalizeNewLines(string text, string newLine)
    {
        var canonical = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return newLine == "\n" ? canonical : canonical.Replace("\n", newLine, StringComparison.Ordinal);
    }
}
