namespace MdExplorer.Application;

/// <summary>面包屑分段等纯展示逻辑（不做 IO）。</summary>
public static class PathText
{
    public sealed record Segment(string Name, string FullPath);

    /// <summary>将绝对路径拆为面包屑段（根段名为盘符/根符号）。</summary>
    public static IReadOnlyList<Segment> Segments(string absolutePath)
    {
        var segments = new List<Segment>();
        var root = Path.GetPathRoot(absolutePath) ?? string.Empty;
        var rest = absolutePath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        segments.Add(new Segment(DisplayRoot(root, absolutePath), root.Length == 0 ? absolutePath : root));
        var current = root;
        foreach (var part in rest)
        {
            current = current.Length == 0 ? part : Path.Combine(current, part);
            segments.Add(new Segment(part, current));
        }

        return segments;
    }

    private static string DisplayRoot(string root, string fullPath)
    {
        if (root.Length == 0) return fullPath;
        // Windows "C:\" 显 "C:"；Linux "/" 显 "根目录" 更友好时保留根符号给桌面用户识别，采用根符号原样。
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } trimmed
            ? trimmed
            : root;
    }

    /// <summary>冲突保留两者时的新名字：name (2).ext、name (3).ext …（不查盘，序号由调用方递增探测）。</summary>
    public static string KeepBothName(string fileName, int index)
    {
        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        return $"{stem} ({index}){ext}";
    }
}
