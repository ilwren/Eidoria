using System.IO.Compression;
using System.Xml;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using Formatting = MdExplorer.Domain.Formatting;

namespace MdExplorer.UI.Preview;

/// <summary>第三方 Office 控件的宿主格式枚举；PDF 仅表示 PDFium 不可用时的纯托管回落。</summary>
public enum OfficePreviewFormat
{
    Docx,
    Pptx,
    Xlsx,
    Pdf,
}

/// <summary>宿主预检得到的文档事实。页数对 DOCX 需排版后才知道，因此以段落/表格计数表达。</summary>
public sealed record OfficeDocumentFacts(
    string Summary,
    IReadOnlyList<string> SheetNames,
    int? SlideCount = null,
    int? ParagraphCount = null,
    int? TableCount = null,
    long ExpandedBytes = 0,
    int EntryCount = 0,
    IReadOnlyList<double>? SheetNominalWidths = null);

/// <summary>
/// 已通过宿主有界预检的 Office 文档字节。第三方控件只接收这一负载，
/// 避免把任意 ZIP/XML 展开量直接交给其内部解析器。
/// </summary>
public sealed record OfficePreviewDocument(
    byte[] Data,
    OfficePreviewFormat Format,
    OfficeDocumentFacts Facts);

/// <summary>
/// OfficeViewer.Avalonia 的宿主收口：统一从本地/SAF 文件服务有界读入，
/// 再对 Open XML 包做条目、展开量、XML 节点和电子表格网格规模预检。
/// </summary>
public static class OfficeDocumentLoader
{
    public const string EngineDescription = "OfficeViewer.Avalonia 0.1.6（MIT）· Avalonia 原生只读预览";

    private const int MaxEntries = 1536;
    private const int MaxSlides = 500;
    private const int MaxSheets = 128;
    private const int MaxSpreadsheetRows = 5000;
    private const int MaxSpreadsheetColumns = 256;

    public static long QuickPreviewMaxBytes => (OperatingSystem.IsAndroid() ? 16L : 32L) * 1024 * 1024;
    public static long WorkbenchMaxBytes => (OperatingSystem.IsAndroid() ? 32L : 64L) * 1024 * 1024;

    /// <summary>直接 PDF 控件在快速预览中的整文件上限；超过后才进入 PDFium 光栅兼容路径。</summary>
    public static long DirectPdfPreviewMaxBytes => QuickPreviewMaxBytes;

    /// <summary>直接 PDF 控件在文档工作台中的整文件上限，与 Office 工作台一致。</summary>
    public static long DirectPdfWorkbenchMaxBytes => WorkbenchMaxBytes;

    /// <summary>
    /// 直接 PDF 控件（OfficeViewer 的 Avalonia PdfViewer）会把每一页都排成控件树。
    /// 100+ 页文档实测把提交内存顶到约 1 GB，而且关闭预览后不回落——所以页数超过这个上限
    /// 一律改走 PDFium：快速预览只渲染当前页，工作台走虚拟化连续阅读（缓存 10 页 / 96 MiB）。
    /// </summary>
    public const int DirectPdfMaxPages = 24;

    /// <summary>
    /// 页数与字节数都合格才允许把原始 PDF 交给直接控件。页数 ≤0 表示数不出来（加密、损坏、
    /// 本机没有 PDFium），此时沿用旧行为放行，由直接控件自己报错、宿主再回落到 PDFium。
    /// </summary>
    public static bool CanUseDirectPdf(int pageCount, long byteLength, long maxBytes) =>
        pageCount <= DirectPdfMaxPages && byteLength <= maxBytes;

    public static string DisplayName(OfficePreviewFormat format) => format switch
    {
        OfficePreviewFormat.Docx => "Word 文档",
        OfficePreviewFormat.Pptx => "PowerPoint 演示文稿",
        OfficePreviewFormat.Xlsx => "Excel 工作簿",
        _ => "PDF 文档",
    };

    public static async Task<OfficePreviewDocument> LoadAsync(
        IFileSystemService fileSystem,
        FileItem item,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!FileKindClassifier.TryGetOfficeDocumentFormat(item, out var officeFormat))
            throw new InvalidDataException("此类型不是可预览的 DOCX、PPTX 或 XLSX。");
        var format = officeFormat switch
        {
            OfficeDocumentFormat.Word => OfficePreviewFormat.Docx,
            OfficeDocumentFormat.Presentation => OfficePreviewFormat.Pptx,
            _ => OfficePreviewFormat.Xlsx,
        };
        var label = DisplayName(format);
        if (item.Length is { } knownLength && knownLength > maxBytes)
            throw new InvalidDataException(
                $"此{label}为 {Formatting.Bytes(knownLength)}，超过内置预览的 {Formatting.Bytes(maxBytes)} 安全上限。");

        await using var source = await fileSystem.OpenReadAsync(item.FullPath, cancellationToken).ConfigureAwait(true);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(true)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new InvalidDataException($"此{label}超过内置预览的 {Formatting.Bytes(maxBytes)} 安全上限。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(true);
        }

        var data = memory.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var facts = await Task.Run(() => ValidateAndDescribe(data, format), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return new OfficePreviewDocument(data, format, facts);
    }

    /// <summary>
    /// 只做廉价预分流：有效文件头且没有明文加密字典。压缩对象流里的加密标记
    /// 可能漏检，最终仍以直接控件的页数/错误状态为准。
    /// </summary>
    public static bool IsDirectPdfCandidate(byte[] data)
    {
        var headerLength = Math.Min(data.Length, 1024);
        return data.AsSpan(0, headerLength).IndexOf("%PDF-"u8) >= 0 &&
               data.AsSpan().IndexOf("/Encrypt"u8) < 0;
    }

    /// <summary>
    /// 创建直接交给 PDF 控件的负载。它不做整页光栅化；只有控件明确无法处理的
    /// 加密标记、无效文件头或超限文档才在此被拒绝，并由调用方转入 PDFium 路径。
    /// </summary>
    public static OfficePreviewDocument CreateDirectPdf(byte[] data, long maxBytes)
    {
        if (data.Length > maxBytes)
            throw new InvalidDataException(
                $"此 PDF 超过直接预览的 {Formatting.Bytes(maxBytes)} 安全上限。");
        var headerLength = Math.Min(data.Length, 1024);
        if (data.AsSpan(0, headerLength).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("此文件不包含有效的 PDF 文件头。");
        if (data.AsSpan().IndexOf("/Encrypt"u8) >= 0)
            throw new InvalidDataException("此 PDF 已加密，直接预览控件不支持输入密码。");
        return new OfficePreviewDocument(
            data,
            OfficePreviewFormat.Pdf,
            new OfficeDocumentFacts("PDF 直接预览 · 原生控件连续渲染", []));
    }

    public static bool TryCreateDirectPdf(
        byte[] data,
        long maxBytes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OfficePreviewDocument? document)
    {
        try
        {
            document = CreateDirectPdf(data, maxBytes);
            return true;
        }
        catch (InvalidDataException)
        {
            document = null;
            return false;
        }
    }

    private sealed record Limits(
        long MaxExpandedBytes,
        long MaxEntryBytes,
        long MaxXmlBytes,
        long MaxMediaBytes,
        long MaxXmlElements,
        long MaxSpreadsheetPositions)
    {
        public static Limits Current => OperatingSystem.IsAndroid()
            ? new Limits(96L * 1024 * 1024, 48L * 1024 * 1024, 32L * 1024 * 1024, 64L * 1024 * 1024, 180_000, 60_000)
            : new Limits(192L * 1024 * 1024, 64L * 1024 * 1024, 48L * 1024 * 1024, 128L * 1024 * 1024, 320_000, 120_000);
    }

    private static OfficeDocumentFacts ValidateAndDescribe(byte[] data, OfficePreviewFormat format)
    {
        var limits = Limits.Current;
        try
        {
            using var source = new MemoryStream(data, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
                throw new InvalidDataException($"文档包条目数异常（{archive.Entries.Count:N0}），已拒绝预览。");

            long expandedBytes = 0;
            long xmlBytes = 0;
            long mediaBytes = 0;
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("此 Office 文档已加密，内置预览暂不支持输入密码。");
                if (path.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("此 Office 文档包含宏部件，已拒绝在内置预览中打开。");
                if (entry.Length < 0 || entry.Length > limits.MaxEntryBytes)
                    throw new InvalidDataException("文档包内单个部件过大，已拒绝预览。");

                expandedBytes += entry.Length;
                if (expandedBytes > limits.MaxExpandedBytes)
                    throw new InvalidDataException(
                        $"文档包展开后超过 {Formatting.Bytes(limits.MaxExpandedBytes)}，已拒绝预览。");
                if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    xmlBytes += entry.Length;
                    if (xmlBytes > limits.MaxXmlBytes)
                        throw new InvalidDataException("文档的 XML 部件过大，已拒绝预览。");
                }
                else if (path.Contains("/media/", StringComparison.OrdinalIgnoreCase))
                {
                    mediaBytes += entry.Length;
                    if (mediaBytes > limits.MaxMediaBytes)
                        throw new InvalidDataException("文档内嵌媒体过大，已拒绝预览。");
                }
            }

            RequireEntry(archive, "[Content_Types].xml");
            var facts = format switch
            {
                OfficePreviewFormat.Docx => DescribeDocx(archive, limits),
                OfficePreviewFormat.Pptx => DescribePptx(archive, limits),
                OfficePreviewFormat.Xlsx => DescribeXlsx(archive, limits),
                _ => throw new InvalidDataException("不支持的 Office 文档格式。"),
            };
            return facts with { ExpandedBytes = expandedBytes, EntryCount = archive.Entries.Count };
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"文档的 XML 部件损坏：{ex.Message}", ex);
        }
    }

    private static OfficeDocumentFacts DescribeDocx(ZipArchive archive, Limits limits)
    {
        long elements = 0;
        var paragraphs = 0;
        var tables = 0;
        var drawings = 0;
        elements += ScanXml(archive, "word/document.xml", reader =>
        {
            switch (reader.LocalName)
            {
                case "p": paragraphs++; break;
                case "tbl": tables++; break;
                case "drawing":
                case "pict": drawings++; break;
            }
        });
        elements += ScanXml(archive, "word/styles.xml", onElement: null, required: false);
        elements += ScanXml(archive, "word/numbering.xml", onElement: null, required: false);
        EnsureElementBudget(elements, limits);
        if (paragraphs == 0 && tables == 0)
            throw new InvalidDataException("此 Word 文档没有可预览的正文。");

        var summary = $"{paragraphs:N0} 个段落 · {tables:N0} 个表格";
        if (drawings > 0)
            summary += $" · {drawings:N0} 个图形";
        return new OfficeDocumentFacts(summary, [], ParagraphCount: paragraphs, TableCount: tables);
    }

    private static OfficeDocumentFacts DescribePptx(ZipArchive archive, Limits limits)
    {
        long elements = 0;
        var slides = 0;
        elements += ScanXml(archive, "ppt/presentation.xml", reader =>
        {
            if (reader.LocalName == "sldId")
                slides++;
        });
        if (slides <= 0 || slides > MaxSlides)
            throw new InvalidDataException($"演示文稿幻灯片数异常（{slides:N0}），已拒绝预览。");

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (path.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ppt/slideLayouts/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ppt/slideMasters/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ppt/charts/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ppt/theme/", StringComparison.OrdinalIgnoreCase))
            {
                elements += ScanXml(archive, path, onElement: null);
                EnsureElementBudget(elements, limits);
            }
        }

        return new OfficeDocumentFacts($"{slides:N0} 张幻灯片", [], SlideCount: slides);
    }

    private static OfficeDocumentFacts DescribeXlsx(ZipArchive archive, Limits limits)
    {
        const string relationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        long elements = 0;
        var sheetNames = new List<string>();
        var sheetRelationshipIds = new List<string>();
        elements += ScanXml(archive, "xl/workbook.xml", reader =>
        {
            if (reader.LocalName != "sheet")
                return;
            sheetNames.Add(reader.GetAttribute("name") ?? $"工作表 {sheetNames.Count + 1}");
            sheetRelationshipIds.Add(reader.GetAttribute("id", relationshipsNamespace) ?? string.Empty);
        });
        if (sheetNames.Count == 0 || sheetNames.Count > MaxSheets)
            throw new InvalidDataException($"工作簿工作表数异常（{sheetNames.Count:N0}），已拒绝预览。");

        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        elements += ScanXml(archive, "xl/_rels/workbook.xml.rels", reader =>
        {
            if (reader.LocalName != "Relationship")
                return;
            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            var mode = reader.GetAttribute("TargetMode");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target) &&
                !string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase))
                relationships[id] = ResolvePackagePath("xl/workbook.xml", target);
        });
        elements += ScanXml(archive, "xl/sharedStrings.xml", onElement: null, required: false);
        elements += ScanXml(archive, "xl/styles.xml", onElement: null, required: false);
        EnsureElementBudget(elements, limits);

        var sheetNominalWidths = new List<double>();
        var maxRowsOverall = 0;
        var maxColumnsOverall = 0;
        long populatedCells = 0;
        for (var index = 0; index < sheetNames.Count; index++)
        {
            var relationshipId = sheetRelationshipIds[index];
            if (string.IsNullOrWhiteSpace(relationshipId) ||
                !relationships.TryGetValue(relationshipId, out var sheetPath))
                throw new InvalidDataException($"工作簿缺少工作表「{sheetNames[index]}」的数据部件。");

            var maxRow = 0;
            var maxColumn = 0;
            // OfficeViewer 0.1.6 的实际列宽约为 SpreadsheetML 单位 ×7；宿主取 7.25 留出边框余量。
            var defaultColumnWidth = 8.43 * 7.25;
            var columnWidths = new Dictionary<int, double>();
            elements += ScanXml(archive, sheetPath, reader =>
            {
                switch (reader.LocalName)
                {
                    case "sheetFormatPr":
                        if (double.TryParse(reader.GetAttribute("defaultColWidth"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var parsedDefaultWidth) && parsedDefaultWidth > 0)
                            defaultColumnWidth = Math.Max(48, parsedDefaultWidth * 7.25);
                        break;
                    case "col":
                        if (int.TryParse(reader.GetAttribute("max"), out var colMax))
                            maxColumn = Math.Max(maxColumn, colMax);
                        var colMin = int.TryParse(reader.GetAttribute("min"), out var parsedMin) ? parsedMin : 1;
                        var hidden = reader.GetAttribute("hidden") is "1" or "true";
                        var columnWidth = hidden
                            ? 0
                            : double.TryParse(reader.GetAttribute("width"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var parsedWidth) && parsedWidth > 0
                                ? Math.Max(1, parsedWidth * 7.25)
                                : defaultColumnWidth;
                        // 只记录宿主允许范围内的列宽；超范围仍由 maxColumn 在整表扫描后统一拒绝。
                        for (var column = Math.Max(1, colMin);
                             column <= Math.Min(colMax, MaxSpreadsheetColumns);
                             column++)
                            columnWidths[column - 1] = columnWidth;
                        break;
                    case "row":
                        if (int.TryParse(reader.GetAttribute("r"), out var rowNumber))
                            maxRow = Math.Max(maxRow, rowNumber);
                        break;
                    case "c":
                        populatedCells++;
                        if (TryParseCellAddress(reader.GetAttribute("r"), out var cellRow, out var cellColumn))
                        {
                            maxRow = Math.Max(maxRow, cellRow);
                            maxColumn = Math.Max(maxColumn, cellColumn);
                        }
                        break;
                    case "mergeCell":
                        var range = reader.GetAttribute("ref");
                        var end = range?.Split(':').LastOrDefault();
                        if (TryParseCellAddress(end, out var mergeRow, out var mergeColumn))
                        {
                            maxRow = Math.Max(maxRow, mergeRow);
                            maxColumn = Math.Max(maxColumn, mergeColumn);
                        }
                        break;
                }
            });
            EnsureElementBudget(elements, limits);
            if (populatedCells > limits.MaxSpreadsheetPositions)
                throw new InvalidDataException("工作簿有值单元格过多，已拒绝在内置预览中展开。");
            if (maxRow > MaxSpreadsheetRows || maxColumn > MaxSpreadsheetColumns ||
                (long)maxRow * Math.Max(1, maxColumn) > limits.MaxSpreadsheetPositions)
            {
                throw new InvalidDataException(
                    $"工作表「{sheetNames[index]}」范围过大（{maxRow:N0} 行 × {maxColumn:N0} 列），已拒绝在内置预览中展开。");
            }
            var nominalWidth = 52d + 16d + 18d; // 行号列 + 上游网格边距 + 垂直滚动条。
            for (var column = 0; column < maxColumn; column++)
                nominalWidth += columnWidths.TryGetValue(column, out var width) ? width : defaultColumnWidth;
            sheetNominalWidths.Add(Math.Clamp(nominalWidth, 320, 1600));
            maxRowsOverall = Math.Max(maxRowsOverall, maxRow);
            maxColumnsOverall = Math.Max(maxColumnsOverall, maxColumn);
        }

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                elements += ScanSpreadsheetDrawing(archive, path);
                EnsureElementBudget(elements, limits);
            }
        }

        var summary = $"{sheetNames.Count:N0} 张工作表 · 最大 {maxRowsOverall:N0} 行 × {maxColumnsOverall:N0} 列";
        return new OfficeDocumentFacts(summary, sheetNames, SheetNominalWidths: sheetNominalWidths);
    }

    private static long ScanSpreadsheetDrawing(ZipArchive archive, string path)
    {
        var entry = RequireEntry(archive, path);
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, CreateXmlSettings());
        long elements = 0;
        string? pending = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                elements++;
                pending = reader.LocalName is "col" or "row" ? reader.LocalName : null;
            }
            else if ((reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) && pending is not null)
            {
                if (int.TryParse(reader.Value, out var zeroBased))
                {
                    if ((pending == "row" && zeroBased >= MaxSpreadsheetRows) ||
                        (pending == "col" && zeroBased >= MaxSpreadsheetColumns))
                        throw new InvalidDataException("工作表图形锚点超出内置预览范围，已拒绝打开。");
                }
                pending = null;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                pending = null;
            }
        }
        return elements;
    }

    private static long ScanXml(
        ZipArchive archive,
        string path,
        Action<XmlReader>? onElement,
        bool required = true)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            if (required)
                throw new InvalidDataException($"文档包缺少必要部件 {path}。");
            return 0;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, CreateXmlSettings());
        long elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            elements++;
            onElement?.Invoke(reader);
        }
        return elements;
    }

    private static XmlReaderSettings CreateXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 64L * 1024 * 1024,
    };

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path) ?? throw new InvalidDataException($"文档包缺少必要部件 {path}。");

    private static void EnsureElementBudget(long elements, Limits limits)
    {
        if (elements > limits.MaxXmlElements)
            throw new InvalidDataException(
                $"文档结构过于复杂（超过 {limits.MaxXmlElements:N0} 个 XML 元素），已拒绝预览。");
    }

    private static string ResolvePackagePath(string sourcePart, string target)
    {
        var source = new Uri("http://package/" + sourcePart, UriKind.Absolute);
        return Uri.UnescapeDataString(new Uri(source, target).AbsolutePath.TrimStart('/'));
    }

    private static bool TryParseCellAddress(string? address, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(address))
            return false;
        var text = address.Replace("$", string.Empty, StringComparison.Ordinal);
        var index = 0;
        long parsedColumn = 0;
        while (index < text.Length && char.IsLetter(text[index]))
        {
            parsedColumn = parsedColumn * 26 + char.ToUpperInvariant(text[index]) - 'A' + 1;
            if (parsedColumn > int.MaxValue)
            {
                row = int.MaxValue;
                column = int.MaxValue;
                return true;
            }
            index++;
        }
        if (index == 0 || index >= text.Length || !long.TryParse(text.AsSpan(index), out var parsedRow))
            return false;
        if (parsedRow > int.MaxValue)
        {
            row = int.MaxValue;
            column = int.MaxValue;
            return true;
        }
        row = (int)parsedRow;
        column = (int)parsedColumn;
        return row > 0 && column > 0;
    }
}

internal sealed class OfficePreviewProvider(IFileSystemService fileSystem) : IPreviewProvider
{
    public string Id => "builtin.office";
    public int Priority => 118;
    public bool CanHandle(FileItem item) =>
        item.Kind == FileKind.Document && FileKindClassifier.IsOfficeDocument(item);

    public async Task<PreviewContent> LoadAsync(FileItem item, CancellationToken cancellationToken)
    {
        try
        {
            var document = await OfficeDocumentLoader.LoadAsync(
                fileSystem, item, OfficeDocumentLoader.QuickPreviewMaxBytes, cancellationToken).ConfigureAwait(true);
            return new PreviewContent(Office: document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new PreviewContent(Message: $"无法读取此 Office 文档：{ex.Message} 可先用系统应用打开。");
        }
    }
}
