namespace MdExplorer.Domain;

/// <summary>
/// 列表/网格缩略图的内容来源。概念稿 §3.1 要求「代码、PDF、图片用真实内容微缩预览，
/// 其余类型才用图标井」；代码微缩渲染成本高、密排小字在小尺寸下也读不出内容，
/// 当前只对图片与 PDF 首页兑现真实内容，其余继续图标井。
/// </summary>
public enum ListingThumbnailStyle
{
    /// <summary>不提供真实内容缩略图，继续显示类型图标井。</summary>
    None,

    /// <summary>直接解码位图片。</summary>
    Image,

    /// <summary>用 PDFium 光栅化第 1 页作为缩略图。</summary>
    PdfFirstPage,
}

public static class ListingThumbnails
{
    /// <summary>桌面端参与缩略图的 PDF 体积上限：再大会在枚举目录时掏出过多内存。</summary>
    public const long PdfDesktopMaxBytes = 32L * 1024 * 1024;

    /// <summary>Android 端的对应上限更紧：移动设备的内存与 I/O 预算都更窄。</summary>
    public const long PdfMobileMaxBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 决定一个列表条目走哪种缩略图路线。
    /// <paramref name="pdfRasterAvailable"/> 为假（如 Android &lt; API 31，PDFium 资产不可用）时
    /// PDF 一律回落图标井，且不写负缓存——升级系统后应当能立刻拿到首页。
    /// 体积超限同样回落且不缓存失败：键里带大小，文件一改自然重判。
    /// </summary>
    public static ListingThumbnailStyle Decide(
        FileKind kind,
        string? extension,
        long? length,
        bool pdfRasterAvailable)
    {
        if (kind == FileKind.Image)
            return ListingThumbnailStyle.Image;
        var isPdf = kind == FileKind.Document
            && string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf || !pdfRasterAvailable)
            return ListingThumbnailStyle.None;
        if (length is { } known && known > PdfMaxBytes())
            return ListingThumbnailStyle.None;
        return ListingThumbnailStyle.PdfFirstPage;
    }

    /// <summary>当前平台的 PDF 缩略图体积上限（导出给 UI 有界读取复用同一口径）。</summary>
    public static long PdfMaxBytes() =>
        OperatingSystem.IsAndroid() ? PdfMobileMaxBytes : PdfDesktopMaxBytes;
}
