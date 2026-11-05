using System.Reflection;
using MdExplorer.Domain;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>视图模式三态与详细信息列头排序（无头构造 Shell：不进 InitializeAsync，只驱动状态机）。</summary>
public sealed class BrowserViewModeAndSortTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("mdexplorer-vm-").FullName;
    private readonly string _startDir = Directory.CreateTempSubdirectory("mdexplorer-vmstart-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _dataDir, _startDir })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private ShellViewModel CreateShell() => AppHost.CreateShell(AppHost.Build(_dataDir, _startDir));

    [Fact]
    public void ViewModeCycleCoversThreeModesAndPersists()
    {
        var shell = CreateShell();
        var browser = shell.Browser;
        Assert.True(browser.IsListView); // 默认列表

        browser.ToggleViewModeCommand.Execute(null);
        Assert.True(browser.IsGridView);
        Assert.Equal("Grid", shell.SettingsStore.Settings.ViewMode);

        browser.ToggleViewModeCommand.Execute(null);
        Assert.True(browser.IsDetailsView);
        Assert.False(browser.IsListView);
        Assert.False(browser.IsGridView);
        Assert.Equal("Details", shell.SettingsStore.Settings.ViewMode);

        browser.ToggleViewModeCommand.Execute(null);
        Assert.True(browser.IsListView);
        Assert.Equal("List", shell.SettingsStore.Settings.ViewMode);
    }

    [Fact]
    public void SetViewModeCommandAcceptsModeNames()
    {
        var shell = CreateShell();
        shell.Browser.SetViewModeCommand.Execute("Details");
        Assert.Equal(BrowserViewMode.Details, shell.Browser.ViewMode);
        shell.Browser.SetViewModeCommand.Execute("whatever");
        Assert.Equal(BrowserViewMode.List, shell.Browser.ViewMode); // 未知值回退列表
    }

    [Fact]
    public async Task PersistedDetailsModeIsRestoredOnConstruction()
    {
        var first = CreateShell();
        first.Browser.ViewMode = BrowserViewMode.Details;
        Assert.Equal("Details", first.SettingsStore.Settings.ViewMode);
        // VM 的保存是即发即忘；显式落盘后再以同数据目录重建，验证解析侧恢复
        await first.SettingsStore.SaveSettingsAsync(CancellationToken.None);

        var second = CreateShell();
        Assert.Equal(BrowserViewMode.Details, second.Browser.ViewMode);
        Assert.True(second.Browser.IsDetailsView);
    }

    [Fact]
    public void ColumnSortTogglesDirectionAndGlyphFollows()
    {
        var shell = CreateShell();
        var browser = shell.Browser;

        browser.ApplyColumnSort(SortField.Size); // 新字段 → 升序
        Assert.Equal("Size", shell.SettingsStore.Settings.SortField);
        Assert.Equal("Ascending", shell.SettingsStore.Settings.SortDirection);
        Assert.Equal("↑", browser.SizeSortGlyph);
        Assert.Equal("", browser.NameSortGlyph);

        browser.ApplyColumnSort(SortField.Size); // 同字段 → 翻转
        Assert.Equal("Descending", shell.SettingsStore.Settings.SortDirection);
        Assert.Equal("↓", browser.SizeSortGlyph);

        browser.ApplyColumnSort(SortField.Name); // 换字段 → 回到升序
        Assert.Equal("Ascending", shell.SettingsStore.Settings.SortDirection);
        Assert.Equal("↑", browser.NameSortGlyph);
        Assert.Equal("", browser.SizeSortGlyph);
        Assert.Equal("名称", browser.SortFieldLabel);
    }

    [Fact]
    public void MotionAndFolderMemorySettingsUpdateModelAndVersionIsDynamic()
    {
        var shell = CreateShell();

        shell.ReduceMotion = true;
        shell.RememberFolderView = true;

        Assert.True(shell.SettingsStore.Settings.ReduceMotion);
        Assert.True(shell.SettingsStore.Settings.RememberFolderView);
        var informationalVersion = typeof(ShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(informationalVersion));
        Assert.Equal(informationalVersion.Split('+', 2)[0], shell.AppVersionText);
    }

    [Fact]
    public async Task FolderViewMemoryRestoresPerFolderAndCanBeClearedOrDisabled()
    {
        var docs = Directory.CreateDirectory(Path.Combine(_startDir, "docs")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_startDir, "other")).FullName;
        await File.WriteAllTextAsync(Path.Combine(docs, "b.txt"), "1234");
        await File.WriteAllTextAsync(Path.Combine(docs, "a.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(other, "note.txt"), "x");

        var shell = CreateShell();
        var browser = shell.Browser;
        browser.SetWatchEnabled(false);
        shell.RememberFolderView = true;

        await browser.NavigateToAsync(docs, pushHistory: false);
        browser.ViewMode = BrowserViewMode.Grid;
        browser.SetSort(SortField.Size, SortDirection.Descending);

        // 记忆开启时，全局项仍是“未记忆文件夹”的默认值，不被当前 docs 覆盖。
        Assert.Equal("List", shell.SettingsStore.Settings.ViewMode);
        Assert.Equal("Name", shell.SettingsStore.Settings.SortField);
        Assert.Equal("Ascending", shell.SettingsStore.Settings.SortDirection);
        Assert.Contains(
            shell.SettingsStore.Settings.FolderViewStates.Values,
            state => state.ViewMode == "Grid" && state.SortField == "Size" && state.SortDirection == "Descending");

        await browser.NavigateToAsync(other);
        Assert.Equal(BrowserViewMode.List, browser.ViewMode);
        Assert.Equal(SortField.Name, browser.CurrentSortField);
        Assert.Equal(SortDirection.Ascending, browser.CurrentSortDirection);
        browser.ViewMode = BrowserViewMode.Details;
        browser.SetSort(SortField.LastWriteTime, SortDirection.Ascending);

        await browser.NavigateToAsync(docs);
        Assert.Equal(BrowserViewMode.Grid, browser.ViewMode);
        Assert.Equal(SortField.Size, browser.CurrentSortField);
        Assert.Equal(SortDirection.Descending, browser.CurrentSortDirection);
        Assert.Equal("↓", browser.SizeSortGlyph);

        // 跨 Shell 重建后仍从 JSON 模型恢复每个文件夹自己的呈现。
        await shell.SettingsStore.SaveSettingsAsync(CancellationToken.None);
        await Task.Delay(50);
        var restored = CreateShell();
        restored.Browser.SetWatchEnabled(false);
        Assert.True(restored.RememberFolderView);
        await restored.Browser.NavigateToAsync(other, pushHistory: false);
        Assert.Equal(BrowserViewMode.Details, restored.Browser.ViewMode);
        Assert.Equal(SortField.LastWriteTime, restored.Browser.CurrentSortField);

        await restored.ClearFolderViewPreferencesCommand.ExecuteAsync(null);
        Assert.Empty(restored.SettingsStore.Settings.FolderViewStates);
        await restored.Browser.NavigateToAsync(docs);
        Assert.Equal(BrowserViewMode.List, restored.Browser.ViewMode);
        Assert.Equal(SortField.Name, restored.Browser.CurrentSortField);

        // 关闭记忆时把眼前呈现收回全局，避免切换后突然跳回旧默认值。
        restored.Browser.ViewMode = BrowserViewMode.Grid;
        restored.Browser.SetSort(SortField.Kind, SortDirection.Ascending);
        restored.RememberFolderView = false;
        Assert.Equal("Grid", restored.SettingsStore.Settings.ViewMode);
        Assert.Equal("Kind", restored.SettingsStore.Settings.SortField);
    }
}
