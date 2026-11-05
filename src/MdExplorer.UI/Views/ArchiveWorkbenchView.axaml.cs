using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class ArchiveWorkbenchView : UserControl
{
    private bool? _isWideLayout;

    public ArchiveWorkbenchView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private async void OnArchiveEntryTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ArchiveWorkbenchEntryViewModel row } && Shell is { } shell)
            await shell.SelectArchiveEntryAsync(row);
    }

    private void OnArchiveEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ArchiveWorkbenchEntryViewModel { IsFolder: true } row } &&
            Shell is { } shell)
        {
            shell.NavigateArchiveFolder(row.VirtualPath);
            e.Handled = true;
        }
    }

    private void OnArchiveEnterFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArchiveWorkbenchEntryViewModel { IsFolder: true } row } &&
            Shell is { } shell)
            shell.NavigateArchiveFolder(row.VirtualPath);
        e.Handled = true;
    }

    private void OnArchiveCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArchiveWorkbenchEntryViewModel row })
            row.IsChecked = !row.IsChecked;
        e.Handled = true;
    }

    private void OnArchiveBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArchiveBreadcrumbModel crumb } && Shell is { } shell)
            shell.NavigateArchiveFolder(crumb.Path);
        e.Handled = true;
    }

    /// <summary>
    /// 宽可用面让条目与详情/解压选项同屏；窄面把搜索与详情收进上下分工，
    /// 保证主列表仍可滚动，而不是按比例缩小桌面三栏。
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
            return;

        var wide = width >= 840;
        if (_isWideLayout == wide)
            return;
        _isWideLayout = wide;
        // 窄屏顶栏已经给出压缩包名，页眉卡会重复占高；收起它并把高度还给条目列表，
        // 返回动作则并入面包屑胶囊，避免只能依赖系统返回键。
        ArchiveHeaderCard.IsVisible = wide;
        ArchiveFactsPanel.IsVisible = wide;
        ArchiveCompactBackButton.IsVisible = !wide;

        ArchiveToolbarGrid.ColumnDefinitions.Clear();
        ArchiveToolbarGrid.RowDefinitions.Clear();
        ArchiveContentGrid.ColumnDefinitions.Clear();
        ArchiveContentGrid.RowDefinitions.Clear();

        if (wide)
        {
            ArchiveToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ArchiveToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(12)));
            ArchiveToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(320)));
            ArchiveToolbarGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(ArchiveBreadcrumbBar, 0);
            Grid.SetColumn(ArchiveBreadcrumbBar, 0);
            Grid.SetRow(ArchiveFilterBox, 0);
            Grid.SetColumn(ArchiveFilterBox, 2);

            ArchiveContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ArchiveContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16)));
            ArchiveContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(360)));
            ArchiveContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(ArchiveEntriesCard, 0);
            Grid.SetColumn(ArchiveEntriesCard, 0);
            Grid.SetRow(ArchiveDetailCard, 0);
            Grid.SetColumn(ArchiveDetailCard, 2);
        }
        else
        {
            ArchiveToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ArchiveToolbarGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ArchiveToolbarGrid.RowDefinitions.Add(new RowDefinition(new GridLength(8)));
            ArchiveToolbarGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(ArchiveBreadcrumbBar, 0);
            Grid.SetColumn(ArchiveBreadcrumbBar, 0);
            Grid.SetRow(ArchiveFilterBox, 2);
            Grid.SetColumn(ArchiveFilterBox, 0);

            ArchiveContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ArchiveContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            ArchiveContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(12)));
            ArchiveContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(256)));
            Grid.SetRow(ArchiveEntriesCard, 0);
            Grid.SetColumn(ArchiveEntriesCard, 0);
            Grid.SetRow(ArchiveDetailCard, 2);
            Grid.SetColumn(ArchiveDetailCard, 0);
        }
    }
}
