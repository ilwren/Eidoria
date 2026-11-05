using System.Runtime.InteropServices;
using Md3.Avalonia.Notifications;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>
/// Md3.Avalonia.Notifications 到本项目平台端口的适配。Md3 包只负责原生投递，Windows 的
/// AppUserModelID 快捷方式注册与 Android 13+ 运行时权限都必须由宿主完成；宿主通过
/// <see cref="PlatformPreparation"/> 注入一次性准备动作，适配器负责缓存结果并把原生异常
/// 收敛为可呈现的 <see cref="SystemNotificationResult"/>。
/// </summary>
public sealed class MdSystemNotificationServiceAdapter : ISystemNotificationService
{
    public const string ApplicationName = "Eidoria";
    public const string WindowsAppUserModelId = "MdExplorer.Eidoria";
    public const string AndroidChannelId = "eidoria.tasks";
    public const string AndroidChannelName = "任务与自动化";

    /// <summary>桌面/Android 宿主注入的首次发送准备动作（注册或请求权限）；测试与无头宿主可为空。</summary>
    public static Func<CancellationToken, Task>? PlatformPreparation { get; set; }

    private readonly Func<IMdSystemNotificationService> _backendFactory;
    private readonly Func<CancellationToken, Task>? _platformPreparationOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMdSystemNotificationService? _backend;
    private bool _platformPrepared;
    private SystemNotificationResult? _platformPreparationFailure;

    public MdSystemNotificationServiceAdapter()
        : this(CreateDefaultBackend, null)
    {
    }

    internal MdSystemNotificationServiceAdapter(
        Func<IMdSystemNotificationService> backendFactory,
        Func<CancellationToken, Task>? platformPreparationOverride)
    {
        _backendFactory = backendFactory;
        _platformPreparationOverride = platformPreparationOverride;
    }

    public string PlatformName
    {
        get
        {
            if (OperatingSystem.IsAndroid()) return "Android";
            if (OperatingSystem.IsWindows()) return "Windows";
            if (OperatingSystem.IsLinux()) return "Linux";
            if (OperatingSystem.IsMacOS()) return "macOS";
            return "Unknown";
        }
    }

    public string SupportDescription
    {
        get
        {
            if (OperatingSystem.IsAndroid())
                return "Android 系统通知；首次发送时会请求通知权限，拒绝后可在系统应用设置中重新允许。";
            if (OperatingSystem.IsWindows())
            {
                return HasPlatformPreparation
                    ? "Windows Toast；首次发送时会为当前用户注册 Eidoria 开始菜单快捷方式与 AppUserModelID。"
                    : "当前构建未接入 Windows Toast 宿主注册；请使用在 Windows 上构建的桌面版本。";
            }
            if (OperatingSystem.IsLinux())
                return "Linux 桌面通知；需要 libnotify 与正在运行的 freedesktop 通知服务。";
            if (OperatingSystem.IsMacOS())
                return "macOS 系统通知可由 Md3 后端投递，但 macOS 不是 Eidoria 的正式目标平台。";
            return "当前平台没有可用的系统通知后端。";
        }
    }

    private bool HasPlatformPreparation =>
        _platformPreparationOverride is not null || PlatformPreparation is not null;

    public async Task<SystemNotificationResult> ShowAsync(
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return SystemNotificationResult.Failed("系统通知标题不能为空。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preparation = await PreparePlatformAsync(cancellationToken).ConfigureAwait(false);
            if (preparation is not null)
                return preparation;

            _backend ??= _backendFactory();
            await _backend.ShowAsync(
                new MdSystemNotification(title.Trim(), body ?? string.Empty),
                cancellationToken).ConfigureAwait(false);
            return SystemNotificationResult.Sent();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MdSystemNotificationPermissionException ex)
        {
            return SystemNotificationResult.PermissionDenied(Describe(ex));
        }
        catch (PlatformNotSupportedException ex)
        {
            return SystemNotificationResult.Unsupported(Describe(ex));
        }
        catch (MdSystemNotificationException ex)
        {
            return ex.InnerException is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                ? SystemNotificationResult.Unsupported(Describe(ex))
                : SystemNotificationResult.Failed(Describe(ex));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return SystemNotificationResult.Unsupported(Describe(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return SystemNotificationResult.PermissionDenied(Describe(ex));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or COMException)
        {
            return SystemNotificationResult.Failed(Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SystemNotificationResult?> PreparePlatformAsync(CancellationToken cancellationToken)
    {
        if (_platformPrepared)
            return null;
        if (_platformPreparationFailure is not null)
            return _platformPreparationFailure;

        var preparation = _platformPreparationOverride ?? PlatformPreparation;
        if (preparation is null)
        {
            _platformPrepared = true;
            return null;
        }

        try
        {
            await preparation(cancellationToken).ConfigureAwait(false);
            _platformPrepared = true;
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _platformPreparationFailure = SystemNotificationResult.PermissionDenied(Describe(ex));
            return _platformPreparationFailure;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or COMException or PlatformNotSupportedException)
        {
            _platformPreparationFailure = SystemNotificationResult.Failed(
                $"系统通知平台准备失败：{Describe(ex)}");
            return _platformPreparationFailure;
        }
    }

    private static IMdSystemNotificationService CreateDefaultBackend() =>
        MdSystemNotificationService.CreateDefault(new MdSystemNotificationOptions
        {
            ApplicationName = ApplicationName,
            WindowsAppUserModelId = WindowsAppUserModelId,
            AndroidChannelId = AndroidChannelId,
            AndroidChannelName = AndroidChannelName,
        });

    private static string Describe(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Trim();
        if (exception.InnerException is { } inner &&
            !string.IsNullOrWhiteSpace(inner.Message) &&
            !message.Contains(inner.Message, StringComparison.Ordinal))
        {
            message = $"{message}（{inner.GetType().Name}: {inner.Message.Trim()}）";
        }
        return message;
    }
}
