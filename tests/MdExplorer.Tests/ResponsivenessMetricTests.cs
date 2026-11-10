using System.Diagnostics;
using MdExplorer.Domain;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.60：把「卡」「搜不准」变成可复现的数字与断言。
/// <para>
/// 这些用例不碰真实时钟以外的环境：帧间隔用伪造时间戳喂，
/// 查询解析与字面打分是纯函数。真机上跑出来的数字由 Shell 的诊断报告负责，
/// 这里只钉死算法本身——算法一旦回归，报告里的数字就不可信了。
/// </para>
/// </summary>
public sealed class ResponsivenessMetricTests
{
    // ── 帧节奏 ────────────────────────────────────────────────────────────

    [Fact]
    public void FramePacingReportsPercentilesNotJustAverage()
    {
        var stats = new FramePacingStats(targetFps: 30d);
        // 10 帧匀速 16.7 ms，然后连续掉 3 帧（长间隔 100 ms）。
        var timestamp = 1_000_000L;
        stats.Record(timestamp);
        var evenStep = (long)(Stopwatch.Frequency * 0.0167);
        for (var i = 0; i < 10; i++)
        {
            timestamp += evenStep;
            stats.Record(timestamp);
        }
        for (var i = 0; i < 3; i++)
        {
            timestamp += (long)(Stopwatch.Frequency * 0.1);
            stats.Record(timestamp);
        }

        Assert.Equal(13, stats.SampleCount);
        // 平均值会被 3 个长间隔拉高，但 p50 仍然贴近匀速段——这正是要报告分位数的理由。
        Assert.InRange(stats.IntervalPercentileMs(0.5), 10d, 30d);
        Assert.True(stats.MaxIntervalMs > 90d);
        Assert.True(stats.DroppedFrames >= 3);
        var text = stats.Describe();
        Assert.Contains("p95", text, StringComparison.Ordinal);
        Assert.Contains("掉帧", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FramePacingNeverClaimsSmoothWithoutSamples()
    {
        var stats = new FramePacingStats();
        Assert.False(stats.HasSamples);
        Assert.Equal(0d, stats.Fps);
        Assert.Contains("还没有画面", stats.Describe(), StringComparison.Ordinal);

        // 只有一帧：有首帧、没有帧率——如实说「还没有第二帧」，不写 0 fps。
        stats.MarkFirstFramePending(1_000L);
        stats.Record(1_000L + (long)(Stopwatch.Frequency * 0.25));
        Assert.Contains("还没有第二帧", stats.Describe(), StringComparison.Ordinal);
        Assert.InRange(stats.FirstFrameMs, 200d, 300d);
    }

    [Fact]
    public void FramePacingSeparatesDecoderOverrunsFromDroppedFrames()
    {
        var stats = new FramePacingStats(targetFps: 30d);
        stats.Record(1_000L);
        stats.Record(1_000L + (long)(Stopwatch.Frequency * 0.0167));
        stats.RecordOverrun(95d);
        stats.RecordOverrun(120d);

        Assert.Equal(2, stats.Overruns);
        Assert.Equal(120d, stats.MaxOverrunMs);
        Assert.Contains("解码超时", stats.Describe(), StringComparison.Ordinal);
        // 超时不该冒充掉帧：它们的修改方向完全不同。
        Assert.Equal(0, stats.DroppedFrames);
    }

    [Fact]
    public void FramePacingRetargetChangesDropCriterionAndDescription()
    {
        // v0.63：Windows 取帧路线解码跟不上时把目标从 30 降到 ≈10 fps，
        // 之后 ~95 ms 的间隔就是「正常节奏」而不是掉帧，摘要里要看得出当前目标。
        var stats = new FramePacingStats(targetFps: 30d);
        Assert.Equal(30d, stats.TargetFps);

        stats.Retarget(10d);
        Assert.Equal(10d, stats.TargetFps);

        var step = (long)(Stopwatch.Frequency * 0.095); // 95 ms ≈ 10.5 fps 的正常间隔
        stats.Record(1_000L);
        for (var i = 0; i < 6; i++)
            stats.Record(1_000L + step * (i + 1));
        Assert.Equal(0, stats.DroppedFrames);
        Assert.Contains("目标 10 fps", stats.Describe());

        // 升回 30 fps 后，同样的 95 ms 间隔又会被如实记成掉帧。
        stats.Retarget(30d);
        stats.Record(1_000L + step * 10 + (long)(Stopwatch.Frequency * 0.095));
        Assert.Equal(1, stats.DroppedFrames);

        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Retarget(0d));
    }

    // ── UI 停顿 ───────────────────────────────────────────────────────────

    [Fact]
    public void UiStallRecorderMeasuresOnlyTheExcessOverThePlannedInterval()
    {
        var recorder = new UiStallRecorder(intervalMs: 100d, stallThresholdMs: 50d);
        var now = 1_000L;
        recorder.Record(now);
        now += (long)(Stopwatch.Frequency * 0.1);   // 准时：不算停顿
        recorder.Record(now);
        now += (long)(Stopwatch.Frequency * 0.35);  // 卡了 350 ms → 停顿 250 ms
        recorder.Record(now);
        now += (long)(Stopwatch.Frequency * 0.02);  // 提前醒来：不算停顿，但会成为下一次间隔的起点
        recorder.Record(now);

        Assert.Equal(1, recorder.LongStalls);
        Assert.InRange(recorder.MaxStallMs, 230d, 270d);
        Assert.Contains("界面停顿", recorder.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void UiStallRecorderSaysUnsampledInsteadOfZero()
    {
        var recorder = new UiStallRecorder();
        Assert.False(recorder.HasSamples);
        Assert.Contains("未采样", recorder.Describe(), StringComparison.Ordinal);
    }

    // ── 进度节流 ──────────────────────────────────────────────────────────

    [Fact]
    public void ProgressThrottleCapsDispatchRateAndAlwaysSendsTheFinalOne()
    {
        var throttle = new ProgressThrottle(minimumIntervalMs: 200d);
        var now = 1_000L;
        var dispatched = 0;

        // 模拟「每 25 篇报一次」的密集进度：1 万篇 = 400 次调用，全发生在 2 秒内。
        for (var i = 0; i < 400; i++)
        {
            now += (long)(Stopwatch.Frequency * 0.005); // 每 5 ms 一次
            if (throttle.ShouldDispatch(now))
                dispatched++;
        }
        Assert.InRange(dispatched, 1, 12);

        // 终态必须派发，否则进度条停在 99%。
        Assert.True(throttle.ShouldDispatch(now, isFinal: true));
        Assert.Equal(dispatched + 1, throttle.DispatchCount);
    }

    [Fact]
    public void IndexRebuildTelemetryDescribesThroughputPerMinute()
    {
        var telemetry = new IndexRebuildTelemetry(ElapsedMs: 2_000d, WrittenCount: 300, ProgressDispatchCount: 10, CandidateCount: 1_000);
        Assert.InRange(telemetry.DocumentsPerMinute, 29_000d, 31_000d);
        // 文案里的数字按不变文化格式化（不随系统区域变逗号/空格），所以断言写成 1000。
        Assert.Contains("1000 篇", telemetry.Describe(), StringComparison.Ordinal);
        Assert.False(IndexRebuildTelemetry.None.HasData);
    }
}

/// <summary>
/// v0.60：AI 搜索的查询解析与字面打分。用户报的两个具体查询（「视频」「笔吧验机」）
/// 在这里直接变成断言——它们失败，用户就一定会再看到一堆无关结果。
/// </summary>
public sealed class SemanticQueryTests
{
    [Fact]
    public void ChineseQueryWithoutSpacesIsSplitIntoBigrams()
    {
        var plan = SemanticQuery.Parse("笔吧验机");
        // 整段保留 + 二元组：「笔吧」「吧验」「验机」。没有二元组，「笔吧验机」永远匹配不到
        // 只含「验机」的文件名——这正是用户报的问题。
        Assert.Contains("笔吧验机", plan.Terms);
        Assert.Contains("验机", plan.Terms);
        Assert.Contains("笔吧", plan.Terms);
    }

    [Fact]
    public void TypeWordsBecomeKindHintsNotNoiseTerms()
    {
        var plan = SemanticQuery.Parse("视频");
        Assert.Contains(FileKind.Video, plan.KindHints);
        // 类型词不该再当字面词：否则「视频」只能命中文件名里真写了这两个字的文件。
        Assert.DoesNotContain("视频", plan.Terms);

        Assert.Contains(FileKind.Image, SemanticQuery.Parse("找一些照片").KindHints);
        Assert.Contains(FileKind.Archive, SemanticQuery.Parse("zip 备份").KindHints);
        Assert.Contains(FileKind.Code, SemanticQuery.Parse("源码").KindHints);
        // 扩展名直接敲进来也算类型意图（走 FileKindClassifier，不另立一张表）。
        Assert.Contains(FileKind.Video, SemanticQuery.Parse("mp4").KindHints);
        Assert.Contains(FileKind.Document, SemanticQuery.Parse("pdf").KindHints);
        // 既可能是主题、也可能是类型的词不做类型推断，否则一堆文件会凭空排到前面。
        Assert.Empty(SemanticQuery.Parse("季度报告").KindHints);
    }

    [Fact]
    public void SingleCharactersAndPunctuationDoNotBecomeTerms()
    {
        var plan = SemanticQuery.Parse("的 ！！ a 视频");
        Assert.DoesNotContain("的", plan.Terms);
        Assert.DoesNotContain("a", plan.Terms);
        Assert.Contains(FileKind.Video, plan.KindHints);
    }

    [Fact]
    public void LexicalScoreRanksExactNameAboveFolderAndExtensionMatches()
    {
        var terms = new[] { "验机" };

        // 名字就是这个词（无扩展名的文件 / 文件夹）：最高分。
        var exact = SemanticQuery.LexicalScore("验机", "downloads", terms, out var source);
        Assert.Equal(1f, exact);
        Assert.Equal(SemanticHitMatch.Name, source);

        // 主名完全相同（带扩展名）：次高，仍高于「包含」。
        var stemExact = SemanticQuery.LexicalScore("验机.mp4", "downloads", terms, out source);
        Assert.Equal(0.95f, stemExact);
        Assert.Equal(SemanticHitMatch.Name, source);

        var contains = SemanticQuery.LexicalScore("笔吧验机全流程.mp4", "downloads", terms, out source);
        Assert.InRange(contains, 0.7f, 0.95f);
        Assert.Equal(SemanticHitMatch.Name, source);

        var folder = SemanticQuery.LexicalScore("episode-12.mp4", "笔吧验机", terms, out source);
        Assert.InRange(folder, 0.5f, 0.7f);
        Assert.Equal(SemanticHitMatch.Folder, source);

        Assert.Equal(0f, SemanticQuery.LexicalScore("holiday.mp4", "pictures", terms, out _));
    }

    [Fact]
    public void IndexTextCarriesKindWordsSoTypeQueriesHaveSomethingToMatch()
    {
        // 索引文本里必须出现「视频」，否则重建后的向量对「视频」这种词毫无区分度。
        Assert.Contains("视频", SemanticQuery.KindWord(FileKind.Video), StringComparison.Ordinal);
        Assert.Contains("图片", SemanticQuery.KindWord(FileKind.Image), StringComparison.Ordinal);
        Assert.Contains("文件夹", SemanticQuery.KindWord(FileKind.Folder), StringComparison.Ordinal);
    }

    [Fact]
    public void TermsAreCappedSoHugeQueriesStayLinear()
    {
        var plan = SemanticQuery.Parse(string.Join(' ', Enumerable.Repeat("关键字", 200)));
        Assert.True(plan.Terms.Count <= SemanticQuery.MaxTerms);
    }
}
