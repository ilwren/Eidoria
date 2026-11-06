using System.Diagnostics;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Performance;

/// <summary>
/// §12.2 性能验收的**结构性冒烟**：这些用例只保证「管线没有在 CI 上退化成平方级或整盘重扫」，
/// 不是桌面 SSD 冷缓存的验收数字。真正的门槛（1 万条首批 ≤ 500 ms、桌面冷启 ≤ 800 ms）
/// 仍要发布前在真机冷缓存下实测；这里的上界故意放得很宽，因为 GitHub 共享 runner 抖动大，
/// 把这里写成硬门槛只会得到「随机变红」而不是「性能回归」。
/// <para>
/// 数值全部经 ITestOutputHelper 打印，CI 日志里可直接读；docs/DEVELOPMENT.md §12.2 引用的
/// 就是这里输出的实测值。
/// </para>
/// </summary>
[Trait("Category", "Performance")]
public sealed class PerformanceSmokeTests : IClassFixture<TenThousandFileTree>
{
    /// <summary>1 万条目录首批上屏：CI 上界（真机目标 500 ms，见 §12.2）。</summary>
    private const double FirstPageCeilingMs = 8_000;

    /// <summary>组合根 + ShellViewModel 构造：冷启动里「读设置 + 建服务」的那一段。</summary>
    private const double ShellCompositionCeilingMs = 3_000;

    /// <summary>1 万个文本文件建内容索引：v0.41 的规模上界（此前只在小目录验证过）。</summary>
    private const double ContentIndexRebuildCeilingMs = 180_000;

    /// <summary>
    /// 文件全未变时再重建一次内容索引（v0.49）：这时只该走目录枚举与元数据比对，
    /// 不读正文、不写库。上界取得很宽——这里要抓的是「退化回重读全部正文」。
    /// </summary>
    private const double ContentIndexUnchangedRebuildCeilingMs = 20_000;

    /// <summary>内容索引查询：库已建好后的单次检索。</summary>
    private const double ContentIndexQueryCeilingMs = 5_000;

    private readonly TenThousandFileTree _tree;
    private readonly ITestOutputHelper _output;

    public PerformanceSmokeTests(TenThousandFileTree tree, ITestOutputHelper output)
    {
        _tree = tree;
        _output = output;
    }

    [Fact]
    public void ShellCompositionStaysWithinBudget()
    {
        var stopwatch = Stopwatch.StartNew();
        var services = AppHost.Build(_tree.DataDirectory, _tree.StartDirectory);
        var shell = AppHost.CreateShell(services);
        stopwatch.Stop();

        _output.WriteLine($"[perf] 组合根 + Shell 构造：{stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.NotNull(shell.Browser);
        Assert.True(
            stopwatch.Elapsed.TotalMilliseconds < ShellCompositionCeilingMs,
            $"组合根 + Shell 构造耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，" +
            $"超过 CI 上界 {ShellCompositionCeilingMs:F0} ms");
    }

    [Fact]
    public async Task TenThousandEntryDirectoryMaterializesFirstPageWithinBudget()
    {
        var shell = AppHost.CreateShell(AppHost.Build(_tree.DataDirectory, _tree.StartDirectory));
        shell.Browser.SetWatchEnabled(false);

        var stopwatch = Stopwatch.StartNew();
        await shell.Browser.NavigateToAsync(_tree.BulkDirectory, pushHistory: false);
        stopwatch.Stop();

        _output.WriteLine(
            $"[perf] 1 万条目录首批上屏：{stopwatch.Elapsed.TotalMilliseconds:F1} ms（首屏 {shell.Browser.Items.Count} 行，" +
            $"总数 {shell.Browser.TotalItemCount}）");

        Assert.Equal(TenThousandFileTree.FileCount, shell.Browser.TotalItemCount);
        Assert.Equal(shell.Browser.PageSize, shell.Browser.Items.Count);
        Assert.True(
            stopwatch.Elapsed.TotalMilliseconds < FirstPageCeilingMs,
            $"1 万条目录首批耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 CI 上界 {FirstPageCeilingMs:F0} ms");

        // 继续补齐整份列表也该是线性增长，而不是回落到逐项重读磁盘。
        var fill = Stopwatch.StartNew();
        while (shell.Browser.HasMoreItems)
            await shell.Browser.LoadMoreItemsCommand.ExecuteAsync(null);
        fill.Stop();
        _output.WriteLine($"[perf] 补齐剩余 {shell.Browser.TotalItemCount - shell.Browser.PageSize} 行：{fill.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(TenThousandFileTree.FileCount, shell.Browser.Items.Count);
    }

    [Fact]
    public async Task ContentIndexRebuildAndQueryOverTenThousandFilesStaysWithinBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = AppHost.Build(_tree.DataDirectory, _tree.StartDirectory);

        var rebuild = Stopwatch.StartNew();
        var stats = await services.ContentIndex.RebuildAsync([_tree.BulkDirectory], null, ct);
        rebuild.Stop();

        _output.WriteLine(
            $"[perf] 内容索引重建 {stats.DocumentCount} 篇：{rebuild.Elapsed.TotalMilliseconds:F0} ms，" +
            $"库体积 {stats.IndexBytes / 1024.0 / 1024.0:F2} MB");

        Assert.Equal(TenThousandFileTree.FileCount, stats.DocumentCount);
        Assert.True(
            rebuild.Elapsed.TotalMilliseconds < ContentIndexRebuildCeilingMs,
            $"内容索引重建耗时 {rebuild.Elapsed.TotalMilliseconds:F0} ms，超过 CI 上界 {ContentIndexRebuildCeilingMs:F0} ms");

        // v0.49：同样的目录、文件一个没变，再来一次重建。正常情况下这一步只读目录元数据。
        var again = Stopwatch.StartNew();
        var second = await services.ContentIndex.RebuildAsync([_tree.BulkDirectory], null, ct);
        again.Stop();

        _output.WriteLine(
            $"[perf] 内容索引重建（{second.DocumentCount} 篇全未变）：{again.Elapsed.TotalMilliseconds:F0} ms，" +
            $"库体积 {second.IndexBytes / 1024.0 / 1024.0:F2} MB");

        Assert.Equal(stats.DocumentCount, second.DocumentCount);
        Assert.True(
            again.Elapsed.TotalMilliseconds < ContentIndexUnchangedRebuildCeilingMs,
            $"未变文件的重建耗时 {again.Elapsed.TotalMilliseconds:F0} ms，超过上界 " +
            $"{ContentIndexUnchangedRebuildCeilingMs:F0} ms——说明又去重读正文了");
        // 什么都没变就不该往库里加东西。
        Assert.True(
            second.IndexBytes <= stats.IndexBytes,
            $"未变文件的重建把库体积从 {stats.IndexBytes} 涨到了 {second.IndexBytes}");

        var query = Stopwatch.StartNew();
        var hits = await services.ContentIndex.SearchAsync("性能冒烟样本", 50, ct);
        query.Stop();

        _output.WriteLine($"[perf] 内容索引查询命中 {hits.Count} 条：{query.Elapsed.TotalMilliseconds:F1} ms");

        Assert.NotEmpty(hits);
        Assert.True(
            query.Elapsed.TotalMilliseconds < ContentIndexQueryCeilingMs,
            $"内容索引查询耗时 {query.Elapsed.TotalMilliseconds:F1} ms，超过 CI 上界 {ContentIndexQueryCeilingMs:F0} ms");

        // 顺带把「清空」也压在规模上跑一遍：清空后不该还留得住命中。
        await services.ContentIndex.ClearAsync(ct);
        Assert.Empty(await services.ContentIndex.SearchAsync("性能冒烟样本", 50, ct));
    }
}

/// <summary>一次性铺 1 万个文本文件的临时树；整个测试类共用一份，避免每个用例重复建盘。</summary>
public sealed class TenThousandFileTree : IDisposable
{
    public const int FileCount = 10_000;

    public TenThousandFileTree()
    {
        RootDirectory = Directory.CreateTempSubdirectory("eidoria-perf-").FullName;
        DataDirectory = Directory.CreateDirectory(Path.Combine(RootDirectory, "data")).FullName;
        StartDirectory = Directory.CreateDirectory(Path.Combine(RootDirectory, "start")).FullName;
        BulkDirectory = Directory.CreateDirectory(Path.Combine(StartDirectory, "Bulk")).FullName;

        for (var i = 0; i < FileCount; i++)
        {
            // 正文同时含 ASCII 与中文：让内容索引走到分词那条真实路径，而不是全 ASCII 的顺风路径。
            File.WriteAllText(
                Path.Combine(BulkDirectory, $"doc-{i:00000}.txt"),
                $"条目 {i}：性能冒烟样本，含少量中文用于验证分词与摘要路径。");
        }
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string StartDirectory { get; }
    public string BulkDirectory { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
