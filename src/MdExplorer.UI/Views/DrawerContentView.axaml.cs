using Avalonia.Controls;
using Avalonia.Interactivity;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class DrawerContentView : UserControl
{
    public DrawerContentView() => InitializeComponent();

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private async void OnNavEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NavEntry entry } && Shell is { } shell)
            await shell.OpenFolderAsync(entry.Path);
    }

    private async void OnVolumeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: VolumeItemModel volume } && Shell is { } shell)
            await shell.OpenFolderAsync(volume.Path);
    }

    private async void OnAuthorizedRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AuthorizedRootModel root } && Shell is { } shell)
            await shell.OpenAuthorizedRootAsync(root);
    }

    private async void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } && Shell is { } shell)
            await shell.OpenFolderAsync(item.FullPath);
    }
}
