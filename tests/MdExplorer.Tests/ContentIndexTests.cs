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
        // 逐段 Path.Combine：Windows 上直接拼 "notes/x.md" 会保留正斜杠，
        // 而索引里存的是枚举出来的带反斜杠全路径，断言就会假红。
        var parts = new List<string> { _root };
        parts.AddRange(relativeName.Split('/'));
        var path = Path.Combine(parts.ToArray());
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

    // ── PDF 正文（v0.57） ─────────────────────────────────────────────────

    [Fact]
    public async Task TextPdfDocumentsAreIndexedThroughTheTextLayer()
    {
        var root = Path.Combine(_root, "pdf-root");
        Directory.CreateDirectory(root);
        var pdf = Path.Combine(root, "quarterly.pdf");
        TestPdf.Write(pdf, "Quarterly bonds and interest rates");

        var index = new SqliteContentIndex(Path.Combine(_root, "pdf.db"), new LocalTextFileService());
        var stats = await index.RebuildAsync([root], null, TestContext.Current.CancellationToken);
        Assert.Equal(1, stats.DocumentCount);

        var hits = await index.SearchAsync("bonds", 10, TestContext.Current.CancellationToken);
        var hit = Assert.Single(hits);
        Assert.Equal("quarterly.pdf", hit.Name);
        Assert.Contains("bonds", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrokenPdfDoesNotBreakTheRebuild()
    {
        var root = Path.Combine(_root, "pdf-broken");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "broken.pdf"), "%PDF-1.4\nnot really a pdf");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "recoverable notes");

        var index = new SqliteContentIndex(Path.Combine(_root, "broken.db"), new LocalTextFileService());
        var stats = await index.RebuildAsync([root], null, TestContext.Current.CancellationToken);

        // 坏 PDF 只是「这篇没有正文」，整次重建照常完成，别的文件不受影响。
        Assert.Equal(1, stats.DocumentCount);
        Assert.Single(await index.SearchAsync("recoverable", 10, TestContext.Current.CancellationToken));
    }

    // ── 增量维护（v0.56） ─────────────────────────────────────────────────

    [Fact]
    public async Task IncrementalUpdate_ReindexesTouchedFilesAndDropsDeletedOnes()
    {
        var rewritten = Write("alpha.md", "alpha original");
        var doomed = Write("beta.md", "beta content");
        var untouched = Write("gamma.md", "gamma content");
        var index = Index();
        await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);
        Assert.Equal(3, (await index.GetStatsAsync(TestContext.Current.CancellationToken)).DocumentCount);

        // 改一篇、删一篇、加一篇——只把这三条路径交给增量，不重扫整棵树。
        File.WriteAllText(rewritten, "alpha rewritten delta");
        File.Delete(doomed);
        var added = Write("delta.md", "delta added");

        var writes = await index.ApplyChangesAsync(
            [rewritten, doomed, added], TestContext.Current.CancellationToken);
        Assert.Equal(3, writes);

        // 正文跟着变：旧词查不到，新词两篇都命中，没动过的还在。
        Assert.Empty(await index.SearchAsync("original", 10, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await index.SearchAsync("delta", 10, TestContext.Current.CancellationToken)).Count);
        Assert.Empty(await index.SearchAsync("beta", 10, TestContext.Current.CancellationToken));
        Assert.Single(await index.SearchAsync("gamma", 10, TestContext.Current.CancellationToken));
        Assert.Equal(3, (await index.GetStatsAsync(TestContext.Current.CancellationToken)).DocumentCount);
    }

    [Fact]
    public async Task IncrementalUpdate_SkipsUnchangedAndOutOfScopePaths()
    {
        var kept = Write("kept.md", "kept content");
        var outside = Path.Combine(Path.GetTempPath(), "eidoria-outside-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(outside, "outside content");
        var index = Index();
        try
        {
            await index.RebuildAsync([_root], null, TestContext.Current.CancellationToken);

            // 没变的文件不该重写，根外的文件不该被拉进来，非白名单扩展名也不该被收。
            var image = Write("picture.png", "not really a png");
            var writes = await index.ApplyChangesAsync(
                [kept, outside, image], TestContext.Current.CancellationToken);

            Assert.Equal(0, writes);
            Assert.Single(await index.SearchAsync("kept", 10, TestContext.Current.CancellationToken));
            Assert.Empty(await index.SearchAsync("outside", 10, TestContext.Current.CancellationToken));
            Assert.Equal(1, (await index.GetStatsAsync(TestContext.Current.CancellationToken)).DocumentCount);
        }
        finally
        {
            try { File.Delete(outside); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task IncrementalUpdate_IsANoOpBeforeTheIndexIsBuilt()
    {
        var index = new SqliteContentIndex(
            Path.Combine(_root, "never-built.db"), new LocalTextFileService());
        var file = Write("lonely.md", "lonely content");

        // 没建过索引时不许凭空建库：那会让「内容索引还关着」的用户多出一个 db 文件。
        Assert.Equal(0, await index.ApplyChangesAsync([file], TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "never-built.db")));
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

    [Fact]
    public void SnippetReportsWhereTheMatchSits()
    {
        // v0.48：摘要里那一段高亮的位置由索引层给出，UI 不再自己搜关键词。
        var raw = new string('甲', 200) + "预算表" + new string('乙', 200);
        var snippet = SqliteContentIndex.BuildSnippet(raw, "预算表", out var offset, out var length);

        Assert.True(offset >= 0);
        Assert.Equal("预算表", snippet.Substring(offset, length));
        // 头部省略号也要算进偏移，否则高亮会整体错一位。
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void SnippetOffsetsSurviveCaseInsensitiveLatinMatch()
    {
        var snippet = SqliteContentIndex.BuildSnippet("Quarterly BUDGET review", "budget", out var offset, out var length);

        Assert.Equal("BUDGET", snippet.Substring(offset, length));
    }

    [Fact]
    public void SnippetWithoutMatchHasNoHighlight()
    {
        SqliteContentIndex.BuildSnippet("第一行 第二行", "找不到", out var offset, out var length);

        Assert.Equal(-1, offset);
        Assert.Equal(0, length);
    }

    [Fact]
    public async Task SearchResultCarriesHighlightOffsets()
    {
        Write("notes/budget.md", "# 计划\n\n季度预算与决算口径说明。\n");
        var index = Index();
        await index.RebuildAsync([_root], null, CancellationToken.None);

        var hit = Assert.Single(await index.SearchAsync("预算", 10, CancellationToken.None));

        Assert.True(hit.MatchOffset >= 0);
        Assert.Equal("预算", hit.Snippet.Substring(hit.MatchOffset, hit.MatchLength));
    }

    // ── v0.54 大小 / 修改时间筛选 ──────────────────────────────────────────

    /// <summary>筛选在 SQL 里做，所以被 LIMIT 截断的永远是「通过筛选之后」的结果。</summary>
    [Fact]
    public async Task SearchRespectsSizeAndDateFilters()
    {
        Write("notes/small.md", "预算 决算 报告样本");
        Write("notes/big.md", "预算 决算 报告样本 " + new string('大', 100_000));
        var oldPath = Write("notes/old.md", "预算 决算 报告样本");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
        var index = Index();
        var ct = TestContext.Current.CancellationToken;

        var stats = await index.RebuildAsync([_root], null, ct);
        Assert.Equal(3, stats.DocumentCount);

        Assert.Equal(3, (await index.SearchAsync("预算", 10, ct)).Count);

        var bigOnly = await index.SearchAsync("预算", 10, ct, new SearchFilter(MinBytes: 60 * 1024));
        Assert.Single(bigOnly);
        Assert.Equal("big.md", bigOnly[0].Name);

        var recentOnly = await index.SearchAsync(
            "预算", 10, ct, new SearchFilter(ModifiedAfter: DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Equal(2, recentOnly.Count);
        Assert.DoesNotContain(recentOnly, hit => hit.Name == "old.md");

        // 上限 1 条 + 大小筛选：先筛后截断，不应该「先截断成 1 条再筛空」。
        var limited = await index.SearchAsync("预算", 1, ct, new SearchFilter(MinBytes: 60 * 1024));
        Assert.Single(limited);
        Assert.Equal("big.md", limited[0].Name);
    }

    // ── v0.49 磁盘 I/O：分块提交、跳过未变文件、原文入库封顶 ────────────────

    /// <summary>数一数真正读了几次正文：重建的 I/O 成本几乎都花在这里。</summary>
    private sealed class CountingTextFiles(ITextFileService inner) : ITextFileService
    {
        public int Reads { get; private set; }

        public Task<TextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.ReadAsync(path, maxBytes, cancellationToken);
        }

        public Task<TextFileSnapshot> SaveAsync(
            TextFileSnapshot expected, string text, bool overwriteChanged, CancellationToken cancellationToken) =>
            inner.SaveAsync(expected, text, overwriteChanged, cancellationToken);
    }

    [Fact]
    public async Task SecondRebuildReadsNothingWhenFilesAreUnchanged()
    {
        for (var index = 0; index < 30; index++)
            Write($"notes/doc-{index}.md", $"第 {index} 篇：季度预算与决算口径说明。\n");

        var counting = new CountingTextFiles(new LocalTextFileService());
        var index2 = new SqliteContentIndex(Path.Combine(_root, "content.db"), counting);

        var first = await index2.RebuildAsync([_root], null, CancellationToken.None);
        Assert.Equal(30, first.DocumentCount);
        Assert.Equal(30, counting.Reads);

        var second = await index2.RebuildAsync([_root], null, CancellationToken.None);

        Assert.Equal(30, second.DocumentCount);
        // 关键断言：第二次重建一篇正文都不该读——元数据没变就什么都不用做。
        Assert.Equal(30, counting.Reads);
    }

    [Fact]
    public async Task SecondRebuildDoesNotGrowTheDatabase()
    {
        Write("notes/a.md", "预算与决算口径。");
        Write("notes/b.md", "退货流程说明。");
        var index = Index();

        var first = await index.RebuildAsync([_root], null, CancellationToken.None);
        Assert.True(first.IndexBytes > 0);

        var second = await index.RebuildAsync([_root], null, CancellationToken.None);

        // 收尾 checkpoint 之后 WAL 已折回主库：没有新增写入，体积就该一模一样。
        Assert.Equal(first.IndexBytes, second.IndexBytes);
        Assert.Equal(first.DocumentCount, second.DocumentCount);
    }

    [Fact]
    public async Task CancellingMidwayKeepsCommittedBatches()
    {
        // 分批只与「写了多少条」有关：造 3 批多一点，取消后应留下整批的进度，而不是全丢。
        var count = SqliteContentIndex.CommitBatch * 2 + 40;
        for (var index = 0; index < count; index++)
            Write($"bulk/doc-{index}.md", $"第 {index} 篇：性能冒烟样本。\n");

        var index2 = Index("partial.db");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.ItemsDone >= count / 2)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => index2.RebuildAsync([_root], progress, cts.Token));

        var stats = await index2.GetStatsAsync(CancellationToken.None);

        // 取消不是「白干」：已提交的分批留在库里，下次重建补齐剩下的。
        Assert.True(stats.DocumentCount > 0, "取消后不该一篇都不剩");
        Assert.True(stats.DocumentCount < count, $"取消后不该已经索引完全部 {count} 篇");
    }

    private sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    [Fact]
    public async Task LargeFileIsSearchableButItsStoredRawIsCapped()
    {
        var tail = "结尾处的独特词甲乙丙";
        var content = new string('前', SqliteContentIndex.MaxStoredRawChars + 5_000) + tail;
        Write("notes/long.md", content);
        Write("notes/small.md", "短文档");
        var index = Index();

        var stats = await index.RebuildAsync([_root], null, CancellationToken.None);
        var hits = await index.SearchAsync("甲乙丙", 10, CancellationToken.None);

        // 检索覆盖全文：词在 4 万字符之后也能搜到。
        Assert.True(stats.DocumentCount >= 1);
        Assert.Single(hits);
        Assert.Equal("long.md", hits[0].Name);
        // 入库原文封顶带来的已知代价：摘要只从保留的前 4 万字符里截，
        // 所以词在更深处时能搜到、但摘要里看不到它，也不给高亮区间。
        Assert.DoesNotContain("甲乙丙", hits[0].Snippet, StringComparison.Ordinal);
        Assert.Equal(-1, hits[0].MatchOffset);
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
