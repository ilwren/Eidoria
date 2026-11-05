namespace MdExplorer.Platform.Abstractions;

/// <summary>
/// 缩略图档位（三档）。像素宽是解码目标宽度，也是缓存键的一部分——
/// 换档必然换缓存，不会出现「选了小档却拿到大图」或「明明换了大档还是糊的」。
/// </summary>
public enum ThumbnailTier
{
    /// <summary>128 px：列表密度大、条目多时用。</summary>
    Small = 128,

    /// <summary>192 px：默认档。</summary>
    Medium = 192,

    /// <summary>288 px：网格视图与高分屏用。</summary>
    Large = 288,
}

/// <summary>缩略图档位的字符串口径（设置文件里存字符串，与 ThemeMode 一致）。</summary>
public static class ThumbnailTierNames
{
    public const string Small = "Small";
    public const string Medium = "Medium";
    public const string Large = "Large";

    /// <summary>解析设置值；无法识别时回落 <see cref="ThumbnailTier.Medium"/>，不抛异常。</summary>
    public static ThumbnailTier Parse(string? value) => value switch
    {
        _ when string.Equals(value, Small, StringComparison.OrdinalIgnoreCase) => ThumbnailTier.Small,
        _ when string.Equals(value, Large, StringComparison.OrdinalIgnoreCase) => ThumbnailTier.Large,
        _ => ThumbnailTier.Medium,
    };

    public static string ToSettingValue(ThumbnailTier tier) => tier switch
    {
        ThumbnailTier.Small => Small,
        ThumbnailTier.Large => Large,
        _ => Medium,
    };

    /// <summary>解码目标像素宽。</summary>
    public static int Pixels(ThumbnailTier tier) => (int)tier;
}

/// <summary>
/// 一次缩略图请求。它同时是缓存键的输入：路径 + 档位 + 大小 + 修改时间。
/// 目标的修改时间或大小一变，键就变了——旧缓存自然变成未命中，不需要额外的失效逻辑。
/// </summary>
public readonly record struct ThumbnailRequest(
    string SourcePath,
    long Length,
    DateTimeOffset LastWriteTime,
    ThumbnailTier Tier);

/// <summary>缓存查询结果。</summary>
public enum ThumbnailCacheStatus
{
    /// <summary>没缓存过（需要真正解码）。</summary>
    Miss,

    /// <summary>命中：<see cref="ThumbnailLookup.Payload"/> 是编码后的图片字节。</summary>
    Hit,

    /// <summary>上次解码失败（坏图、无权限、容器不支持），不要再试——负缓存。</summary>
    KnownFailure,
}

public readonly record struct ThumbnailLookup(ThumbnailCacheStatus Status, byte[]? Payload)
{
    public static ThumbnailLookup Miss => new(ThumbnailCacheStatus.Miss, null);
    public static ThumbnailLookup KnownFailure => new(ThumbnailCacheStatus.KnownFailure, null);
    public static ThumbnailLookup Hit(byte[] payload) => new(ThumbnailCacheStatus.Hit, payload);
}

/// <summary>缩略图缓存占用（设置页展示；不含临时文件）。</summary>
public readonly record struct ThumbnailCacheStats(int Entries, int Failures, long Bytes);

/// <summary>
/// 缩略图磁盘缓存端口。实现只管「按请求键存取字节」与自我限额，
/// 不参与解码（解码要 Avalonia/Skia，属于 UI 侧），也不猜文件类型。
/// <para>
/// 契约：所有方法都允许在后台线程调用；任何 IO 错误都不得抛出——
/// 缓存坏掉最多是「这次没命中」，不能让浏览卡住或崩。
/// </para>
/// </summary>
public interface IThumbnailCache
{
    Task<ThumbnailLookup> LookupAsync(ThumbnailRequest request, CancellationToken cancellationToken);

    /// <summary>写入成功解码并编码后的图片字节（PNG）。</summary>
    Task StoreAsync(ThumbnailRequest request, byte[] encodedImage, CancellationToken cancellationToken);

    /// <summary>记下「这个文件解不出来」，避免每次翻页都重试同一张坏图。</summary>
    Task StoreFailureAsync(ThumbnailRequest request, CancellationToken cancellationToken);

    Task<ThumbnailCacheStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>清空缓存（包含负缓存标记），返回释放的字节数。</summary>
    Task<long> ClearAsync(CancellationToken cancellationToken);
}
