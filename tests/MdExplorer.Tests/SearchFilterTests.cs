using MdExplorer.Domain;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.54 搜索筛选：界面上那两排胶囊到 <see cref="SearchFilter"/> 的映射与边界判断。
/// 边界都写成「含下界、不含上界」，测试就是为了让这条口径以后不会悄悄变。
/// </summary>
public sealed class SearchFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void SizeBandsAreHalfOpen()
    {
        var small = SearchFilterPresets.Build(SearchFilterPresets.SizeSmall, SearchFilterPresets.Any, Now);
        Assert.NotNull(small);
        Assert.Equal(102_400L, small.MaxBytes);
        Assert.Null(small.MinBytes);
        Assert.True(small.Matches(0, Now));
        Assert.True(small.Matches(100 * 1024 - 1, Now));
        Assert.False(small.Matches(100 * 1024, Now));

        var medium = SearchFilterPresets.Build(SearchFilterPresets.SizeMedium, SearchFilterPresets.Any, Now)!;
        Assert.Equal(102_400L, medium.MinBytes);
        Assert.Equal(1_048_576L, medium.MaxBytes);
        Assert.True(medium.Matches(100 * 1024, Now));
        Assert.False(medium.Matches(1024 * 1024, Now));

        var huge = SearchFilterPresets.Build(SearchFilterPresets.SizeHuge, SearchFilterPresets.Any, Now)!;
        Assert.Null(huge.MaxBytes);
        Assert.False(huge.Matches(10 * 1024 * 1024 - 1, Now));
        Assert.True(huge.Matches(10 * 1024 * 1024, Now));
    }

    [Fact]
    public void UnknownLengthNeverMatchesASizeBound()
    {
        var filter = SearchFilterPresets.Build(SearchFilterPresets.SizeLarge, SearchFilterPresets.Any, Now)!;

        // 文件夹（Length 为 null）在设了大小界限时一律不算通过：宁可不在结果里，
        // 也不给一个「看起来满足」的假结果。
        Assert.False(filter.Matches(null, Now));
        Assert.True(filter.Matches(2 * 1024 * 1024, Now));
    }

    [Fact]
    public void DateBandsAnchorToTheCallersClock()
    {
        var today = SearchFilterPresets.Build(SearchFilterPresets.Any, SearchFilterPresets.DateToday, Now)!;
        Assert.Equal(new DateTimeOffset(Now.Date, Now.Offset), today.ModifiedAfter);
        Assert.True(today.Matches(1, Now));
        Assert.False(today.Matches(1, Now.AddDays(-1)));

        var week = SearchFilterPresets.Build(SearchFilterPresets.Any, SearchFilterPresets.DateWeek, Now)!;
        Assert.Equal(Now.AddDays(-7), week.ModifiedAfter);

        var month = SearchFilterPresets.Build(SearchFilterPresets.Any, SearchFilterPresets.DateMonth, Now)!;
        Assert.Equal(Now.AddDays(-30), month.ModifiedAfter);

        var year = SearchFilterPresets.Build(SearchFilterPresets.Any, SearchFilterPresets.DateYear, Now)!;
        Assert.Equal(Now.AddDays(-365), year.ModifiedAfter);
    }

    [Fact]
    public void BothBandsApplyTogether()
    {
        var filter = SearchFilterPresets.Build(
            SearchFilterPresets.SizeSmall, SearchFilterPresets.DateWeek, Now)!;

        Assert.True(filter.Matches(1024, Now.AddDays(-3)));
        Assert.False(filter.Matches(1024, Now.AddDays(-30)));   // 太旧
        Assert.False(filter.Matches(200 * 1024, Now.AddDays(-3))); // 太大
    }

    [Fact]
    public void AnyOnBothSidesMeansNoFilter()
    {
        Assert.Null(SearchFilterPresets.Build(SearchFilterPresets.Any, SearchFilterPresets.Any, Now));
        Assert.Null(SearchFilterPresets.Build(null, "  ", Now));
        // 认不出来的一律收回「全部」，不猜。
        Assert.Null(SearchFilterPresets.Build("Huge-ish", "Yesterday", Now));
        Assert.Equal(SearchFilterPresets.SizeHuge, SearchFilterPresets.Normalize("huge", [SearchFilterPresets.SizeHuge]));
        Assert.Equal(SearchFilterPresets.Any, SearchFilterPresets.Normalize("garbage", [SearchFilterPresets.SizeHuge]));
        Assert.Equal(SearchFilterPresets.Any, SearchFilterPresets.Normalize(null, [SearchFilterPresets.SizeHuge]));
        Assert.True(SearchFilter.None.IsEmpty);
    }
}
