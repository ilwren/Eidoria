using System.Globalization;

namespace MdExplorer.Domain;

/// <summary>大小与时间的展示格式化（十进制单位、当前区域日期；纯函数可测）。</summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        double value = bytes.Value;
        int unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0
            ? $"{bytes.Value} B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string DateTime(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var today = now.Date;
        if (local.Date == today)
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1))
            return "昨天 " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Year == now.Year)
            return local.ToString("M月d日", CultureInfo.CurrentCulture);
        return local.ToString("yyyy/M/d", CultureInfo.CurrentCulture);
    }

    public static string FullDateTime(DateTimeOffset value) =>
        value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
