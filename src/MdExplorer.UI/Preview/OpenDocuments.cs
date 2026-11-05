using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using Formatting = MdExplorer.Domain.Formatting;

namespace MdExplorer.UI.Preview;

/// <summary>
/// OpenDocument 家族的只读预览结果。呈现层把它当 Markdown 渲染，因此这里保留的只有
/// 结构与文字，不保留版式。
/// </summary>
public sealed record OpenDocumentPreview(
    string Markdown,
    OpenDocumentFormat Format,
    int CharacterCount,
    bool Truncated);

/// <summary>
/// 内置 OpenDocument（ODT/ODS/ODP 与其平面 XML、模板变体）只读读取器。
/// <para>
/// 为什么自己读：ODF 就是「ZIP 里的 content.xml」或「单文件平面 XML」，宿主已经有有界读取与
/// 禁用 DTD 的 XML 预检；能渲染 ODF 版式的成熟 .NET 库要么依赖 LibreOffice 运行时，
/// 要么只解析不渲染（照样得自己写呈现）。所以这里只做「文本级」还原：标题层级、段落、
/// 列表、表格、幻灯片顺序，把它们映射成 Markdown 交给既有的渲染器。
/// </para>
/// <para>
/// 边界很明确：不还原分页、字体、颜色、图片、图形、嵌入对象、公式与批注；不写回。
/// 这些内容缺失时预览会少东西，所以在文档开头显式说明，并保留“用系统应用打开”。
/// </para>
/// </summary>
public static class OpenDocumentReader
{
    public const string EngineDescription = "内置 OpenDocument 文本级只读预览（ZIP + XML，无 LibreOffice 运行时）";

    /// <summary>整包上限：与 Office 快速预览一致，Android 更保守。</summary>
    public static long MaxBytes => (OperatingSystem.IsAndroid() ? 16L : 32L) * 1024 * 1024;

    /// <summary>解析期内允许的 XML 字符数（压缩炸弹与超深文档的第二道闸）。</summary>
    private const int MaxXmlCharacters = 4_000_000;

    /// <summary>输出 Markdown 的字符预算；超出即截断并如实标注。</summary>
    private const int MaxOutputCharacters = 200_000;

    private const int MaxTableRows = 200;
    private const int MaxTableColumns = 32;
    private const int MaxSlides = 300;
    private const int MaxDepth = 16;

    private const string Note =
        "OpenDocument 只读预览：保留标题层级、段落、列表、表格单元格与幻灯片顺序；不还原分页、字体、图片、图形与嵌入对象。需要完整版式请用系统应用打开。";

    private static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace PresentationNs = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>是否平面 XML 形态（单文件，不需要解 ZIP）。</summary>
    public static bool IsFlatXml(string? extension) => extension?.ToLowerInvariant() is ".fodt" or ".fods" or ".fodp";

    public static OpenDocumentPreview Read(byte[] data, string? extension)
    {
        if (!TryResolveFormat(extension, out var format))
            throw new InvalidDataException("此类型不是可预览的 OpenDocument 文本、电子表格或演示文稿。");

        var root = LoadRoot(data, extension);
        var body = root.Descendants(OfficeNs + "body").FirstOrDefault()
                   ?? throw new InvalidDataException("OpenDocument 缺少 office:body，可能不是有效文档。");

        var content = body.Elements().FirstOrDefault()
                      ?? throw new InvalidDataException("OpenDocument 的 office:body 为空。");

        var builder = new MarkdownBuilder(MaxOutputCharacters);
        builder.Quote(Note);
        if (content.Name == OfficeNs + "text")
        {
            format = OpenDocumentFormat.Text;
            WriteTextContent(content, builder, 0);
        }
        else if (content.Name == OfficeNs + "spreadsheet")
        {
            format = OpenDocumentFormat.Spreadsheet;
            WriteSpreadsheet(content, builder);
        }
        else if (content.Name == OfficeNs + "presentation")
        {
            format = OpenDocumentFormat.Presentation;
            WritePresentation(content, builder);
        }
        else
        {
            throw new InvalidDataException($"暂不支持 OpenDocument 的此类内容（{content.Name.LocalName}）。");
        }

        if (builder.Truncated)
            builder.TruncationNote($"内容超过 {MaxOutputCharacters:N0} 字符，预览已截断；完整内容请用系统应用打开。");

        var markdown = builder.ToString();
        return new OpenDocumentPreview(markdown, format, markdown.Length, builder.Truncated);
    }

    /// <summary>扩展名 → 家族；平面 XML 与模板变体都归到同一类。</summary>
    private static bool TryResolveFormat(string? extension, out OpenDocumentFormat format)
    {
        switch (extension?.ToLowerInvariant())
        {
            case ".odt":
            case ".ott":
            case ".fodt":
                format = OpenDocumentFormat.Text;
                return true;
            case ".ods":
            case ".ots":
            case ".fods":
                format = OpenDocumentFormat.Spreadsheet;
                return true;
            case ".odp":
            case ".otp":
            case ".fodp":
                format = OpenDocumentFormat.Presentation;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static XElement LoadRoot(byte[] data, string? extension)
    {
        if (IsFlatXml(extension))
            return LoadXml(new MemoryStream(data, writable: false));

        using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        var entry = archive.GetEntry("content.xml");
        if (entry is null)
            throw new InvalidDataException("OpenDocument 压缩包里没有 content.xml，可能不是有效文档。");
        if (entry.Length > MaxBytes)
            throw new InvalidDataException(
                $"content.xml 解压后为 {Formatting.Bytes(entry.Length)}，超过 {Formatting.Bytes(MaxBytes)} 的预览上限。");

        using var stream = entry.Open();
        return LoadXml(stream);
    }

    private static XElement LoadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None).Root
                   ?? throw new InvalidDataException("OpenDocument 的 XML 没有根元素。");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"OpenDocument 的 XML 无法解析：{ex.Message}");
        }
    }

    // ---------- 文本文档 ----------

    private static void WriteTextContent(XElement container, MarkdownBuilder builder, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (var child in container.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "h" when child.Name.Namespace == TextNs:
                    builder.Heading(OutlineLevel(child), InlineText(child));
                    break;
                case "p" when child.Name.Namespace == TextNs:
                    builder.Paragraph(InlineText(child));
                    break;
                case "list" when child.Name.Namespace == TextNs:
                    WriteList(child, builder, depth);
                    break;
                case "table" when child.Name.Namespace == TableNs:
                    WriteTable(child, builder);
                    break;
                case "section" or "index-body" or "table-of-content" when child.Name.Namespace == TextNs:
                    WriteTextContent(child, builder, depth + 1);
                    break;
            }
        }
    }

    private static void WriteList(XElement list, MarkdownBuilder builder, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (var item in list.Elements(TextNs + "list-item"))
        {
            var parts = item.Elements()
                .Where(e => e.Name == TextNs + "p" || e.Name == TextNs + "h")
                .Select(InlineText)
                .Where(text => text.Length > 0)
                .ToArray();

            var label = string.Join(" ", parts);
            if (label.Length > 0)
                builder.ListItem(depth, label);

            // 嵌套列表排在父项文字之后，缩进反映层级。
            foreach (var nested in item.Elements(TextNs + "list"))
                WriteList(nested, builder, depth + 1);
        }
    }

    // ---------- 电子表格 ----------

    private static void WriteSpreadsheet(XElement spreadsheet, MarkdownBuilder builder)
    {
        var index = 0;
        foreach (var table in spreadsheet.Elements(TableNs + "table"))
        {
            index++;
            var name = (string?)table.Attribute(TableNs + "name");
            if (string.IsNullOrWhiteSpace(name))
                name = $"工作表 {index}";
            builder.Heading(2, $"工作表 · {name}");
            builder.Table(ReadTableRows(table));
        }

        if (index == 0)
            builder.Paragraph("（这个电子表格里没有工作表。）");
    }

    // ---------- 演示文稿 ----------

    private static void WritePresentation(XElement presentation, MarkdownBuilder builder)
    {
        var index = 0;
        foreach (var page in presentation.Elements(DrawNs + "page"))
        {
            if (++index > MaxSlides)
            {
                builder.Quote($"幻灯片超过 {MaxSlides} 页，预览只显示前 {MaxSlides} 页。");
                break;
            }

            var name = (string?)page.Attribute(DrawNs + "name");
            builder.Heading(2, string.IsNullOrWhiteSpace(name) ? $"第 {index} 页" : $"第 {index} 页 · {name}");

            // presentation:notes 是演讲者备注，不属于页面内容。
            var paragraphs = page.Descendants(TextNs + "p")
                .Where(p => !p.Ancestors(PresentationNs + "notes").Any())
                .Select(InlineText)
                .Where(text => text.Length > 0)
                .ToArray();

            foreach (var paragraph in paragraphs)
                builder.Bullet(paragraph);
        }

        if (index == 0)
            builder.Paragraph("（这个演示文稿里没有幻灯片。）");
    }

    // ---------- 表格（ODT 与 ODS 共用） ----------

    private static IReadOnlyList<IReadOnlyList<string>> ReadTableRows(XElement table)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in table.Elements(TableNs + "table-row"))
        {
            var cells = ReadRow(row);
            var repeat = RepeatCount(row, "number-rows-repeated", MaxTableRows);
            for (var i = 0; i < repeat && rows.Count < MaxTableRows; i++)
                rows.Add(cells);

            if (rows.Count >= MaxTableRows)
                break;
        }

        // 结尾的空行没有信息量，去掉；中间的空行保留，它可能是分隔。
        while (rows.Count > 0 && rows[^1].Count == 0)
            rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static IReadOnlyList<string> ReadRow(XElement row)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements())
        {
            if (cell.Name != TableNs + "table-cell" && cell.Name != TableNs + "covered-table-cell")
                continue;

            var text = CellText(cell);
            var repeat = RepeatCount(cell, "number-columns-repeated", MaxTableColumns);
            for (var i = 0; i < repeat && cells.Count < MaxTableColumns; i++)
                cells.Add(text);
        }

        while (cells.Count > 0 && cells[^1].Length == 0)
            cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    private static string CellText(XElement cell)
    {
        var paragraphs = cell.Elements(TextNs + "p")
            .Select(InlineText)
            .Where(text => text.Length > 0)
            .ToArray();
        if (paragraphs.Length > 0)
            return MarkdownBuilder.Escape(string.Join(" / ", paragraphs).Replace('\n', ' '));

        // 没有显示文本时退回数值属性，避免整列空白却其实有数据。
        var raw = (string?)cell.Attribute(OfficeNs + "value")
                  ?? (string?)cell.Attribute(OfficeNs + "date-value")
                  ?? (string?)cell.Attribute(OfficeNs + "time-value")
                  ?? (string?)cell.Attribute(OfficeNs + "boolean-value")
                  ?? (string?)cell.Attribute(OfficeNs + "string-value");
        return raw is null ? string.Empty : MarkdownBuilder.Escape(raw);
    }

    private static void WriteTable(XElement table, MarkdownBuilder builder) => builder.Table(ReadTableRows(table));

    // ---------- 行内文字 ----------

    private static int OutlineLevel(XElement heading)
    {
        var raw = (string?)heading.Attribute(TextNs + "outline-level");
        return int.TryParse(raw, out var level) ? Math.Clamp(level, 1, 6) : 1;
    }

    /// <summary>把一段 ODF 行内内容拍平成 Markdown 文本（不保留样式，只保留文字与链接地址）。</summary>
    private static string InlineText(XElement element)
    {
        var builder = new StringBuilder();
        AppendInline(element, builder, 0);
        return MarkdownBuilder.Escape(Normalize(builder.ToString()));
    }

    private static void AppendInline(XElement element, StringBuilder builder, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
                continue;
            }

            if (node is not XElement child)
                continue;

            if (child.Name == TextNs + "s")
            {
                builder.Append(' ', RepeatCount(child, "c", 16));
                continue;
            }

            if (child.Name == TextNs + "tab")
            {
                builder.Append(' ');
                continue;
            }

            if (child.Name == TextNs + "line-break")
            {
                builder.Append('\n');
                continue;
            }

            // 批注、脚注与索引标记不是正文，展开只会把预览搅乱。
            if (child.Name == TextNs + "note" || child.Name == OfficeNs + "annotation")
                continue;

            if (child.Name == TextNs + "a")
            {
                var label = new StringBuilder();
                AppendInline(child, label, depth + 1);
                var textValue = Normalize(label.ToString());
                var href = (string?)child.Attribute(XLinkNs + "href");
                if (!string.IsNullOrWhiteSpace(href) && !string.Equals(href, textValue, StringComparison.Ordinal))
                    builder.Append($"{textValue} ({href})");
                else
                    builder.Append(textValue);
                continue;
            }

            AppendInline(child, builder, depth + 1);
        }
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (previousWasSpace)
                    continue;
                builder.Append(' ');
                previousWasSpace = true;
                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static int RepeatCount(XElement element, string localName, int cap)
    {
        var raw = (string?)element.Attribute(TableNs + localName) ?? (string?)element.Attribute(TextNs + localName);
        if (!int.TryParse(raw, out var value) || value < 1)
            return 1;
        return Math.Min(value, cap);
    }

    /// <summary>Markdown 拼装：负责空行、缩进与字符预算，超出预算即截断并打标。</summary>
    private sealed class MarkdownBuilder(int limit)
    {
        private readonly StringBuilder _builder = new();
        private bool _needsBlankLine;

        public bool Truncated { get; private set; }

        public override string ToString() => _builder.ToString().TrimEnd() + "\n";

        public void Heading(int level, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            AppendBlock($"{new string('#', Math.Clamp(level, 1, 6))} {text}");
        }

        public void Paragraph(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            AppendBlock(text);
        }

        /// <summary>截断说明必须能写在预算之外，否则它自己会被预算拒掉。</summary>
        public void TruncationNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (_builder.Length > 0)
                _builder.Append("\n\n");
            _builder.Append("> ").Append(text);
        }

        public void Quote(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            AppendBlock($"> {text}");
        }

        public void Bullet(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            AppendBlock($"- {text}");
        }

        public void ListItem(int depth, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            AppendBlock($"{new string(' ', Math.Min(depth, 6) * 2)}- {text}");
        }

        public void Table(IReadOnlyList<IReadOnlyList<string>> rows)
        {
            if (rows.Count == 0)
                return;
            var columns = rows.Max(row => row.Count);
            if (columns == 0)
                return;

            var builder = new StringBuilder();
            for (var index = 0; index < rows.Count; index++)
            {
                builder.Append('|');
                for (var column = 0; column < columns; column++)
                {
                    var cell = column < rows[index].Count ? rows[index][column] : string.Empty;
                    builder.Append(' ').Append(cell).Append(" |");
                }

                builder.Append('\n');
                if (index == 0)
                {
                    builder.Append('|');
                    for (var column = 0; column < columns; column++)
                        builder.Append(" --- |");
                    builder.Append('\n');
                }
            }

            AppendBlock(builder.ToString().TrimEnd());
        }

        private void AppendBlock(string text)
        {
            if (text.Length == 0 || Truncated)
                return;

            var prefix = _needsBlankLine ? "\n\n" : string.Empty;
            if (_builder.Length + prefix.Length >= limit)
            {
                Truncated = true;
                return;
            }

            _builder.Append(prefix);
            var remaining = limit - _builder.Length;
            if (text.Length > remaining)
            {
                _builder.Append(text, 0, remaining);
                Truncated = true;
                return;
            }

            _builder.Append(text);
            _needsBlankLine = true;
        }

        /// <summary>转义会被 Markdown 当成语法的字符；中文正文不受影响。</summary>
        public static string Escape(string text)
        {
            if (text.AsSpan().IndexOfAny("\\*_`|") < 0)
                return text;

            var builder = new StringBuilder(text.Length + 8);
            foreach (var ch in text)
            {
                if (ch is '\\' or '*' or '_' or '`' or '|')
                    builder.Append('\\');
                builder.Append(ch);
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// OpenDocument 的内置预览提供者。它排在内置 Office（118）之后、Markdown（110）之前：
/// 同一批扩展名不会被别的提供者抢走，也不会覆盖 OOXML 的专用控件。
/// </summary>
internal sealed class OpenDocumentPreviewProvider(IFileSystemService fileSystem) : IPreviewProvider
{
    public string Id => "builtin.opendocument";

    public int Priority => 117;

    public bool CanHandle(FileItem item) =>
        item.Kind == FileKind.Document && FileKindClassifier.IsOpenDocument(item);

    public async Task<PreviewContent> LoadAsync(FileItem item, CancellationToken cancellationToken)
    {
        try
        {
            var data = await ReadBoundedAsync(item, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await Task.Run(() => OpenDocumentReader.Read(data, item.Extension), cancellationToken)
                .ConfigureAwait(true);
            return new PreviewContent(Markdown: preview.Markdown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new PreviewContent(
                Message: $"无法读取此 OpenDocument 文档：{ex.Message} 可先用系统应用打开。");
        }
    }

    private async Task<byte[]> ReadBoundedAsync(FileItem item, CancellationToken cancellationToken)
    {
        var limit = OpenDocumentReader.MaxBytes;
        if (item.Length is { } known && known > limit)
            throw new InvalidDataException(
                $"此文件为 {Formatting.Bytes(known)}，超过内置预览的 {Formatting.Bytes(limit)} 上限。");

        await using var source = await fileSystem
            .OpenReadAsync(item.FullPath, cancellationToken)
            .ConfigureAwait(true);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(true)) > 0)
        {
            if (memory.Length + read > limit)
                throw new InvalidDataException($"此文件超过内置预览的 {Formatting.Bytes(limit)} 上限。");
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
