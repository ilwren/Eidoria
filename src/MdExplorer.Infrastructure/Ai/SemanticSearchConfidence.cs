namespace MdExplorer.Infrastructure.Ai;

/// <summary>
/// 语义命中可信度（v0.45）：只用于**提示**，绝不筛掉任何结果。
/// </summary>
/// <remarks>
/// 余弦相似度的绝对值随模型与量化方式漂移，写死一把「合格线」很容易误判；
/// 因此这里用「绝对下限 + 相对第一名的比例」双条件：
/// <list type="bullet">
/// <item><b>绝对下限</b> <see cref="MinimumMeaningfulScore"/>：BGE small zh / MiniLM L6 v2 量化模型上，
/// 真正相关的描述通常落在 0.48–0.70，无关文本多在 0.20–0.40；低于 0.45 视为「没有强匹配」。</item>
/// <item><b>相对比例</b> <see cref="RelativeFloorRatio"/>：低于第一名 85% 的尾部命中，多半只是陪跑。</item>
/// </list>
/// 两者取较大值即单条命中的低置信阈值。门槛只影响文案与标签，排序与结果集完全不变，
/// 所以模型换代最多让标注变保守，不会让搜索「查不到东西」。
/// </remarks>
public sealed record SemanticSearchConfidence(
    float TopScore,
    float Floor,
    int LowConfidenceCount,
    int TotalCount,
    bool HasStrongMatch)
{
    /// <summary>「没有强匹配」的绝对下限：低于它的最高分说明这次查询基本没找到相关文档。</summary>
    public const float MinimumMeaningfulScore = 0.45f;

    /// <summary>相对第一名的下限比例：低于「第一名 × 0.85」的命中标注为低置信。</summary>
    public const float RelativeFloorRatio = 0.85f;

    public bool HasLowConfidence => LowConfidenceCount > 0;

    /// <summary>单条命中是否低置信。TopScore 为 0（全部非有限值）时一律视为低置信。</summary>
    public bool IsLowConfidence(float score)
    {
        if (!float.IsFinite(score) || !float.IsFinite(TopScore) || TopScore <= 0f)
            return true;
        return score < Floor;
    }

    public static SemanticSearchConfidence Evaluate(IReadOnlyList<float> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var top = 0f;
        var finite = 0;
        foreach (var score in scores)
        {
            if (!float.IsFinite(score))
                continue;
            finite++;
            if (score > top)
                top = score;
        }

        var floor = MathF.Max(MinimumMeaningfulScore, top * RelativeFloorRatio);
        var low = 0;
        foreach (var score in scores)
        {
            if (!float.IsFinite(score) || score < floor)
                low++;
        }

        return new SemanticSearchConfidence(
            TopScore: top,
            Floor: floor,
            LowConfidenceCount: low,
            TotalCount: scores.Count,
            HasStrongMatch: finite > 0 && top >= MinimumMeaningfulScore);
    }
}
