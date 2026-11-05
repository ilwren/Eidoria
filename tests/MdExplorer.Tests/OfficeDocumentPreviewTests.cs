using System.IO.Compression;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.UI.Preview;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.23：DOCX/PPTX/XLSX 的宿主有界预检、Provider 分发与工作台状态。</summary>
public sealed class OfficeDocumentPreviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-office-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("report.docx", true)]
    [InlineData("slides.pptx", true)]
    [InlineData("budget.xlsx", true)]
    [InlineData("legacy.doc", false)]
    [InlineData("legacy.xls", false)]
    [InlineData("legacy.ppt", false)]
    public void WorkbenchAcceptsOnlyOpenXmlOfficeFormats(string name, bool expected)
    {
        var item = new FileItem(
            FileLocator.FromPath(Path.Combine(_root, name)),
            name,
            FileKind.Document,
            128,
            DateTimeOffset.Now,
            IsHidden: false,
            IsSymlink: false,
            Path.GetExtension(name));
        Assert.Equal(expected, DocumentWorkbenchViewModel.CanOpen(item));
        Assert.Equal(expected, FileKindClassifier.IsOfficeDocument(item));
    }

    [Fact]
    public void WorkbenchAllowsReadOnlyOfficeDocumentsFromSaf()
    {
        var item = new FileItem(
            FileLocator.FromStorage("content://com.android.externalstorage.documents/tree/report.docx"),
            "report.docx",
            FileKind.Document,
            128,
            DateTimeOffset.Now,
            IsHidden: false,
            IsSymlink: false,
            ".docx");
        Assert.True(DocumentWorkbenchViewModel.CanOpen(item));
        Assert.Equal("Word 文档", FileKindClassifier.Describe(item));
    }

    [Fact]
    public async Task ProviderLoadsFactsForDocxPptxAndXlsx()
    {
        var docx = await WriteAsync("report.docx", DocxPackage());
        var pptx = await WriteAsync("slides.pptx", PptxPackage());
        var xlsx = await WriteAsync("budget.xlsx", XlsxPackage());
        var registry = BuiltIns();

        var docContent = await LoadAsync(registry, docx);
        Assert.Equal(OfficePreviewFormat.Docx, docContent.Office?.Format);
        Assert.True(docContent.Office?.Facts.ParagraphCount >= 3);
        Assert.Contains("段落", docContent.Office?.Facts.Summary, StringComparison.Ordinal);

        var pptContent = await LoadAsync(registry, pptx);
        Assert.Equal(OfficePreviewFormat.Pptx, pptContent.Office?.Format);
        Assert.Equal(2, pptContent.Office?.Facts.SlideCount);

        var xlsContent = await LoadAsync(registry, xlsx);
        Assert.Equal(OfficePreviewFormat.Xlsx, xlsContent.Office?.Format);
        Assert.Equal(["总览", "明细"], xlsContent.Office?.Facts.SheetNames);
        Assert.Equal([320d, 320d], xlsContent.Office?.Facts.SheetNominalWidths);
        Assert.Contains("2 张工作表", xlsContent.Office?.Facts.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1_000_000L, true)]
    [InlineData(24, 1_000_000L, true)]
    [InlineData(25, 1_000_000L, false)]
    [InlineData(0, 1_000_000L, true)] // 数不出页数（加密/损坏）按旧行为放行
    [InlineData(5, 40_000_000L, false)]
    public void DirectPdfControlStaysInsidePageAndByteBudgets(int pages, long bytes, bool expected)
    {
        // 直接控件按页排控件树：100+ 页 PDF 会把提交内存顶到 ~1 GB 且关闭预览后不回落，
        // 所以超过页数/字节上限一律交给 PDFium 视图。
        Assert.Equal(expected, OfficeDocumentLoader.CanUseDirectPdf(pages, bytes, 32L * 1024 * 1024));
    }

    [Fact]
    public void DirectPdfCandidateRejectsEncryptedInvalidAndOversizedPayloads()
    {
        var plain = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");
        Assert.True(OfficeDocumentLoader.IsDirectPdfCandidate(plain));
        Assert.True(OfficeDocumentLoader.TryCreateDirectPdf(
            plain, OfficeDocumentLoader.DirectPdfPreviewMaxBytes, out var document));
        Assert.Equal(OfficePreviewFormat.Pdf, document!.Format);
        Assert.Contains("直接预览", document.Facts.Summary, StringComparison.Ordinal);

        var encrypted = Encoding.ASCII.GetBytes("%PDF-1.7\ntrailer << /Encrypt 9 0 R >>\n");
        Assert.False(OfficeDocumentLoader.IsDirectPdfCandidate(encrypted));
        Assert.Throws<InvalidDataException>(() =>
            OfficeDocumentLoader.CreateDirectPdf(encrypted, OfficeDocumentLoader.DirectPdfPreviewMaxBytes));

        Assert.False(OfficeDocumentLoader.IsDirectPdfCandidate(Encoding.ASCII.GetBytes("not pdf")));
        Assert.False(OfficeDocumentLoader.TryCreateDirectPdf(plain, plain.Length - 1, out var oversized));
        Assert.Null(oversized);
    }

    [Fact]
    public async Task WorkbenchPrefersDirectPdfControl()
    {
        var start = Directory.CreateDirectory(Path.Combine(_root, "pdf-start")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(_root, "pdf-data")).FullName;
        await File.WriteAllBytesAsync(
            Path.Combine(start, "direct.pdf"),
            Encoding.ASCII.GetBytes("%PDF-1.7\n% direct-control candidate\n"),
            TestContext.Current.CancellationToken);

        var shell = AppHost.CreateShell(AppHost.Build(data, start));
        shell.Browser.SetWatchEnabled(false);
        await shell.OpenDocumentWorkbenchAsync(await StatAsync(Path.Combine(start, "direct.pdf")));

        var workbench = shell.DocumentWorkbench;
        Assert.True(workbench.IsDirectPdf);
        Assert.Equal(PdfViewEngine.Direct, workbench.PdfEngine);
        Assert.True(workbench.ShowOfficeWorkbench);
        Assert.False(workbench.ShowPdfWorkbench);
        Assert.True(workbench.CanSwitchToRasterPdf);
        Assert.Contains("PDF 直接预览", workbench.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderRejectsMalformedEncryptedAndOversizedPackages()
    {
        var malformed = Path.Combine(_root, "broken.docx");
        await File.WriteAllTextAsync(malformed, "not an office package", TestContext.Current.CancellationToken);
        var malformedContent = await LoadAsync(BuiltIns(), await StatAsync(malformed));
        Assert.Null(malformedContent.Office);
        Assert.Contains("无法读取", malformedContent.Message, StringComparison.Ordinal);

        var encrypted = await WriteAsync("encrypted.xlsx", Package(
            ("[Content_Types].xml", ContentTypes("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", "/xl/workbook.xml")),
            ("EncryptionInfo", "encrypted"),
            ("EncryptedPackage", "payload")));
        var encryptedContent = await LoadAsync(BuiltIns(), encrypted);
        Assert.Null(encryptedContent.Office);
        Assert.Contains("已加密", encryptedContent.Message, StringComparison.Ordinal);

        var sparse = await WriteAsync("sparse.xlsx", XlsxPackage(sparseCell: "ZZ100000"));
        var sparseContent = await LoadAsync(BuiltIns(), sparse);
        Assert.Null(sparseContent.Office);
        Assert.Contains("范围过大", sparseContent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkbenchOpensOfficeDocumentsReadOnlyAndSwitchesSheets()
    {
        var start = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(start, "budget.xlsx"), XlsxPackage(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(start, "slides.pptx"), PptxPackage(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(start, "report.docx"), DocxPackage(), TestContext.Current.CancellationToken);

        var shell = AppHost.CreateShell(AppHost.Build(data, start));
        shell.Browser.SetWatchEnabled(false);
        var workbench = shell.DocumentWorkbench;

        await shell.OpenDocumentWorkbenchAsync(await StatAsync(Path.Combine(start, "budget.xlsx")));
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsOfficeDocument);
        Assert.True(workbench.UseOfficeViewer);
        Assert.True(workbench.IsOfficeExcel);
        Assert.Equal(2, workbench.OfficeSheetCount);
        Assert.Equal("总览", workbench.CurrentOfficeSheetName);
        workbench.NextOfficeSheetCommand.Execute(null);
        Assert.Equal(1, workbench.OfficeSheetIndex);
        Assert.Equal("明细", workbench.CurrentOfficeSheetName);
        Assert.False(workbench.IsDirty);

        await shell.OpenDocumentWorkbenchAsync(await StatAsync(Path.Combine(start, "slides.pptx")));
        Assert.Equal(OfficePreviewFormat.Pptx, workbench.OfficeDocument?.Format);
        Assert.Equal(2, workbench.OfficePageCount);
        Assert.Contains("PowerPoint", workbench.StatusText, StringComparison.Ordinal);

        await shell.OpenDocumentWorkbenchAsync(await StatAsync(Path.Combine(start, "report.docx")));
        Assert.Equal(OfficePreviewFormat.Docx, workbench.OfficeDocument?.Format);
        Assert.True(workbench.OfficeDocument?.Facts.ParagraphCount >= 3);
        Assert.Contains("Word", workbench.StatusText, StringComparison.Ordinal);
    }

    private static PreviewProviderRegistry BuiltIns() =>
        BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService());

    private static async Task<PreviewContent> LoadAsync(PreviewProviderRegistry registry, FileItem item)
    {
        var provider = registry.Resolve(item);
        Assert.NotNull(provider);
        Assert.Equal("builtin.office", provider!.Id);
        return await provider.LoadAsync(item, TestContext.Current.CancellationToken);
    }

    private async Task<FileItem> WriteAsync(string name, byte[] package)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, package, TestContext.Current.CancellationToken);
        return await StatAsync(path);
    }

    private static async Task<FileItem> StatAsync(string path)
    {
        var item = await new LocalFileSystemService().StatAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        return item;
    }

    private static byte[] DocxPackage() => Package(
        ("[Content_Types].xml", ContentTypes("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", "/word/document.xml")),
        ("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>季度报告</w:t></w:r></w:p>
                <w:p><w:r><w:t>第一段正文。</w:t></w:r></w:p>
                <w:p><w:r><w:t>第二段正文。</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              </w:body>
            </w:document>
            """));

    private static byte[] PptxPackage() => Package(
        ("[Content_Types].xml", ContentTypes("application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml", "/ppt/presentation.xml")),
        ("ppt/presentation.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldSz cx="12192000" cy="6858000" type="wide" />
              <p:sldIdLst>
                <p:sldId id="256" r:id="rId1" />
                <p:sldId id="257" r:id="rId2" />
              </p:sldIdLst>
            </p:presentation>
            """),
        ("ppt/_rels/presentation.xml.rels", Relationships(
            ("rId1", "slides/slide1.xml"), ("rId2", "slides/slide2.xml"))),
        ("ppt/slides/slide1.xml", Slide("第一张")),
        ("ppt/slides/slide2.xml", Slide("第二张")));

    private static string Slide(string title) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:cSld><p:spTree>
            <p:sp><p:spPr><a:xfrm><a:off x="0" y="0" /><a:ext cx="4000000" cy="1000000" /></a:xfrm></p:spPr>
              <p:txBody><a:p><a:r><a:rPr lang="zh-CN" sz="2400" /><a:t>{title}</a:t></a:r></a:p></p:txBody>
            </p:sp>
          </p:spTree></p:cSld>
        </p:sld>
        """;

    private static byte[] XlsxPackage(string sparseCell = "B2") => Package(
        ("[Content_Types].xml", ContentTypes("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", "/xl/workbook.xml")),
        ("xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="总览" sheetId="1" r:id="rId1" />
                <sheet name="明细" sheetId="2" r:id="rId2" />
              </sheets>
            </workbook>
            """),
        ("xl/_rels/workbook.xml.rels", Relationships(
            ("rId1", "worksheets/sheet1.xml"), ("rId2", "worksheets/sheet2.xml"))),
        ("xl/worksheets/sheet1.xml", Worksheet(sparseCell)),
        ("xl/worksheets/sheet2.xml", Worksheet("B2")));

    private static string Worksheet(string secondCell) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is><t>项目</t></is></c></row>
            <row r="2"><c r="{secondCell}"><v>42</v></c></row>
          </sheetData>
        </worksheet>
        """;

    private static string ContentTypes(string contentType, string partName) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="xml" ContentType="application/xml" />
          <Override PartName="{partName}" ContentType="{contentType}" />
        </Types>
        """;

    private static string Relationships(params (string Id, string Target)[] values)
    {
        var items = string.Join("\n", values.Select(value =>
            $"<Relationship Id=\"{value.Id}\" Target=\"{value.Target}\" />"));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            {items}
            </Relationships>
            """;
    }

    private static byte[] Package(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = entry.Open();
                writer.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return stream.ToArray();
    }
}
