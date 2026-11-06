using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.53 最近搜索词：纯逻辑（去重/顺序/上限）单测 + 真正读写设置文件的往返
/// （后者保证重启后列表还在、手改坏的值会被收干净）。
/// </summary>
public sealed class RecentSearchesTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "eidoria-recent-search-" + Guid.NewGuid().ToString("N"));

    public RecentSearchesTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void PushPutsNewestFirstAndDropsDuplicates()
    {
        var first = RecentSearches.Push([], "预算");
        Assert.Equal(["预算"], first);

        var second = RecentSearches.Push(first, "决算");
        Assert.Equal(["决算", "预算"], second);

        // 再搜一次老词：移到最前，不新增一行；大小写视为同一个词。
        var third = RecentSearches.Push(second, "预算");
        Assert.Equal(["预算", "决算"], third);
        Assert.Equal(["Budget", "预算"], RecentSearches.Push(["预算"], "Budget"));
    }

    [Fact]
    public void PushTrimsDropsBlankAndCapsTheList()
    {
        Assert.Equal(["abc"], RecentSearches.Push([], "  abc  "));
        Assert.Equal(["abc"], RecentSearches.Push(["abc"], "   "));
        Assert.Empty(RecentSearches.Push(["", "   "], null));

        var all = new List<string>();
        for (var index = 0; index < 20; index++)
            all = RecentSearches.Push(all, $"词 {index}");

        Assert.Equal(RecentSearches.DefaultCapacity, all.Count);
        Assert.Equal("词 19", all[0]);
        Assert.Equal("词 12", all[^1]);
    }

    [Fact]
    public void NormalizeRepairsAHandEditedList()
    {
        // 空白丢弃、首尾修剪、大小写去重（A 与 a 是同一个词）。
        Assert.Equal(["a", "b"], RecentSearches.Normalize(["", " a ", "b", "A", null!]));
        Assert.Empty(RecentSearches.Normalize(null));
    }

    [Fact]
    public async Task RecentQueriesSurviveARestart()
    {
        var store = SettingsStore.LoadOrDefault(_dataDir);
        store.Settings.RecentSearchQueries = RecentSearches.Push(store.Settings.RecentSearchQueries, "季度预算");
        store.Settings.RecentSearchQueries = RecentSearches.Push(store.Settings.RecentSearchQueries, "退货流程");
        await store.SaveSettingsAsync(TestContext.Current.CancellationToken);

        var reopened = SettingsStore.LoadOrDefault(_dataDir);
        Assert.Equal(["退货流程", "季度预算"], reopened.Settings.RecentSearchQueries);
    }
}
