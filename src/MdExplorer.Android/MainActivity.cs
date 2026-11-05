using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using MdExplorer.UI.Media;
using MdExplorer.UI.Services;
using AvaloniaApp = MdExplorer.UI.App;

namespace MdExplorer.Android;

[Activity(
    Label = "Eidoria",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequestCode = 4102;
    private static MainActivity? _current;
    private TaskCompletionSource<bool>? _notificationPermissionCompletion;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _current = this;
        // 内嵌播放后端必须在 AppHost.Build（Avalonia 初始化）之前注册。
        AppHost.MediaPlaybackFactory = static () => new AndroidMediaPlaybackService();
        // 视频画面走宿主托管的原生窗口：TextureView 由 Avalonia 的 NativeControlHost 挂进视觉树并按控件定位。
        MediaSurfaceBridge.Factory = static parent => new AndroidVideoSurfaceAttachment(parent);
        MdSystemNotificationServiceAdapter.PlatformPreparation = static cancellationToken =>
            _current?.RequestNotificationPermissionAsync(cancellationToken)
            ?? Task.FromException(new InvalidOperationException("Android 前台 Activity 尚未就绪，无法请求通知权限。"));
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(_current, this))
        {
            _current = null;
            _notificationPermissionCompletion?.TrySetCanceled();
        }
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationPermissionRequestCode)
            return;

        _notificationPermissionCompletion?.TrySetResult(
            grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        _notificationPermissionCompletion = null;
    }

    private async Task RequestNotificationPermissionAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted)
            return;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _notificationPermissionCompletion = completion;
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            completion);

        RunOnUiThread(() => RequestPermissions(
            [global::Android.Manifest.Permission.PostNotifications],
            NotificationPermissionRequestCode));

        if (!await completion.Task.ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                "Android 通知权限未授予；可在系统设置中允许 Eidoria 发送通知。");
        }
    }
}

[Application]
public class MainApplication : AvaloniaAndroidApplication<AvaloniaApp>
{
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
