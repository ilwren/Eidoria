using System.Text.Json;
using System.Text.Json.Serialization;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>设置/收藏的 JSON 持久化（源生成序列化、原子替换写、损坏回落默认）。</summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private readonly string _favoritesPath;
    private readonly List<string> _favorites = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<string> Favorites => _favorites;

    private SettingsStore(string dataDirectory)
    {
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _favoritesPath = Path.Combine(dataDirectory, "favorites.json");
    }

    public static async Task<SettingsStore> LoadAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var store = new SettingsStore(dataDirectory);
        try
        {
            if (File.Exists(store._settingsPath))
            {
                await using var stream = File.OpenRead(store._settingsPath);
                var loaded = await JsonSerializer.DeserializeAsync(
                    stream, MdExplorerJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
                if (loaded is not null) store.Settings = Normalize(loaded);
            }

            if (File.Exists(store._favoritesPath))
            {
                await using var stream = File.OpenRead(store._favoritesPath);
                var favorites = await JsonSerializer.DeserializeAsync(
                    stream, MdExplorerJsonContext.Default.ListString, cancellationToken).ConfigureAwait(false);
                if (favorites is not null)
                    store._favorites.AddRange(favorites.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct());
            }
        }
        catch (JsonException)
        {
            // 文件损坏时回落默认，旧文件由下一次写入覆盖（浏览主流程不受影响）
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return store;
    }

    /// <summary>启动期同步加载（避免 UI 线程同步等待异步 IO）；失败回落默认。</summary>
    public static SettingsStore LoadOrDefault(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var store = new SettingsStore(dataDirectory);
        try
        {
            if (File.Exists(store._settingsPath))
            {
                var json = File.ReadAllText(store._settingsPath);
                if (JsonSerializer.Deserialize(json, MdExplorerJsonContext.Default.AppSettings) is { } loaded)
                    store.Settings = Normalize(loaded);
            }
            if (File.Exists(store._favoritesPath))
            {
                var json = File.ReadAllText(store._favoritesPath);
                if (JsonSerializer.Deserialize(json, MdExplorerJsonContext.Default.ListString) is { } favorites)
                    store._favorites.AddRange(favorites.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct());
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return store;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        // 旧版没有这些字段时源生成反序列化会保留默认值；手改 JSON 显式写 null 时则在此兜底。
        settings.ThemeMode ??= "System";
        settings.SeedColor ??= "#006A6A";
        settings.ViewMode ??= "List";
        settings.SortField ??= "Name";
        settings.SortDirection ??= "Ascending";
        settings.OpenTabPaths ??= [];
        settings.RecentFolderPaths ??= [];
        settings.RecentFilePaths ??= [];
        settings.SearchIndexRootPaths ??= [];
        settings.SearchIndexRootPaths.RemoveAll(string.IsNullOrWhiteSpace);
        if (double.IsNaN(settings.DualPaneWidth) || double.IsInfinity(settings.DualPaneWidth) || settings.DualPaneWidth <= 0)
            settings.DualPaneWidth = 560;

        // 可调项一律夹到实际支持的区间：手改 JSON 或旧版本缺字段都不会让界面进入无效状态。
        // 对照窗格的独立呈现偏好：留空表示跟随全局；非法值一律收回 null，避免写入无法识别的模式。
        settings.DualPaneViewMode = NormalizeChoice(settings.DualPaneViewMode, ["List", "Grid", "Details"]);
        settings.DualPaneSortField = NormalizeChoice(settings.DualPaneSortField, ["Name", "Size", "Kind", "LastWriteTime"]);
        settings.DualPaneSortDirection = NormalizeChoice(settings.DualPaneSortDirection, ["Ascending", "Descending"]);
        settings.Density = string.Equals(settings.Density, "Compact", StringComparison.OrdinalIgnoreCase)
            ? "Compact"
            : "Comfortable";
        settings.ItemPageSize = Math.Clamp(settings.ItemPageSize, 60, 480);
        settings.RecentItemLimit = Math.Clamp(settings.RecentItemLimit, 6, 60);
        if (!double.IsFinite(settings.PullRefreshTriggerDistance))
            settings.PullRefreshTriggerDistance = 72;
        settings.PullRefreshTriggerDistance = Math.Clamp(settings.PullRefreshTriggerDistance, 48, 120);
        if (!double.IsFinite(settings.AutoLoadMoreDistance))
            settings.AutoLoadMoreDistance = 320;
        settings.AutoLoadMoreDistance = Math.Clamp(settings.AutoLoadMoreDistance, 120, 600);
        settings.SearchResultLimit = Math.Clamp(settings.SearchResultLimit, 200, 20000);
        settings.AppTrashQuotaBytes = Math.Clamp(
            settings.AppTrashQuotaBytes, 200_000_000, 10_000_000_000);
        settings.AppTrashRetentionDays = Math.Clamp(settings.AppTrashRetentionDays, 1, 365);
        settings.FolderViewStates ??= [];
        foreach (var key in settings.FolderViewStates
                     .Where(pair => pair.Value is null)
                     .Select(pair => pair.Key)
                     .ToArray())
            settings.FolderViewStates.Remove(key);

        settings.AutomationWorkflow ??= MdExplorer.Domain.AutomationWorkflowDefinition.CreateDefault();
        settings.AutomationWorkflow.Name ??= "整理当前文件夹";
        settings.AutomationWorkflow.Nodes ??= [];
        settings.AutomationWorkflow.Connections ??= [];
        settings.AutomationWorkflow.Nodes.RemoveAll(static node =>
            node is null || string.IsNullOrWhiteSpace(node.Id));
        settings.AutomationWorkflow.Connections.RemoveAll(static connection =>
            connection is null ||
            string.IsNullOrWhiteSpace(connection.SourceNodeId) ||
            string.IsNullOrWhiteSpace(connection.TargetNodeId));
        return settings;
    }

    private static string? NormalizeChoice(string? value, string[] allowed) =>
        allowed.FirstOrDefault(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));

    public async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(_settingsPath, async stream =>
                await JsonSerializer.SerializeAsync(stream, Settings, MdExplorerJsonContext.Default.AppSettings, cancellationToken)
                    .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public bool IsFavorite(string path) =>
        _favorites.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task SetFavoriteAsync(string path, bool favorite, CancellationToken cancellationToken)
    {
        var contains = IsFavorite(path);
        if (favorite && !contains) _favorites.Add(path);
        else if (!favorite && contains)
            _favorites.RemoveAll(f => string.Equals(f, path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        else return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _favorites.ToList();
            await WriteAtomicAsync(_favoritesPath, async stream =>
                await JsonSerializer.SerializeAsync(stream, snapshot, MdExplorerJsonContext.Default.ListString, cancellationToken)
                    .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task WriteAtomicAsync(string path, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await write(stream).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class MdExplorerJsonContext : JsonSerializerContext { }
