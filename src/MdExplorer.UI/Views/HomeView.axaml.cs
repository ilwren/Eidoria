using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdExplorer.Domain;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        HomeSearch.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || DataContext is not ShellViewModel shell)
                return;
            var query = HomeSearch.Text?.Trim() ?? string.Empty;
            if (query.Length == 0) return;
            shell.Page = ShellPage.Browser;
            await shell.Browser.SearchRecursiveAsync(query);
        };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private async void OnVolumeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: VolumeItemModel volume } && Shell is { } shell)
            await shell.OpenFolderAsync(volume.Path);
    }

    private async void OnPlaceTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: NavEntry entry } && Shell is { } shell)
            await shell.OpenFolderAsync(entry.Path);
    }

    private async void OnFavoriteTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } && Shell is { } shell)
        {
            if (item.IsFolder)
                await shell.OpenFolderAsync(item.FullPath);
            else if (FileLocator.IsStorageUri(item.FullPath))
            {
                // 存储内文件收藏：父夹定位需注册表解析，交由 Browser 的服务层处理
                if (shell.BrowserParentOf(item.FullPath) is { } storageParent)
                    await shell.OpenFolderAsync(storageParent);
            }
            else if (Path.GetDirectoryName(item.FullPath) is { } parent)
                await shell.OpenFolderAsync(parent);
        }
    }

    private async void OnRecentFileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } && Shell is { } shell)
            await shell.OpenRecentFileAsync(item);
    }

    private async void OnAuthorizedRootTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button) return; // 移除按钮的轻触不触发打开
        if (sender is Control { DataContext: AuthorizedRootModel root } && Shell is { } shell)
            await shell.OpenAuthorizedRootAsync(root);
    }

    private async void OnRemoveAuthorizedRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AuthorizedRootModel root } && Shell is { } shell)
            await shell.RemoveAuthorizedRootAsync(root);
        e.Handled = true;
    }

    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && Shell is { } shell &&
            Enum.TryParse<FileKind>(tag, out var kind))
            await shell.OpenCategoryAsync(kind);
    }
}
