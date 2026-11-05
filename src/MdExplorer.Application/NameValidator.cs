namespace MdExplorer.Application;

/// <summary>
/// 跨平台取交集的文件名校验（触盘前拦截）。规则：非空、无路径分隔与控制字符、无 Windows 保留名/禁用字符、长度 ≤ 255。
/// </summary>
public static class NameValidator
{
    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "名称不能为空";
        var trimmed = name.Trim();
        if (trimmed is "." or "..") return "名称不合法";
        if (trimmed.Length > 255) return "名称过长（上限 255 字符）";
        if (trimmed.IndexOfAny(InvalidChars) >= 0) return "名称不能包含 / \\ : * ? \" < > |";
        if (trimmed.Any(char.IsControl)) return "名称不能包含控制字符";
        if (trimmed.EndsWith(' ') || trimmed.EndsWith('.')) return "名称不能以空格或点结尾（Windows 限制）";
        var stem = Path.GetFileNameWithoutExtension(trimmed);
        if (Reserved.Contains(stem)) return "该名称为系统保留名";
        return null;
    }
}
