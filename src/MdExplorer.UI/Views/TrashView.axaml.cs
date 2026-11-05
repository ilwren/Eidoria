using Avalonia.Controls;
using Avalonia.Interactivity;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class TrashView : UserControl
{
    public TrashView() => InitializeComponent();

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrashItemModel row } && DataContext is ShellViewModel shell)
            shell.RestoreTrashCommand.Execute(row);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrashItemModel row } && DataContext is ShellViewModel shell)
            shell.DeleteTrashCommand.Execute(row);
    }
}
