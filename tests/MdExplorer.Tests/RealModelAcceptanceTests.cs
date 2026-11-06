using System.Diagnostics;
using System.Globalization;
using System.Text;
using MdExplorer.Infrastructure;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// 端到端验收：**只用真实权重**跑一遍「文件名索引 → 语义索引 → 自然语言查询」整条链路，
/// 输出一份人能读的报告（质量 + 延迟 + 环境指纹）。一条命令跑它：
/// <c>scripts/accept-real-models.sh</c>（Windows 用 <c>scripts/accept-real-models.ps1</c>）。
/// <para>
/// 这里刻意**不设严格质量门槛**：合成语料上的 top-1 由模型本身决定，写死一个数字只会让
/// CI 在某天因为 ONNX 运行时版本变化而红，而人真正要看的（命中分布、分数区间、延迟）
/// 都在报告里。硬门禁仍在 <see cref="RealModelEmbeddingTests"/>：相关文本必须比无关文本更像。
/// </para>
/// </summary>
public sealed class RealModelAcceptanceTests
{
    private const string EnableVariable = "MDEXPLORER_RUN_REAL_MODEL_TESTS";
    private const string ReportVariable = "MDEXPLORER_ACCEPTANCE_REPORT";

    private readonly ITestOutputHelper _output;

    public RealModelAcceptanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>合成语料：每篇一个明确意图，中英混排，另有两篇只用来当干扰项。</summary>
    private static readonly (string Name, string Content)[] Corpus =
    [
        ("account-password.md", "忘记登录密码后，可以通过邮箱链接重设账号密码。密码至少 8 位，90 天需要更换一次。\nIf you forgot your login password, use the email link to reset the account password."),
        ("refund-policy.md", "收到商品后 7 天内可以提交退货申请，审核通过后 3 个工作日按原支付方式退款。\nSubmit a return request within 7 days of delivery; approved refunds return to the original payment method."),
        ("tomato-egg-recipe.md", "番茄炒蛋：先把鸡蛋打散炒熟盛出，再炒番茄出汁，合并后加少许盐和糖。\nTomato and egg stir-fry: scramble the eggs first, then cook the tomatoes until juicy and combine."),
        ("weekend-weather.md", "周末天气：周六多云转晴，周日有阵雨，气温 18 到 26 度，适合上午出门。\nWeekend weather: sunny on Saturday, scattered showers on Sunday, 18 to 26 degrees."),
        ("laptop-battery.md", "笔记本电池续航：把亮度降到 60%、关掉后台同步后，续航从 5 小时提升到 7 小时。\nLaptop battery life: lowering brightness and disabling background sync added two hours of runtime."),
        ("invoice-tax.md", "开票与报税：企业抬头需要提供税号，个人抬头只填姓名；电子发票会在 24 小时内发到邮箱。\nInvoices and tax: company invoices need a tax ID; e-invoices arrive by email within 24 hours."),
        ("git-rebase.md", "变基冲突：先 git rebase --continue，如果冲突太多就 git rebase --abort 回到原分支再决定。\nResolving git rebase conflicts: fix the files, run git rebase --continue, or abort to return to the original branch."),
        ("sourdough.md", "天然酵种面包：酵种不涨可能是温度太低或喂养间隔太长，提高室温并按时喂养即可。\nSourdough troubleshooting: a starter that will not rise usually needs a warmer spot and regular feeding."),
        ("flight-delay.md", "航班延误：延误超过 3 小时可以申请改签或按票面退款，行李丢失要当场填写事故报告。\nFlight delays: over three hours qualifies for rebooking or a refund; report lost luggage at the desk."),
        ("toothbrush.md", "电动牙刷：声波式对牙龈更温和，刷头每 3 个月更换一次，压强过大时会自动降速提示。\nElectric toothbrushes: sonic models are gentler on gums and heads should be replaced every three months."),
        ("quarterly-report.md", "季度报告模板：包含收入、成本、毛利与人力投入四张表，附录放上一季度的回顾。\nQuarterly report template with revenue, cost, gross margin and headcount tables."),
        ("travel-photos.md", "旅行照片整理：按拍摄日期建文件夹，重复照片用感知哈希去重，再备份到两块硬盘。\nOrganizing travel photos by capture date, de-duplicating near-identical shots and backing up twice."),
    ];

    /// <summary>查询与其应有的命中文件。中英各 8 条，都不含目标文件名本身。</summary>
    private static readonly (string Query, string Expected)[] Queries =
    [
        ("忘记密码了怎么重置", "account-password.md"),
        ("退货之后退款要等多久", "refund-policy.md"),
        ("番茄和鸡蛋一起炒怎么做", "tomato-egg-recipe.md"),
        ("周六周日适合出门吗", "weekend-weather.md"),
        ("笔记本电池怎么能用久一点", "laptop-battery.md"),
        ("公司报销要开发票怎么办", "invoice-tax.md"),
        ("变基时冲突太多想放弃", "git-rebase.md"),
        ("面包的酵种一直不发酵", "sourdough.md"),
        ("reset a forgotten login password", "account-password.md"),
        ("how many days until the money comes back after a return", "refund-policy.md"),
        ("quick recipe with tomato and egg", "tomato-egg-recipe.md"),
        ("will it rain on the weekend", "weekend-weather.md"),
        ("make my laptop battery last longer", "laptop-battery.md"),
        ("need a company invoice with tax id", "invoice-tax.md"),
        ("too many conflicts during git rebase", "git-rebase.md"),
        ("sourdough starter is not rising", "sourdough.md"),
    ];

    [Fact]
    public async Task RealModelEndToEndAcceptanceReport()
    {
        EnsureEnabled();
        var modelsDirectory = Environment.GetEnvironmentVariable(AiModelCatalog.DirectoryEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(modelsDirectory),
            $"请先设置 {AiModelCatalog.DirectoryEnvironmentVariable} 指向已下载的模型目录。");

        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), "mdexplorer-acceptance", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(workspace, "files");
        var dataDirectory = Path.Combine(workspace, "data");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dataDirectory);

        try
        {
            foreach (var (name, content) in Corpus)
                await File.WriteAllTextAsync(Path.Combine(root, name), content, cancellationToken);

            var catalog = new AiModelCatalog(modelsDirectory!);
            var settings = SettingsStore.LoadOrDefault(dataDirectory);
            settings.Settings.EnableLocalAi = true;
            settings.Settings.UseSearchIndex = true;
            settings.Settings.SearchIndexRootPaths = [root];

            var fileNameIndex = new FileNameIndexService(dataDirectory, settings);
            var nameIndexTimer = Stopwatch.StartNew();
            await fileNameIndex.RebuildAsync(null, cancellationToken);
            nameIndexTimer.Stop();

            using var embeddings = new OnnxTextEmbeddingService(catalog);
            var availableModels = embeddings.GetAvailableModels();
            Assert.NotEmpty(availableModels);

            var semantic = new SemanticSearchService(dataDirectory, fileNameIndex, settings, embeddings);
            var semanticIndexTimer = Stopwatch.StartNew();
            var stats = await semantic.RebuildAsync(null, cancellationToken);
            semanticIndexTimer.Stop();

            var modelLoadTimer = Stopwatch.StartNew();
            var warmup = embeddings.Embed(
                availableModels[0].Id, "预热：建立一次推理会话", isQuery: true, cancellationToken);
            modelLoadTimer.Stop();

            var results = new List<QueryResult>();
            foreach (var (query, expected) in Queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timings = new List<double>(3);
                IReadOnlyList<SemanticSearchHit> hits = [];
                for (var run = 0; run < 3; run++)
                {
                    var timer = Stopwatch.StartNew();
                    hits = await semantic.SearchAsync(query, null, 5, cancellationToken);
                    timer.Stop();
                    timings.Add(timer.Elapsed.TotalMilliseconds);
                }

                timings.Sort();
                Assert.NotEmpty(hits);
                Assert.All(hits, hit => Assert.True(float.IsFinite(hit.Score)));

                var topNames = hits.Select(hit => hit.Item.Name).ToArray();
                results.Add(new QueryResult(
                    query,
                    expected,
                    Top1: topNames.Length > 0 && topNames[0] == expected,
                    Top3: topNames.Take(3).Contains(expected, StringComparer.Ordinal),
                    Rank: Array.IndexOf(topNames, expected) + 1,
                    Top1Score: hits[0].Score,
                    ExpectedScore: hits.FirstOrDefault(hit => hit.Item.Name == expected)?.Score,
                    ModelName: hits[0].ModelName,
                    MedianMilliseconds: timings[timings.Count / 2],
                    Top3Names: topNames.Take(3).ToArray()));
            }

            var report = BuildReport(
                modelsDirectory!, catalog, availableModels, workspace, stats,
                nameIndexTimer.Elapsed.TotalMilliseconds,
                semanticIndexTimer.Elapsed.TotalMilliseconds,
                modelLoadTimer.Elapsed.TotalMilliseconds,
                warmup.Length, results);

            var reportPath = ResolveReportPath(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, report, cancellationToken);
            _output.WriteLine(report);
            _output.WriteLine($"[acceptance] 报告已写入：{reportPath}");

            // 只拦「整条链路坏了」：合成语料上至少一半查询要排在第一位。
            var top1 = results.Count(result => result.Top1);
            Assert.True(
                top1 * 2 >= results.Count,
                $"真实权重的 top-1 命中只有 {top1}/{results.Count}，链路可能有问题（详见报告 {reportPath}）。");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record QueryResult(
        string Query,
        string Expected,
        bool Top1,
        bool Top3,
        int Rank,
        float Top1Score,
        float? ExpectedScore,
        string ModelName,
        double MedianMilliseconds,
        string[] Top3Names);

    private static string BuildReport(
        string modelsDirectory,
        AiModelCatalog catalog,
        IReadOnlyList<TextEmbeddingModel> models,
        string workspace,
        SemanticSearchStats stats,
        double nameIndexMilliseconds,
        double semanticIndexMilliseconds,
        double modelLoadMilliseconds,
        int embeddingDimensions,
        IReadOnlyList<QueryResult> results)
    {
        var builder = new StringBuilder();
        var top1 = results.Count(result => result.Top1);
        var top3 = results.Count(result => result.Top3);
        var reciprocalRank = results.Sum(result => result.Rank > 0 ? 1d / result.Rank : 0d) / results.Count;
        var latencies = results.Select(result => result.MedianMilliseconds).OrderBy(value => value).ToArray();

        builder.AppendLine("# Eidoria 端到端验收报告（真实权重）");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 运行环境：{Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} / .NET {Environment.Version}");
        builder.AppendLine($"- 模型目录：`{modelsDirectory}`");
        foreach (var model in models)
            builder.AppendLine($"- 模型 `{model.Id}`：{model.Name} · revision `{model.Revision}` · {model.Dimensions} 维");
        builder.AppendLine();
        builder.AppendLine("## 链路段耗时");
        builder.AppendLine();
        builder.AppendLine("| 阶段 | 耗时 | 备注 |");
        builder.AppendLine("|---|---|---|");
        builder.AppendLine($"| 文件名索引重建 | {nameIndexMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms | {stats.IndexedItemCount} 项 |");
        builder.AppendLine($"| AI 向量索引重建 | {semanticIndexMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms | 含 ONNX Session 加载 |");
        builder.AppendLine($"| 首次推理（模型加载） | {modelLoadMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms | 输出 {embeddingDimensions} 维 |");
        builder.AppendLine($"| 查询中位延迟 | {Median(latencies).ToString("F1", CultureInfo.InvariantCulture)} ms | 16 条查询各跑 3 次取中位 |");
        builder.AppendLine($"| 查询 P90 | {Percentile(latencies, 0.9).ToString("F1", CultureInfo.InvariantCulture)} ms | — |");
        builder.AppendLine();
        builder.AppendLine("## 质量");
        builder.AppendLine();
        builder.AppendLine($"- Top-1 命中：**{top1}/{results.Count}**（{(double)top1 / results.Count:P0}）");
        builder.AppendLine($"- Top-3 命中：**{top3}/{results.Count}**（{(double)top3 / results.Count:P0}）");
        builder.AppendLine($"- MRR：**{reciprocalRank.ToString("F3", CultureInfo.InvariantCulture)}**");
        builder.AppendLine($"- 命中模型分布：{string.Join("、", results.GroupBy(r => r.ModelName).Select(g => $"{g.Key} ×{g.Count()}"))}");
        builder.AppendLine();
        builder.AppendLine("| # | 查询 | 期望 | 实际 Top-1 | 名次 | Top-1 分数 | 该文件分数 | 模型 | 中位延迟 |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|");
        var index = 0;
        foreach (var result in results)
        {
            index++;
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {index} | {Escape(result.Query)} | {result.Expected} | {Escape(result.Top3Names.FirstOrDefault() ?? "—")} | " +
                $"{(result.Rank > 0 ? result.Rank.ToString(CultureInfo.InvariantCulture) : "未进前 5")} | " +
                $"{result.Top1Score.ToString("F3", CultureInfo.InvariantCulture)} | " +
                $"{(result.ExpectedScore is { } score ? score.ToString("F3", CultureInfo.InvariantCulture) : "—")} | " +
                $"{result.ModelName} | {result.MedianMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms |"));
        }
        builder.AppendLine();
        builder.AppendLine("## 怎么读这份报告");
        builder.AppendLine();
        builder.AppendLine("- **Top-1 / MRR** 是端到端质量：它包含模型、路由（中文走 BGE、英文走 MiniLM）、索引与排序的全部影响。");
        builder.AppendLine("- **Top-1 分数与该文件分数** 是同一模型的余弦相似度；两者差距太小（< 0.05）说明这条查询本身区分度低，不是链路坏了。");
        builder.AppendLine("- **延迟** 是 CPU 上的单次查询耗时，含一次查询编码；它不代表批量索引吞吐。");
        builder.AppendLine("- 语料是 12 篇合成文档（含 2 篇纯干扰项），查询刻意不含目标文件名，避免退化成关键词匹配。");
        builder.AppendLine($"- 工作目录：`{workspace}`（跑完即删，报告里保留路径便于对照）。");
        return builder.ToString();
    }

    private static string ResolveReportPath(string workspace)
    {
        var configured = Environment.GetEnvironmentVariable(ReportVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(workspace, "report.md")
            : Path.GetFullPath(configured);
    }

    private static void EnsureEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            Assert.Skip(
                $"设置 {EnableVariable}=1 并下载清单里的真实权重后运行；或用 scripts/accept-real-models.sh（Windows：scripts/accept-real-models.ps1）一条命令跑完。");
        }
    }

    private static double Median(double[] sorted) =>
        sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;
        var rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
