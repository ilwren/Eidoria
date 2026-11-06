using MdExplorer.Domain;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.50 关键词相关性排序：分档口径是纯函数，先把它钉死，界面只负责照顺序呈现。
/// 分档顺序 = 完全同名 &gt; 主名相同 &gt; 主名前缀 &gt; 文件名前缀 &gt; 词首 &gt; 主名包含 &gt; 只有扩展名包含。
/// </summary>
public sealed class SearchRankingTests
{
    private static FileItem Item(string fullPath) => new(
        FileLocator.FromPath(fullPath),
        Path.GetFileName(fullPath),
        FileKindClassifier.Classify(fullPath),
        0,
        DateTimeOffset.UnixEpoch,
        false,
        false,
        Path.GetExtension(fullPath));

    [Fact]
    public void ScoreIsMonotonicAcrossTiers()
    {
        Assert.Equal(SearchRanking.ExactName, SearchRanking.Score("budget", "budget"));
        Assert.Equal(SearchRanking.ExactStem, SearchRanking.Score("budget.md", "budget"));
        Assert.Equal(SearchRanking.StemPrefix, SearchRanking.Score("budget-2024.md", "budget"));
        Assert.Equal(SearchRanking.StemSegmentPrefix, SearchRanking.Score("2024-budget-review.md", "budget"));
        Assert.Equal(SearchRanking.StemContains, SearchRanking.Score("oldbudgetdraft.md", "budget"));

        Assert.True(SearchRanking.Score("budget", "budget") > SearchRanking.Score("budget.md", "budget"));
        Assert.True(SearchRanking.Score("budget.md", "budget") > SearchRanking.Score("budget-2024.md", "budget"));
        Assert.True(SearchRanking.Score("budget-2024.md", "budget")
            > SearchRanking.Score("2024-budget-review.md", "budget"));
        Assert.True(SearchRanking.Score("2024-budget-review.md", "budget")
            > SearchRanking.Score("oldbudgetdraft.md", "budget"));
    }

    [Fact]
    public void MatchesOnlyInsideTheExtensionScoreLowest()
    {
        // 主名里没有，只有扩展名里出现：仍然是命中（索引也是按完整名字过滤的），但排在最后。
        Assert.Equal(SearchRanking.NameContains, SearchRanking.Score("readme.codetxt", "txt"));
        Assert.True(SearchRanking.Score("txt-notes.md", "txt") > SearchRanking.Score("readme.codetxt", "txt"));
    }

    [Fact]
    public void ChineseQueriesUseTheSameTiers()
    {
        Assert.Equal(SearchRanking.ExactStem, SearchRanking.Score("预算.md", "预算"));
        Assert.Equal(SearchRanking.StemPrefix, SearchRanking.Score("预算复盘.md", "预算"));
        // 汉字相邻不是词首：搜「预算」时「旧预算草稿」只是包含。
        Assert.Equal(SearchRanking.StemContains, SearchRanking.Score("旧预算草稿.md", "预算"));
        // 查询里带空格时按整体比对（去空白后判断），不会因为多打一个空格就查不到。
        Assert.Equal(SearchRanking.ExactStem, SearchRanking.Score("预算.md", " 预算 "));
    }

    [Fact]
    public void CaseInsensitiveAndNoMatchWhenAbsent()
    {
        Assert.Equal(SearchRanking.ExactStem, SearchRanking.Score("Budget.MD", "budget"));
        Assert.Equal(SearchRanking.NoMatch, SearchRanking.Score("budget.md", "invoice"));
        Assert.Equal(SearchRanking.NoMatch, SearchRanking.Score("budget.md", "   "));
        Assert.Equal(SearchRanking.NoMatch, SearchRanking.Score(null, "budget"));
        Assert.Equal(SearchRanking.NoMatch, SearchRanking.Score("budget.md", null));
    }

    [Fact]
    public void RankPutsBestNameFirstAndDropsNonMatches()
    {
        var ranked = SearchRanking.Rank(
            [
                Item("/docs/oldbudgetdraft.md"),
                Item("/docs/预算.md"),
                Item("/docs/budget.md"),
                Item("/docs/invoice.md"),
                Item("/docs/2024-budget-review.md"),
                Item("/docs/Budget Plan.md"),
            ],
            "budget");

        // 主名相同（budget.md）→ 主名前缀（Budget Plan.md）→ 词首（2024-budget-review.md）
        // → 主名包含（oldbudgetdraft.md）；不同档之间不靠名称定序，两个平台顺序一致。
        Assert.Equal(
            ["/docs/budget.md", "/docs/Budget Plan.md", "/docs/2024-budget-review.md", "/docs/oldbudgetdraft.md"],
            ranked.Select(item => item.FullPath).ToArray());
        Assert.DoesNotContain(ranked, item => item.Name == "invoice.md");
        Assert.DoesNotContain(ranked, item => item.Name == "预算.md");
    }

    [Fact]
    public void RankIsStableForEqualScores()
    {
        // 同档同名的两份结果按完整路径定序，重复搜索不会换顺序。
        var first = SearchRanking.Rank([Item("/b/budget.md"), Item("/a/budget.md")], "budget");
        var second = SearchRanking.Rank([Item("/a/budget.md"), Item("/b/budget.md")], "budget");
        Assert.Equal(first.Select(item => item.FullPath), second.Select(item => item.FullPath));
        Assert.Equal("/a/budget.md", first[0].FullPath);
    }

    [Fact]
    public void RankWithEmptyQueryReturnsNothing()
    {
        Assert.Empty(SearchRanking.Rank([Item("/a/budget.md")], string.Empty));
    }
}
