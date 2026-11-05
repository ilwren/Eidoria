using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Infrastructure.Plugins;

namespace MdExplorer.UI.ViewModels;

/// <summary>管理本地导入的声明式 MEPX 包；插件目前只能提供宿主渲染的 Material 3 种子色主题。</summary>
public sealed partial class ExtensionCenterViewModel : ObservableObject
{
    private readonly PluginPackageCatalog _catalog;
    private readonly IDialogs _dialogs;
    private readonly Action<string> _applySeedColor;
    private readonly Func<string> _currentSeedColor;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    public ObservableCollection<PluginCardModel> Packages { get; } = [];
    public ObservableCollection<PluginPackageIssue> Issues { get; } = [];
    public string PackageDirectory => _catalog.PackageDirectory;
    public string PackageCountText => Packages.Count == 1 ? "1 个本地扩展" : $"{Packages.Count} 个本地扩展";
    public bool HasPackages => Packages.Count > 0;
    public bool HasIssues => Issues.Count > 0;

    public ExtensionCenterViewModel(
        PluginPackageCatalog catalog,
        IDialogs dialogs,
        Action<string> applySeedColor,
        Func<string> currentSeedColor)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _applySeedColor = applySeedColor;
        _currentSeedColor = currentSeedColor;
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            var snapshot = _catalog.Scan();
            Packages.Clear();
            foreach (var package in snapshot.Packages)
                Packages.Add(new PluginCardModel(package, _currentSeedColor()));
            Issues.Clear();
            foreach (var issue in snapshot.Issues)
                Issues.Add(issue);
            StatusText = snapshot.Issues.Count == 0
                ? $"{snapshot.Packages.Count} 个扩展包已检查。"
                : $"已加载 {snapshot.Packages.Count} 个扩展；另有 {snapshot.Issues.Count} 个包无效或不受支持。";
            NotifyCollectionStateChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Packages.Clear();
            Issues.Clear();
            StatusText = $"无法读取扩展目录：{ex.Message}";
            NotifyCollectionStateChanged();
        }
    }

    public async Task ImportPackageAsync(Stream packageStream, string fileName)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (!fileName.EndsWith(PluginPackageCatalog.PackageExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择 .mepx 插件包。");

            var candidate = await _catalog.InspectAsync(packageStream, CancellationToken.None).ConfigureAwait(true);
            var manifest = candidate.Manifest;
            var replaces = Packages.FirstOrDefault(item => item.Id == manifest.Id);
            var details =
                $"{manifest.Name} · {manifest.Version}\n发布者：{manifest.Publisher}\n" +
                $"能力：主题色（{manifest.Theme!.SeedColor}）\nSHA-256：{candidate.Sha256}\n\n" +
                "此包未签名。当前版本只接受一个 JSON 清单，不包含可执行代码、脚本、程序集或网络能力；" +
                "安装前请确认来源可信。" +
                (replaces is null ? string.Empty : "\n同 ID 的已安装包将被替换。");
            if (!await _dialogs.ConfirmAsync("安装声明式扩展？", details, "安装").ConfigureAwait(true))
                return;

            await _catalog.InstallAsync(candidate, CancellationToken.None).ConfigureAwait(true);
            Refresh();
            StatusText = $"已安装「{manifest.Name}」。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            StatusText = $"安装失败：{ex.Message}";
            await _dialogs.ConfirmAsync("扩展包无法安装", ex.Message, "知道了").ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(PluginCardModel? package)
    {
        if (package is null || IsBusy)
            return;
        if (!await _dialogs.ConfirmAsync(
                "移除扩展？", $"将从本机移除「{package.Name}」及其主题定义，不会删除其他文件。", "移除",
                destructive: true).ConfigureAwait(true))
            return;

        IsBusy = true;
        try
        {
            await _catalog.RemoveAsync(package.Id, CancellationToken.None).ConfigureAwait(true);
            Refresh();
            StatusText = $"已移除「{package.Name}」。当前应用主题色不会自动更改。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"移除失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ApplyTheme(PluginCardModel? package)
    {
        if (package?.ThemeSeedColor is not { } seedColor)
            return;
        _applySeedColor(seedColor);
        Refresh();
        StatusText = $"已应用「{package.Name}」的种子色主题。";
    }

    [RelayCommand]
    private void Rescan() => Refresh();

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(PackageCountText));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(HasIssues));
    }
}

public sealed class PluginCardModel(InstalledPluginPackage package, string currentSeedColor)
{
    public string Id => package.Manifest.Id;
    public string Name => package.Manifest.Name;
    public string Version => package.Manifest.Version;
    public string Publisher => package.Manifest.Publisher;
    public string Description => package.Manifest.Description;
    public string ThemeSeedColor => package.Manifest.Theme?.SeedColor ?? string.Empty;
    public bool HasTheme => package.Manifest.Theme is not null;
    public bool IsThemeApplied => string.Equals(ThemeSeedColor, currentSeedColor, StringComparison.OrdinalIgnoreCase);
    public string HashText => package.Sha256;
}
