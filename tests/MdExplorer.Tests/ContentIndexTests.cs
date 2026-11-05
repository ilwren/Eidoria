using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.41：全文内容索引。分词规则与查询构造是纯函数，先单独钉死；
/// 再跑一遍真实 SQLite 往返——它同时验证 CI 上的 <c>e_sqlite3</c> 是否带 FTS5，
/// 不带的话这里会红，而不是等用户点「建立索引」才发现。
/// </summary>
public sealed class ContentIndexTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eidoria-content-" + Guid.NewGuid().ToString("N"));

    public ContentIndexTests() => Directory.CreateDirectory(_root);

    private SqliteContentIndex Index(string name = "content-index.db") =>
        new(Path.Combine(_root, name), new LocalTextFileService());

    private string Write(string relativeName, string content)
    {
        var path = Path.Combine(_root, relativeName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
        return path;
    }

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

    // ── 分词 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ChineseRunsBecomeOverlappingBigrams()
    {
        // 单个汉字查不到是 unicode61 的老问题；二元组展开让「两个字」也能命中。
        Assert.Equal("文件 件管 管理 理器 ", SqliteContentIndex.ExpandForIndex("文件管理器"));
    }

    [Fact]
    public void LatinWordsKeepTheirShape()
    {
        Assert.Equal("hello world", SqliteContentIndex.ExpandForIndex("hello world"));
    }

    [Fact]
    public void SingleChineseCharacterStaysReadable()
    {
        Assert.Equal("猫 ", SqliteContentIndex.ExpandForIndex("猫"));
    }

    [Fact]
    public void MixedTextExpandsOnlyTheChinesePart()
    {
        Assert.Equal("v1 版本 本说 说明 ", SqliteContentIndex.ExpandForIndex("v1 版本说明"));
    }

    // ── 查询构造 ──────────────────────────────────────────────────────────

    [Fact]
    public void QueryTermsAreQuotedAndAnded()
    {
        Assert.Equal("\"文件\" AND \"host\"", SqliteContentIndex.BuildMatchQuery("文件 host"));
    }

    [Fact]
    public void ChineseQueryIsBigrammedToo()
    {
        Assert.Equal("\"文件\" AND \"件管\"", SqliteContentIndex.BuildMatchQuery("文件管"));
    }

    [Fact]
    public void QueryWithoutTermsIsEmpty()
    {
        // 纯符号不该变成 FTS5 语法；调用方拿到空串就不再查库。
        Assert.Equal(string.Empty, SqliteContentIndex.BuildMatchQuery("  !!! ??  "));
        Assert.Equal(string.Empty, SqliteContentIndex.BuildMatchQuery(null));
        Assert.Equal(string.Empty, SqliteContentIndex.BuildMatchQuery("   "));
    }

    [Fact]
    public void PunctuationSplitsTermsInsteadOfBreakingTheSyntax()
    {
        // 用户输入里的引号/括号不该变成 FTS5 语法；这里把它当分隔符，最多查不到。
        Assert.Equal("\"a\" AND \"b\"", SqliteContentIndex.BuildMatchQuery("a\"b"));
    }

    // ── 摘要 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SnippetKeepsTheQueryInView()
    {
        var raw = new string('甲', 200) + "预算表" + new string('乙', 200);
        var snippet = SqliteContentIndex.BuildSnippet(raw, "预算表");
        Assert.Contains("预算表", snippet, StringComparison.Ordinal);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length < raw.Length);
    }

    [Fact]
    public void SnippetWithoutHitFallsBackToTheHead()
    {
        var snippet = SqliteContentIndex.BuildSnippet("第一行\n第二行", "找不到");
        Assert.Equal("第一行 第二行", snippet);
    }

    [Fact]
    public void SnippetCollapsesWhitespace()
    {
        Assert.Equal("a b c", SqliteContentIndex.BuildSnippet("a\n\n\t b   c", string.Empty));
    }

    // ── 真实 SQLite 往返 ──────────────────────────────────────────────────

    [Fact]
    public async Task RebuildIndexesWhitelistedTextAndSearchFindsIt()
    {
        var markdown = Write("notes/预算.md", "# 计划\n\n季度预算与决算口径说明。\n");
        Write("notes/ignored.png", "这不是文本");
        Write("notes/english.txt", "quarterly budget review");
        Write("notes/huge.txt", new string('x', (int)SqliteContentIndex.MaxFileBytes + 16));

        var index = Index();
        var stats = await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.DocumentCount);
        Assert.True(stats.IsBuilt);
        Assert.Contains(_root, stats.Roots);
        Assert.NotNull(stats.UpdatedAt);
        Assert.True(stats.IndexBytes > 0);

        var hits = await index.SearchAsync("预算", 10, TestContext.Current.CancellationToken);
        var hit = Assert.Single(hits);
        Assert.Equal(markdown, hit.Path);
        Assert.Equal("预算.md", hit.Name);
        Assert.Contains("预算", hit.Snippet, StringComparison.Ordinal);
        Assert.EndsWith(".md", hit.Name, StringComparison.Ordinal);
        Assert.True(hit.LastWriteTime > DateTimeOffset.UnixEpoch);

        var latin = await index.SearchAsync("budget", 10, TestContext.Current.CancellationToken);
        Assert.Equal("english.txt", Assert.Single(latin).Name);
    }

    [Fact]
    public async Task RebuildSkipsUnchangedFilesAndDropsDeletedOnes()
    {
        var first = Write("a.md", "初版内容：项目排期");
        var index = Index();

        await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        var afterFirst = await index.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, afterFirst.DocumentCount);

        // 未改动：重建不该改变 updated_at（说明它没有重新解码写库）。
        await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        var afterSecond = await index.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(afterFirst.UpdatedAt, afterSecond.UpdatedAt);

        // 改了正文：命中跟着变，旧词不再命中。
        await Task.Delay(10, TestContext.Current.CancellationToken);
        File.WriteAllText(first, "改版内容：项目延期");
        await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        Assert.Empty(await index.SearchAsync("排期", 10, TestContext.Current.CancellationToken));
        Assert.Single(await index.SearchAsync("延期", 10, TestContext.Current.CancellationToken));

        // 文件被删：重建后索引里也不该留。
        File.Delete(first);
        var rebuilt = await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        Assert.Equal(0, rebuilt.DocumentCount);
        Assert.Empty(await index.SearchAsync("延期", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatsDoNotCreateTheDatabaseBeforeTheFirstRebuild()
    {
        var path = Path.Combine(_root, "content-index.db");
        var index = Index();

        var stats = await index.GetStatsAsync(TestContext.Current.CancellationToken);

        Assert.False(stats.IsBuilt);
        Assert.Empty(stats.Roots);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SearchOnMissingIndexReturnsEmptyInsteadOfThrowing()
    {
        var index = Index();
        Assert.Empty(await index.SearchAsync("任何词", 10, TestContext.Current.CancellationToken));
        Assert.Empty(await index.SearchAsync("   ", 10, TestContext.Current.CancellationToken));
        Assert.Empty(await index.SearchAsync("词", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearEmptiesTheIndexButKeepsTheServiceUsable()
    {
        Write("a.md", "内容：清理测试");
        var index = Index();
        await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        Assert.Single(await index.SearchAsync("清理", 10, TestContext.Current.CancellationToken));

        await index.ClearAsync(TestContext.Current.CancellationToken);
        var stats = await index.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.DocumentCount);
        Assert.Empty(await index.SearchAsync("清理", 10, TestContext.Current.CancellationToken));

        // 清空之后还能重新建立。
        var rebuilt = await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        Assert.Equal(1, rebuilt.DocumentCount);
    }

    [Fact]
    public async Task NonexistentRootsAreIgnored()
    {
        var index = Index();
        var stats = await index.RebuildAsync(
            [Path.Combine(_root, "does-not-exist")], null, TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.DocumentCount);
        Assert.Empty(stats.Roots);
    }

    [Fact]
    public async Task RebuildReportsProgress()
    {
        for (var i = 0; i < 30; i++)
            Write($"f{i:D2}.txt", $"条目 {i}");

        var reports = new List<OperationProgress>();
        var index = Index();
        await index.RebuildAsync([_root], new CollectingProgress(reports), TestContext.Current.CancellationToken);

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.True(report.ItemsTotal >= report.ItemsDone));
        Assert.Equal(30, reports[^1].ItemsTotal);
        Assert.Equal(30, reports[^1].ItemsDone);
    }

    /// <summary>同步收集进度：Progress&lt;T&gt; 会投递到线程池，测试里不等它。</summary>
    private sealed class CollectingProgress(List<OperationProgress> sink) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            lock (sink)
                sink.Add(value);
        }
    }
}
