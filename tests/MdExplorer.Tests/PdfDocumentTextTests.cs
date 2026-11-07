using MdExplorer.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.57：PDF 正文抽取。边界按 <c>docs/PDF-TEXT.md</c> 的实施口径钉——
/// 只服务全文索引、取不到就返回 null（加密/损坏/扫描件/超限），绝不让一份坏 PDF 拖垮整次重建。
/// </summary>
public sealed class PdfDocumentTextTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eidoria-pdf-" + Guid.NewGuid().ToString("N"));

    public PdfDocumentTextTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void TextLayerIsExtracted()
    {
        var path = Path.Combine(_root, "text.pdf");
        TestPdf.Write(path, "Quarterly bonds and interest rates");

        var text = PdfDocumentText.TryExtract(path);

        Assert.NotNull(text);
        Assert.Contains("bonds", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyPdfExtensionsAreClaimed()
    {
        Assert.True(PdfDocumentText.IsSupportedExtension(".pdf"));
        Assert.True(PdfDocumentText.IsSupportedExtension(".PDF"));
        Assert.False(PdfDocumentText.IsSupportedExtension(".docx"));
        Assert.False(PdfDocumentText.IsSupportedExtension(null));
    }

    [Fact]
    public void PageWithoutTextLayerYieldsNullSoScansAreHonest()
    {
        // 扫描件的页面只有图、没有文字层：索引里就不该有它，而不是塞一段空白进去。
        Assert.Null(PdfDocumentText.TryExtract(TestPdf.Build(hasText: false)));
    }

    [Fact]
    public void BrokenOrMissingFileYieldsNull()
    {
        var broken = Path.Combine(_root, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.4\nthis is not a real pdf");
        Assert.Null(PdfDocumentText.TryExtract(broken));

        Assert.Null(PdfDocumentText.TryExtract(Path.Combine(_root, "missing.pdf")));
        Assert.Null(PdfDocumentText.TryExtract(Array.Empty<byte>()));
    }

    [Fact]
    public void MultiPageDocumentsAreJoinedInReadingOrder()
    {
        var text = PdfDocumentText.TryExtract(TestPdf.Build(hasText: true, pages: 3));

        Assert.NotNull(text);
        var first = text.IndexOf("firstpage", StringComparison.Ordinal);
        var last = text.IndexOf("thirdpage", StringComparison.Ordinal);
        Assert.True(first >= 0, $"第一页正文缺失：{text}");
        Assert.True(last > first, $"页序不对：{text}");
    }
}

/// <summary>测试用的最小 PDF 生成器：正文用标准字体写进去，免去在仓库里存二进制样本。</summary>
internal static class TestPdf
{
    internal static byte[] Build(bool hasText, int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var index = 1; index <= pages; index++)
        {
            var page = builder.AddPage(PageSize.A4);
            if (!hasText)
                continue;
            page.AddText($"{Ordinal(index)}page bonds and interest rates", 12, new PdfPoint(30, 760), font);
        }

        return builder.Build();
    }

    internal static void Write(string path, string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(30, 760), font);
        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>第 1/2/3 页各写一个可辨识的词：firstpage / secondpage / thirdpage。</summary>
    private static string Ordinal(int index) => index switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        _ => "page" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
