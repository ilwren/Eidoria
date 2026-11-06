using MdExplorer.Infrastructure.Ai;

namespace MdExplorer.Tests;

/// <summary>
/// v0.45 语义命中可信度：只影响「低置信」标注与状态文案，绝不改变结果集与排序。
/// 这些用例把两把尺子（绝对下限、相对第一名比例）固定下来，避免文案与门槛日后各走各的。
/// </summary>
public class SemanticSearchConfidenceTests
{
    [Fact]
    public void EmptyResultSetHasNoStrongMatch()
    {
        var confidence = SemanticSearchConfidence.Evaluate([]);

        Assert.False(confidence.HasStrongMatch);
        Assert.False(confidence.HasLowConfidence);
        Assert.Equal(0, confidence.TotalCount);
        Assert.Equal(0f, confidence.TopScore);
        Assert.Equal(SemanticSearchConfidence.MinimumMeaningfulScore, confidence.Floor);
    }

    [Fact]
    public void StrongSingleHitIsNotFlagged()
    {
        var confidence = SemanticSearchConfidence.Evaluate([0.62f]);

        Assert.True(confidence.HasStrongMatch);
        Assert.False(confidence.HasLowConfidence);
        Assert.Equal(0.62f, confidence.TopScore, 3);
        Assert.False(confidence.IsLowConfidence(0.62f));
    }

    [Fact]
    public void EverythingBelowTheFloorMeansNoStrongMatch()
    {
        // 真实无关文本在两个量化模型上多在 0.20–0.40：这条查询基本没找到东西，必须说出来。
        var confidence = SemanticSearchConfidence.Evaluate([0.31f, 0.28f, 0.19f]);

        Assert.False(confidence.HasStrongMatch);
        Assert.Equal(3, confidence.LowConfidenceCount);
        Assert.All([0.31f, 0.28f, 0.19f], score => Assert.True(confidence.IsLowConfidence(score)));
    }

    [Fact]
    public void TailingHitsFarBelowTheLeaderAreLowConfidence()
    {
        // 第一名 0.64 相关；0.41 远低于「第一名 × 0.85 = 0.544」，是陪跑项。
        var confidence = SemanticSearchConfidence.Evaluate([0.64f, 0.52f, 0.41f]);

        Assert.True(confidence.HasStrongMatch);
        Assert.Equal(1, confidence.LowConfidenceCount);
        Assert.False(confidence.IsLowConfidence(0.52f));
        Assert.True(confidence.IsLowConfidence(0.41f));
    }

    [Fact]
    public void RelativeFloorIsCappedByTheAbsoluteMinimum()
    {
        // 第一名只有 0.30 时，相对比例算出来 0.255，但绝对下限 0.45 更严格：不许「矮子里拔将军」。
        var confidence = SemanticSearchConfidence.Evaluate([0.30f, 0.29f]);

        Assert.Equal(SemanticSearchConfidence.MinimumMeaningfulScore, confidence.Floor, 3);
        Assert.False(confidence.HasStrongMatch);
        Assert.Equal(2, confidence.LowConfidenceCount);
    }

    [Fact]
    public void ThresholdIsInclusiveAtTheFloor()
    {
        var confidence = SemanticSearchConfidence.Evaluate([0.80f]);
        var floor = confidence.Floor;

        Assert.False(confidence.IsLowConfidence(floor));
        Assert.True(confidence.IsLowConfidence(MathF.BitDecrement(floor)));
    }

    [Fact]
    public void NonFiniteScoresNeverBecomeStrongMatches()
    {
        var confidence = SemanticSearchConfidence.Evaluate([float.NaN, float.PositiveInfinity]);

        Assert.False(confidence.HasStrongMatch);
        Assert.Equal(2, confidence.LowConfidenceCount);
        Assert.True(confidence.IsLowConfidence(float.NaN));
    }

    [Fact]
    public void LowConfidenceCountIsBoundedByTheResultCount()
    {
        var scores = new float[50];
        for (var index = 0; index < scores.Length; index++)
            scores[index] = 0.10f + (index * 0.002f);

        var confidence = SemanticSearchConfidence.Evaluate(scores);

        Assert.Equal(50, confidence.TotalCount);
        Assert.InRange(confidence.LowConfidenceCount, 0, scores.Length);
        Assert.False(confidence.HasStrongMatch);
    }

    [Fact]
    public void EvaluateRejectsNullScores()
    {
        Assert.Throws<ArgumentNullException>(() => SemanticSearchConfidence.Evaluate(null!));
    }
}
