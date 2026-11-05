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
