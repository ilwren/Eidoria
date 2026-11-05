namespace MdExplorer.Application;

/// <summary>
/// 批量重命名规则。Pattern 占位符：{name}=原名（去扩展名，查找替换之后）、{ext}=扩展名（不含点）、
/// {n}=计数、{n:W}=计数补零至 W 位（1–6）。Pattern 不含 {ext} 时自动保留原扩展名，防误删后缀。
/// CaseMode：Keep（原样）| Lower | Upper，作用于最终名主干与扩展名整体。
/// </summary>
public sealed record BatchRenameOptions(
    string Pattern,
    string Find,
    string Replace,
    int Start,
    int Step,
    string CaseMode)
{
    public static BatchRenameOptions Default => new("{name}_{n}", "", "", 1, 1, "Keep");
}

/// <summary>一行预览：Proposed 为拟定新名；Error 非空表示该行不可应用（整批阻断）。</summary>
public sealed record BatchRenameRow(string Original, string Proposed, string? Error)
{
    public bool IsChanged => !string.Equals(Original, Proposed, StringComparison.Ordinal);
}

/// <summary>
/// 纯逻辑批量重命名规划器：展开规则并做三层校验（单名合法性 / 批内查重 / 与同目录未选条目撞名）。
/// 大小写比较一律 OrdinalIgnoreCase（与 NTFS/APFS 语义对齐，Linux 上偏保守可接受）。
/// </summary>
public static class BatchRenamePlanner
{
    public static IReadOnlyList<BatchRenameRow> Plan(
        IReadOnlyList<string> names,
        BatchRenameOptions options,
        IReadOnlyCollection<string> siblingNames)
    {
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var siblings = new HashSet<string>(
            siblingNames.Where(s => !selected.Contains(s)),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<BatchRenameRow>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var original = names[i];
            var proposed = Expand(original, options, options.Start + options.Step * i);
            var error = NameValidator.Validate(proposed)
                        ?? (siblings.Contains(proposed) ? "与已存在项目同名" : null);
            rows.Add(new BatchRenameRow(original, proposed, error));
        }

        // 批内查重（与自身原名相同不算冲突，大小写不敏感地两两比对）
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            seen[row.Proposed] = seen.GetValueOrDefault(row.Proposed) + 1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Error is null && seen[rows[i].Proposed] > 1)
                rows[i] = rows[i] with { Error = "与批量内其他新名重复" };
        }
        return rows;
    }

    internal static string Expand(string original, BatchRenameOptions options, int counter)
    {
        var extWithDot = Path.GetExtension(original);
        var stem = original[..^extWithDot.Length];
        if (options.Find.Length > 0)
            stem = stem.Replace(options.Find, options.Replace, StringComparison.Ordinal);
        var ext = extWithDot.TrimStart('.');

        var pattern = options.Pattern;
        var hasExtToken = pattern.Contains("{ext}", StringComparison.Ordinal);
        var result = pattern
            .Replace("{name}", stem, StringComparison.Ordinal)
            .Replace("{ext}", ext, StringComparison.Ordinal);
        result = ReplaceCounter(result, counter);
        if (!hasExtToken && extWithDot.Length > 0)
            result += extWithDot;

        return options.CaseMode switch
        {
            "Lower" => result.ToLowerInvariant(),
            "Upper" => result.ToUpperInvariant(),
            _ => result,
        };
    }

    private static string ReplaceCounter(string pattern, int counter)
    {
        // 支持 {n} 与 {n:W}（W=1..6 数字）；自研扫描，避免正则（AOT 下 Regex 解释执行亦可，但简单扫描更确定）
        var result = pattern;
        while (true)
        {
            var start = result.IndexOf("{n", StringComparison.Ordinal);
            if (start < 0) return result;
            var end = result.IndexOf('}', start);
            if (end < 0) return result;
            var token = result[(start + 2)..end]; // "" 或 ":W"
            string text;
            if (token.Length == 0)
            {
                text = counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (token.Length == 2 && token[0] == ':' && token[1] is >= '1' and <= '6')
            {
                text = counter.ToString("D" + token[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                return result; // 未知占位符原样保留，交 NameValidator 拦截花括号外的问题（或留待用户修正）
            }
            result = string.Concat(result.AsSpan(0, start), text, result.AsSpan(end + 1));
        }
    }
}
