using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class StorageInsightsView : UserControl
{
    private bool? _isWideLayout;

    public StorageInsightsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnVolumeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: VolumeItemModel volume } && Shell is { } shell)
            shell.TrySetStorageRoot(volume.Path);
    }

    private async void OnTopFolderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: StorageFolderUsageModel folder } && Shell is { } shell)
            await shell.OpenFolderAsync(folder.Path);
    }

    private void OnAnalyzeFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StorageFolderUsageModel folder } && Shell is { } shell)
            shell.AnalyzeStorageFolderCommand.Execute(folder);
        e.Handled = true;
    }

    private async void OnFileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } && Shell is { } shell)
            await shell.OpenStorageItemAsync(item);
    }

    /// <summary>按页面实际可用宽度切换占用排行与类型分布的同屏/上下关系。</summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
            return;

        var wide = width >= 760;
        if (_isWideLayout == wide)
            return;
        _isWideLayout = wide;

        StorageDetailsGrid.ColumnDefinitions.Clear();
        StorageDetailsGrid.RowDefinitions.Clear();
        if (wide)
        {
            StorageDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            StorageDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16)));
            StorageDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(360)));
            StorageDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Grid.SetRow(TopFoldersCard, 0);
            Grid.SetColumn(TopFoldersCard, 0);
            Grid.SetRow(KindsCard, 0);
            Grid.SetColumn(KindsCard, 2);
        }
        else
        {
            StorageDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            StorageDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            StorageDetailsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(12)));
            StorageDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Grid.SetRow(TopFoldersCard, 0);
            Grid.SetColumn(TopFoldersCard, 0);
            Grid.SetRow(KindsCard, 2);
            Grid.SetColumn(KindsCard, 0);
        }
    }
}
