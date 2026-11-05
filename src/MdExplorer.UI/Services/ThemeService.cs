using Avalonia;
using Avalonia.Styling;
using Md3.Avalonia.Themes.Dynamic;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>将 AppSettings 的主题部分落到 Application + MdThemeManager（HCT 动态配色）。</summary>
public static class ThemeService
{
    public static IReadOnlyList<(string Name, string Seed)> SeedPresets { get; } =
    [
        ("青", "#006A6A"),
        ("紫", "#6750A4"),
        ("蓝", "#0B57D0"),
        ("绿", "#146C2E"),
        ("橙", "#A85100"),
        ("粉", "#B3265E"),
        ("灰", "#5F6368"),
    ];

    public static void Apply(AppSettings settings)
    {
        if (Avalonia.Application.Current is not { } app)
            return;

        var mode = ParseMode(settings.ThemeMode);
        app.RequestedThemeVariant = mode switch
        {
            MdThemeMode.Light => ThemeVariant.Light,
            MdThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var dark = mode == MdThemeMode.Dark ||
                   mode == MdThemeMode.System && app.ActualThemeVariant == ThemeVariant.Dark;

        var options = new MdThemeOptions
        {
            SeedColor = string.IsNullOrWhiteSpace(settings.SeedColor) ? "#006A6A" : settings.SeedColor,
            ThemeMode = mode,
        };
        MdThemeManager.Apply(app, options, dark);
    }

    private static MdThemeMode ParseMode(string? value) => value switch
    {
        "Light" => MdThemeMode.Light,
        "Dark" => MdThemeMode.Dark,
        _ => MdThemeMode.System,
    };
}
