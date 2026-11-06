namespace MdExplorer.Domain;

/// <summary>
/// 搜索结果的「大小 / 修改时间」筛选（v0.54）。放在 Domain 是因为它是纯规则：
/// 索引快照、SQLite 全文库、AI 语义结果三条通道都要用同一套判断，口径不能各写一遍。
/// <para>
/// 边界含义：<see cref="MinBytes"/> 为**下界含**、<see cref="MaxBytes"/> 为**上界不含**
/// （「100 KB–1 MB」不该把 1 MB 也算进来）；<see cref="ModifiedAfter"/> 是**不早于**（&gt;=）、
/// <see cref="ModifiedBefore"/> 是**不晚于**（&lt;=）。
/// </para>
/// </summary>
public sealed record SearchFilter(
    long? MinBytes = null,
    long? MaxBytes = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null)
{
    /// <summary>什么都不筛。</summary>
    public static SearchFilter None { get; } = new();

    public bool IsEmpty =>
        MinBytes is null && MaxBytes is null && ModifiedAfter is null && ModifiedBefore is null;

    /// <summary>
    /// 单条结果是否通过筛选。<paramref name="length"/> 为 null（文件夹/未知大小）时，
    /// 只要设了大小界限就算不过——宁可不显示，也不给一个「看起来满足」的假结果。
    /// </summary>
    public bool Matches(long? length, DateTimeOffset lastWrite)
    {
        if (MinBytes is { } min && (length is not { } size || size < min))
            return false;
        if (MaxBytes is { } max && (length is not { } size2 || size2 >= max))
            return false;
        if (ModifiedAfter is { } after && lastWrite < after)
            return false;
        if (ModifiedBefore is { } before && lastWrite > before)
            return false;
        return true;
    }
}

/// <summary>
/// 界面上那两排筛选胶囊（大小 / 修改时间）到 <see cref="SearchFilter"/> 的映射。
/// 键值用字符串而不是枚举，是为了和现有「类型」筛选、设置存储的写法保持一致，
/// 也让非法值（手改设置、旧版本残留）能安全回落到「全部」。
/// </summary>
public static class SearchFilterPresets
{
    public const string Any = "Any";

    // 大小档位
    public const string SizeSmall = "Small";    // < 100 KB
    public const string SizeMedium = "Medium";  // 100 KB – 1 MB
    public const string SizeLarge = "Large";    // 1 – 10 MB
    public const string SizeHuge = "Huge";      // >= 10 MB

    // 时间档位
    public const string DateToday = "Today";
    public const string DateWeek = "Week";
    public const string DateMonth = "Month";
    public const string DateYear = "Year";

    private const long Kilobyte = 1024;
    private const long Megabyte = 1024 * 1024;

    /// <summary>
    /// 组合两档筛选；都为「全部」时返回 null（调用方据此完全不筛，连一次比较都不做）。
    /// <paramref name="now"/> 由调用方传入，测试才能钉住边界。
    /// </summary>
    public static SearchFilter? Build(string? sizeKey, string? dateKey, DateTimeOffset now)
    {
        long? min = null;
        long? max = null;
        switch (Normalize(sizeKey, [SizeSmall, SizeMedium, SizeLarge, SizeHuge]))
        {
            case SizeSmall:
                max = 100 * Kilobyte;
                break;
            case SizeMedium:
                min = 100 * Kilobyte;
                max = Megabyte;
                break;
            case SizeLarge:
                min = Megabyte;
                max = 10 * Megabyte;
                break;
            case SizeHuge:
                min = 10 * Megabyte;
                break;
        }

        DateTimeOffset? after = Normalize(dateKey, [DateToday, DateWeek, DateMonth, DateYear]) switch
        {
            DateToday => new DateTimeOffset(now.Date, now.Offset),
            DateWeek => now.AddDays(-7),
            DateMonth => now.AddDays(-30),
            DateYear => now.AddDays(-365),
            _ => null,
        };

        if (min is null && max is null && after is null)
            return null;
        return new SearchFilter(min, max, after);
    }

    /// <summary>把任意输入收敛到白名单里的某个值，认不出来就是「全部」。</summary>
    public static string Normalize(string? value, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Any;
        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return Any;
    }
}
