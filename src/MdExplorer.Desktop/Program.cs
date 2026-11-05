using Avalonia;
using MdExplorer.UI.Services;

namespace MdExplorer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
        // 系统内置媒体栈：Windows 走 Media Foundation（WinRT MediaPlayer），
        // Linux 走发行版自带的 GStreamer；两端都不捆绑第三方原生库。
        // 音频两端都已接线；视频画面也都在应用内：Linux 走 appsink 推帧（Wayland 没有可交给
        // 解码器的窗口 id），Windows 走 MediaComposition 取帧。两端共用同一条 IMediaFrameSink。
#if WINDOWS
        AppHost.MediaPlaybackFactory = static () => new WindowsMediaPlaybackService();
#else
        if (OperatingSystem.IsLinux())
            AppHost.MediaPlaybackFactory = static () => new GStreamerMediaPlaybackService();
#endif
        // 可选第二条路径：用户自己放的 LGPL-only libmpv（不随包分发、不联网下载）。
        // 两条都注册后由 MediaBackendSelector 按用户偏好 + 可用性裁决，结论写进预览卡诊断。
        // libmpv 不存在时这是零成本——原生库探测失败即降级，连画面控件都不创建。
        AppHost.MpvPlaybackFactory = static () => new MpvMediaPlaybackService();
        // 裁定 libmpv 的搜索路径（MDEXPLORER_LIBMPV_ROOT）在创建控件之前完成一次即可。
        MpvNativeLibrary.ConfigureRootPath();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<MdExplorer.UI.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
