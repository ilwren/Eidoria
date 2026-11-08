using MdExplorer.Domain;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>一条命中的「为什么在这里」来源（v0.60）。</summary>
public enum SemanticMatchSource
{
    /// <summary>纯向量相似度。</summary>
    Semantic,

    /// <summary>文件名里有查询词。</summary>
    Name,

    /// <summary>所在文件夹名里有查询词。</summary>
    Folder,

    /// <summary>查询点名了类型（「视频」「图片」），文件正是该类型。</summary>
    FileType,

    /// <summary>全文索引里的正文命中。</summary>
    Content,
}

/// <summary>
/// 一次语义查询的解析结果：字面词 + 用户点名的文件类型（v0.60）。
/// <para>
/// 纯向量检索对「视频」「笔吧验机」这种词无能为力：小模型 + 正文 4k 前缀截断下，
/// 相似度排序基本是随机的。用户真正想要的其实很朴素——文件名/文件夹里有这几个字，
/// 或者文件就是那种类型。所以先把查询拆成<b>可字面匹配</b>的词与<b>类型意图</b>，
/// 再与向量分数融合，并逐条告诉用户「凭什么把它排在这里」。
/// </para>
/// </summary>
public sealed record SemanticQueryPlan(
    IReadOnlyList<string> Terms,
    IReadOnlySet<FileKind> KindHints)
{
    public bool HasKindHints => KindHints.Count > 0;
    public bool HasTerms => Terms.Count > 0;

    /// <summary>字面命中与向量命中同时为空时，这条查询什么也找不到。</summary>
    public bool IsEmpty => !HasTerms && !HasKindHints;
}

/// <summary>
/// 查询解析与字面打分（v0.60）。零 I/O、零模型调用，所以顺序在任何平台、任何一次运行都一样。
/// <para>
/// 中文没有空格：「笔吧验机」整体不会出现在任何文件名里，但「验机」会出现。
/// 因此 CJK 连续段既保留整段，也展开成二元组（bigram）；英文/数字按词切。
/// 词数量有上限，避免超长查询把每次搜索变成 O(词数 × 条目数) 的字符串扫描。
/// </para>
/// </summary>
public static class SemanticQuery
{
    /// <summary>参与字面匹配的最大词数。超过就只留前 <c>MaxTerms</c> 个（按原顺序，靠前的更重要）。</summary>
    public const int MaxTerms = 24;

    /// <summary>字面命中的最低词长：单字（「的」「了」）噪声太大，不参与。</summary>
    public const int MinimumTermLength = 2;

    /// <summary>
    /// 类型词 → 文件类型。只收「明确在指文件类型」的词：用户说「视频」就是想要 mp4/mkv。
    /// 「报告」「笔记」「数据」这类既可能是类型、也可能是主题的词不进这张表——它们被当成
    /// 类型会让一堆 PDF/文本凭空排到前面，正好是用户抱怨的那种噪声。
    /// </summary>
    private static readonly (string Word, FileKind Kind)[] KindWords =
    [
        ("视频", FileKind.Video),
        ("影片", FileKind.Video),
        ("录像", FileKind.Video),
        ("电影", FileKind.Video),
        ("video", FileKind.Video),
        ("movie", FileKind.Video),
        ("图片", FileKind.Image),
        ("照片", FileKind.Image),
        ("图像", FileKind.Image),
        ("截图", FileKind.Image),
        ("image", FileKind.Image),
        ("photo", FileKind.Image),
        ("picture", FileKind.Image),
        ("音频", FileKind.Audio),
        ("音乐", FileKind.Audio),
        ("歌曲", FileKind.Audio),
        ("录音", FileKind.Audio),
        ("audio", FileKind.Audio),
        ("music", FileKind.Audio),
        ("压缩包", FileKind.Archive),
        ("压缩文件", FileKind.Archive),
        ("归档", FileKind.Archive),
        ("archive", FileKind.Archive),
        ("zip", FileKind.Archive),
        ("代码", FileKind.Code),
        ("源码", FileKind.Code),
        ("脚本", FileKind.Code),
        ("code", FileKind.Code),
        ("source", FileKind.Code),
        ("字体", FileKind.Font),
        ("font", FileKind.Font),
        ("程序", FileKind.Executable),
        ("安装包", FileKind.Executable),
        ("文件夹", FileKind.Folder),
        ("目录", FileKind.Folder),
        ("folder", FileKind.Folder),
    ];

    /// <summary>
    /// 用户直接敲扩展名（「mp4」「pdf」「7z」）也算类型意图。用现有的分类器判定，
    /// 不另立一张表——否则两处口径迟早分叉。
    /// </summary>
    private static FileKind KindOfExtension(string token)
    {
        var trimmed = token.TrimStart('.');
        if (trimmed.Length is < 1 or > 5 || !trimmed.All(char.IsAsciiLetterOrDigit))
            return FileKind.Unknown;
        return FileKindClassifier.Classify("probe." + trimmed);
    }

    /// <summary>把查询解析成字面词 + 类型意图。</summary>
    public static SemanticQueryPlan Parse(string? query)
    {
        var terms = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new HashSet<FileKind>();

        if (string.IsNullOrWhiteSpace(query))
            return new SemanticQueryPlan(terms, kinds);

        // 整串先入一次：用户明确输入「报告」这种已在类型词表里的词时，文件名里也该能命中。
        foreach (var (word, kind) in KindWords)
        {
            if (query.Contains(word, StringComparison.OrdinalIgnoreCase))
                kinds.Add(kind);
        }

        foreach (var raw in SplitSegments(query))
        {
            if (IsCjkRun(raw))
            {
                AddTerm(raw);
                if (raw.Length > 2)
                {
                    // 二元组：中文没有分词器，相邻两个字就是最小的可检索单位。
                    for (var i = 0; i + 2 <= raw.Length && terms.Count < MaxTerms; i++)
                        AddTerm(raw.Substring(i, 2));
                }
            }
            else
            {
                AddTerm(raw);
                var extensionKind = KindOfExtension(raw);
                if (extensionKind is not (FileKind.Unknown or FileKind.Folder))
                    kinds.Add(extensionKind);
            }

            if (terms.Count >= MaxTerms)
                break;
        }

        return new SemanticQueryPlan(terms, kinds);

        void AddTerm(string value)
        {
            value = value.Trim();
            if (value.Length < MinimumTermLength || terms.Count >= MaxTerms)
                return;
            // 类型词已经由 KindHints 表达，不必再当字面词重复加分。
            if (KindWords.Any(pair => string.Equals(pair.Word, value, StringComparison.OrdinalIgnoreCase) &&
                                      pair.Kind != FileKind.Unknown))
                return;
            if (seen.Add(value))
                terms.Add(value);
        }
    }

    /// <summary>按空白与常见分隔符切段；CJK 连续段单独成段。</summary>
    private static IEnumerable<string> SplitSegments(string query)
    {
        var buffer = new System.Text.StringBuilder(16);
        foreach (var ch in query)
        {
            if (IsCjk(ch))
            {
                if (buffer.Length > 0 && !IsCjk(buffer[^1]))
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                buffer.Append(ch);
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (buffer.Length > 0 && IsCjk(buffer[^1]))
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                buffer.Append(ch);
                continue;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\u3040' and <= '\u30FF';

    private static bool IsCjkRun(string value)
    {
        foreach (var ch in value)
        {
            if (!IsCjk(ch))
                return false;
        }
        return value.Length > 0;
    }

    /// <summary>
    /// 单条的字面相关度（0–1）。看文件名与所在文件夹名，不看正文——
    /// 正文由全文索引通道负责，向量通道负责「意思相近」。三路各管一段，互不冒充。
    /// </summary>
    public static float LexicalScore(
        string? name, string? parentFolder, IReadOnlyList<string> terms, out SemanticMatchSource source)
    {
        source = SemanticMatchSource.Semantic;
        if (string.IsNullOrWhiteSpace(name) || terms.Count == 0)
            return 0f;

        var stem = Path.GetFileNameWithoutExtension(name);
        var best = 0f;
        foreach (var term in terms)
        {
            var score = ScoreTerm(name, stem, term);
            if (score > best)
            {
                best = score;
                source = SemanticMatchSource.Name;
            }
        }

        if (!string.IsNullOrWhiteSpace(parentFolder))
        {
            foreach (var term in terms)
            {
                if (!parentFolder.Contains(term, StringComparison.OrdinalIgnoreCase))
                    continue;
                // 文件夹命中略低于同名文件命中：用户先想到的是文件名。
                var folderScore = 0.62f;
                if (folderScore > best)
                {
                    best = folderScore;
                    source = SemanticMatchSource.Folder;
                }
                break;
            }
        }

        return best;
    }

    private static float ScoreTerm(string name, string stem, string term)
    {
        if (string.Equals(name, term, StringComparison.OrdinalIgnoreCase))
            return 1f;
        if (string.Equals(stem, term, StringComparison.OrdinalIgnoreCase))
            return 0.95f;
        if (stem.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return 0.88f;
        if (stem.Contains(term, StringComparison.OrdinalIgnoreCase))
            return 0.78f;
        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return 0.72f;
        // 只有扩展名里包含（例如搜「mp」命中 .mp4）：最弱的一档，但比没有强。
        return name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 0.6f : 0f;
    }

    /// <summary>类型的用户语言名（写进索引文本，让「视频」这类词有东西可匹配）。</summary>
    public static string KindWord(FileKind kind) => kind switch
    {
        FileKind.Folder => "文件夹",
        FileKind.Image => "图片 图像 照片",
        FileKind.Video => "视频 影片 录像",
        FileKind.Audio => "音频 音乐 录音",
        FileKind.Document => "文档 报告",
        FileKind.Archive => "压缩包 归档",
        FileKind.Code => "代码 源码",
        FileKind.Text => "文本 笔记",
        FileKind.Data => "数据 表格",
        FileKind.Font => "字体",
        FileKind.Executable => "程序 可执行",
        _ => "其他",
    };
}
