using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Domain;

namespace MdExplorer.UI.ViewModels;

/// <summary>任务中心中的一项文件操作：排队/运行状态、进度、取消与完成结果都可追溯。</summary>
public enum OperationTaskStatus
{
    Queued,
    Running,
    WaitingConfirmation,
    Completed,
    Failed,
    Cancelled,
}

public sealed partial class OperationTaskViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Func<OperationOutcome, Task>? _retryFailed;

    internal OperationTaskViewModel(string title) => _title = title;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsWaitingConfirmation))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(HasResultSummary))]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private OperationTaskStatus _status = OperationTaskStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double _progressPercent;

    [ObservableProperty]
    private string _detail = "等待开始";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool _isDetailsExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private bool _isRetrying;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; private set; }

    public string TimeText => FinishedAt is { } finished
        ? $"{StartedAt:HH:mm:ss} – {finished:HH:mm:ss}"
        : $"开始于 {StartedAt:HH:mm:ss}";

    public string StatusText => Status switch
    {
        OperationTaskStatus.Queued => "排队中",
        OperationTaskStatus.Running => "运行中",
        OperationTaskStatus.WaitingConfirmation => "等待确认",
        OperationTaskStatus.Completed => "已完成",
        OperationTaskStatus.Failed => "未完全完成",
        OperationTaskStatus.Cancelled => "已取消",
        _ => string.Empty,
    };

    public bool IsActive => Status is OperationTaskStatus.Queued or OperationTaskStatus.Running or OperationTaskStatus.WaitingConfirmation;
    public bool IsFinished => !IsActive;
    public bool CanCancel => IsActive;
    public bool HasErrors => ErrorText is not null;
    public bool IsQueued => Status == OperationTaskStatus.Queued;
    public bool IsRunning => Status == OperationTaskStatus.Running;
    public bool IsWaitingConfirmation => Status == OperationTaskStatus.WaitingConfirmation;
    public bool IsCompleted => Status == OperationTaskStatus.Completed;
    public bool IsFailed => Status == OperationTaskStatus.Failed;
    public bool IsCancelled => Status == OperationTaskStatus.Cancelled;
    public string ProgressPercentText => $"{ProgressPercent:0}%";

    public IReadOnlyList<OperationError> Errors { get; private set; } = [];
    public IReadOnlyList<string> ProducedPaths { get; private set; } = [];
    public int SucceededCount { get; private set; }
    public int SkippedCount { get; private set; }
    public int ErrorCount => Errors.Count;
    public int ProducedCount => ProducedPaths.Count;
    internal OperationOutcome? LastOutcome { get; private set; }

    public bool HasResultSummary =>
        IsFinished && (SucceededCount > 0 || SkippedCount > 0 || ErrorCount > 0 || ProducedCount > 0);

    public string ResultSummaryText
    {
        get
        {
            var parts = new List<string>(4);
            if (SucceededCount > 0) parts.Add($"完成 {SucceededCount} 项");
            if (SkippedCount > 0) parts.Add($"跳过 {SkippedCount} 项");
            if (ErrorCount > 0) parts.Add($"失败 {ErrorCount} 项");
            if (ProducedCount > 0) parts.Add($"产物 {ProducedCount} 项");
            return string.Join(" · ", parts);
        }
    }

    public bool HasProducedPaths => ProducedCount > 0;
    public string ProducedText
    {
        get
        {
            if (ProducedCount == 0) return string.Empty;
            var text = string.Join("\n", ProducedPaths.Take(4).Select(path => $"· {path}"));
            return ProducedCount > 4 ? $"{text}\n…等共 {ProducedCount} 项" : text;
        }
    }

    public bool HasDetails => IsFinished && (ErrorCount > 0 || ProducedCount > 0 || SkippedCount > 0);
    public string DetailsButtonText => IsDetailsExpanded ? "收起详情" : "详情";
    public bool CanRetry =>
        IsFailed && !IsRetrying && _retryFailed is not null && LastOutcome?.Errors.Count > 0;

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal void SetRetryHandler(Func<OperationOutcome, Task>? retryFailed)
    {
        _retryFailed = retryFailed;
        OnPropertyChanged(nameof(CanRetry));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsActive)
            return;
        Detail = "正在取消…";
        _cancellation.Cancel();
    }

    [RelayCommand]
    private void ToggleDetails() => IsDetailsExpanded = !IsDetailsExpanded;

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (!CanRetry || _retryFailed is null || LastOutcome is null)
            return;

        var handler = _retryFailed;
        var outcome = LastOutcome;
        IsRetrying = true;
        try
        {
            await handler(outcome).ConfigureAwait(true);
            // 一次性消费旧任务的重试入口；新任务会携带自己的失败子集与重试入口。
            _retryFailed = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Detail = $"重试启动失败：{ex.Message}";
        }
        finally
        {
            IsRetrying = false;
            OnPropertyChanged(nameof(CanRetry));
        }
    }

    internal void MarkRunning()
    {
        if (Status != OperationTaskStatus.Queued)
            return;
        Status = OperationTaskStatus.Running;
        Detail = "正在准备…";
    }

    internal void MarkWaitingConfirmation(string detail)
    {
        if (Status is not (OperationTaskStatus.Queued or OperationTaskStatus.Running))
            return;
        Status = OperationTaskStatus.WaitingConfirmation;
        Detail = detail;
    }

    internal void ResumeAfterConfirmation()
    {
        if (Status != OperationTaskStatus.WaitingConfirmation)
            return;
        Status = OperationTaskStatus.Running;
        Detail = "正在继续…";
    }

    internal void Report(OperationProgress progress)
    {
        // Progress<T> 的回调是异步投递的；终态之后到达的旧进度不得把 100% 回退。
        if (Status is OperationTaskStatus.Completed or OperationTaskStatus.Failed or OperationTaskStatus.Cancelled)
            return;
        if (Status == OperationTaskStatus.Queued)
            Status = OperationTaskStatus.Running;
        ProgressPercent = Math.Clamp(progress.Fraction, 0, 1) * 100;
        Detail = progress.ItemsTotal > 0
            ? $"{progress.CurrentItemName}（{progress.ItemsDone}/{progress.ItemsTotal}）"
            : progress.CurrentItemName;
    }

    internal void Complete(OperationOutcome outcome)
    {
        FinishedAt = DateTimeOffset.Now;
        LastOutcome = outcome;
        Errors = outcome.Errors;
        ProducedPaths = outcome.ProducedPaths;
        SucceededCount = Math.Max(0, outcome.SucceededCount);
        SkippedCount = Math.Max(0, outcome.SkippedCount);
        IsDetailsExpanded = outcome.Errors.Count > 0;

        if (outcome.Cancelled)
        {
            Status = OperationTaskStatus.Cancelled;
            Detail = "任务已取消";
        }
        else if (outcome.Errors.Count > 0)
        {
            Status = OperationTaskStatus.Failed;
            ProgressPercent = 100;
            Detail = outcome.SucceededCount > 0 ? "部分项目未完成" : "任务未能完成";
            ErrorText = BuildErrorText(outcome.Errors);
        }
        else
        {
            Status = OperationTaskStatus.Completed;
            ProgressPercent = 100;
            Detail = outcome.SucceededCount == 0 && SkippedCount > 0
                ? "所选项目均已跳过"
                : "任务已完成";
        }

        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(ProducedPaths));
        OnPropertyChanged(nameof(SucceededCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(ProducedCount));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(HasResultSummary));
        OnPropertyChanged(nameof(HasProducedPaths));
        OnPropertyChanged(nameof(ProducedText));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(CanRetry));
    }

    private static string BuildErrorText(IReadOnlyList<OperationError> errors)
    {
        var text = string.Join("\n", errors.Take(20).Select(e => $"· {e.ItemName}：{e.Message}"));
        return errors.Count > 20 ? $"{text}\n…等共 {errors.Count} 项失败" : text;
    }

    public void Dispose() => _cancellation.Dispose();
}
