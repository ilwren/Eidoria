using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.20 第一切片：完整小文本安全读写与文档工作台的显式编辑状态机。</summary>
public sealed class DocumentWorkbenchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-document-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TextServicePreservesUtf8BomAndCrLf()
    {
        var path = Path.Combine(_root, "notes.txt");
        var original = new UTF8Encoding(false).GetBytes("第一行\r\n第二行");
        await File.WriteAllBytesAsync(path, [(byte)0xEF, (byte)0xBB, (byte)0xBF, .. original], TestContext.Current.CancellationToken);

        var service = new LocalTextFileService();
        var snapshot = await service.ReadAsync(path, DocumentWorkbenchViewModel.MaxTextBytes, TestContext.Current.CancellationToken);

        Assert.Equal(TextFileEncodingKind.Utf8, snapshot.Encoding);
        Assert.True(snapshot.HasBom);
        Assert.Equal("\r\n", snapshot.NewLine);
        Assert.Equal("第一行\r\n第二行", snapshot.Text);

        var saved = await service.SaveAsync(snapshot, "甲\n乙", overwriteChanged: false, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal([(byte)0xEF, (byte)0xBB, (byte)0xBF], bytes[..3]);
        Assert.Equal("甲\r\n乙", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal("甲\r\n乙", saved.Text);
    }

    [Fact]
    public async Task TextServiceDetectsUtf32BeforeUtf16Bom()
    {
        var path = Path.Combine(_root, "utf32.txt");
        var body = new UTF32Encoding(false, false).GetBytes("万象");
        await File.WriteAllBytesAsync(path, [(byte)0xFF, (byte)0xFE, 0, 0, .. body], TestContext.Current.CancellationToken);

        var snapshot = await new LocalTextFileService().ReadAsync(
            path, DocumentWorkbenchViewModel.MaxTextBytes, TestContext.Current.CancellationToken);

        Assert.Equal(TextFileEncodingKind.Utf32Le, snapshot.Encoding);
        Assert.True(snapshot.HasBom);
        Assert.Equal("万象", snapshot.Text);
    }

    [Fact]
    public async Task TextServiceRefusesExternalChangeUnlessOverwriteIsExplicit()
    {
        var path = Path.Combine(_root, "conflict.txt");
        await File.WriteAllTextAsync(path, "打开时内容", TestContext.Current.CancellationToken);
        var service = new LocalTextFileService();
        var snapshot = await service.ReadAsync(path, DocumentWorkbenchViewModel.MaxTextBytes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "外部版本", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TextFileChangedException>(() =>
            service.SaveAsync(snapshot, "编辑器版本", overwriteChanged: false, TestContext.Current.CancellationToken));
        Assert.Equal("外部版本", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        await service.SaveAsync(snapshot, "编辑器版本", overwriteChanged: true, TestContext.Current.CancellationToken);
        Assert.Equal("编辑器版本", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextServiceRejectsBinaryInvalidAndOversizedFiles()
    {
        var service = new LocalTextFileService();
        var binary = Path.Combine(_root, "binary.txt");
        await File.WriteAllBytesAsync(binary, [(byte)'a', 0, (byte)'b'], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ReadAsync(binary, 1024, TestContext.Current.CancellationToken));

        var invalid = Path.Combine(_root, "invalid.txt");
        await File.WriteAllBytesAsync(invalid, [(byte)0xC3, (byte)0x28], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ReadAsync(invalid, 1024, TestContext.Current.CancellationToken));

        var large = Path.Combine(_root, "large.txt");
        await File.WriteAllTextAsync(large, "123456", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ReadAsync(large, 5, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("notes.txt", FileKind.Text, ".txt", true)]
    [InlineData("config.json", FileKind.Data, ".json", true)]
    [InlineData("database.sqlite", FileKind.Data, ".sqlite", false)]
    [InlineData("paper.pdf", FileKind.Document, ".pdf", true)]
    public void WorkbenchEligibilityExcludesBinaryData(string name, FileKind kind, string extension, bool expected)
    {
        var item = new FileItem(
            new FileLocator(Path.Combine(_root, name)), name, kind, 10, DateTimeOffset.Now, false, false, extension);
        Assert.Equal(expected, DocumentWorkbenchViewModel.CanOpen(item));
    }

    [Fact]
    public async Task WorkbenchEditsAndSavesThroughTaskQueue()
    {
        var (shell, path) = await CreateShellWithFileAsync("Program.cs", "class Before {}\n");
        var workbench = shell.DocumentWorkbench;

        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(shell.IsDocumentWorkbenchPage);
        Assert.False(workbench.IsEditing);
        Assert.False(workbench.IsDirty);
        Assert.Equal("class Before {}\n", workbench.EditorText);

        workbench.BeginEditCommand.Execute(null);
        workbench.EditorText = "class After {}\n";
        Assert.True(workbench.IsDirty);
        Assert.Contains("+1", workbench.ChangeSummaryText);
        Assert.Contains("class After", workbench.DiffPreviewText);

        await workbench.SaveCommand.ExecuteAsync(null);

        Assert.Equal("class After {}\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(workbench.IsDirty);
        Assert.Contains(shell.OperationTasks, task => task.Title.Contains("正在保存", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkbenchConflictCanReloadDiskVersion()
    {
        var (shell, path) = await CreateShellWithFileAsync("notes.md", "# 初始\n");
        var workbench = shell.DocumentWorkbench;
        workbench.BeginEditCommand.Execute(null);
        workbench.EditorText = "# 编辑器版本\n";
        await File.WriteAllTextAsync(path, "# 磁盘版本\n", TestContext.Current.CancellationToken);

        await workbench.SaveCommand.ExecuteAsync(null);

        Assert.True(workbench.HasExternalConflict);
        Assert.Equal("# 磁盘版本\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        await workbench.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("# 磁盘版本\n", workbench.EditorText);
        Assert.False(workbench.IsDirty);
        Assert.True(workbench.IsEditing);
    }

    [Fact]
    public async Task WorkbenchFormatsJsonWithoutSaving()
    {
        var (shell, path) = await CreateShellWithFileAsync("config.json", "{\"theme\":\"teal\"}");
        var workbench = shell.DocumentWorkbench;
        workbench.BeginEditCommand.Execute(null);

        workbench.FormatCommand.Execute(null);

        Assert.Contains("\n  \"theme\": \"teal\"\n", workbench.EditorText);
        Assert.True(workbench.IsDirty);
        Assert.Equal("{\"theme\":\"teal\"}", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkbenchCloseGuardRequiresExplicitDiscard()
    {
        var (shell, _) = await CreateShellWithFileAsync("draft.txt", "before\n");
        var workbench = shell.DocumentWorkbench;

        Assert.False(workbench.NeedsCloseConfirmation);
        Assert.True(await workbench.ConfirmCloseAsync());
        Assert.False(shell.IsDialogOpen);

        workbench.BeginEditCommand.Execute(null);
        workbench.EditorText = "after\n";
        Assert.True(workbench.NeedsCloseConfirmation);

        var cancelledClose = workbench.ConfirmCloseAsync();
        Assert.True(shell.IsDialogOpen);
        shell.DialogCancelCommand.Execute(null);
        Assert.False(await cancelledClose);
        Assert.True(workbench.IsDirty);

        var approvedClose = workbench.ConfirmCloseAsync();
        Assert.True(shell.IsDialogOpen);
        shell.DialogConfirmCommand.Execute(null);
        Assert.True(await approvedClose);
    }

    [Fact]
    public void PdfPageReaderLayoutHonorsRotation()
    {
        var page = new PdfPageViewModel(2, aspectRatio: 0.5);

        page.UpdateReaderLayout(readerWidth: 600, sideways: false);
        Assert.Equal(600, page.ReaderWidth);
        Assert.Equal(1200, page.ReaderHeight);
        Assert.Equal("第 3 页", page.PageLabel);

        page.UpdateReaderLayout(readerWidth: 600, sideways: true);
        Assert.Equal(300, page.ReaderHeight);
        Assert.False(page.HasPageImage);
    }

    private async Task<(ShellViewModel Shell, string Path)> CreateShellWithFileAsync(string name, string content)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "data")).FullName;
        var startDir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "start")).FullName;
        var path = Path.Combine(startDir, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        var shell = AppHost.CreateShell(AppHost.Build(dataDir, startDir));
        shell.Browser.SetWatchEnabled(false);
        var item = await new LocalFileSystemService().StatAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        await shell.OpenDocumentWorkbenchAsync(item);
        return (shell, path);
    }
}
