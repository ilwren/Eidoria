using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class SearchCenterView : UserControl
{
    private bool? _isWideLayout;

    public SearchCenterView()
    {
        InitializeComponent();
        SearchCenterSearch.KeyDown += OnSearchKeyDown;
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Shell is not { } shell)
            return;

        e.Handled = true;
        shell.RunSearchCenterSearchCommand.Execute(null);
    }

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } && Shell is { } shell)
            await shell.OpenSearchCenterResultAsync(item);
    }

    private void OnRemoveRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchIndexRootModel root } && Shell is { } shell)
            shell.RemoveSearchIndexRootCommand.Execute(root);
        e.Handled = true;
    }

    /// <summary>按页面实际可用宽度切换：宽时结果与索引管理同屏，窄时上下分工而不是缩放同一布局。</summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
            return;

        var wide = width >= 760;
        if (_isWideLayout == wide)
            return;
        _isWideLayout = wide;

        SearchContentGrid.ColumnDefinitions.Clear();
        SearchContentGrid.RowDefinitions.Clear();
        if (wide)
        {
            SearchContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SearchContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16)));
            SearchContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(320)));
            SearchContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            Grid.SetRow(ResultsCard, 0);
            Grid.SetColumn(ResultsCard, 0);
            Grid.SetRow(IndexCard, 0);
            Grid.SetColumn(IndexCard, 2);
        }
        else
        {
            SearchContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SearchContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            SearchContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(12)));
            SearchContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(376)));

            Grid.SetRow(ResultsCard, 0);
            Grid.SetColumn(ResultsCard, 0);
            Grid.SetRow(IndexCard, 2);
            Grid.SetColumn(IndexCard, 0);
        }
    }
}
