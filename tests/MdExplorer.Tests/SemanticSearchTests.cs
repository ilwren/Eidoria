using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Infrastructure.Ai;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>AI semantic indexing and ranking without model weights: the embedding port is deterministic.</summary>
public sealed class SemanticSearchTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _root;
    private readonly string _dataDir;
    private readonly SettingsStore _settings;
    private readonly FileNameIndexService _fileIndex;
    private readonly FakeEmbeddings _embeddings;
    private readonly SemanticSearchService _semantic;

    public SemanticSearchTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "mdexplorer-semantic-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_baseDir, "files");
        _dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDir);
        _settings = SettingsStore.LoadOrDefault(_dataDir);
        _settings.Settings.EnableLocalAi = true;
        _settings.Settings.SearchIndexRootPaths = [_root];
        _fileIndex = new FileNameIndexService(_dataDir, _settings);
        _embeddings = new FakeEmbeddings();
        _semantic = new SemanticSearchService(_dataDir, _fileIndex, _settings, _embeddings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RebuildEmbedsTextSearchesByMeaningAndStoresNoRawContent()
    {
        var notes = Path.Combine(_root, "quarterly-notes.md");
        var finance = Path.Combine(_root, "market.txt");
        await File.WriteAllTextAsync(notes, "A short note about feline companionship.");
        await File.WriteAllTextAsync(finance, "Quarterly bonds and interest rates.");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);

        var built = await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(2, built.IndexedItemCount);
        Assert.True(built.IsCurrent);
        Assert.Equal("Test embedding", Assert.Single(built.ModelNames));

        // Reopen a fresh service to exercise the binary snapshot loader, not only the in-memory cache.
        var reopened = new SemanticSearchService(_dataDir, _fileIndex, _settings, new FakeEmbeddings());
        Assert.True((await reopened.GetStatsAsync(TestContext.Current.CancellationToken)).IsCurrent);
        var staleRevision = new SemanticSearchService(_dataDir, _fileIndex, _settings, new FakeEmbeddings("revision-b"));
        Assert.False((await staleRevision.GetStatsAsync(TestContext.Current.CancellationToken)).IsCurrent);

        var hits = await reopened.SearchAsync(
            "find a pet note", null, 10, TestContext.Current.CancellationToken);
        var hit = Assert.Single(hits);
        Assert.Equal(notes, hit.Item.FullPath);
        Assert.True(hit.Score > 0.99f);

        var indexPath = Path.Combine(_dataDir, "semantic-search-v1.bin");
        var bytes = await File.ReadAllBytesAsync(indexPath);
        Assert.DoesNotContain("feline", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        await reopened.ClearAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(indexPath));
        Assert.Equal(0, (await reopened.GetStatsAsync(TestContext.Current.CancellationToken)).IndexedItemCount);
    }

    [Fact]
    public async Task RebuildReusesUnchangedVectorsAndOnlyReembedsChangedFiles()
    {
        var changed = Path.Combine(_root, "changed.md");
        var unchanged = Path.Combine(_root, "unchanged.txt");
        await File.WriteAllTextAsync(changed, "feline companion notes");
        await File.WriteAllTextAsync(unchanged, "quarterly interest rate summary");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);

        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(2, _embeddings.EmbeddingCount);

        // A manual update with no metadata changes should retain the vectors and avoid model calls.
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(2, _embeddings.EmbeddingCount);

        await File.WriteAllTextAsync(changed, "bond market outlook with updated details");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(2));
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, _embeddings.EmbeddingCount);

        // Same-length edits with a restored timestamp must still invalidate the cached vector.
        var preservedWriteTime = File.GetLastWriteTimeUtc(changed);
        await File.WriteAllTextAsync(changed, "bond market outlook with revised details");
        File.SetLastWriteTimeUtc(changed, preservedWriteTime);
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(4, _embeddings.EmbeddingCount);
        Assert.True((await _semantic.GetStatsAsync(TestContext.Current.CancellationToken)).IsCurrent);

        // A model revision change invalidates every stored vector instead of reusing incompatible embeddings.
        var revisedEmbeddings = new FakeEmbeddings("revision-b");
        var revisedService = new SemanticSearchService(_dataDir, _fileIndex, _settings, revisedEmbeddings);
        await revisedService.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(2, revisedEmbeddings.EmbeddingCount);
    }

    [Fact]
    public async Task RebuildRejectsNonFiniteModelEmbeddings()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "local text");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        var invalid = new SemanticSearchService(_dataDir, _fileIndex, _settings,
            new FakeEmbeddings(emitInvalidVector: true));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await invalid.RebuildAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SemanticIndexRequiresExplicitLocalAiEnablement()
    {
        _settings.Settings.EnableLocalAi = false;
        var stats = await _semantic.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.False(stats.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken));
    }

    // ── v0.60：混合检索（用户报的两个具体查询） ────────────────────────────

    [Fact]
    public async Task SearchingVideoKindFindsVideoFilesEvenWhenMeaningIsUnrelated()
    {
        // 「视频」两个字不在任何文件名里，向量也帮不上忙（FakeEmbeddings 只认 pet/feline）。
        // 期望：类型通道把 .mp4/.mkv 全部摆到前面，理由写清「类型命中」。
        await File.WriteAllTextAsync(Path.Combine(_root, "trip.mp4"), "binary-ish placeholder");
        await File.WriteAllTextAsync(Path.Combine(_root, "lecture.mkv"), "binary-ish placeholder");
        await File.WriteAllTextAsync(Path.Combine(_root, "shopping.txt"), "milk, eggs, bread");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);

        var hits = await _semantic.SearchAsync("视频", null, 10, TestContext.Current.CancellationToken);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal(FileKind.Video, hit.Item.Kind));
        Assert.All(hits, hit => Assert.Equal(SemanticHitMatch.FileType, hit.Match));
        Assert.All(hits, hit => Assert.Contains("类型命中", hit.Rationale, StringComparison.Ordinal));
        Assert.DoesNotContain(hits, hit => hit.Item.Name == "shopping.txt");
    }

    [Fact]
    public async Task ChineseTopicQueryMatchesFilesContainingATwoCharacterFragment()
    {
        // 用户原话：「笔吧验机」是视频主题，结果里连「验机」都没有。
        // 文件名里没有整段，只有「验机」；文件夹名里也没有。这一条必须命中。
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "2024-验机实录.mp4"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(downloads, "cooking.webm"), "placeholder");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);

        // 用「查询与文档完全正交」的嵌入：向量通道一条都给不出来，能不能命中完全看字面通道。
        // 这正是用户遇到的处境——小模型对「笔吧验机」没有判断力，字面通道必须兜住。
        var literalOnly = new SemanticSearchService(_dataDir, _fileIndex, _settings, new OrthogonalEmbeddings());
        var hits = await literalOnly.SearchAsync("笔吧验机", null, 10, TestContext.Current.CancellationToken);

        var top = Assert.Single(hits);
        Assert.Equal("2024-验机实录.mp4", top.Item.Name);
        Assert.Equal(SemanticHitMatch.Name, top.Match);
        Assert.Contains("文件名命中", top.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentChannelUsesTheFullTextIndexPerTermInsteadOfTheWholeSentence()
    {
        // 全文索引的 MATCH 是 AND：整句「笔吧验机」要求文档同时含「笔吧」「吧验」「验机」。
        // 服务必须按词逐个查，否则真实文件连一条都进不来。这里用假的正文通道把调用模式钉死。
        var queried = new List<string>();
        var contentFile = Path.Combine(_root, "访谈记录.txt");
        await File.WriteAllTextAsync(contentFile, "这期视频聊了验机流程");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);
        var info = new FileInfo(contentFile);

        Task<IReadOnlyList<ContentSearchHit>> Search(string term, int limit, CancellationToken ct)
        {
            queried.Add(term);
            return Task.FromResult<IReadOnlyList<ContentSearchHit>>(
                term == "验机"
                    ? [new ContentSearchHit(contentFile, "访谈记录.txt", "这期视频聊了验机流程", info.LastWriteTime, info.Length, 7, 2)]
                    : []);
        }

        var hits = await _semantic.SearchAsync(
            "笔吧验机", null, 10, TestContext.Current.CancellationToken,
            contentIndex: new AlwaysAvailableContentIndex(), contentSearch: Search);

        Assert.Contains(hits, hit => hit.Item.Name == "访谈记录.txt" && hit.Match == SemanticHitMatch.Content);
        Assert.All(new[] { "笔吧验机", "验机" }, term => Assert.Contains(term, queried));
        var contentHit = hits.Single(hit => hit.Match == SemanticHitMatch.Content);
        Assert.Contains("验机", contentHit.Snippet, StringComparison.Ordinal);
        Assert.Equal(7, contentHit.SnippetHighlightStart);
    }

    [Fact]
    public async Task KindFilterStillAppliesToEveryChannel()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "验机笔记.md"), "验机");
        await File.WriteAllTextAsync(Path.Combine(_root, "验机录像.mp4"), "placeholder");
        await _fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);
        await _semantic.RebuildAsync(null, TestContext.Current.CancellationToken);

        var onlyVideos = await _semantic.SearchAsync(
            "验机", new HashSet<FileKind> { FileKind.Video }, 10, TestContext.Current.CancellationToken);
        Assert.All(onlyVideos, hit => Assert.Equal(FileKind.Video, hit.Item.Kind));
        Assert.Contains(onlyVideos, hit => hit.Item.Name == "验机录像.mp4");
    }

    /// <summary>只用于本组测试：正文通道永远「可用」（真正的可用性由 Shell 判定）。</summary>
    private sealed class AlwaysAvailableContentIndex : IContentIndexService
    {
        public Task<ContentIndexStats> GetStatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ContentIndexStats(1, 0, DateTimeOffset.UtcNow, []));

        public Task<ContentIndexStats> RebuildAsync(
            IReadOnlyList<string> roots, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
            GetStatsAsync(cancellationToken);

        public Task<IReadOnlyList<ContentSearchHit>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken, SearchFilter? filter = null) =>
            Task.FromResult<IReadOnlyList<ContentSearchHit>>([]);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>查询与文档正交（余弦 0）：只用来验证「模型帮不上忙时字面通道仍然有效」。</summary>
    private sealed class OrthogonalEmbeddings : ITextEmbeddingService
    {
        public IReadOnlyList<TextEmbeddingModel> GetAvailableModels() =>
            [new TextEmbeddingModel("test-embed", "Test embedding", "revision-a", 2)];

        public float[] Embed(string modelId, string text, bool isQuery, CancellationToken cancellationToken) =>
            isQuery ? [0f, 1f] : [1f, 0f];
    }

    private sealed class FakeEmbeddings(string revision = "revision-a", bool emitInvalidVector = false) : ITextEmbeddingService
    {
        private int _embeddingCount;
        public int EmbeddingCount => Volatile.Read(ref _embeddingCount);

        public IReadOnlyList<TextEmbeddingModel> GetAvailableModels() =>
            [new TextEmbeddingModel("test-embed", "Test embedding", revision, 2)];

        public float[] Embed(string modelId, string text, bool isQuery, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _embeddingCount);
            if (emitInvalidVector)
                return [float.NaN, 0f];
            return text.Contains("pet", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("feline", StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f]
                : [0f, 1f];
        }
    }
}
