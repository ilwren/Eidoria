using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class SettingsView : UserControl
{
    private static readonly (string Key, string Title, string Subtitle)[] Categories =
    [
        ("appearance", "外观", "主题、颜色与界面密度"),
        ("browse", "浏览", "文件视图、排序与加载行为"),
        ("startup", "启动与最近", "启动会话和最近访问记录"),
        ("media", "媒体", "预览与播放偏好"),
        ("ai-search", "AI 与搜索", "本地模型、索引与搜索行为"),
        ("storage", "存储与权限", "Android 文件夹授权与应用内回收站"),
        ("notifications", "通知", "系统通知偏好与状态"),
        ("about", "关于", "版本、数据目录与许可证"),
    ];

    private readonly (string Key, Button Wide, Button Compact)[] _navigationButtons;
    private readonly (string Key, Border Card)[] _categoryCards;
    private string _selectedCategory = "appearance";
    private bool? _isWideLayout;

    public SettingsView()
    {
        InitializeComponent();

        _navigationButtons =
        [
            ("appearance", FindControl<Button>("WideNavAppearance")!, FindControl<Button>("CompactNavAppearance")!),
            ("browse", FindControl<Button>("WideNavBrowse")!, FindControl<Button>("CompactNavBrowse")!),
            ("startup", FindControl<Button>("WideNavStartup")!, FindControl<Button>("CompactNavStartup")!),
            ("media", FindControl<Button>("WideNavMedia")!, FindControl<Button>("CompactNavMedia")!),
            ("ai-search", FindControl<Button>("WideNavAiSearch")!, FindControl<Button>("CompactNavAiSearch")!),
            ("storage", FindControl<Button>("WideNavStorage")!, FindControl<Button>("CompactNavStorage")!),
            ("notifications", FindControl<Button>("WideNavNotifications")!, FindControl<Button>("CompactNavNotifications")!),
            ("about", FindControl<Button>("WideNavAbout")!, FindControl<Button>("CompactNavAbout")!),
        ];
        _categoryCards =
        [
            ("appearance", FindControl<Border>("SettingsCardAppearance")!),
            ("browse", FindControl<Border>("SettingsCardBrowse")!),
            ("startup", FindControl<Border>("SettingsCardStartup")!),
            ("media", FindControl<Border>("SettingsCardMedia")!),
            ("ai-search", FindControl<Border>("SettingsCardLocalAi")!),
            ("ai-search", FindControl<Border>("SettingsCardSearch")!),
            ("storage", FindControl<Border>("SettingsCardStorage")!),
            ("about", FindControl<Border>("SettingsCardAbout")!),
            ("notifications", FindControl<Border>("SettingsCardNotifications")!),
        ];

        DataContextChanged += (_, _) => UpdatePlatformAvailability();
        foreach (var (name, seed) in ThemeService.SeedPresets)
        {
            var swatch = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new Avalonia.CornerRadius(22),
                Margin = new Avalonia.Thickness(0, 0, 10, 10),
                Background = new SolidColorBrush(Color.Parse(seed)),
                Tag = seed,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            swatch.Tapped += (_, _) =>
            {
                if (DataContext is ShellViewModel shell)
                    shell.SetSeedColorCommand.Execute(seed);
            };
            SeedPanel.Children.Add(swatch);
        }

        SelectCategory("appearance");
        UpdatePlatformAvailability();
    }

    /// <summary>Used by in-view navigation and screenshot tests; keys are stable, localized labels stay in XAML.</summary>
    public void SelectCategory(string categoryKey)
    {
        var categoryIndex = Array.FindIndex(Categories, item => item.Key == categoryKey);
        if (categoryIndex < 0 || (categoryKey == "storage" && !IsAndroid))
            categoryIndex = 0;
        var category = Categories[categoryIndex];

        _selectedCategory = category.Key;
        SettingsSectionTitle.Text = category.Title;
        SettingsSectionSubtitle.Text = category.Subtitle;

        foreach (var (key, card) in _categoryCards)
            card.IsVisible = key == _selectedCategory && (key != "storage" || IsAndroid);

        foreach (var (key, wide, compact) in _navigationButtons)
        {
            SetSelectedClass(wide, key == _selectedCategory);
            SetSelectedClass(compact, key == _selectedCategory);
        }

        SettingsContentScroll.Offset = new Avalonia.Vector(0, 0);
    }

    private bool IsAndroid => DataContext is ShellViewModel shell && shell.Capabilities.IsAndroid;

    private static void SetSelectedClass(Control control, bool selected)
    {
        if (selected && !control.Classes.Contains("selected"))
            control.Classes.Add("selected");
        else if (!selected && control.Classes.Contains("selected"))
            control.Classes.Remove("selected");
    }

    private void OnSettingsNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string categoryKey })
            SelectCategory(categoryKey);
    }

    private void OnSettingsLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateSettingsLayout(e.NewSize.Width);

    private void UpdateSettingsLayout(double width)
    {
        if (width <= 0 || _isWideLayout == (width >= 980))
            return;

        var wide = width >= 980;
        _isWideLayout = wide;
        SettingsResponsiveLayout.ColumnDefinitions[0].Width = wide
            ? new GridLength(248)
            : new GridLength(0);
        WideSettingsNavigation.IsVisible = wide;
        CompactSettingsNavigation.IsVisible = !wide;
    }

    private void UpdatePlatformAvailability()
    {
        var storageAvailable = IsAndroid;
        foreach (var (key, wide, compact) in _navigationButtons.Where(item => item.Key == "storage"))
        {
            wide.IsVisible = storageAvailable;
            compact.IsVisible = storageAvailable;
        }
        if (!storageAvailable && _selectedCategory == "storage")
            SelectCategory("appearance");
        else
            SelectCategory(_selectedCategory);
    }

    private async void OnAuthorizedRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AuthorizedRootModel root } && DataContext is ShellViewModel shell)
            await shell.OpenAuthorizedRootAsync(root);
    }

    private async void OnReauthorizeRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AuthorizedRootModel root } && DataContext is ShellViewModel shell)
            await shell.ReauthorizeAuthorizedRootAsync(root);
        e.Handled = true;
    }

    private async void OnRemoveAuthorizedRootClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AuthorizedRootModel root } && DataContext is ShellViewModel shell)
            await shell.RemoveAuthorizedRootAsync(root);
        e.Handled = true;
    }

    private async void OnLicensesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IDialogs dialogs) return;
        await dialogs.ShowPropertiesAsync("开源许可证",
            "本应用基于以下开源组件构建：\n\n" +
            "· Avalonia 12（MIT）\n" +
            "· Md3.Avalonia（Apache-2.0），含 Material Color Utilities（Apache-2.0）\n" +
            "· Material Symbols Rounded 字体（Apache-2.0）\n" +
            "· CommunityToolkit.Mvvm（MIT）\n" +
            "· .NET 10 运行时与类库（MIT）\n" +
            "· Inter 字体（SIL OFL 1.1）\n\n" +
            "完整文本见随包附带的 THIRD-PARTY-NOTICES 与各组件仓库。本应用自身以 Apache-2.0 许可发布。");
    }
}
