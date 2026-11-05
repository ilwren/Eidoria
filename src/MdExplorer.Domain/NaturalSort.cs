namespace MdExplorer.Domain;

/// <summary>自然序比较：数字段按数值比较（"2" 在 "10" 前），其余不区分大小写逐字比较。</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix], cy = y[iy];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                // 取两侧完整数字段，跳前导零后先比位数再逐位比
                int sx = ix, sy = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;
                var numX = x.AsSpan(sx, ix - sx).TrimStart('0');
                var numY = y.AsSpan(sy, iy - sy).TrimStart('0');
                if (numX.Length != numY.Length) return numX.Length.CompareTo(numY.Length);
                int cmp = numX.CompareTo(numY, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                // 数值相等时继续向后位，前导零个数差异最后由全串长度裁决
            }
            else
            {
                int cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (cmp != 0) return cmp;
                ix++; iy++;
            }
        }
        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}

/// <summary>文件条目排序（文件夹在前可配、自然序、多键回退到名称）。</summary>
public static class FileItemOrdering
{
    public static List<FileItem> Sort(IEnumerable<FileItem> items, SortField field, SortDirection direction, bool foldersFirst = true)
    {
        var list = items.ToList();
        var nameComparer = NaturalStringComparer.Instance;
        list.Sort((a, b) =>
        {
            if (foldersFirst && a.IsFolder != b.IsFolder)
                return a.IsFolder ? -1 : 1;

            int cmp = field switch
            {
                SortField.Name => nameComparer.Compare(a.Name, b.Name),
                SortField.LastWriteTime => a.LastWriteTime.CompareTo(b.LastWriteTime),
                SortField.Size => (a.Length ?? -1).CompareTo(b.Length ?? -1),
                SortField.Kind => string.Compare(
                    a.IsFolder ? "" : a.Extension ?? "",
                    b.IsFolder ? "" : b.Extension ?? "",
                    StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            if (cmp == 0 && field != SortField.Name)
                cmp = nameComparer.Compare(a.Name, b.Name);
            return direction == SortDirection.Ascending ? cmp : -cmp;
        });
        return list;
    }
}
