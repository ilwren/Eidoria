namespace MdExplorer.Domain;

/// <summary>
/// 最近搜索词的纯逻辑（v0.53）：去重（忽略大小写）、最新在前、上限封顶、空白丢弃。
/// 放在 Domain 里只为一件事——这段规则能单测，而不必启动整个界面。
/// </summary>
public static class RecentSearches
{
    /// <summary>最多保留多少条。再多也没人点，白占设置文件体积。</summary>
    public const int DefaultCapacity = 8;

    /// <summary>
    /// 把 <paramref name="query"/> 推到最前面：已存在时先移走再插入（保留用户本次的写法），
    /// 超出上限从尾部裁掉。返回新列表，不改入参。
    /// </summary>
    public static List<string> Push(IReadOnlyList<string>? current, string? query, int capacity = DefaultCapacity)
    {
        var limit = Math.Max(1, capacity);
        var result = new List<string>(limit);
        var trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
            result.Add(trimmed);

        foreach (var existing in current ?? [])
        {
            if (string.IsNullOrWhiteSpace(existing))
                continue;
            var value = existing.Trim();
            // 忽略大小写去重：刚插入的那条也会在这里被挡掉，所以「再搜一次同样的词」不会变成两行。
            if (result.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (result.Count >= limit)
                break;
            result.Add(value);
        }

        return result;
    }

    /// <summary>夹到上限（读设置时用：手改 JSON 也可能塞进来很长的列表）。</summary>
    public static List<string> Normalize(IReadOnlyList<string>? current, int capacity = DefaultCapacity) =>
        Push(current, null, capacity);
}
