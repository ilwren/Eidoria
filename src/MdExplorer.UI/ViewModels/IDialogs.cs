using Avalonia.Media.Imaging;
using MdExplorer.Domain;

namespace MdExplorer.UI.ViewModels;

/// <summary>冲突对比中的一侧快照。缩略图由调用方按需解码并负责释放。</summary>
public sealed class ConflictItemSnapshot : IDisposable
{
    public ConflictItemSnapshot(
        string heading,
        FileItem item,
        string locationText,
        string? sha256 = null,
        Bitmap? thumbnail = null)
    {
        Heading = heading;
        Item = item;
        LocationText = locationText;
        Sha256 = sha256;
        Display = new FileItemViewModel(item) { Thumbnail = thumbnail };
    }

    public string Heading { get; }
    public FileItem Item { get; }
    public string LocationText { get; }
    public string? Sha256 { get; }
    public FileItemViewModel Display { get; }

    public string Name => Item.Name;
    public string FullPath => Item.FullPath;
    public string SizeText => Item.IsFolder ? "文件夹 · 大小未计算" : Formatting.Bytes(Item.Length);
    public string ModifiedText => Formatting.FullDateTime(Item.LastWriteTime);
    public string HashText
    {
        get
        {
            if (Item.IsFolder)
                return "文件夹不计算校验和";
            if (string.IsNullOrWhiteSpace(Sha256))
                return "SHA-256 未计算";
            var hash = Sha256.Trim();
            return hash.Length > 16
                ? $"SHA-256 {hash[..8].ToUpperInvariant()}…{hash[^4..].ToUpperInvariant()}"
                : $"SHA-256 {hash.ToUpperInvariant()}";
        }
    }

    public void Dispose()
    {
        Display.Thumbnail?.Dispose();
        Display.Thumbnail = null;
    }
}

/// <summary>一次顶层传输冲突的只读提示。Index 从 0 开始，TotalCount 为本批预检到的冲突总数。</summary>
public sealed record TransferConflictPrompt(
    ConflictItemSnapshot Source,
    ConflictItemSnapshot Target,
    int Index,
    int TotalCount,
    bool IsMove)
{
    public int RemainingAfterCurrent => Math.Max(0, TotalCount - Index - 1);
    public int RemainingIncludingCurrent => Math.Max(1, TotalCount - Index);
    public bool HasRemaining => RemainingAfterCurrent > 0;
    public string CountText => $"第 {Index + 1}/{Math.Max(1, TotalCount)} 个冲突";
    public string ApplyToRemainingText => $"应用到其余 {RemainingAfterCurrent} 项";
    public bool HashesMatch =>
        !string.IsNullOrWhiteSpace(Source.Sha256) &&
        string.Equals(Source.Sha256, Target.Sha256, StringComparison.OrdinalIgnoreCase);
    public string? ComparisonText
    {
        get
        {
            if (HashesMatch)
                return "SHA-256 相同，内容一致";
            if (!string.IsNullOrWhiteSpace(Source.Sha256) && !string.IsNullOrWhiteSpace(Target.Sha256))
                return "SHA-256 不同";
            if (Source.Item.Length is { } sourceLength &&
                Target.Item.Length is { } targetLength &&
                sourceLength != targetLength)
                return "大小不同";
            return null;
        }
    }
    public bool HasComparisonText => ComparisonText is not null;
}

public sealed record TransferConflictDecision(ConflictPolicy Policy, bool ApplyToRemaining);

/// <summary>
/// Shell 向各页 ViewModel 提供的交互面（弹窗/进度/Snackbar/打开文件）。
/// 语义对齐 DEVELOPMENT.md §5.7；呈现形态由 Shell 视图负责。
/// </summary>
public interface IDialogs
{
    /// <summary>文本输入弹窗；验证器返回非空表示错误并阻止关闭。取消返回 null。</summary>
    Task<string?> PromptAsync(string title, string? initialText, string confirmLabel,
        Func<string, string?>? validate = null);

    /// <summary>确认弹窗。</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);

    /// <summary>
    /// 逐项询问一个顶层同名冲突。默认保留两者；用户明确勾选后才把同一策略应用到剩余冲突。
    /// 取消返回 null，调用方不得继续本批传输。
    /// </summary>
    Task<TransferConflictDecision?> AskConflictAsync(TransferConflictPrompt prompt);

    /// <summary>属性弹窗（只读信息）。</summary>
    Task ShowPropertiesAsync(string title, string body);

    /// <summary>
    /// 批量重命名弹窗：规则编辑 + 实时预览校验。确认返回与 <paramref name="names"/> 一一对应的新名；
    /// 取消返回 null。<paramref name="siblingNames"/> 为同目录未选中条目名（撞名校验）。
    /// </summary>
    Task<IReadOnlyList<string>?> BatchRenameAsync(IReadOnlyList<string> names, IReadOnlyList<string> siblingNames);

    /// <summary>短提示（Snackbar 语义）。</summary>
    void Notify(string message);

    /// <summary>
    /// 在任务中心队列中运行文件写操作：自动处理串行排队、进度上报、单项取消、结果提示与错误明细留档。
    /// 返回操作结果供调用方决定刷新。只有调用方能精确界定失败子集时才传
    /// <paramref name="retryFailed"/>；处理函数收到上一轮结果并自行启动一个新的任务，避免整批盲目重跑。
    /// <paramref name="suppressSystemNotification"/> 用于工作流自身已有显式通知节点时，避免任务完成再重复通知。
    /// </summary>
    Task<OperationOutcome> RunOperationAsync(
        string title,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationOutcome>> operation,
        Func<OperationOutcome, Task>? retryFailed = null,
        bool suppressSystemNotification = false);
}
