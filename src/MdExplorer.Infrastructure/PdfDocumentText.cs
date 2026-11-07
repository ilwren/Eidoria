using System.Diagnostics;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MdExplorer.Infrastructure;

/// <summary>
/// PDF 正文抽取（v0.57）：**只服务全文索引，不接预览**。
/// <para>
/// 预览仍走既定的两条引擎（OfficeViewer 直接控件优先、PDFtoImage/PDFium 光栅兼容）；
/// 这里只把文字层取出来喂给 <c>SqliteContentIndex</c>，避免「同一份 PDF 两个引擎给出两块不一样的内容」。
/// 决策与边界见 <c>docs/PDF-TEXT.md</c>。
/// </para>
/// <para>
/// 三条硬顶，任何一条触发都返回 null（这篇不进索引），而不是拖垮整次重建：
/// 文件 ≤ 32 MB、最多 400 页、单篇输出 ≤ 40 万字符、单篇耗时 ≤ 15 秒。
/// 加密件、损坏件、扫描件（没有文本层）同样返回 null —— 扫描件要 OCR，本版不做。
/// </para>
/// </summary>
public static class PdfDocumentText
{
    /// <summary>单篇 PDF 体积上限：与 Office/OpenDocument 同档。</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>最多取这么多页：目录式大文档的正文早就够搜了，其余留给「用系统应用打开」。</summary>
    public const int MaxPages = 400;

    /// <summary>单篇输出上限：与 Office 抽取同口径（存进索引还会二次封顶到 4 万字符）。</summary>
    public const int MaxOutputChars = 400_000;

    /// <summary>
    /// 单篇解析的耗时上限。PdfPig 是托管实现，畸形文件可能长时间打转；
    /// 超时就当这篇失败，宁可少搜一篇，也别让重建卡死在一个文件上。
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static bool IsSupportedExtension(string? extension) =>
        string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>抽取一篇 PDF 的正文；取不到（加密/损坏/扫描件/超限/超时）返回 null。</summary>
    public static string? TryExtract(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
                return null;
            return TryExtract(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>从内存字节抽取正文（测试与 SAF 字节流共用入口）。</summary>
    public static string? TryExtract(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var document = PdfDocument.Open(bytes);
            var builder = new StringBuilder();
            var pages = 0;
            foreach (var page in document.GetPages())
            {
                if (pages >= MaxPages)
                    break;
                pages++;
                if (stopwatch.Elapsed > Timeout)
                    return null;
                AppendPageText(builder, page);
                if (builder.Length >= MaxOutputChars)
                    break;
            }

            if (builder.Length == 0)
                return null;
            var text = builder.Length <= MaxOutputChars ? builder.ToString() : builder.ToString(0, MaxOutputChars);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 加密（PdfDocumentEncryptedException）、损坏、字体/编码不认识：
            // 一律当「这篇没有正文」。一份坏 PDF 不该让整次重建失败。
            return null;
        }
    }

    private static void AppendPageText(StringBuilder builder, Page page)
    {
        try
        {
            // 取词而不是整页字符串：词之间补空格，英文才能被 unicode61 正常分词；
            // 中文没有空格，二元组展开那一层照旧管用。
            foreach (var word in page.GetWords())
            {
                if (word.Text is { Length: > 0 } text)
                {
                    builder.Append(text);
                    builder.Append(' ');
                }

                if (builder.Length >= MaxOutputChars)
                    return;
            }

            builder.Append('\n');
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 个别页的字体/定位数据不完整时退回整页文本，别丢掉这一页。
            builder.Append(page.Text);
            builder.Append('\n');
        }
    }
}
