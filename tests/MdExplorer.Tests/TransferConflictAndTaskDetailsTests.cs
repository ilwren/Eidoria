using System.Security.Cryptography;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>逐项冲突确认、等待确认状态、任务结果明细与失败项精确重试。</summary>
public sealed class TransferConflictAndTaskDetailsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eidoria-conflicts-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private ShellViewModel CreateShell()
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "data")).FullName;
        var startDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "start")).FullName;
        var shell = AppHost.CreateShell(AppHost.Build(dataDir, startDir));
        shell.Browser.SetWatchEnabled(false);
        return shell;
    }

    private string CreateFile(string relativeDirectory, string fileName, string content, DateTime? modified = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, relativeDirectory)).FullName;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        if (modified is { } value)
            File.SetLastWriteTime(path, value);
        return path;
    }

    private static async Task<TransferConflictPrompt> WaitForConflictAsync(
        ShellViewModel shell, Task transfer, string expectedSourceName)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 200; i++)
        {
            if (shell.IsConflictDialog &&
                shell.CurrentConflict is { } prompt &&
                prompt.Source.Name == expectedSourceName)
                return prompt;
            if (transfer.IsCompleted)
                break;
            await Task.Delay(25, ct);
        }
        throw new Xunit.Sdk.XunitException($"未等到 {expectedSourceName} 的冲突确认对话框。");
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task CopyConflictsAreAskedPerItemWithComparisonAndDefaultsToKeepBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var sourceA = CreateFile("source", "a.txt", "new-a", new DateTime(2026, 10, 28, 9, 0, 0));
        var sourceB = CreateFile("source", "b.txt", "new-b", new DateTime(2026, 10, 28, 10, 0, 0));
        var destA = CreateFile("dest", "a.txt", "old-a-longer", new DateTime(2026, 10, 20, 9, 0, 0));
        var destB = CreateFile("dest", "b.txt", "old-b", new DateTime(2026, 10, 20, 10, 0, 0));
        var destDir = Path.GetDirectoryName(destA)!;

        var transfer = shell.Browser.TransferItemsAsync([sourceA, sourceB], destDir, isMove: false);
        var firstPrompt = await WaitForConflictAsync(shell, transfer, "a.txt");

        Assert.Equal(0, firstPrompt.Index);
        Assert.Equal(2, firstPrompt.TotalCount);
        Assert.Equal(1, firstPrompt.RemainingAfterCurrent);
        Assert.Equal(5, firstPrompt.Source.Item.Length);
        Assert.Equal(12, firstPrompt.Target.Item.Length);
        Assert.True(firstPrompt.Source.Item.LastWriteTime > firstPrompt.Target.Item.LastWriteTime);
        Assert.Equal(Sha256("new-a"), firstPrompt.Source.Sha256);
        Assert.Equal(Sha256("old-a-longer"), firstPrompt.Target.Sha256);
        Assert.True(shell.IsConflictKeepBoth);
        Assert.False(shell.ConflictApplyToRemaining);
        Assert.Equal(2, shell.PendingConflictCount);
        var waitingTask = Assert.Single(shell.OperationTasks);
        Assert.Equal(OperationTaskStatus.WaitingConfirmation, waitingTask.Status);
        Assert.Equal(1, shell.WaitingConfirmationTaskCount);

        shell.DialogConfirmCommand.Execute(null);
        var secondPrompt = await WaitForConflictAsync(shell, transfer, "b.txt");
        Assert.Equal(1, secondPrompt.Index);
        Assert.Equal(1, shell.PendingConflictCount);
        shell.SelectConflictPolicyCommand.Execute(nameof(ConflictPolicy.Skip));
        shell.DialogConfirmCommand.Execute(null);

        var outcome = await transfer;
        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.Equal(1, outcome.SucceededCount);
        Assert.Equal(1, outcome.SkippedCount);
        Assert.Equal("old-a-longer", await File.ReadAllTextAsync(destA, ct));
        Assert.Equal("new-a", await File.ReadAllTextAsync(Path.Combine(destDir, "a (2).txt"), ct));
        Assert.Equal("old-b", await File.ReadAllTextAsync(destB, ct));
        Assert.False(File.Exists(Path.Combine(destDir, "b (2).txt")));
        Assert.Equal(OperationTaskStatus.Completed, waitingTask.Status);
        Assert.Equal(1, waitingTask.SkippedCount);
        Assert.Equal(0, shell.PendingConflictCount);
    }

    [Fact]
    public async Task MoveConflictCanApplyOverwriteToRemainingItemsExplicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var sourceA = CreateFile("move-source", "a.txt", "new-a");
        var sourceB = CreateFile("move-source", "b.txt", "new-b");
        var destA = CreateFile("move-dest", "a.txt", "old-a");
        var destB = CreateFile("move-dest", "b.txt", "old-b");
        var destDir = Path.GetDirectoryName(destA)!;

        var transfer = shell.Browser.TransferItemsAsync([sourceA, sourceB], destDir, isMove: true);
        _ = await WaitForConflictAsync(shell, transfer, "a.txt");
        shell.SelectConflictPolicyCommand.Execute(nameof(ConflictPolicy.Overwrite));
        shell.ConflictApplyToRemaining = true;
        shell.DialogConfirmCommand.Execute(null);

        var outcome = await transfer;
        Assert.True(outcome!.Success);
        Assert.Equal(2, outcome.SucceededCount);
        Assert.False(File.Exists(sourceA));
        Assert.False(File.Exists(sourceB));
        Assert.Equal("new-a", await File.ReadAllTextAsync(destA, ct));
        Assert.Equal("new-b", await File.ReadAllTextAsync(destB, ct));
        Assert.False(shell.IsDialogOpen);
    }

    [Fact]
    public async Task CancellingConflictPromptWritesNothingAndCancelsTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var source = CreateFile("cancel-source", "same.txt", "new");
        var dest = CreateFile("cancel-dest", "same.txt", "old");
        var destDir = Path.GetDirectoryName(dest)!;

        var transfer = shell.Browser.TransferItemsAsync([source], destDir, isMove: false);
        _ = await WaitForConflictAsync(shell, transfer, "same.txt");
        shell.DialogCancelCommand.Execute(null);

        var outcome = await transfer;
        Assert.True(outcome!.Cancelled);
        Assert.Equal(OperationTaskStatus.Cancelled, Assert.Single(shell.OperationTasks).Status);
        Assert.Equal("old", await File.ReadAllTextAsync(dest, ct));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destDir, "same (2).txt")));
    }

    [Fact]
    public async Task DuplicateNamesWithinBatchPromptAgainstFirstPlannedItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var first = CreateFile("batch-one", "same.txt", "first");
        var second = CreateFile("batch-two", "same.txt", "second");
        var destDir = Directory.CreateDirectory(Path.Combine(_root, "batch-dest")).FullName;

        var transfer = shell.Browser.TransferItemsAsync([first, second], destDir, isMove: false);
        var prompt = await WaitForConflictAsync(shell, transfer, "same.txt");

        Assert.Equal(1, prompt.TotalCount);
        Assert.Equal(second, prompt.Source.FullPath);
        Assert.Equal(first, prompt.Target.FullPath);
        Assert.Equal("本批先写入", prompt.Target.Heading);
        shell.DialogConfirmCommand.Execute(null);

        var outcome = await transfer;
        Assert.True(outcome!.Success);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destDir, "same.txt"), ct));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(destDir, "same (2).txt"), ct));
    }

    [Fact]
    public async Task CopyWithinSameFolderDoesNotPromptAndKeepsBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var source = CreateFile("same-folder", "copy.txt", "content");
        var folder = Path.GetDirectoryName(source)!;

        var outcome = await shell.Browser.TransferItemsAsync([source], folder, isMove: false);

        Assert.True(outcome!.Success);
        Assert.False(shell.IsDialogOpen);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(folder, "copy (2).txt"), ct));
    }

    [Fact]
    public async Task FailedTransferRetriesOnlyTheFailedSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = CreateShell();
        var good = CreateFile("retry-source", "good.txt", "good");
        var sourceDir = Path.GetDirectoryName(good)!;
        var missing = Path.Combine(sourceDir, "late.txt");
        var destDir = Directory.CreateDirectory(Path.Combine(_root, "retry-dest")).FullName;

        var outcome = await shell.Browser.TransferItemsAsync([good, missing], destDir, isMove: false);

        Assert.False(outcome!.Success);
        Assert.Equal(1, outcome.SucceededCount);
        var failedError = Assert.Single(outcome.Errors);
        Assert.Equal(missing, failedError.ItemPath);
        var failedTask = Assert.Single(shell.OperationTasks);
        Assert.Equal(OperationTaskStatus.Failed, failedTask.Status);
        Assert.True(failedTask.CanRetry);
        Assert.Equal(1, failedTask.ErrorCount);
        Assert.Equal(1, failedTask.ProducedCount);

        await File.WriteAllTextAsync(missing, "late", ct);
        await failedTask.RetryCommand.ExecuteAsync(null);

        Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(destDir, "good.txt"), ct));
        Assert.False(File.Exists(Path.Combine(destDir, "good (2).txt")));
        Assert.Equal("late", await File.ReadAllTextAsync(Path.Combine(destDir, "late.txt"), ct));
        Assert.False(failedTask.CanRetry);
        var retryTask = Assert.Single(shell.OperationTasks, task => task.Title.Contains("重试", StringComparison.Ordinal));
        Assert.Equal(OperationTaskStatus.Completed, retryTask.Status);
    }

    [Fact]
    public async Task TaskDetailsRetainCountsErrorsAndProducedPaths()
    {
        var shell = CreateShell();
        var produced = new[] { Path.Combine(_root, "one.txt"), Path.Combine(_root, "two.txt") };

        var outcome = await shell.RunOperationAsync(
            "混合结果",
            (_, _) => Task.FromResult(new OperationOutcome(
                2,
                [new OperationError("bad.txt", "拒绝访问", Path.Combine(_root, "bad.txt"))],
                produced,
                SkippedCount: 3)),
            _ => Task.CompletedTask);

        Assert.False(outcome.Success);
        var task = Assert.Single(shell.OperationTasks);
        Assert.Equal(2, task.SucceededCount);
        Assert.Equal(3, task.SkippedCount);
        Assert.Equal(1, task.ErrorCount);
        Assert.Equal(2, task.ProducedCount);
        Assert.Contains("完成 2 项", task.ResultSummaryText);
        Assert.Contains("跳过 3 项", task.ResultSummaryText);
        Assert.Contains("失败 1 项", task.ResultSummaryText);
        Assert.Contains("one.txt", task.ProducedText);
        Assert.True(task.IsDetailsExpanded);
        Assert.True(task.CanRetry);
        Assert.Equal(1, shell.FailedOperationTaskCount);
    }
}
