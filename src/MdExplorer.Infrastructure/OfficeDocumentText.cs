using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 从 Office / OpenDocument 文档里抽出**纯文本**，只为内容索引服务（v0.47）。
/// </summary>
/// <remarks>
/// 这里不做渲染、不认版式：只把正文文字按段落顺序拼成可被 FTS5 检索的字符串。
/// 取舍：
/// <list type="bullet">
/// <item>OOXML（.docx/.pptx/.xlsx）与 ODF（.odt/.ods/.odp）都是 ZIP + XML，用 <see cref="ZipArchive"/>
/// 读指定部件、用 <see cref="XmlReader"/> 流式扫文本节点——不解码整棵树、不加载整份文档。</item>
/// <item>平面 XML（.fodt/.fods/.fodp）直接按同一套规则扫文本节点。</item>
/// <item>**禁 DTD 并限深限宽**：文档来自用户磁盘，压缩炸弹与 XXE 都不能由这里打开缺口；
/// 单部件字节数、总字符数与 ZIP 展开比都有上限。</item>
/// <item>抽不出来（加密、损坏、超出上限、完全没有文字）就返回 <c>null</c>——
/// 调用方按「这篇不进索引」处理，而不是塞进一段乱码。</item>
/// </list>
/// PDF 不在范围内：PDFium 只光栅化，取文本层需要新的依赖（待单独决策）。
/// </remarks>
public static class OfficeDocumentText
{
    /// <summary>单篇最多收多少字符；够搜到内容，又不会让一篇表格拖垮整个索引。</summary>
    public const int MaxCharacters = 400_000;

    /// <summary>单个 XML 部件解压后的字节上限（正文部件通常远小于它）。</summary>
    public const long MaxEntryBytes = 32L * 1024 * 1024;

    /// <summary>ZIP 总展开量上限：超过即视为可疑压缩包，整篇不索引。</summary>
    public const long MaxTotalUncompressedBytes = 256L * 1024 * 1024;

    private static readonly string[] Extensions =
    [
        ".docx", ".pptx", ".xlsx", ".docm", ".pptm", ".xlsm",
        ".odt", ".ods", ".odp", ".ott", ".ots", ".otp",
        ".fodt", ".fods", ".fodp",
    ];

    private static readonly string[] FlatExtensions = [".fodt", ".fods", ".fodp"];

    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) &&
        Array.Exists(Extensions, candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>这个扩展名是不是「平面 XML」（非 ZIP）。</summary>
    public static bool IsFlatXml(string? extension) =>
        !string.IsNullOrEmpty(extension) &&
        Array.Exists(FlatExtensions, candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 抽正文。返回 null 表示「这篇没有可取文字 / 读不了 / 超出上限」，由调用方跳过。
    /// 不抛异常：索引重建不该因为某一份坏文档整体失败。
    /// </summary>
    public static string? TryExtract(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var extension = Path.GetExtension(path);
        if (!IsSupportedExtension(extension))
            return null;

        try
        {
            return IsFlatXml(extension) ? ExtractFromXmlFile(path) : ExtractFromPackage(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or XmlException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ExtractFromPackage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        long total = 0;
        foreach (var entry in archive.Entries)
            total += entry.Length;
        if (total > MaxTotalUncompressedBytes)
            return null;

        var builder = new StringBuilder();
        foreach (var name in SelectParts(archive, Path.GetExtension(path)))
        {
            if (builder.Length >= MaxCharacters)
                break;
            var entry = archive.GetEntry(name);
            if (entry is null || entry.Length <= 0 || entry.Length > MaxEntryBytes)
                continue;
            // 不同部件之间补一个换行：否则上一部分末尾的文字会和下一部分首字粘成一个词。
            AppendBreak(builder);
            using var partStream = entry.Open();
            AppendXmlText(partStream, builder);
        }

        return Cleanup(builder);
    }

    private static string? ExtractFromXmlFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxEntryBytes)
            return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        var builder = new StringBuilder();
        AppendXmlText(stream, builder);
        return Cleanup(builder);
    }

    /// <summary>按格式挑要读的部件；顺序影响摘要里的先后，所以表格/幻灯片都按序号排。</summary>
    private static IEnumerable<string> SelectParts(ZipArchive archive, string extension)
    {
        var name = extension.ToLowerInvariant();
        return name switch
        {
            ".docx" or ".docm" => ["word/document.xml"],
            ".xlsx" or ".xlsm" => archive.Entries
                .Select(entry => entry.FullName)
                .Where(full => full.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase))
                .Concat(archive.Entries
                    .Select(entry => entry.FullName)
                    .Where(full => full.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                                   full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(full => full, StringComparer.OrdinalIgnoreCase)),
            ".pptx" or ".pptm" => archive.Entries
                .Select(entry => entry.FullName)
                .Where(full => full.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                               full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(SlideNumber)
                .ThenBy(full => full, StringComparer.OrdinalIgnoreCase),
            // ODF：content.xml 里就是全部正文（样式在 styles.xml，不索引）。
            ".odt" or ".ods" or ".odp" or ".ott" or ".ots" or ".otp" => ["content.xml"],
            _ => Array.Empty<string>(),
        };
    }

    private static int SlideNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name.AsSpan("slide".Length), out var number) ? number : int.MaxValue;
    }

    /// <summary>
    /// 流式扫文本节点：遇到段落/行/单元格元素补换行，文本节点原样追加。
    /// 不对元素做命名空间白名单——正文文字在 OOXML 与 ODF 里都落在文本节点上，
    /// 而过宽的担心由「限深 + 限长」兜住。
    /// </summary>
    private static void AppendXmlText(Stream stream, StringBuilder builder)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            MaxCharactersInDocument = MaxEntryBytes * 4,
            MaxCharactersFromEntities = 1024,
        };

        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (builder.Length >= MaxCharacters)
                return;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element when IsLineBreakElement(reader.LocalName):
                    AppendBreak(builder);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    var value = reader.Value;
                    if (value.Length == 0)
                        break;
                    builder.Append(value);
                    break;
            }
        }
    }

    private static bool IsLineBreakElement(string localName) => localName switch
    {
        "p" or "br" or "cr" => true,          // Word 段落/换行、文本运行内换行
        "row" or "c" => true,                 // 表格行/单元格：拼成一行更利于检索
        "sp" or "si" => true,                 // 幻灯片段落、共享字符串条目
        "h" or "list-item" => true,           // ODF 标题与列表项
        _ => false,
    };

    private static void AppendBreak(StringBuilder builder)
    {
        if (builder.Length == 0)
            return;
        if (builder[^1] != '\n')
            builder.Append('\n');
    }

    /// <summary>
    /// 收尾：按行去空白、丢空行、行内压缩空白。
    /// 美化排版的 XML 会在段落之间留下「换行 + 缩进」的纯空白节点，
    /// 直接保留会变成一堆空行（v0.47 首轮 CI 就是被这个坑到的）。
    /// </summary>
    private static string? Cleanup(StringBuilder builder)
    {
        if (builder.Length == 0)
            return null;

        var raw = builder.ToString();
        var output = new StringBuilder(raw.Length);
        var line = new StringBuilder(128);

        void FlushLine()
        {
            var text = CollapseSpaces(line).Trim();
            line.Clear();
            if (text.Length == 0)
                return;
            if (output.Length > 0)
                output.Append('\n');
            output.Append(text);
        }

        foreach (var ch in raw)
        {
            if (ch == '\r')
                continue;
            if (ch == '\n')
            {
                FlushLine();
                continue;
            }

            line.Append(ch);
        }

        FlushLine();
        var result = output.ToString();
        return result.Length == 0 ? null : result;
    }

    private static string CollapseSpaces(StringBuilder line)
    {
        var output = new StringBuilder(line.Length);
        var lastWasSpace = false;
        foreach (var ch in line.ToString())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace)
                    continue;
                lastWasSpace = true;
                output.Append(' ');
                continue;
            }

            lastWasSpace = false;
            output.Append(ch);
        }

        return output.ToString();
    }
}
