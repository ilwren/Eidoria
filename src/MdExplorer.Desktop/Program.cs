using Avalonia;
#if WINDOWS
using Avalonia.Win32;
#endif
#if MDEXPLORER_LIBMPV
using MdExplorer.UI.Media;
#endif
using MdExplorer.UI.Services;

namespace MdExplorer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if MDEXPLORER_LIBMPV
        // Install the optional control factory up front; native search-path configuration stays
        // inside the selected service constructor, so the System route never mutates process PATH.
        MediaControlBridge.Factory = static service => service is LibMpvMediaPlaybackService mpv
            ? new LibMpvVideoControl(mpv)
            : null;
#endif

#if WINDOWS
        // 必须在创建窗口前固定进程身份；快捷方式注册延迟到用户首次真正发送通知时。
        WindowsNotificationRegistration.TrySetProcessAppUserModelId();
        MdSystemNotificationServiceAdapter.PlatformPreparation = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsNotificationRegistration.RegisterShortcutForCurrentUser();
            return Task.CompletedTask;
        };
#endif
        // Windows 设置可在系统 Media Foundation（保留现有帧推送路线）与 libmpv 直接 renderer 之间选择。
        // Linux 默认仍用发行版 GStreamer；Android 在 Activity 注册自己的系统 MediaPlayer 服务。
        AppHost.MediaPlaybackFactory = static settings =>
        {
#if MDEXPLORER_LIBMPV
            if (OperatingSystem.IsWindows() && string.Equals(settings.MediaBackend, "LibMpv", StringComparison.Ordinal))
                return new LibMpvMediaPlaybackService();
#else
            if (OperatingSystem.IsWindows() && string.Equals(settings.MediaBackend, "LibMpv", StringComparison.Ordinal))
                return new MdExplorer.Infrastructure.UnsupportedMediaPlaybackService(
                    "当前 Native AOT 构建未包含尚未验证 AOT/Trim 的 libmpv wrapper；请使用普通桌面构建比较，或改回系统 API。");
#endif
#if WINDOWS
            return new WindowsMediaPlaybackService();
#else
            if (OperatingSystem.IsLinux())
                return new GStreamerMediaPlaybackService();
            return new MdExplorer.Infrastructure.UnsupportedMediaPlaybackService(
                "此桌面平台没有注册内嵌媒体后端。可用系统应用打开媒体文件。");
#endif
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<MdExplorer.UI.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
#if WINDOWS && MDEXPLORER_LIBMPV
        // MpvView's EGL renderer requires an OpenGL-capable Avalonia backend; retain a software
        // fallback so the shell can still start on machines where ANGLE/EGL is unavailable.
        builder.With(new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
        });
#endif
        return builder;
    }
}
