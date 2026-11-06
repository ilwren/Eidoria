namespace MdExplorer.Domain;

/// <summary>
/// 关键词搜索的相关性打分（v0.50）。纯函数、零 I/O：只看文件名与查询串决定先后，
/// 所以同一份结果在桌面、Android、测试与截图里顺序完全一致。
/// <para>
/// 原先文件名索引按「名称字母序」返回，等于没有相关性判断：搜「预算」时
/// <c>预算.md</c>、<c>2024 预算复盘.md</c>、<c>旧预算草稿备份.md</c> 三者的先后与查询无关。
/// 这里按「用户心里的相关度」分档：完全同名 &gt; 主名相同 &gt; 主名以它开头 &gt; 文件名以它开头
/// &gt; 主名的某个词从它开始 &gt; 主名包含 &gt; 只有扩展名里包含。
/// </para>
/// <para>
/// 分档而不是加权累加：好处是顺序可解释（每一档都能对用户说清「为什么它排前面」），
/// 也不会因为某个文件名字更长就被莫名其妙地排到前面。
/// </para>
/// </summary>
public static class SearchRanking
{
    /// <summary>文件名（含扩展名）与查询完全相同。</summary>
    public const int ExactName = 1_000;

    /// <summary>去掉扩展名后与查询完全相同。</summary>
    public const int ExactStem = 950;

    /// <summary>去扩展名后以查询开头。</summary>
    public const int StemPrefix = 880;

    /// <summary>完整文件名以查询开头（命中落在扩展名上时才会走到这里）。</summary>
    public const int NamePrefix = 840;

    /// <summary>主名里有一个「词」从查询处开始（前一个字符是空格/点/下划线/括号等分隔符）。</summary>
    public const int StemSegmentPrefix = 780;

    /// <summary>主名中间包含查询。</summary>
    public const int StemContains = 700;

    /// <summary>只有完整文件名（通常就是扩展名）包含查询。</summary>
    public const int NameContains = 460;

    /// <summary>不匹配。</summary>
    public const int NoMatch = 0;

    /// <summary>
    /// 返回 0（不匹配）到 1000（完全相同）之间的档位分。
    /// <para>
    /// v0.52：查询里的空格是 **AND**——每个词都得在文件名里出现，档位分取**最弱的那个词**。
    /// 「任何一个词匹配得差，整体就往后排」符合直觉，也不用发明一套加权公式。
    /// </para>
    /// </summary>
    public static int Score(string? name, string? query)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(query))
            return NoMatch;

        var terms = SplitTerms(query);
        if (terms.Count == 0)
            return NoMatch;
        if (terms.Count == 1)
            return ScoreTerm(name, terms[0]);

        var score = int.MaxValue;
        foreach (var term in terms)
        {
            var single = ScoreTerm(name, term);
            if (single == NoMatch)
                return NoMatch;
            score = Math.Min(score, single);
        }
        return score;
    }

    /// <summary>是否命中（等价于 <see cref="Score"/> 大于 0）：索引侧用它替换整串子串匹配。</summary>
    public static bool Matches(string? name, string? query) => Score(name, query) > NoMatch;

    /// <summary>按空白切词；连续空白、首尾空白都吃掉。</summary>
    public static List<string> SplitTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        return [.. query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static int ScoreTerm(string name, string needle)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var inName = name.Contains(needle, StringComparison.OrdinalIgnoreCase);
        var inStem = stem.Contains(needle, StringComparison.OrdinalIgnoreCase);
        if (!inName && !inStem)
            return NoMatch;

        if (string.Equals(name, needle, StringComparison.OrdinalIgnoreCase))
            return ExactName;
        if (string.Equals(stem, needle, StringComparison.OrdinalIgnoreCase))
            return ExactStem;
        if (stem.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return StemPrefix;
        if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return NamePrefix;
        if (IsSegmentStart(stem, needle))
            return StemSegmentPrefix;
        return inStem ? StemContains : NameContains;
    }

    /// <summary>
    /// 按相关性排序：分数降序 → 名称（忽略大小写）→ 完整路径，后两级只为让同分结果有稳定顺序，
    /// 免得每次搜索都换个排法。
    /// </summary>
    public static List<FileItem> Rank(IEnumerable<FileItem> items, string? query)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items
            .Select(item => (Item: item, Score: Score(item.Name, query)))
            .Where(pair => pair.Score > NoMatch)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Item.FullPath, ComparePath)
            .Select(pair => pair.Item)];
    }

    private static StringComparer ComparePath =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>命中处的前一个字符若是分隔符（空白、点、下划线、连字符、括号等），算「词首命中」。</summary>
    private static bool IsSegmentStart(string stem, string needle)
    {
        var index = stem.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
            return false;
        var previous = stem[index - 1];
        // 下划线当分隔符（my_report 里的 report 算词首），汉字相邻则不算：
        // 搜「预算」时「旧预算」是包含，不是词首命中。
        return !char.IsLetterOrDigit(previous) || previous == '_';
    }
}
