using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>本地文件名索引：快照构建/查询口径、失效复核、变更失效与扫描回落。</summary>
public sealed class SearchIndexTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _root;
    private readonly string _dataDir;
    private readonly SettingsStore _settings;
    private readonly FileNameIndexService _index;

    public SearchIndexTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "mdexplorer-index-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_baseDir, "files");
        _dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDir);
        _settings = SettingsStore.LoadOrDefault(_dataDir);
        _settings.Settings.SearchIndexRootPaths = [_root];
        _index = new FileNameIndexService(_dataDir, _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
    }

    private string FileIn(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<List<FileItem>> CollectAsync(IAsyncEnumerable<FileItem> source)
    {
        var items = new List<FileItem>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task Rebuild_SearchesAcrossRoot_AndFiltersKindsAndHidden()
    {
        FileIn("alpha-report.pdf");
        FileIn(Path.Combine("sub", "Alpha notes.txt"));
        FileIn(Path.Combine(".hidden", "alpha-secret.txt"));
        FileIn("beta.png");

        var stats = await _index.RebuildAsync(null, TestContext.Current.CancellationToken);

        Assert.True(stats.IsCurrent);
        Assert.False(stats.IsPartial);
        Assert.Equal(1, stats.RootCount);
        Assert.Equal(6, stats.EntryCount); // 4 个文件 + sub + .hidden
        Assert.True(stats.IndexBytes > 0);
        Assert.NotNull(stats.UpdatedAt);
        Assert.True(await _index.CoversAsync(Path.Combine(_root, "sub"), TestContext.Current.CancellationToken));

        var visible = await CollectAsync(_index.SearchAsync(
            new SearchSpec("alpha", string.Empty), TestContext.Current.CancellationToken));
        Assert.Equal(2, visible.Count);

        var withHidden = await CollectAsync(_index.SearchAsync(
            new SearchSpec("alpha", string.Empty, IncludeHidden: true), TestContext.Current.CancellationToken));
        Assert.Equal(3, withHidden.Count);

        var documents = await CollectAsync(_index.SearchAsync(
            new SearchSpec(string.Empty, _root, new HashSet<FileKind> { FileKind.Document }),
            TestContext.Current.CancellationToken));
        Assert.Single(documents);
        Assert.Equal("alpha-report.pdf", documents[0].Name);
    }

    [Fact]
    public async Task Search_SkipsDeletedEntries_AndClearRemovesSnapshot()
    {
        var doomed = FileIn("gone.txt");
        await _index.RebuildAsync(null, TestContext.Current.CancellationToken);

        // 暂停变更失效的影响：直接删除后即使快照仍在，命中复核也会过滤失效条目；
        // 若监视器先一步标脏，则查询同样为空。两种路径都不应返回已删除文件。
        File.Delete(doomed);
        var hits = await CollectAsync(_index.SearchAsync(
            new SearchSpec("gone", string.Empty), TestContext.Current.CancellationToken));
        Assert.Empty(hits);

        await _index.ClearAsync(TestContext.Current.CancellationToken);
        var stats = await _index.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.EntryCount);
        Assert.Equal(0, stats.IndexBytes);
        Assert.False(stats.IsCurrent);
        Assert.False(await _index.CoversAsync(_root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndexedSearch_FallsBackWhenNotCoveredOrDisabled()
    {
        FileIn("inside.txt");
        await _index.RebuildAsync(null, TestContext.Current.CancellationToken);
        var combined = new IndexedSearchService(_index, new SearchService(), _settings);

        var outside = Path.Combine(_baseDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside-hit.txt"), "x");
        var fallbackHits = await CollectAsync(combined.SearchByNameAsync(
            new SearchSpec("outside-hit", outside), TestContext.Current.CancellationToken));
        Assert.Single(fallbackHits);

        // 关闭索引后，索引根内的递归搜索改走实时扫描，能看到重建之后新建的文件。
        _settings.Settings.UseSearchIndex = false;
        FileIn("fresh.txt");
        var freshHits = await CollectAsync(combined.SearchByNameAsync(
            new SearchSpec("fresh", _root), TestContext.Current.CancellationToken));
        Assert.Single(freshHits);
    }

    [Fact]
    public async Task RootChange_MakesSnapshotNotCurrent()
    {
        FileIn("a.txt");
        await _index.RebuildAsync(null, TestContext.Current.CancellationToken);

        var other = Path.Combine(_baseDir, "other");
        Directory.CreateDirectory(other);
        _settings.Settings.SearchIndexRootPaths.Add(other);

        var stats = await _index.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stats.RootCount);
        Assert.False(stats.IsCurrent);
        Assert.False(await _index.CoversAsync(_root, TestContext.Current.CancellationToken));
        Assert.Empty(await CollectAsync(_index.SearchAsync(
            new SearchSpec("a", string.Empty), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Snapshot_PersistsAcrossServiceInstances()
    {
        FileIn(Path.Combine("docs", "persisted.md"));
        await _index.RebuildAsync(null, TestContext.Current.CancellationToken);

        var reopened = new FileNameIndexService(_dataDir, _settings);
        var stats = await reopened.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.True(stats.IsCurrent);
        Assert.Equal(2, stats.EntryCount);

        var hits = await CollectAsync(reopened.SearchAsync(
            new SearchSpec("persisted", string.Empty), TestContext.Current.CancellationToken));
        Assert.Single(hits);
    }

    [Fact]
    public async Task FileCreatedAfterBuild_InvalidatesSnapshotForScanFallback()
    {
        FileIn("before.txt");
        await _index.RebuildAsync(null, TestContext.Current.CancellationToken);
        Assert.True((await _index.GetStatsAsync(TestContext.Current.CancellationToken)).IsCurrent);

        FileIn("after.txt");
        var invalidated = false;
        for (var i = 0; i < 50 && !invalidated; i++)
        {
            invalidated = !(await _index.GetStatsAsync(TestContext.Current.CancellationToken)).IsCurrent;
            if (!invalidated)
                await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(invalidated, "索引根内的新建文件应触发变更失效");
        var combined = new IndexedSearchService(_index, new SearchService(), _settings);
        var hits = await CollectAsync(combined.SearchByNameAsync(
            new SearchSpec("after", _root), TestContext.Current.CancellationToken));
        Assert.Single(hits);
    }
}
