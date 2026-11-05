namespace MdExplorer.Platform.Abstractions;

/// <summary>系统通知投递结果。通知是文件操作的旁路能力，失败以结果返回，不把已完成的文件任务改判失败。</summary>
public enum SystemNotificationStatus
{
    Sent,
    Unsupported,
    PermissionDenied,
    Failed,
}

public sealed record SystemNotificationResult(SystemNotificationStatus Status, string Message)
{
    public bool Success => Status == SystemNotificationStatus.Sent;

    public static SystemNotificationResult Sent(string message = "系统通知已提交。") =>
        new(SystemNotificationStatus.Sent, message);

    public static SystemNotificationResult Unsupported(string message) =>
        new(SystemNotificationStatus.Unsupported, message);

    public static SystemNotificationResult PermissionDenied(string message) =>
        new(SystemNotificationStatus.PermissionDenied, message);

    public static SystemNotificationResult Failed(string message) =>
        new(SystemNotificationStatus.Failed, message);
}

/// <summary>
/// 操作系统原生通知端口。实现负责平台身份、运行时权限与原生 API 差异；UI/自动化层只提交
/// 标题与正文。操作系统可能因勿扰、权限或桌面服务缺失而抑制呈现，调用方不得把 Sent
/// 理解为用户已经看见。
/// </summary>
public interface ISystemNotificationService
{
    /// <summary>当前平台的简短名称（Windows/Linux/Android/macOS/Unknown）。</summary>
    string PlatformName { get; }

    /// <summary>面向设置的可用性与前置条件说明；不得为了生成文案而真正发送通知。</summary>
    string SupportDescription { get; }

    Task<SystemNotificationResult> ShowAsync(string title, string body, CancellationToken cancellationToken);
}
