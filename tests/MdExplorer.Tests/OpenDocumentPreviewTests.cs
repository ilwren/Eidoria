using System.IO.Compression;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.UI.Preview;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// OpenDocument 内置只读预览：标题/列表/表格的结构还原、平面 XML、重复行列、备注剔除、
/// DTD 拒绝与字符预算截断。它读的是 ZIP + XML，不依赖任何第三方渲染器。
/// </summary>
public sealed class OpenDocumentPreviewTests
{
    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
        "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    private static string Package(string body, string officeBody = "text") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {Namespaces} office:version=\"1.2\">" +
        $"<office:body><office:{officeBody}>{body}</office:{officeBody}></office:body></office:document-content>";

    private static string Flat(string body, string officeBody = "text") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Namespaces} office:mimetype=\"application/vnd.oasis.opendocument.text\">" +
        $"<office:body><office:{officeBody}>{body}</office:{officeBody}></office:body></office:document>";

    private static byte[] Archive(string contentXml)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("content.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(contentXml));
        }

        return memory.ToArray();
    }

    [Fact]
    public void Text_KeepsHeadingLevelsListsAndTables()
    {
        const string body = """
            <text:h text:outline-level="1">季度总结</text:h>
            <text:h text:outline-level="3">交付</text:h>
            <text:p>本季度完成 <text:span text:style-name="T1">三项</text:span> 里程碑。</text:p>
            <text:list>
              <text:list-item><text:p>第一项</text:p></text:list-item>
              <text:list-item>
                <text:p>第二项</text:p>
                <text:list><text:list-item><text:p>子项</text:p></text:list-item></text:list>
              </text:list-item>
            </text:list>
            <table:table table:name="预算">
              <table:table-row>
                <table:table-cell><text:p>项目</text:p></table:table-cell>
                <table:table-cell><text:p>金额</text:p></table:table-cell>
              </table:table-row>
              <table:table-row>
                <table:table-cell><text:p>服务器</text:p></table:table-cell>
                <table:table-cell office:value-type="float" office:value="1280"><text:p>1280</text:p></table:table-cell>
              </table:table-row>
            </table:table>
            """;

        var preview = OpenDocumentReader.Read(Archive(Package(body)), ".odt");

        Assert.Equal(OpenDocumentFormat.Text, preview.Format);
        Assert.False(preview.Truncated);
        Assert.Contains("# 季度总结", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("### 交付", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("本季度完成 三项 里程碑。", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("- 第一项", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("- 第二项", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("  - 子项", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("| 项目 | 金额 |", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("| 服务器 | 1280 |", preview.Markdown, StringComparison.Ordinal);
        Assert.True(preview.Markdown.StartsWith("> OpenDocument 只读预览", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_FlatXmlNeedsNoArchive()
    {
        const string body = "<text:h text:outline-level=\"2\">平面文档</text:h><text:p>单文件 XML 也能读。</text:p>";

        var preview = OpenDocumentReader.Read(Encoding.UTF8.GetBytes(Flat(body)), ".fodt");

        Assert.Contains("## 平面文档", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("单文件 XML 也能读。", preview.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Spreadsheet_ExpandsRepeatedColumnsAndKeepsSheetNames()
    {
        const string body = """
            <table:table table:name="一季度">
              <table:table-row>
                <table:table-cell><text:p>城市</text:p></table:table-cell>
                <table:table-cell table:number-columns-repeated="2"><text:p>销量</text:p></table:table-cell>
              </table:table-row>
              <table:table-row table:number-rows-repeated="2">
                <table:table-cell><text:p>上海</text:p></table:table-cell>
                <table:table-cell office:value-type="float" office:value="42"><text:p>42</text:p></table:table-cell>
                <table:table-cell office:value-type="float" office:value="7"/>
              </table:table-row>
            </table:table>
            <table:table table:name="二季度">
              <table:table-row><table:table-cell><text:p>合计</text:p></table:table-cell></table:table-row>
            </table:table>
            """;

        var preview = OpenDocumentReader.Read(Archive(Package(body, "spreadsheet")), ".ods");

        Assert.Equal(OpenDocumentFormat.Spreadsheet, preview.Format);
        Assert.Contains("## 工作表 · 一季度", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("## 工作表 · 二季度", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("| 城市 | 销量 | 销量 |", preview.Markdown, StringComparison.Ordinal);
        // 重复行展开成两行，缺失显示文本的单元格退回 office:value。
        Assert.Contains("| 上海 | 42 | 7 |", preview.Markdown, StringComparison.Ordinal);
        Assert.Equal(2, preview.Markdown.Split("| 上海 | 42 | 7 |", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Presentation_OrdersSlidesAndSkipsSpeakerNotes()
    {
        const string body = """
            <draw:page draw:name="封面">
              <draw:frame><draw:text-box><text:p>万象屿</text:p></draw:text-box></draw:frame>
              <presentation:notes><draw:frame><draw:text-box><text:p>备注不要出现</text:p></draw:text-box></draw:frame></presentation:notes>
            </draw:page>
            <draw:page draw:name="第二页">
              <draw:frame><draw:text-box><text:p>要点一</text:p><text:p>要点二</text:p></draw:text-box></draw:frame>
            </draw:page>
            """;

        var preview = OpenDocumentReader.Read(Archive(Package(body, "presentation")), ".odp");

        Assert.Equal(OpenDocumentFormat.Presentation, preview.Format);
        Assert.Contains("## 第 1 页 · 封面", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("## 第 2 页 · 第二页", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("- 万象屿", preview.Markdown, StringComparison.Ordinal);
        Assert.Contains("- 要点二", preview.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("备注不要出现", preview.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void LongDocument_IsTruncatedWithAnExplicitNote()
    {
        var body = new StringBuilder();
        for (var index = 0; index < 2600; index++)
            body.Append("<text:p>").Append(new string('长', 120)).Append(index).Append("</text:p>");

        var preview = OpenDocumentReader.Read(Archive(Package(body.ToString())), ".odt");

        Assert.True(preview.Truncated);
        Assert.Contains("预览已截断", preview.Markdown, StringComparison.Ordinal);
        Assert.True(preview.CharacterCount <= 200_100, preview.CharacterCount.ToString());
    }

    [Fact]
    public void DtdAndMissingContentAreRejected()
    {
        var includeDtd = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY bar \"baz\">]>" +
                         $"<office:document-content {Namespaces}><office:body><office:text/></office:body></office:document-content>";
        Assert.Throws<InvalidDataException>(() => OpenDocumentReader.Read(Archive(includeDtd), ".odt"));

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("styles.xml").Open().Dispose();
        Assert.Throws<InvalidDataException>(() => OpenDocumentReader.Read(memory.ToArray(), ".odt"));

        Assert.Throws<InvalidDataException>(() => OpenDocumentReader.Read(Array.Empty<byte>(), ".txt"));
    }

    [Fact]
    public async Task Provider_RendersMarkdownForOdtFileOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eidoria-odf-{Guid.NewGuid():N}.odt");
        await File.WriteAllBytesAsync(path, Archive(Package(
            "<text:h text:outline-level=\"1\">磁盘上的 ODT</text:h><text:p>正文。</text:p>")),
            TestContext.Current.CancellationToken);
        try
        {
            var item = new FileItem(
                FileLocator.FromPath(path), Path.GetFileName(path), FileKind.Document,
                new FileInfo(path).Length, DateTimeOffset.Now, false, false, ".odt");
            var registry = BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService());
            var provider = registry.Resolve(item);
            Assert.NotNull(provider);
            Assert.Equal("builtin.opendocument", provider!.Id);

            var content = await provider.LoadAsync(item, TestContext.Current.CancellationToken);
            Assert.NotNull(content.Markdown);
            Assert.Contains("# 磁盘上的 ODT", content.Markdown!, StringComparison.Ordinal);
            Assert.Null(content.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Provider_IsNotUsedForOoxml()
    {
        var registry = BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService());
        foreach (var name in new[] { "报告.docx", "预算.xlsx", "幻灯片.pptx", "说明.txt" })
        {
            var item = new FileItem(
                FileLocator.FromPath("/data/" + name), name, FileKind.Document, 128,
                DateTimeOffset.UnixEpoch, false, false, Path.GetExtension(name));
            Assert.NotEqual("builtin.opendocument", registry.Resolve(item)?.Id);
        }
    }
}
