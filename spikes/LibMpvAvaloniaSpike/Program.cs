using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;

namespace MdExplorer.LibMpvAvaloniaSpike;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (OperatingSystem.IsWindows())
        {
            builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
            });
        }

        return builder;
    }
}
