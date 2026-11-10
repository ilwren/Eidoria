using MdExplorer.Domain;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// 概念稿 §3.1：图片与 PDF 用真实内容微缩预览，其余类型仍是图标井。
/// 这里只测「该不该去取图」的判定本身；解码/光栅化与缓存键在 ThumbnailCacheTests 覆盖。
/// </summary>
public sealed class ListingThumbnailEligibilityTests
{
    [Fact]
    public void Images_AlwaysDecode()
    {
        Assert.Equal(
            ListingThumbnailStyle.Image,
            ListingThumbnails.Decide(FileKind.Image, ".png", 512, pdfRasterAvailable: false));
        Assert.Equal(
            ListingThumbnailStyle.Image,
            ListingThumbnails.Decide(FileKind.Image, ".jpg", null, pdfRasterAvailable: true));
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".PDF")]
    public void Pdf_UnderLimit_RendersFirstPage(string extension)
    {
        var style = ListingThumbnails.Decide(
            FileKind.Document, extension, 2 * 1024 * 1024, pdfRasterAvailable: true);
        Assert.Equal(ListingThumbnailStyle.PdfFirstPage, style);
    }

    [Fact]
    public void Pdf_UnknownLength_StillAttempts()
    {
        // 长度未知（某些存储提供者报不出大小）时按乐观处理：读取环路仍会自己限流。
        Assert.Equal(
            ListingThumbnailStyle.PdfFirstPage,
            ListingThumbnails.Decide(FileKind.Document, ".pdf", null, pdfRasterAvailable: true));
    }

    [Fact]
    public void Pdf_OverDesktopLimit_FallsBackToGlyph()
    {
        Assert.Equal(
            ListingThumbnailStyle.None,
            ListingThumbnails.Decide(
                FileKind.Document, ".pdf", ListingThumbnails.PdfDesktopMaxBytes + 1, pdfRasterAvailable: true));
    }

    [Fact]
    public void Pdf_AtExactLimit_StillEligible()
    {
        Assert.Equal(
            ListingThumbnailStyle.PdfFirstPage,
            ListingThumbnails.Decide(
                FileKind.Document, ".pdf", ListingThumbnails.PdfDesktopMaxBytes, pdfRasterAvailable: true));
    }

    [Fact]
    public void Pdf_WithoutPdfium_StaysGlyph_AndIsNotPermanent()
    {
        // Android < API 31：直接给图标井；不写负缓存，升级后要立刻能取到首页。
        Assert.Equal(
            ListingThumbnailStyle.None,
            ListingThumbnails.Decide(FileKind.Document, ".pdf", 1024, pdfRasterAvailable: false));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".txt")]
    public void NonPdfDocuments_KeepIconWell(string extension)
    {
        var kind = FileKindClassifier.Classify("report" + extension);
        Assert.Equal(
            ListingThumbnailStyle.None,
            ListingThumbnails.Decide(kind, extension, 1024, pdfRasterAvailable: true));
    }

    [Theory]
    [InlineData(FileKind.Folder)]
    [InlineData(FileKind.Video)]
    [InlineData(FileKind.Audio)]
    [InlineData(FileKind.Code)]
    [InlineData(FileKind.Archive)]
    public void OtherKinds_KeepIconWell(FileKind kind)
    {
        Assert.Equal(
            ListingThumbnailStyle.None,
            ListingThumbnails.Decide(kind, ".md", 128, pdfRasterAvailable: true));
    }
}
