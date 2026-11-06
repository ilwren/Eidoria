using System.IO.Compression;
using System.Text;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.47：Office / OpenDocument 正文抽取。关键词索引只认纯文本时，用户的 Word/PPT/Excel
/// 在「全文」里等于不存在；这里把正文抽出来喂给 FTS5，同时守住三条线——
/// 不因坏文档整体失败、不为压缩炸弹展开整包、不把乱码塞进索引。
/// </summary>
public sealed class OfficeDocumentTextTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eidoria-officetext-" + Guid.NewGuid().ToString("N"));

    public OfficeDocumentTextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static void WriteZip(string path, params (string Name, string Content)[] parts)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    [Fact]
    public void DocxParagraphsBecomeSearchableText()
    {
        var path = PathFor("report.docx");
        WriteZip(path,
            ("word/document.xml",
             """
             <?xml version="1.0" encoding="UTF-8"?>
             <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
               <w:p><w:r><w:t>季度复盘：退货率下降 12%</w:t></w:r></w:p>
               <w:p><w:r><w:t>下一步聚焦供货周期</w:t></w:r></w:p>
             </w:body></w:document>
             """));

        var text = OfficeDocumentText.TryExtract(path);

        Assert.NotNull(text);
        Assert.Contains("退货率下降", text, StringComparison.Ordinal);
        Assert.Contains("供货周期", text, StringComparison.Ordinal);
        // 段落之间补换行，避免两段粘成一个搜索不到的长词。
        Assert.Contains("12%\n下一步", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PptxSlidesAreReadInNumericOrder()
    {
        var path = PathFor("deck.pptx");
        WriteZip(path,
            ("ppt/slides/slide10.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>第十张</a:t></p:sld>"),
            ("ppt/slides/slide2.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>第二张</a:t></p:sld>"));

        var text = OfficeDocumentText.TryExtract(path);

        Assert.NotNull(text);
        // 按数字而不是字典序：slide2 在 slide10 之前。
        Assert.True(text!.IndexOf("第二张", StringComparison.Ordinal) <
                    text.IndexOf("第十张", StringComparison.Ordinal));
    }

    [Fact]
    public void XlsxSharedStringsAreIndexed()
    {
        var path = PathFor("ledger.xlsx");
        WriteZip(path,
            ("xl/sharedStrings.xml",
             "<sst xmlns=\"s\"><si><t>差旅报销</t></si><si><t>办公用品</t></si></sst>"));

        var text = OfficeDocumentText.TryExtract(path);

        Assert.NotNull(text);
        Assert.Contains("差旅报销", text, StringComparison.Ordinal);
        Assert.Contains("办公用品", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDocumentContentXmlIsIndexed()
    {
        var path = PathFor("notes.odt");
        WriteZip(path,
            ("content.xml",
             "<office:document-content xmlns:office=\"o\" xmlns:text=\"t\"><text:p>会议纪要</text:p></office:document-content>"));

        var text = OfficeDocumentText.TryExtract(path);

        Assert.Equal("会议纪要", text);
    }

    [Fact]
    public void FlatOpenDocumentXmlIsIndexedWithoutUnzip()
    {
        var path = PathFor("flat.fodt");
        File.WriteAllText(path,
            "<office:document xmlns:office=\"o\" xmlns:text=\"t\"><text:p>平面 XML 正文</text:p></office:document>");

        Assert.Equal("平面 XML 正文", OfficeDocumentText.TryExtract(path));
    }

    [Fact]
    public void UnsupportedExtensionIsRejectedWithoutOpening()
    {
        var path = PathFor("plain.txt");
        File.WriteAllText(path, "这不是 Office 文档");

        Assert.False(OfficeDocumentText.IsSupportedExtension(".txt"));
        Assert.Null(OfficeDocumentText.TryExtract(path));
    }

    [Fact]
    public void CorruptPackageYieldsNullInsteadOfThrowing()
    {
        var path = PathFor("broken.docx");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Null(OfficeDocumentText.TryExtract(path));
    }

    [Fact]
    public void DtdIsRefused()
    {
        // 用户磁盘上的文档可以带 DOCTYPE；索引不该在这里打开 XXE 的口子。
        var path = PathFor("evil.fodt");
        File.WriteAllText(path,
            """
            <?xml version="1.0"?>
            <!DOCTYPE doc [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <doc>&xxe;</doc>
            """);

        Assert.Null(OfficeDocumentText.TryExtract(path));
    }

    [Fact]
    public void DocumentWithoutTextYieldsNull()
    {
        var path = PathFor("empty.docx");
        WriteZip(path, ("word/document.xml", "<w:document xmlns:w=\"w\"><w:body /></w:document>"));

        Assert.Null(OfficeDocumentText.TryExtract(path));
    }

    [Fact]
    public async Task ContentIndexIndexesOfficeDocuments()
    {
        var root = Path.Combine(_root, "root");
        Directory.CreateDirectory(root);
        WriteZip(Path.Combine(root, "meeting.docx"),
            ("word/document.xml",
             "<w:document xmlns:w=\"w\"><w:body><w:p><w:r><w:t>预算审批流程说明</w:t></w:r></w:p></w:body></w:document>"));

        var index = new SqliteContentIndex(Path.Combine(_root, "content.db"), new LocalTextFileService());
        var stats = await index.RebuildAsync([root], null, CancellationToken.None);
        Assert.Equal(1, stats.DocumentCount);

        var hits = await index.SearchAsync("预算审批", 10, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal("meeting.docx", hits[0].Name);
        Assert.Contains("预算审批", hits[0].Snippet, StringComparison.Ordinal);
    }
}
