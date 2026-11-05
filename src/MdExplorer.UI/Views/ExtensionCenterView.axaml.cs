using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class ExtensionCenterView : UserControl
{
    public ExtensionCenterView() => InitializeComponent();

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入声明式扩展",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Eidoria 扩展包 (*.mepx)")
                {
                    Patterns = ["*.mepx"],
                },
            ],
        });
        if (files.Count == 0)
            return;

        await using var stream = await files[0].OpenReadAsync();
        await shell.ExtensionCenter.ImportPackageAsync(stream, files[0].Name);
    }

    private void OnApplyThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginCardModel package } && DataContext is ShellViewModel shell)
            shell.ExtensionCenter.ApplyThemeCommand.Execute(package);
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginCardModel package } && DataContext is ShellViewModel shell)
            shell.ExtensionCenter.RemoveCommand.Execute(package);
    }
}
