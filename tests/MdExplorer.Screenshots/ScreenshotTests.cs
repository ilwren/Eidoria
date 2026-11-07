using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Security.Cryptography;
using MdExplorer.Domain;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Media;
using MdExplorer.UI.Preview;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using MdExplorer.UI.Views;
using Md3.Avalonia.Controls;
using Md3.Avalonia.Extra.Controls;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MdExplorer.Screenshots.TestAppBuilder))]
// 所有截图共用同一个 Avalonia 应用、主题与 Skia 渲染面；禁止测试并行，避免窗口/主题相互污染。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MdExplorer.Screenshots;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MdExplorer.UI.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true,
            });
}

/// <summary>
/// 断点截图生成（兼作 UI 冒烟测试）：以真实视图在无头 Skia 下渲染五档断点与关键页面，
/// PNG 写入 BaseDirectory/screenshots 供 CI 归档（见 ci.yml 的 ui-screenshots 工件）。
/// </summary>
public sealed class ScreenshotTests
{
    private static readonly string SampleRoot = SampleData.Create();
    private static string PicturesDir => Path.Combine(SampleRoot, "Pictures");
    private static string DocumentsDir => Path.Combine(SampleRoot, "Documents");

    private static string OutputDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("MDEXPLORER_SCREENSHOTS")
                      ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async Task<ShellViewModel> CreateShellAsync(
        string themeMode = "Light", bool androidAccess = false, IFoldablePostureSource? foldablePosture = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "mdexplorer-shots", Guid.NewGuid().ToString("N"));
        var built = AppHost.Build(dataDirectory: dataDir, startFolder: PicturesDir, foldablePosture: foldablePosture);
        var services = androidAccess
            ? built with
            {
                Trash = new AppTrashService(dataDir, built.StorageFileSystem),
                Capabilities = new PlatformCapabilities(
                    CanMoveToTrash: true,
                    CanBrowseTrash: true,
                    IsAndroid: true,
                    IsDesktop: false,
                    UsesAppTrash: true),
            }
            : built;
        services.SettingsStore.Settings.ThemeMode = themeMode;
        services.SettingsStore.Settings.SeedColor = "#006A6A";
        ThemeService.Apply(services.SettingsStore.Settings);
        var shell = AppHost.CreateShell(services);
        shell.ThemeSettingsChanged += (_, _) => ThemeService.Apply(services.SettingsStore.Settings);
        await shell.InitializeAsync();
        await shell.OpenFolderAsync(PicturesDir);
        await shell.Browser.WhenIdleAsync();
        // 选中一张照片让预览出图
        var photo = shell.Browser.Items.FirstOrDefault(i => i.Name.StartsWith("IMG_", StringComparison.Ordinal));
        if (photo is not null)
        {
            shell.Browser.SelectedItems.Clear();
            shell.Browser.SelectedItems.Add(photo);
            for (int i = 0; i < 60 && shell.Preview.Image is null; i++)
                await Task.Delay(50);
        }
        return shell;
    }

    private async Task CaptureAsync(
        string fileName,
        double width,
        double height,
        Func<ShellViewModel, Task>? arrange = null,
        string themeMode = "Light",
        bool androidAccess = false,
        Func<Window, Task>? afterShow = null,
        IFoldablePostureSource? foldablePosture = null)
    {
        var shell = await CreateShellAsync(themeMode, androidAccess, foldablePosture);
        shell.SetViewportSize(width, height);
        if (arrange is not null)
            await arrange(shell);

        Window window = !androidAccess && width >= 840
            ? new MainWindow { Width = width, Height = height, DataContext = shell }
            : new Window
            {
                Width = width,
                Height = height,
                Content = new MainView { DataContext = shell },
            };
        window.Show();
        try
        {
            for (int i = 0; i < 40 && shell.Browser.Items.All(x => x.Thumbnail is null); i++)
                await Task.Delay(50);
            if (shell.DocumentWorkbench.UseOfficeViewer || shell.Preview.HasOfficeDocument)
            {
                // 第三方控件在控件真正挂载后才异步解析；等其页面文字进入视觉树再截图。
                for (int i = 0; i < 100; i++)
                {
                    window.CaptureRenderedFrame();
                    var ready = window.GetVisualDescendants()
                        .OfType<OfficeDocumentViewer>()
                        .Any(viewer => viewer.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(text => !string.IsNullOrWhiteSpace(text.Text)));
                    if (ready)
                        break;
                    await Task.Delay(50);
                }
            }
            await Task.Delay(120);
            window.CaptureRenderedFrame(); // 先热一帧让容器实现/布局稳定
            if (afterShow is not null)
            {
                // 需要检查控件本身（例如浮窗状态）的用例在这里介入，拍完再落盘。
                await afterShow(window);
                await Task.Delay(80);
            }
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, fileName));
        }
        finally
        {
            // 只有脏文本会触发关闭守卫；PDF 位图不要在窗口仍可见时提前释放。
            if (shell.DocumentWorkbench.NeedsCloseConfirmation)
                shell.DocumentWorkbench.Clear();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Compact_BrowserList() => await CaptureAsync("01-compact-390x844.png", 390, 844);

    [AvaloniaFact]
    public async Task Compact_Home() => await CaptureAsync("02-compact-home.png", 390, 844, shell =>
    {
        shell.GoHomeCommand.Execute(null);
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task Medium_BrowserGrid() => await CaptureAsync("03-medium-768x1024.png", 768, 1024, async shell =>
    {
        shell.Browser.ViewMode = BrowserViewMode.Grid;
        await shell.Browser.WhenIdleAsync();
    });

    [AvaloniaFact]
    public async Task Large_TwoTabs() => await CaptureAsync("17-large-tabs.png", 1440, 900, async shell =>
    {
        await shell.Browser.OpenInNewTabAsync(Path.Combine(PicturesDir, "Camera"));
        await shell.Browser.ActivateTabCommand.ExecuteAsync(shell.Browser.Tabs[0]);
        shell.Browser.ViewMode = BrowserViewMode.Grid;
        await shell.Browser.WhenIdleAsync();
        Assert.Equal(2, shell.Browser.Tabs.Count);
        Assert.Equal("Pictures", shell.Browser.ActiveTab!.Title);
    });

    [AvaloniaFact]
    public async Task Large_HomePolished() => await CaptureAsync("21-large-home-polished.png", 1440, 900, async shell =>
    {
        if (shell.Browser.SelectedItems.FirstOrDefault() is { } item)
            await shell.Browser.RecordRecentFileAsync(item.FullPath);
        shell.GoHomeCommand.Execute(null);
        await Task.Delay(180);
    });

    [AvaloniaFact]
    public async Task Large_PdfPreview() => await CaptureAsync("18-large-pdf.png", 1440, 900, async shell =>
    {
        await SelectByNameAsync(
            shell, "设计规范 v3.pdf", () => shell.Preview.HasOfficeDocument && shell.Preview.IsDirectPdf);
        Assert.Equal(PdfViewEngine.Direct, shell.Preview.PdfEngine);
        Assert.Equal(OfficePreviewFormat.Pdf, shell.Preview.OfficeDocument?.Format);
        Assert.True(shell.Preview.CanSwitchToRasterPdf);
        Assert.False(shell.Preview.HasPdf);
        Assert.Null(shell.Preview.Image);
    });

    [AvaloniaFact]
    public async Task Large_PdfRasterFallbackPreview() => await CaptureAsync(
        "55-large-pdf-raster-preview.png", 1440, 900, async shell =>
    {
        await SelectByNameAsync(
            shell, "设计规范 v3.pdf", () => shell.Preview.HasOfficeDocument && shell.Preview.IsDirectPdf);
        await shell.Preview.UseRasterPdfCommand.ExecuteAsync(null);
        Assert.Equal(PdfViewEngine.Raster, shell.Preview.PdfEngine);
        Assert.Equal(2, shell.Preview.PdfPageCount);
        await shell.Preview.NextPdfPageCommand.ExecuteAsync(null);
        Assert.Equal(1, shell.Preview.PdfPageIndex);
        Assert.NotNull(shell.Preview.Image);
    });

    [AvaloniaFact]
    public async Task Large_BrowserLoadMore() => await CaptureAsync(
        "56-large-browser-load-more.png", 1440, 900, async shell =>
    {
        await OpenPagingSamplesAsync(shell);
        Assert.True(shell.Browser.ShowLoadMoreFooter);
        Assert.True(shell.Browser.CanLoadMoreItems);
    });

    [AvaloniaFact]
    public async Task Compact_BrowserPullRefresh() => await CaptureAsync(
        "57-compact-browser-pull-refresh.png", 390, 844, async shell =>
    {
        await OpenPagingSamplesAsync(shell);
        shell.Browser.IsRefreshing = true;
        Assert.True(shell.Browser.IsRefreshing);
        Assert.True(shell.Browser.ShowLoadMoreFooter);
    });

    [AvaloniaFact]
    public async Task ExtraLarge_DualPaneDetails() => await CaptureAsync(
        "59-xlarge-dual-pane-details.png", 1920, 1080, async shell =>
    {
        await ArrangeDualPaneAsync(shell);
        // B 独立切到详细信息并按大小降序；A 仍保持自己的视图与排序。
        var primaryMode = shell.Browser.ViewMode;
        var primarySort = shell.Browser.CurrentSortField;
        shell.SecondaryBrowser.ViewMode = BrowserViewMode.Details;
        shell.SecondaryBrowser.SetSort(SortField.Size, SortDirection.Descending);
        await shell.SecondaryBrowser.WhenIdleAsync();
        Assert.True(shell.SecondaryBrowser.IsDetailsView);
        Assert.Equal(primaryMode, shell.Browser.ViewMode);
        Assert.Equal(primarySort, shell.Browser.CurrentSortField);
        Assert.Equal("Size", shell.SettingsStore.Settings.DualPaneSortField);
    });

    /// <summary>v0.28：对照窗格有自己的可编辑地址栏，错误时留在编辑态并给出提示，A 的路径胶囊不受影响。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_DualPaneAddressEdit() => await CaptureAsync(
        "60-xlarge-dual-pane-address-edit.png", 1920, 1080, async shell =>
    {
        await ArrangeDualPaneForDetailsTestAsync(shell);
        shell.ActivateSecondaryPane();
        shell.SecondaryBrowser.BeginEditAddressCommand.Execute(null);
        Assert.True(shell.SecondaryBrowser.IsAddressEditing);
        Assert.False(shell.Browser.IsAddressEditing);
        Assert.False(shell.ShowSecondaryBreadcrumbBar);

        var current = shell.SecondaryBrowser.CurrentPath!;
        Assert.False(await shell.SecondaryBrowser.NavigateToAddressAsync(Path.Combine(current, "不存在的子目录")));
        Assert.True(shell.SecondaryBrowser.HasAddressError);
        Assert.Equal(current, shell.SecondaryBrowser.CurrentPath);
        Assert.True(shell.Browser.IsAddressEditing == false);
    });

    /// <summary>v0.29：B 自己的搜索面板只覆盖对照窗格，A 的工作区与全局搜索面板不受影响。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_DualPaneSearch() => await CaptureAsync(
        "61-xlarge-dual-pane-search.png", 1920, 1080, async shell =>
    {
        await ArrangeDualPaneForDetailsTestAsync(shell);
        shell.ActivateSecondaryPane();
        shell.SecondaryBrowser.OpenSearchPanelCommand.Execute(null);
        shell.SecondaryBrowser.FilterText = "IMG";
        await Task.Delay(80);
        Assert.True(shell.SecondaryBrowser.IsSearchPanelOpen);
        Assert.False(shell.Browser.IsSearchPanelOpen);
        Assert.Equal(string.Empty, shell.Browser.FilterText);
        Assert.True(shell.SecondaryBrowser.HasActiveSearch);
        Assert.All(shell.SecondaryBrowser.Items, item => Assert.Contains("IMG", item.Name));
        Assert.Contains(shell.Browser.Items, item => !item.Name.Contains("IMG"));
    });

    /// <summary>v0.30：B 的标签轨只在第二个标签出现后展开，标签切换留在 B 会话内。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_DualPaneTabs() => await CaptureAsync(
        "73-xlarge-dual-pane-tabs.png", 1920, 1080, async shell =>
    {
        await ArrangeDualPaneForDetailsTestAsync(shell);
        // 先交换目录再开第二个标签，屏幕上才能同时看到两个不同名字的 B 标签。
        var primaryBeforeSwap = shell.Browser.CurrentPath;
        var secondaryBeforeSwap = shell.SecondaryBrowser.CurrentPath;
        await shell.SwapPanesCommand.ExecuteAsync(null);
        Assert.Equal(secondaryBeforeSwap, shell.Browser.CurrentPath);
        Assert.Equal(primaryBeforeSwap, shell.SecondaryBrowser.CurrentPath);

        await shell.SecondaryBrowser.OpenInNewTabAsync(DocumentsDir);
        await shell.SecondaryBrowser.WhenIdleAsync();
        Assert.True(shell.SecondaryBrowser.HasMultipleTabs);
        Assert.True(shell.ShowSecondaryTabs);
        Assert.False(shell.Browser.HasMultipleTabs); // A 只有一个标签，全局标签轨不受 B 影响
        Assert.Equal(DocumentsDir, shell.SecondaryBrowser.CurrentPath);
        Assert.NotEqual(
            shell.SecondaryBrowser.Tabs[0].Title,
            shell.SecondaryBrowser.Tabs[1].Title);
    });

    [AvaloniaFact]
    public async Task Large_ExtensionCenter() => await CaptureAsync(
        "82-large-extension-center.png", 1440, 900, shell =>
        {
            shell.GoExtensionCenterCommand.Execute(null);
            Assert.Equal(ShellPage.ExtensionCenter, shell.Page);
            Assert.Equal("扩展中心", shell.PageTitle);
            return Task.CompletedTask;
        });

    [AvaloniaFact]
    public async Task Large_SettingsPreferences() => await CaptureAsync(
        "58-large-settings.png", 1440, 900, shell =>
    {
        shell.GoSettingsCommand.Execute(null);
        Assert.Equal(ShellPage.Settings, shell.Page);
        Assert.True(shell.IsPageSize120);
        Assert.True(shell.IsDensityComfortable);
        Assert.True(shell.IsPullTrigger72);
        return Task.CompletedTask;
    }, afterShow: window =>
    {
        var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
        var layout = settings.FindControl<Grid>("SettingsResponsiveLayout")!;
        Assert.True(layout.Bounds.Width >= 980);
        Assert.True(settings.FindControl<Border>("WideSettingsNavigation")!.IsVisible);
        Assert.False(settings.FindControl<WrapPanel>("CompactSettingsNavigation")!.IsVisible);
        Assert.True(settings.FindControl<Border>("SettingsCardAppearance")!.IsVisible);
        Assert.False(settings.FindControl<Border>("SettingsCardBrowse")!.IsVisible);

        settings.SelectCategory("browse");
        Assert.True(settings.FindControl<Border>("SettingsCardBrowse")!.IsVisible);
        Assert.False(settings.FindControl<Border>("SettingsCardAppearance")!.IsVisible);
        settings.SelectCategory("appearance");
        return Task.CompletedTask;
    });

    /// <summary>
    /// v0.37：端侧 AI 卡只报事实——装了就写「已就绪 + 实际占用」，没装就写「未安装」。
    /// 这里用稀疏文件把中文嵌入模型「摆」成就绪态，让截图反映的是真实状态文案而不是一句口号。
    /// </summary>
    [AvaloniaFact]
    public async Task Large_SettingsLocalAi() => await CaptureAsync(
        "80-large-settings-local-ai.png", 1440, 900, async shell =>
        {
            shell.GoSettingsCommand.Execute(null);
            Assert.Equal(ShellPage.Settings, shell.Page);
            Assert.Equal(5, shell.LocalAiModels.Count);
            Assert.Contains("尚未下载", shell.LocalAiStatusText, StringComparison.Ordinal);

            var definition = AiModelManifest.LoadEmbedded().First(model => model.Id == "text-embed-zh");
            var root = Path.Combine(shell.LocalAiModelsDirectoryText, definition.Id);
            Directory.CreateDirectory(root);
            foreach (var file in definition.Files)
            {
                var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                // 稀疏文件：不真占 23 MB，但状态行里的体积与真实模型同量级。
                stream.SetLength(file.EndsWith(".onnx", StringComparison.Ordinal) ? 22_600_000 : 64_000);
            }

            shell.RefreshLocalAiCommand.Execute(null);
            Assert.True(shell.LocalAiModels.First(model => model.Id == "text-embed-zh").Installed);
            Assert.Contains("1/5", shell.LocalAiStatusText, StringComparison.Ordinal);
            // 刷新会弹一条同文案的提示条；这张图要拍的是卡片本身，别让提示条盖住下面的内容。
            shell.IsSnackbarVisible = false;
            await Task.CompletedTask;
        },
        afterShow: async window =>
        {
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            settings.SelectCategory("ai-search");
            // 端侧 AI 卡在设置页中段：把卡片**最后**一个入口滚进视口，
            // 卡片本身就会整体落在视口里（只 BringIntoView 标题会把卡片挤到屏幕外）。
            var lastEntry = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Content as string == "打开模型目录");
            lastEntry.BringIntoView();
            for (var frame = 0; frame < 15; frame++)
            {
                window.CaptureRenderedFrame();
                await Task.Delay(20);
            }
        });

    /// <summary>Large 常驻抽屉：品牌区必须固定在顶部，只有导航列表随滚动移动。</summary>
    [AvaloniaFact]
    public async Task Large_DrawerBrandHeaderStaysFixedWhileNavScrolls()
    {
        var shell = await CreateShellAsync();
        // 560 DIP 仍属 Large，但导航内容超过可视高度，能证明品牌区不随列表滚动。
        shell.SetViewportSize(1440, 560);
        var window = new MainWindow { Width = 1440, Height = 560, DataContext = shell };
        window.Show();
        try
        {
            await Task.Delay(150);
            window.CaptureRenderedFrame();

            var drawer = window.GetVisualDescendants().OfType<DrawerContentView>()
                .First(view => view.IsEffectivelyVisible);
            var header = drawer.FindControl<Border>("DrawerBrandHeader");
            Assert.NotNull(header);
            var scroller = drawer.GetVisualDescendants().OfType<ScrollViewer>()
                .OrderByDescending(view => view.Extent.Height - view.Viewport.Height)
                .First();
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
                $"抽屉内容未溢出：extent={scroller.Extent.Height}, viewport={scroller.Viewport.Height}");
            Assert.DoesNotContain(scroller, header!.GetVisualAncestors());

            var before = header.TranslatePoint(new Point(0, 0), drawer);
            window.CaptureRenderedFrame();
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            await Task.Delay(120);
            window.CaptureRenderedFrame();

            var after = header.TranslatePoint(new Point(0, 0), drawer);
            Assert.Equal(before, after);
            Assert.True(scroller.Offset.Y > 0);
            Assert.True(header.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>v0.27：对照窗格详细信息面必须真正替换列表/网格，选集双向镜像且排序只作用于 B。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_SecondaryPaneDetailsMirrorsSelectionAndSorts()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1920, 1080);
        var window = new MainWindow { Width = 1920, Height = 1080, DataContext = shell };
        window.Show();
        try
        {
            await ArrangeDualPaneForDetailsTestAsync(shell);
            var view = Assert.IsType<MainView>(window.Content);
            var details = view.FindControl<DataGrid>("SecondaryFileDetails");
            var list = view.FindControl<Control>("SecondaryFileList");
            var grid = view.FindControl<Control>("SecondaryFileGrid");
            var detailsHost = view.FindControl<Control>("SecondaryDetailsRefresh");
            var listHost = view.FindControl<Control>("SecondaryListRefresh");
            var gridHost = view.FindControl<Control>("SecondaryGridRefresh");
            Assert.NotNull(details);
            Assert.NotNull(list);
            Assert.NotNull(grid);
            Assert.NotNull(detailsHost);
            Assert.NotNull(listHost);
            Assert.NotNull(gridHost);

            // 无头窗口先走一帧，确保绑定与布局已应用，再断言宿主的可见性。
            await Task.Delay(120);
            window.CaptureRenderedFrame();
            var state = $"mode={shell.SecondaryBrowser.ViewMode}, list={listHost!.IsVisible}, " +
                        $"grid={gridHost!.IsVisible}, details={detailsHost!.IsVisible}";
            Assert.False(shell.SecondaryBrowser.IsDetailsView, state);
            Assert.True(listHost.IsVisible, state);
            Assert.False(detailsHost.IsVisible, state);

            shell.SecondaryBrowser.ViewMode = BrowserViewMode.Details;
            await Task.Delay(120);
            window.CaptureRenderedFrame();

            var switched = $"mode={shell.SecondaryBrowser.ViewMode}, list={listHost.IsVisible}, " +
                           $"grid={gridHost.IsVisible}, details={detailsHost.IsVisible}";
            Assert.True(detailsHost.IsVisible, switched);
            Assert.False(listHost.IsVisible, switched);
            Assert.False(gridHost.IsVisible, switched);
            // 详细信息面直接消费 B 会话集合，不另建副本
            Assert.Same(shell.SecondaryBrowser.Items, details.ItemsSource);

            var target = shell.SecondaryBrowser.Items.First(item => !item.IsFolder);
            shell.SecondaryBrowser.SelectedItems.Clear();
            shell.SecondaryBrowser.SelectedItems.Add(target);
            await Task.Delay(80);
            window.CaptureRenderedFrame();
            Assert.Contains(target, details.SelectedItems.OfType<FileItemViewModel>());

            var primaryMode = shell.Browser.ViewMode;
            var primarySort = shell.Browser.CurrentSortField;
            var primaryDirection = shell.Browser.CurrentSortDirection;
            shell.SecondaryBrowser.ApplyColumnSort(SortField.Size);
            Assert.Equal(SortField.Size, shell.SecondaryBrowser.CurrentSortField);
            Assert.Equal(SortDirection.Ascending, shell.SecondaryBrowser.CurrentSortDirection);
            Assert.Equal(primaryMode, shell.Browser.ViewMode);
            Assert.Equal(primarySort, shell.Browser.CurrentSortField);
            Assert.Equal(primaryDirection, shell.Browser.CurrentSortDirection);
            Assert.Equal("Size", shell.SettingsStore.Settings.DualPaneSortField);
            Assert.NotEqual("Size", shell.SettingsStore.Settings.SortField);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>v0.28：B 进入地址编辑时，路径胶囊换成输入框，A 的路径胶囊保持面包屑。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_SecondaryAddressEditorTakesOverPathPill()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1920, 1080);
        var window = new MainWindow { Width = 1920, Height = 1080, DataContext = shell };
        window.Show();
        try
        {
            await ArrangeDualPaneForDetailsTestAsync(shell);
            var view = Assert.IsType<MainView>(window.Content);
            var editor = view.FindControl<TextBox>("SecondaryAddressBox");
            var breadcrumb = view.FindControl<Control>("SecondaryBreadcrumbScroller");
            var primaryBreadcrumb = view.FindControl<Control>("BreadcrumbScroller");
            Assert.NotNull(editor);
            Assert.NotNull(breadcrumb);
            Assert.NotNull(primaryBreadcrumb);

            await Task.Delay(120);
            window.CaptureRenderedFrame();
            // IsVisible 是本地属性、不向下继承，所以判“实际可见”而不是判子控件自己的值。
            Assert.False(editor!.IsEffectivelyVisible);
            Assert.True(breadcrumb!.IsEffectivelyVisible);

            shell.ActivateSecondaryPane();
            shell.SecondaryBrowser.BeginEditAddressCommand.Execute(null);
            await Task.Delay(120);
            window.CaptureRenderedFrame();

            var state = $"editingB={shell.SecondaryBrowser.IsAddressEditing}, editingA={shell.Browser.IsAddressEditing}, " +
                        $"editor={editor.IsEffectivelyVisible}, breadcrumb={breadcrumb.IsEffectivelyVisible}, " +
                        $"pill={shell.ShowSecondaryBreadcrumbBar}";
            Assert.True(editor.IsEffectivelyVisible, state);
            Assert.False(breadcrumb.IsEffectivelyVisible, state);
            Assert.Equal(shell.SecondaryBrowser.CurrentPath, editor.Text);
            // A 侧保持自己的面包屑，不被 B 的编辑态牵连
            Assert.True(primaryBreadcrumb.IsEffectivelyVisible, state);
            Assert.True(shell.ShowBreadcrumbBar, state);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>v0.29：B 搜索面板在窗格内浮出，A 的搜索面板保持关闭。</summary>
    [AvaloniaFact]
    public async Task ExtraLarge_SecondarySearchPanelStaysInsideItsPane()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1920, 1080);
        var window = new MainWindow { Width = 1920, Height = 1080, DataContext = shell };
        window.Show();
        try
        {
            await ArrangeDualPaneForDetailsTestAsync(shell);
            var view = Assert.IsType<MainView>(window.Content);
            var secondaryPanel = view.FindControl<Control>("SecondarySearchPanel");
            var primarySearch = view.FindControl<Control>("BrowserSearch");
            Assert.NotNull(secondaryPanel);
            Assert.NotNull(primarySearch);

            shell.ActivateSecondaryPane();
            shell.SecondaryBrowser.OpenSearchPanelCommand.Execute(null);
            await Task.Delay(120);
            window.CaptureRenderedFrame();

            var state = $"panelB={shell.SecondaryBrowser.IsSearchPanelOpen}, panelA={shell.Browser.IsSearchPanelOpen}, " +
                        $"visible={secondaryPanel!.IsEffectivelyVisible}";
            Assert.True(secondaryPanel.IsEffectivelyVisible, state);
            Assert.False(shell.Browser.IsSearchPanelOpen, state);
            // 面板在 B 窗格内部，不属于 A 的工作区
            var pane = view.FindControl<Border>("SecondaryPane");
            Assert.NotNull(pane);
            Assert.Contains(secondaryPanel, pane!.GetVisualDescendants());
            // 面板只压在文件区上，B 的工具栏与搜索岛仍可见（否则用户无法在面板外操作）
            var toolbar = view.FindControl<Control>("SecondarySearch");
            Assert.NotNull(toolbar);
            Assert.True(toolbar!.IsEffectivelyVisible);

            // 切回 A 时收掉 B 的浮层
            shell.SecondaryBrowser.IsSearchPanelOpen = false;
            await Task.Delay(60);
            window.CaptureRenderedFrame();
            Assert.False(secondaryPanel.IsEffectivelyVisible, state);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ArrangeDualPaneForDetailsTestAsync(ShellViewModel shell)
    {
        await shell.Browser.WhenIdleAsync();
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        await shell.SecondaryBrowser.WhenIdleAsync();
        Assert.True(shell.ShowDualPane);
        Assert.False(string.IsNullOrWhiteSpace(shell.SecondaryBrowser.CurrentPath));
    }

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchText() => await CaptureAsync("38-large-document-editor.png", 1440, 900, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "Program.cs");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsTextDocument);
        workbench.BeginEditCommand.Execute(null);
        workbench.EditorText += "\n// Eidoria 文档工作台编辑中\n";
        Assert.True(workbench.IsDirty);
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchPdf()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        await UseRasterPdfWorkbenchAsync(workbench, simulateDirectFailure: true);
        Assert.Equal(2, workbench.PdfPageCount);
        Assert.Equal(2, workbench.PdfPages.Count);
        Assert.NotNull(workbench.PdfImage);
        workbench.NextPdfPageCommand.Execute(null);
        workbench.ZoomInCommand.Execute(null);
        for (int i = 0; i < 80 && workbench.IsLoading; i++)
            await Task.Delay(50);
        Assert.Equal(1, workbench.PdfPageIndex);
        Assert.Equal(125, workbench.ZoomPercent);
        Assert.NotNull(workbench.PdfImage);

        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var view = Assert.IsType<MainView>(window.Content);
        window.Show();
        try
        {
            ListBox? pageList = null;
            Control? firstPage = null;
            Control? secondPage = null;
            for (int i = 0; i < 100; i++)
            {
                window.CaptureRenderedFrame();
                // DocumentWorkbenchView 有独立 NameScope，MainView.FindControl 跨不过去；按视觉树与控件名定位。
                pageList = view.GetVisualDescendants()
                    .OfType<ListBox>()
                    .FirstOrDefault(control => control.Name == "PdfPageList");
                firstPage = pageList?.ContainerFromIndex(0);
                secondPage = pageList?.ContainerFromIndex(1);
                if (firstPage is not null && secondPage is not null &&
                    workbench.PdfPages.All(page => page.Thumbnail is not null))
                    break;
                await Task.Delay(50);
            }

            Assert.NotNull(pageList);
            Assert.Equal(2, pageList!.ItemCount);
            Assert.True(pageList.Bounds.Width > 100 && pageList.Bounds.Height > 300,
                $"PDF 缩略图轨应有真实布局尺寸，实际 {pageList.Bounds}。");
            Assert.NotNull(firstPage);
            Assert.NotNull(secondPage);
            Assert.Same(workbench.PdfPages[0], firstPage!.DataContext);
            Assert.Same(workbench.PdfPages[1], secondPage!.DataContext);
            Assert.True(firstPage.Bounds.Width > 80 && firstPage.Bounds.Height > 100,
                $"第一页缩略图容器应已实现，实际 {firstPage.Bounds}。");
            Assert.All(workbench.PdfPages, page => Assert.NotNull(page.Thumbnail));
            Assert.Contains(firstPage.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "1");
            Assert.Contains(secondPage.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2");
            Assert.Contains(firstPage.GetVisualDescendants().OfType<Image>(), image => image.Source is not null);
            Assert.Contains(secondPage.GetVisualDescendants().OfType<Image>(), image => image.Source is not null);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "39-large-pdf-workbench.png"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Compact_DocumentWorkbenchText() => await CaptureAsync("40-compact-document-editor.png", 390, 844, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "README.md");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsCompactLayout);
        Assert.True(workbench.IsMarkdown);
        workbench.BeginEditCommand.Execute(null);
        Assert.True(workbench.IsEditing);
    });

    [AvaloniaFact]
    public async Task Compact_DocumentWorkbenchPdf() => await CaptureAsync("41-compact-pdf-workbench.png", 390, 844, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsCompactLayout);
        await UseRasterPdfWorkbenchAsync(workbench);
        Assert.Equal(2, workbench.PdfPageCount);
        Assert.NotNull(workbench.PdfImage);
        workbench.NextPdfPageCommand.Execute(null);
        for (int i = 0; i < 80 && workbench.IsLoading; i++)
            await Task.Delay(50);
        Assert.Equal(1, workbench.PdfPageIndex);
        Assert.NotNull(workbench.PdfImage);
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchPdfContinuous() => await CaptureAsync(
        "45-large-pdf-continuous.png", 1440, 900, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        await UseRasterPdfWorkbenchAsync(workbench);
        workbench.UseContinuousPdfModeCommand.Execute(null);
        workbench.ZoomOutCommand.Execute(null);
        workbench.ZoomOutCommand.Execute(null);
        for (int i = 0; i < 100 && workbench.PdfPages.Any(page => page.PageImage is null); i++)
            await Task.Delay(50);

        Assert.True(workbench.IsContinuousPdfMode);
        Assert.Equal(50, workbench.ZoomPercent);
        Assert.Equal(0, workbench.PdfPageIndex);
        Assert.All(workbench.PdfPages, page => Assert.NotNull(page.PageImage));
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchDocx() => await CaptureAsync(
        "48-large-office-docx.png", 1440, 900, async shell =>
    {
        await OpenOfficeByNameAsync(shell, "报告-终版.docx");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsOfficeDocument);
        Assert.Equal(MdExplorer.UI.Preview.OfficePreviewFormat.Docx, workbench.OfficeDocument?.Format);
        Assert.True(workbench.OfficeDocument?.Facts.ParagraphCount > 5);
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchPptx() => await CaptureAsync(
        "49-large-office-pptx.png", 1440, 900, async shell =>
    {
        await OpenOfficeByNameAsync(shell, "产品发布会.pptx");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(MdExplorer.UI.Preview.OfficePreviewFormat.Pptx, workbench.OfficeDocument?.Format);
        Assert.Equal(2, workbench.OfficePageCount);
        Assert.Contains("PowerPoint", workbench.StatusText);
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchXlsx() => await CaptureAsync(
        "50-large-office-xlsx.png", 1440, 900, async shell =>
    {
        await OpenOfficeByNameAsync(shell, "预算表.xlsx");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(MdExplorer.UI.Preview.OfficePreviewFormat.Xlsx, workbench.OfficeDocument?.Format);
        Assert.Equal(2, workbench.OfficeSheetCount);
        workbench.NextOfficeSheetCommand.Execute(null);
        Assert.Equal("明细", workbench.CurrentOfficeSheetName);
    });

    [AvaloniaFact]
    public async Task Compact_DocumentWorkbenchPptx() => await CaptureAsync(
        "51-compact-office-pptx.png", 390, 844, async shell =>
    {
        await OpenOfficeByNameAsync(shell, "产品发布会.pptx");
        var workbench = shell.DocumentWorkbench;
        Assert.True(workbench.IsCompactLayout);
        Assert.Equal(2, workbench.OfficePageCount);
    });

    [AvaloniaFact]
    public async Task Large_OfficePreviewXlsx() => await CaptureAsync(
        "52-large-office-preview-xlsx.png", 1440, 900, async shell =>
    {
        await shell.OpenFolderAsync(DocumentsDir);
        await shell.Browser.WhenIdleAsync();
        await SelectByNameAsync(shell, "预算表.xlsx", () => shell.Preview.HasOfficeDocument);
        Assert.Equal(2, shell.Preview.OfficeSheetCount);
        shell.Preview.NextOfficeSheetCommand.Execute(null);
        Assert.Equal(1, shell.Preview.OfficeSheetIndex);
        Assert.Contains("明细", shell.Preview.OfficeStatusText);
    });

    // OpenDocument 家族：内置只读器把结构还原成 Markdown，再由既有渲染器呈现，不依赖 LibreOffice。
    [AvaloniaFact]
    public async Task Large_OpenDocumentTextPreview() => await CaptureAsync(
        "77-large-odt-preview.png", 1440, 900, async shell =>
    {
        await shell.OpenFolderAsync(DocumentsDir);
        await shell.Browser.WhenIdleAsync();
        await SelectByNameAsync(shell, "采购方案.odt", () => shell.Preview.HasMarkdown);
        Assert.Contains("# 办公耗材采购方案", shell.Preview.MarkdownText, StringComparison.Ordinal);
        Assert.Contains("| 项目 | 数量 | 金额（元） |", shell.Preview.MarkdownText, StringComparison.Ordinal);
        Assert.Contains("激光打印机硒鼓 × 6", shell.Preview.MarkdownText, StringComparison.Ordinal);
    });

    [AvaloniaFact]
    public async Task Compact_OpenDocumentSheetPreview() => await CaptureAsync(
        "78-compact-ods-preview.png", 390, 844, async shell =>
    {
        await shell.OpenFolderAsync(DocumentsDir);
        await shell.Browser.WhenIdleAsync();
        // 打开预览要在导航之后：进入目录会收起预览页，先置位会被导航重置。
        shell.IsPreviewPageOpen = true;
        await SelectByNameAsync(shell, "季度销量.ods", () => shell.Preview.HasMarkdown);
        Assert.Contains("## 工作表 · 一季度", shell.Preview.MarkdownText, StringComparison.Ordinal);
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchPdfDirect() => await CaptureAsync(
        "53-large-pdf-direct-workbench.png", 1440, 900, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        Assert.Equal(ShellPage.DocumentWorkbench, shell.Page);
        Assert.True(workbench.IsDirectPdf);
        Assert.Equal(PdfViewEngine.Direct, workbench.PdfEngine);
        Assert.True(workbench.ShowOfficeWorkbench);
        Assert.False(workbench.ShowPdfWorkbench);
        Assert.True(workbench.CanSwitchToRasterPdf);
        Assert.Null(workbench.PdfImage);
        Assert.Contains("PDF 直接预览", workbench.StatusText);
    });

    [AvaloniaFact]
    public async Task Compact_DocumentWorkbenchPdfDirect() => await CaptureAsync(
        "54-compact-pdf-direct-workbench.png", 390, 844, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        Assert.True(workbench.IsCompactLayout);
        Assert.True(workbench.IsDirectPdf);
        Assert.Equal(PdfViewEngine.Direct, workbench.PdfEngine);
        Assert.True(workbench.ShowOfficeWorkbench);
        Assert.False(workbench.ShowPdfWorkbench);
        Assert.Null(workbench.PdfImage);
    });

    [AvaloniaFact]
    public async Task Compact_DocumentWorkbenchPdfContinuous() => await CaptureAsync(
        "46-compact-pdf-continuous.png", 390, 844, async shell =>
    {
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        await UseRasterPdfWorkbenchAsync(workbench);
        workbench.UseContinuousPdfModeCommand.Execute(null);
        for (int i = 0; i < 100 && workbench.PdfPages.Any(page => page.PageImage is null); i++)
            await Task.Delay(50);

        Assert.True(workbench.IsCompactLayout);
        Assert.True(workbench.IsContinuousPdfMode);
        Assert.All(workbench.PdfPages, page => Assert.NotNull(page.PageImage));
    });

    [AvaloniaFact]
    public async Task Large_DocumentWorkbenchPdfContinuousScrollSync()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        await OpenDocumentByNameAsync(shell, "设计规范 v3.pdf");
        var workbench = shell.DocumentWorkbench;
        await UseRasterPdfWorkbenchAsync(workbench);
        workbench.UseContinuousPdfModeCommand.Execute(null);
        workbench.ZoomOutCommand.Execute(null);
        workbench.ZoomOutCommand.Execute(null);
        for (int i = 0; i < 100 && workbench.PdfPages.Any(page => page.PageImage is null); i++)
            await Task.Delay(50);
        Assert.All(workbench.PdfPages, page => Assert.NotNull(page.PageImage));

        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var view = Assert.IsType<MainView>(window.Content);
        window.Show();
        try
        {
            ListBox? list = null;
            Control? firstPage = null;
            ScrollViewer? scroller = null;
            for (int i = 0; i < 100; i++)
            {
                window.CaptureRenderedFrame();
                list = view.GetVisualDescendants()
                    .OfType<ListBox>()
                    .FirstOrDefault(control => control.Name == "PdfContinuousList");
                firstPage = list?.ContainerFromIndex(0);
                scroller = list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (list is not null && firstPage is not null && scroller is not null &&
                    list.Bounds.Height > 300 && firstPage.Bounds.Height > 100)
                    break;
                await Task.Delay(50);
            }

            Assert.NotNull(list);
            Assert.NotNull(firstPage);
            Assert.NotNull(scroller);
            Assert.Same(workbench.PdfPages[0], firstPage!.DataContext);
            Assert.Contains(firstPage.GetVisualDescendants().OfType<Image>(), image => image.Source is not null);

            scroller!.Offset = new Vector(0, firstPage.Bounds.Height);
            for (int i = 0; i < 100 && workbench.PdfPageIndex != 1; i++)
            {
                window.CaptureRenderedFrame();
                await Task.Delay(50);
            }
            Assert.Equal(1, workbench.PdfPageIndex);
            var secondPage = list!.ContainerFromIndex(1);
            Assert.NotNull(secondPage);
            Assert.Same(workbench.PdfPages[1], secondPage!.DataContext);
            Assert.Contains(secondPage.GetVisualDescendants().OfType<Image>(), image => image.Source is not null);

            workbench.PreviousPdfPageCommand.Execute(null);
            for (int i = 0; i < 100; i++)
            {
                window.CaptureRenderedFrame();
                firstPage = list.ContainerFromIndex(0);
                var top = firstPage?.TranslatePoint(new Point(0, 0), list)?.Y;
                if (workbench.PdfPageIndex == 0 && top is > -20 and < 80)
                    break;
                await Task.Delay(50);
            }
            Assert.Equal(0, workbench.PdfPageIndex);
            Assert.NotNull(firstPage);
            var firstTop = firstPage!.TranslatePoint(new Point(0, 0), list)?.Y;
            Assert.True(firstTop is > -20 and < 80, $"上一页命令应把第一页滚回视口顶部，实际 Y={firstTop}。");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Large_UnsavedDocumentCloseGuard()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        await OpenDocumentByNameAsync(shell, "Program.cs");
        var workbench = shell.DocumentWorkbench;
        workbench.BeginEditCommand.Execute(null);
        workbench.EditorText += "\n// 未保存更改\n";
        Assert.True(workbench.NeedsCloseConfirmation);

        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            window.Close();
            for (int i = 0; i < 80 && !shell.IsDialogOpen; i++)
            {
                window.CaptureRenderedFrame();
                await Task.Delay(50);
            }

            Assert.True(shell.IsDialogOpen);
            Assert.True(window.IsVisible);
            Assert.False(closed);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "47-large-unsaved-close-dialog.png"));

            shell.DialogCancelCommand.Execute(null);
            for (int i = 0; i < 40 && shell.IsDialogOpen; i++)
                await Task.Delay(25);
            Assert.False(shell.IsDialogOpen);
            Assert.True(window.IsVisible);
            Assert.False(closed);
            await Task.Delay(100);

            window.Close();
            for (int i = 0; i < 80 && !shell.IsDialogOpen; i++)
                await Task.Delay(50);
            Assert.True(shell.IsDialogOpen);
            shell.DialogConfirmCommand.Execute(null);
            for (int i = 0; i < 80 && !closed; i++)
                await Task.Delay(50);
            Assert.True(closed);
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window.IsVisible)
            {
                workbench.Clear();
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task Large_AutomationEditor()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        shell.GoAutomationCommand.Execute(null);
        Assert.Equal(ShellPage.Automation, shell.Page);
        Assert.True(shell.Automation.IsWorkflowValid, shell.Automation.ValidationText);
        Assert.Equal(4, shell.Automation.Nodes.Count);
        Assert.Equal(3, shell.Automation.Connections.Count);

        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var view = Assert.IsType<MainView>(window.Content);
        window.Show();
        try
        {
            Control? editor = null;
            Control[] nodeControls = [];
            for (int i = 0; i < 100; i++)
            {
                window.CaptureRenderedFrame();
                editor = view.GetVisualDescendants()
                    .OfType<Control>()
                    .FirstOrDefault(control => control.Name == "AutomationEditor");
                nodeControls = view.GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control => control.GetType().FullName == "NodifyM.Avalonia.Controls.Node")
                    .ToArray();
                if (editor is not null && editor.Bounds.Width > 500 && nodeControls.Length == 4)
                    break;
                await Task.Delay(50);
            }

            Assert.NotNull(editor);
            Assert.True(editor!.Bounds.Width > 500 && editor.Bounds.Height > 400,
                $"自动化画布应有真实布局尺寸，实际 {editor.Bounds}。");
            Assert.Equal(4, nodeControls.Length);
            Assert.All(nodeControls, node => Assert.True(
                node.Bounds.Width > 100 && node.Bounds.Height > 40,
                $"节点容器应已实现，实际 {node.Bounds}。"));

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "42-large-automation.png"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Compact_AutomationEditor() => await CaptureAsync("43-compact-automation.png", 390, 844, shell =>
    {
        shell.GoAutomationCommand.Execute(null);
        Assert.Equal(ShellPage.Automation, shell.Page);
        Assert.True(shell.Automation.IsWorkflowValid, shell.Automation.ValidationText);
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task Large_AutomationEditorDark() => await CaptureAsync(
        "44-large-automation-dark.png", 1440, 900,
        shell =>
        {
            shell.GoAutomationCommand.Execute(null);
            Assert.Equal(ShellPage.Automation, shell.Page);
            return Task.CompletedTask;
        },
        themeMode: "Dark");

    [AvaloniaFact]
    public async Task Large_AddressBar() => await CaptureAsync("19-large-address.png", 1440, 900, shell =>
    {
        shell.Browser.BeginEditAddressCommand.Execute(null);
        Assert.True(shell.Browser.IsAddressEditing);
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task Large_TaskCenter()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var window = new Window
        {
            Width = 1440,
            Height = 900,
            Content = new MainView { DataContext = shell },
        };
        window.Show();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Task.Delay(120);
            var running = shell.RunOperationAsync("正在复制 128 个项目…", async (progress, ct) =>
            {
                progress.Report(new OperationProgress(0.46, "IMG_20261026_081500.jpg", 59, 128));
                await release.Task.WaitAsync(ct);
                return new OperationOutcome(128, [], []);
            });
            var queued = shell.RunOperationAsync("正在压缩为 ZIP…", (_, _) =>
                Task.FromResult(new OperationOutcome(1, [], [])));
            shell.IsTaskCenterOpen = true;
            await Task.Delay(180);
            window.CaptureRenderedFrame();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "20-large-task-center.png"));

            release.TrySetResult();
            Assert.True((await running).Success);
            Assert.True((await queued).Success);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Expanded_BrowserPreview() => await CaptureAsync("04-expanded-1100x760.png", 1100, 760);

    [AvaloniaFact]
    public async Task Large_BrowserGridPreview() => await CaptureAsync("05-large-1440x900.png", 1440, 900, async shell =>
    {
        shell.Browser.ViewMode = BrowserViewMode.Grid;
        await shell.Browser.WhenIdleAsync();
    });

    [AvaloniaFact]
    public async Task Large_DetailsView() => await CaptureAsync("15-large-details.png", 1440, 900, async shell =>
    {
        shell.Browser.ViewMode = BrowserViewMode.Details;
        await shell.Browser.WhenIdleAsync();
    });

    /// <summary>拖放进行中的投放提示浮层（原生 DnD 无法在无头环境完整驱动，这里直接呈现浮层做视觉冒烟）。</summary>
    [AvaloniaFact]
    public async Task Large_DropHint()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        window.Show();
        try
        {
            await Task.Delay(150);
            var hint = view.FindControl<Border>("DropHint");
            var text = view.FindControl<TextBlock>("DropHintText");
            Assert.NotNull(hint);
            Assert.NotNull(text);
            text!.Text = "释放以复制到「Pictures」";
            hint!.IsVisible = true;
            await Task.Delay(120);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "16-large-drop-hint.png"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExtraLarge_Browser() => await CaptureAsync("06-xlarge-1920x1080.png", 1920, 1080);

    [AvaloniaFact]
    public async Task Large_Dark() => await CaptureAsync("07-large-dark.png", 1440, 900, themeMode: "Dark");

    [AvaloniaFact]
    public async Task Settings_Page() => await CaptureAsync("08-settings-1100x760.png", 1100, 760, shell =>
    {
        shell.GoSettingsCommand.Execute(null);
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task Compact_SettingsResponsive() => await CaptureAsync(
        "81-compact-settings-responsive.png", 390, 844, shell =>
        {
            shell.GoSettingsCommand.Execute(null);
            return Task.CompletedTask;
        },
        afterShow: window =>
        {
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var layout = settings.FindControl<Grid>("SettingsResponsiveLayout")!;
            Assert.True(layout.Bounds.Width < 980);
            Assert.False(settings.FindControl<Border>("WideSettingsNavigation")!.IsVisible);
            Assert.True(settings.FindControl<WrapPanel>("CompactSettingsNavigation")!.IsVisible);
            Assert.True(settings.FindControl<Border>("SettingsCardAppearance")!.IsVisible);
            return Task.CompletedTask;
        });
    /// <summary>弹出层验证：上下文菜单（依赖 ContextMenu/MenuItem 主题齐备）。</summary>
    [AvaloniaFact]
    public async Task Large_ContextMenu_Open()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        window.Show();
        try
        {
            await Task.Delay(150);
            var list = view.FindControl<ListBox>("FileList");
            Assert.NotNull(list);
            Assert.NotNull(list!.ContextMenu);
            list.ContextMenu!.Open(list);
            await Task.Delay(150);
            Assert.True(list.ContextMenu.IsOpen);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "09-large-contextmenu.png"));
            list.ContextMenu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>回归：文件行 ⋮ 按钮必须以自身挂载的 ContextMenu 打开菜单，不能对未挂载控件强行 Open。</summary>
    [AvaloniaFact]
    public async Task Large_ItemMoreButton_OpensContextMenu()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        window.Show();
        try
        {
            await Task.Delay(150);
            window.CaptureRenderedFrame();
            var list = view.FindControl<ListBox>("FileList");
            Assert.NotNull(list);
            var moreButton = list!.GetVisualDescendants()
                .OfType<Button>()
                .First(button => AutomationProperties.GetName(button) == "更多操作" && button.IsEffectivelyVisible);
            var item = Assert.IsType<FileItemViewModel>(moreButton.DataContext);
            var clickPoint = moreButton.TranslatePoint(
                new Point(moreButton.Bounds.Width / 2, moreButton.Bounds.Height / 2), window);
            Assert.NotNull(clickPoint);

            window.MouseMove(clickPoint!.Value);
            window.MouseDown(clickPoint.Value, MouseButton.Left);
            window.MouseUp(clickPoint.Value, MouseButton.Left);
            await Task.Delay(150);

            Assert.NotNull(moreButton.ContextMenu);
            Assert.True(moreButton.ContextMenu!.IsOpen);
            Assert.Same(item, Assert.Single(shell.Browser.SelectedItems));
            moreButton.ContextMenu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>指针回归：在真实桌面窗口顶部横向拖动文件标签超过相邻标签中点后，模型顺序应真实交换。</summary>
    [AvaloniaFact]
    public async Task Large_TabDrag_ReordersTabs()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        await shell.Browser.OpenInNewTabAsync(Path.Combine(PicturesDir, "Camera"));
        var originalOrder = shell.Browser.Tabs.Select(tab => tab.Path).ToArray();
        Assert.Equal(2, originalOrder.Length);

        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var view = Assert.IsType<MainView>(window.Content);
        window.Show();
        try
        {
            await Task.Delay(150);
            window.CaptureRenderedFrame();
            var strip = view.FindControl<ItemsControl>("DesktopTabStrip");
            Assert.NotNull(strip);
            var first = strip!.ContainerFromIndex(0);
            var second = strip.ContainerFromIndex(1);
            Assert.NotNull(first);
            Assert.NotNull(second);
            var firstCenter = first!.TranslatePoint(
                new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), window);
            var secondCenter = second!.TranslatePoint(
                new Point(second.Bounds.Width / 2, second.Bounds.Height / 2), window);
            Assert.NotNull(firstCenter);
            Assert.NotNull(secondCenter);
            Assert.True(firstCenter!.Value.Y < 48, $"桌面标签应位于窗口顶部 48px Chrome 内，实际中心为 {firstCenter.Value}。");

            var start = firstCenter.Value;
            var end = new Point(secondCenter!.Value.X + 24, secondCenter.Value.Y);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            for (var step = 1; step <= 6; step++)
            {
                var point = new Point(
                    start.X + (end.X - start.X) * step / 6,
                    start.Y + (end.Y - start.Y) * step / 6);
                // Headless 的 MouseMove 不会自动继承 MouseDown 的按钮状态，需显式带左键修饰。
                window.MouseMove(point, RawInputModifiers.LeftMouseButton);
                await Task.Delay(20);
            }
            var dragTransform = first.RenderTransform as Avalonia.Media.TranslateTransform;
            Assert.True(dragTransform is { X: > 50 },
                $"拖拽中首个标签应有横向位移；Bounds={first.Bounds}，Transform={first.RenderTransform}，Start={start}，End={end}。");
            window.MouseUp(end, MouseButton.Left);
            await Task.Delay(150);

            Assert.Equal(originalOrder.Reverse(), shell.Browser.Tabs.Select(tab => tab.Path));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>窗口 Chrome 回归：Expanded+ 用顶部标签，桌面窄窗保留自有窗口按钮，最大化时关闭缩放热区并切换还原语义。</summary>
    [AvaloniaFact]
    public async Task MainWindowChrome_AdaptsToWindowSizeAndState()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        var view = Assert.IsType<MainView>(window.Content);
        window.Show();
        try
        {
            await Task.Delay(150);
            window.CaptureRenderedFrame();

            var chrome = view.FindControl<Border>("DesktopWindowChrome");
            var desktopTabs = view.FindControl<Control>("DesktopTabScroller");
            var workspaceTabs = view.FindControl<Border>("WorkspaceTabStripHost");
            var compactControls = view.FindControl<StackPanel>("CompactWindowControls");
            var resizeGrips = view.FindControl<Grid>("WindowResizeGrips");
            var selectionBar = view.FindControl<Border>("SelectionModeBar");
            var maximizeButton = view.FindControl<Control>("WindowMaximizeButton");

            Assert.NotNull(chrome);
            Assert.NotNull(desktopTabs);
            Assert.NotNull(workspaceTabs);
            Assert.NotNull(compactControls);
            Assert.NotNull(resizeGrips);
            Assert.NotNull(selectionBar);
            Assert.NotNull(maximizeButton);

            Assert.True(chrome!.IsVisible);
            Assert.True(desktopTabs!.IsVisible);
            Assert.False(workspaceTabs!.IsVisible);
            Assert.False(compactControls!.IsVisible);
            Assert.True(resizeGrips!.IsVisible);
            Assert.Equal("最大化窗口", AutomationProperties.GetName(maximizeButton!));

            shell.GoSettingsCommand.Execute(null);
            await Task.Delay(50);
            window.CaptureRenderedFrame();
            Assert.True(chrome.IsVisible);
            Assert.False(desktopTabs.IsVisible);

            await shell.OpenFolderAsync(PicturesDir);
            await shell.Browser.WhenIdleAsync();
            window.Width = 768;
            window.Height = 900;
            shell.SetViewportSize(768, 900);
            await Task.Delay(100);
            window.CaptureRenderedFrame();

            Assert.False(chrome.IsVisible);
            Assert.True(workspaceTabs.IsVisible);
            Assert.True(compactControls.IsVisible);
            Assert.Equal(120, selectionBar!.Margin.Right);

            window.Width = 1440;
            window.Height = 900;
            shell.SetViewportSize(1440, 900);
            window.WindowState = WindowState.Maximized;
            await Task.Delay(100);
            window.CaptureRenderedFrame();

            Assert.True(chrome.IsVisible);
            Assert.False(resizeGrips.IsVisible);
            Assert.Equal("还原窗口", AutomationProperties.GetName(maximizeButton));
        }
        finally
        {
            window.WindowState = WindowState.Normal;
            window.Close();
        }
    }

    /// <summary>弹出层验证：窄屏 FAB 的新建菜单（Flyout 呈现）。</summary>
    [AvaloniaFact]
    public async Task Compact_FabFlyout_Open()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(390, 844);
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 390, Height = 844, Content = view };
        window.Show();
        try
        {
            await Task.Delay(150);
            var fab = view.FindControl<Button>("FabCompact");
            Assert.NotNull(fab);
            Assert.NotNull(fab!.Flyout);
            fab.Flyout!.ShowAt(fab);
            await Task.Delay(150);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "10-compact-fab-menu.png"));
            fab.Flyout.Hide();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Expanded_TrashPage() => await CaptureAsync("11-expanded-trash.png", 1100, 760, shell =>
    {
        shell.GoTrashCommand.Execute(null);
        return Task.CompletedTask;
    });
    /// <summary>定点探针：图片解码管线（缩略图 + 预览大图）在无头环境真实可用。</summary>
    [AvaloniaFact]
    public async Task Thumbnails_AndPreview_Decode()
    {
        var png = Path.Combine(PicturesDir, "screenshot_ide.png");
        Assert.True(File.Exists(png), "sample png missing");
        using (var direct = new Bitmap(png))
            Assert.True(direct.PixelSize.Width > 0, "direct Bitmap decode failed");

        var shell = await CreateShellAsync();
        await shell.Browser.WhenIdleAsync();
        var withThumbs = shell.Browser.Items.Where(i => i.Thumbnail is not null).Select(i => i.Name).ToArray();
        Assert.True(withThumbs.Length > 0,
            "no thumbnails decoded; kinds=" + string.Join(",", shell.Browser.Items.Select(i => i.Name + ":" + i.Kind)));
        Assert.NotNull(shell.Preview.Image);
    }
    private static async Task OpenPagingSamplesAsync(ShellViewModel shell)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mdexplorer-paging-screenshots")).FullName;
        for (var i = 1; i <= 150; i++)
        {
            var path = Path.Combine(dir, $"report-{i:000}.txt");
            if (!File.Exists(path))
                await File.WriteAllTextAsync(path, "MdExplorer 分页浏览演示文件");
        }

        await shell.OpenFolderAsync(dir);
        await shell.Browser.WhenIdleAsync();
        shell.Browser.SelectedItems.Clear();
        Assert.Equal(150, shell.Browser.TotalItemCount);
        Assert.Equal(shell.Browser.PageSize, shell.Browser.Items.Count);
        Assert.Equal("已显示 120 / 150 项", shell.Browser.PagingStatusText);
    }

    private static async Task OpenDocumentByNameAsync(ShellViewModel shell, string name)
    {
        var item = shell.Browser.Items.First(i => i.Name == name);
        await shell.OpenDocumentWorkbenchAsync(item.Model);
    }

    private static async Task OpenOfficeByNameAsync(ShellViewModel shell, string name)
    {
        await shell.OpenFolderAsync(DocumentsDir);
        await shell.Browser.WhenIdleAsync();
        await OpenDocumentByNameAsync(shell, name);
    }

    private static async Task UseRasterPdfWorkbenchAsync(
        DocumentWorkbenchViewModel workbench, bool simulateDirectFailure = false)
    {
        Assert.True(workbench.IsDirectPdf);
        Assert.Equal(PdfViewEngine.Direct, workbench.PdfEngine);
        if (simulateDirectFailure)
        {
            // 与 OfficeDocumentViewer 的 HasLoadError 双向绑定等价：验证 VM 自动切换而非手动命令。
            workbench.OfficeViewerLoadFailed = true;
        }
        else
        {
            await workbench.UseRasterPdfCommand.ExecuteAsync(null);
        }
        for (int i = 0; i < 100 && (workbench.PdfPageCount == 0 || workbench.PdfImage is null); i++)
            await Task.Delay(50);
        Assert.Equal(PdfViewEngine.Raster, workbench.PdfEngine);
        Assert.True(workbench.ShowPdfWorkbench);
        Assert.True(workbench.CanSwitchToDirectPdf);
    }

    private static async Task SelectByNameAsync(ShellViewModel shell, string name, Func<bool> ready)
    {
        var item = shell.Browser.Items.First(i => i.Name == name);
        shell.Browser.SelectedItems.Clear();
        shell.Browser.SelectedItems.Add(item);
        for (int i = 0; i < 80 && !ready(); i++)
            await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task Large_MarkdownPreview() => await CaptureAsync("12-large-markdown.png", 1440, 900,
        shell => SelectByNameAsync(shell, "README.md", () => shell.Preview.MarkdownText is not null));

    [AvaloniaFact]
    public async Task Large_CodePreview() => await CaptureAsync("13-large-code.png", 1440, 900,
        shell => SelectByNameAsync(shell, "Program.cs", () => shell.Preview.ShowSource));

    // 播放后端是平台能力：截图时换成假后端，才能把“播放中”的控件与进度条拍进来。
    [AvaloniaFact]
    public async Task Large_AudioPlayback() => await WithStubMediaAsync(async () =>
        await CaptureAsync("71-large-audio-playback.png", 1440, 900, async shell =>
        {
            await SelectByNameAsync(shell, "录音 001.m4a", () => shell.Preview.CanPlayMediaInApp);
            Assert.True(shell.Preview.IsAudioMedia);
            Assert.False(shell.Preview.IsVideoMedia);
            await shell.Preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
            Assert.True(shell.Preview.IsMediaPlaying);
            Assert.Equal("正在播放", shell.Preview.MediaStateText);
            Assert.Equal("00:42", shell.Preview.MediaPositionText);
            Assert.Equal("03:12", shell.Preview.MediaDurationText);
        }));

    [AvaloniaFact]
    public async Task Compact_AudioPlayback() => await WithStubMediaAsync(async () =>
        await CaptureAsync("72-compact-audio-playback.png", 390, 844, async shell =>
        {
            shell.IsPreviewPageOpen = true;
            await SelectByNameAsync(shell, "录音 001.m4a", () => shell.Preview.CanPlayMediaInApp);
            await shell.Preview.ToggleMediaPlaybackCommand.ExecuteAsync(null);
            Assert.True(shell.Preview.IsMediaPlaying);
        }));

    // 推帧路线的界面证据：后端把帧推给宿主，画面由 Avalonia 自己画（Linux Wayland 无窗口 id 时只能这样）。
    [AvaloniaFact]
    public async Task Large_VideoFramePush() => await WithStubMediaAsync(
        async () => await CaptureAsync("76-large-video-frame-push.png", 1440, 900, async shell =>
        {
            // v0.36 起视频默认不在应用内出画面：先看到「两个显式入口」，用户选内嵌后才出画面。
            await SelectByNameAsync(shell, "演示视频.mp4", () => shell.Preview.IsVideoMedia);
            Assert.True(shell.Preview.IsVideoMedia);
            Assert.True(shell.Preview.ShowInlineVideoGate,
                $"inline={shell.Preview.PlayVideoInline} state={shell.Preview.MediaStateText}");
            Assert.False(shell.Preview.ShowFrameVideoSurface);
            shell.Preview.EnableInlineVideoCommand.Execute(null);
            Assert.False(shell.Preview.ShowInlineVideoGate);
            Assert.True(shell.Preview.CanPlayMediaInApp);
            Assert.True(shell.Preview.ShowFrameVideoSurface);
            Assert.False(shell.Preview.ShowNativeVideoSurface);
            Assert.Equal("画面：宿主帧呈现", shell.Preview.MediaRendererText);
        }, afterShow: async window =>
        {
            await Task.Delay(120); // 让内嵌画面挂上帧接收器并完成一次呈现
            var surface = window.GetVisualDescendants().OfType<MediaFrameSurface>().FirstOrDefault();
            Assert.NotNull(surface);
            Assert.True(surface!.HasFrame);
            Assert.Equal(new PixelSize(640, 360), surface.FrameSize);

            // v0.34 反馈：Large 下后端名与“打开”按钮右侧被裁掉。这两条断言直接量右边缘，
            // 横向 StackPanel 把长文本撑出父级时（无限宽度）会立刻失败。
            var backend = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text is { } text && text.Contains("FramePushVideoStub", StringComparison.Ordinal));
            var card = backend.FindAncestorOfType<Border>();
            Assert.NotNull(card);
            var backendRight = backend.TranslatePoint(new Point(backend.Bounds.Width, 0), card!);
            Assert.NotNull(backendRight);
            Assert.True(backendRight!.Value.X <= card!.Bounds.Width + 0.5,
                $"后端名右边缘 {backendRight.Value.X:0.#} 超出媒体卡片 {card.Bounds.Width:0.#}");

            var openLabel = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == "打开" && t.IsEffectivelyVisible);
            var openButton = openLabel.FindAncestorOfType<Button>();
            Assert.NotNull(openButton);
            var previewView = openButton!.FindAncestorOfType<PreviewView>();
            Assert.NotNull(previewView);
            var buttonRight = openButton.TranslatePoint(new Point(openButton.Bounds.Width, 0), previewView!);
            Assert.NotNull(buttonRight);
            Assert.True(buttonRight!.Value.X <= previewView!.Bounds.Width + 0.5,
                $"“打开”按钮右边缘 {buttonRight.Value.X:0.#} 超出预览视图 {previewView.Bounds.Width:0.#}");
        }),
        static () => new FramePushVideoStub());

    // v0.36：视频默认外挂——卡片上是两个显式入口（系统播放器 / 内嵌面板），不是一个假播放按钮。
    [AvaloniaFact]
    public async Task Large_VideoInlineGate() => await WithStubMediaAsync(async () =>
        await CaptureAsync("79-large-video-inline-gate.png", 1440, 900, async shell =>
        {
            await SelectByNameAsync(shell, "演示视频.mp4", () => shell.Preview.IsVideoMedia);
            Assert.True(
                shell.Preview.ShowInlineVideoGate,
                $"video={shell.Preview.IsVideoMedia} inline={shell.Preview.PlayVideoInline} " +
                $"state={shell.Preview.MediaStateText} backend={shell.Preview.MediaBackendName}");
            Assert.False(shell.Preview.CanPlayMediaInApp);
            Assert.False(shell.Preview.ShowFrameVideoSurface);
            Assert.False(shell.Preview.HasMediaProbe); // 闸门在时不重复报“探测不通过”
            Assert.Equal("默认用系统播放器打开", shell.Preview.MediaStateText);
        }),
        static () => new FramePushVideoStub());

    // 探测说“系统接不了”时的界面：没有播放按钮，只有事实与原因。
    [AvaloniaFact]
    public async Task Large_MediaProbeRejected() => await WithStubMediaAsync(async () =>
        await CaptureAsync("74-large-media-probe-rejected.png", 1440, 900, async shell =>
        {
            await SelectByNameAsync(shell, "演示视频.mp4", () => shell.Preview.HasMediaProbe);
            Assert.True(shell.Preview.IsVideoMedia);
            Assert.False(shell.Preview.CanPlayMediaInApp);
            Assert.False(shell.Preview.ShowNativeVideoSurface);
            Assert.Contains("视频", shell.Preview.MediaProbeText, StringComparison.Ordinal);
            Assert.Contains("画面宿主", shell.Preview.MediaProbeReasonText, StringComparison.Ordinal);
        }));

    // ---------- v0.47 折叠屏 / 手机横屏 / Android 小窗 ----------

    /// <summary>
    /// 手机横屏（844×390）：宽度落在 Expanded，但高度只有 390。常驻抽屉 + 底部导航都会把内容挤没，
    /// 所以矮视口统一收敛到「导轨 + 收起抽屉 + 预览改覆盖页」。
    /// </summary>
    [AvaloniaFact]
    public async Task ShortLandscape_UsesRailInsteadOfBottomNav() => await CaptureAsync(
        "83-short-landscape-rail.png", 844, 390, shell =>
        {
            Assert.True(shell.IsShortViewport, $"height={shell.SizeClass}");
            Assert.False(shell.ShowBottomNav);
            Assert.False(shell.ShowDrawerPane);
            Assert.True(shell.ShowRail);
            return Task.CompletedTask;
        },
        afterShow: async window =>
        {
            await Task.Delay(120);
            var bottomNav = window.GetVisualDescendants().OfType<MdNavigationBar>().FirstOrDefault();
            Assert.True(bottomNav is null || !bottomNav.IsEffectivelyVisible, "矮视口不该出现底部导航");
        });

    /// <summary>Android 分屏小窗（400×520）：比手机还矮，仍然必须恰好有一个导航面，且不是底栏。</summary>
    [AvaloniaFact]
    public async Task ShortSplitWindow_KeepsExactlyOneNavigationSurface() => await CaptureAsync(
        "84-short-split-window-rail.png", 400, 520, shell =>
        {
            Assert.True(shell.IsShortViewport);
            Assert.False(shell.ShowBottomNav);
            Assert.True(shell.ShowRail);
            Assert.False(shell.ShowDrawerPane);
            // 矮视口收起「前进 / 刷新」，把宽度让给路径胶囊。
            Assert.False(shell.ShowPathRowExtras);
            return Task.CompletedTask;
        },
        afterShow: async window =>
        {
            await Task.Delay(120);
            var pathPill = window.GetVisualDescendants().OfType<Border>()
                .First(border => border.Name == "PathPill");
            // 400 宽减去 84 导轨、后退/上一级与搜索/溢出：路径胶囊仍要能被读到，不能被压成 0。
            Assert.True(pathPill.Bounds.Width >= 80,
                $"路径胶囊只剩 {pathPill.Bounds.Width:0.#} DIP");
        });

    /// <summary>
    /// 折叠屏展开/合起（673×841 → 1200×841 → 673×841）：不重启就切换导航面，
    /// 且任何时候「底栏 / 抽屉 / 导轨」恰好一个可见。
    /// </summary>
    [AvaloniaFact]
    public async Task Foldable_UnfoldResizeSwitchesNavigationSurface() => await CaptureAsync(
        "85-foldable-unfold-resize.png", 673, 841, shell =>
        {
            // 合起：Medium，导轨；展开到 1200 后应变成常驻抽屉。
            Assert.False(shell.IsShortViewport);
            Assert.True(shell.ShowRail);
            shell.SetViewportSize(1200, 841);
            Assert.Equal(SizeClass.Large, shell.SizeClass);
            Assert.True(shell.ShowDrawerPane);
            Assert.False(shell.ShowRail);
            Assert.False(shell.ShowBottomNav);
            // 再折回：抽屉收起，导轨回来，内容不被留在 0 宽。
            shell.SetViewportSize(673, 841);
            Assert.True(shell.ShowRail);
            Assert.False(shell.ShowDrawerPane);
            return Task.CompletedTask;
        },
        afterShow: async window =>
        {
            await Task.Delay(120);
            var visibleNavs = window.GetVisualDescendants().OfType<MdNavigationBar>()
                .Count(nav => nav.IsEffectivelyVisible);
            Assert.InRange(visibleNavs, 0, 1);
        });

    /// <summary>
    /// 折叠屏半折叠·书本式（竖铰链，1200×841）：主区只占左半，右半留给预览，
    /// 中间那条铰链带谁也不许跨过去。
    /// </summary>
    [AvaloniaFact]
    public async Task Foldable_HalfFoldedBookReservesTheHingeBand() => await CaptureAsync(
        "86-foldable-half-folded-book.png", 1200, 841, shell =>
        {
            Assert.True(shell.IsHalfFolded);
            Assert.True(shell.IsFoldSecondaryBook);
            Assert.False(shell.IsFoldSecondaryTabletop);
            Assert.Equal(616, shell.FoldPrimaryMargin.Right, 3);
            Assert.Equal(0, shell.FoldPrimaryMargin.Bottom);
            Assert.Equal(584, shell.FoldSecondaryWidth, 3);
            // 书本式竖向本来就够高，不该被误判成矮视口；主区 584 宽按 Medium 排（导轨），不出抽屉。
            Assert.False(shell.IsShortViewport);
            Assert.True(shell.IsMedium);
            Assert.True(shell.ShowRail);
            Assert.False(shell.ShowDrawerPane);
            return Task.CompletedTask;
        },
        foldablePosture: new StubFoldablePostureSource(new FoldablePosture(FoldHingeAxis.Vertical, 32)),
        afterShow: async window =>
        {
            await Task.Delay(120);
            // 副半屏在右、主区让开：两者不许重叠，否则就是内容压在铰链上。
            var main = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PaneGrid");
            var secondary = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "FoldSecondaryBookPane");
            Assert.True(secondary.Bounds.Width > 0, "副半屏应当可见");
            Assert.True(main.Bounds.Right <= secondary.Bounds.Left + 0.5,
                $"主区右边缘 {main.Bounds.Right} 压进了副半屏 {secondary.Bounds.Left}");
        });

    /// <summary>
    /// 折叠屏半折叠·桌面式（横铰链，1200×841）：上下两半，下半留给预览；
    /// 每半只有 404.5 高，必须按矮视口处理（抽屉收起、底栏不出现）。
    /// </summary>
    [AvaloniaFact]
    public async Task Foldable_HalfFoldedTabletopShortensTheViewport() => await CaptureAsync(
        "87-foldable-half-folded-tabletop.png", 1200, 841, shell =>
        {
            Assert.True(shell.IsHalfFolded);
            Assert.True(shell.IsFoldSecondaryTabletop);
            Assert.True(shell.IsShortViewport);
            Assert.False(shell.ShowDrawerPane);
            Assert.False(shell.ShowBottomNav);
            Assert.True(shell.ShowRail);
            Assert.Equal(436.5, shell.FoldPrimaryMargin.Bottom, 3);
            return Task.CompletedTask;
        },
        foldablePosture: new StubFoldablePostureSource(new FoldablePosture(FoldHingeAxis.Horizontal, 32)),
        afterShow: async window =>
        {
            await Task.Delay(120);
            var main = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PaneGrid");
            var secondary = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "FoldSecondaryTabletopPane");
            Assert.True(secondary.Bounds.Height > 0, "副半屏应当可见");
            Assert.True(main.Bounds.Bottom <= secondary.Bounds.Top + 0.5,
                $"主区底边 {main.Bounds.Bottom} 压进了副半屏 {secondary.Bounds.Top}");
        });

    /// <summary>截图用的姿态来源：固定报一个姿态（真机上是 Android 铰链角度传感器）。</summary>
    private sealed class StubFoldablePostureSource(FoldablePosture posture) : IFoldablePostureSource
    {
        public FoldablePosture Current { get; } = posture;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private static Task WithStubMediaAsync(Func<Task> body) =>
        WithStubMediaAsync(body, static () => new StubMediaPlayback());

    private static async Task WithStubMediaAsync(Func<Task> body, Func<IMediaPlaybackService> factory)
    {
        AppHost.MediaPlaybackFactory = factory;
        try
        {
            await body();
        }
        finally
        {
            AppHost.MediaPlaybackFactory = null;
        }
    }

    [AvaloniaFact]
    public async Task Expanded_BatchRenameDialog() => await CaptureAsync("14-expanded-batch-rename.png", 1100, 760, async shell =>
    {
        var names = shell.Browser.Items.Where(i => !i.Model.IsFolder).Take(4).Select(i => i.Name).ToArray();
        Assert.True(names.Length >= 2);
        _ = shell.BatchRenameAsync(names, []); // 不 await：保持对话框打开供截图
        await Task.Delay(150);
    });

    [AvaloniaFact]
    public async Task Compact_FloatingPreview() => await CaptureAsync("22-compact-floating-preview.png", 390, 844, shell =>
    {
        shell.IsPreviewPageOpen = true;
        Assert.True(shell.IsPreviewPageOpen);
        Assert.Equal(0, shell.Preview.SelectedTabIndex);
        return Task.CompletedTask;
    });

    /// <summary>
    /// 回归 v0.32 的“浮窗在、内容不展开”：收成标题条后点另一张图，浮窗必须重新展开并换上新的图；
    /// 预览被清空时点开文件，浮窗也不能是空白。
    /// </summary>
    [AvaloniaFact]
    public async Task Compact_FloatingPreviewExpandsWhenAnotherImageIsTapped() => await CaptureAsync(
        "75-compact-floating-preview-expand.png", 390, 844,
        async shell =>
        {
            var photos = shell.Browser.Items.Where(i => i.Kind == FileKind.Image).ToArray();
            Assert.True(photos.Length >= 2);
            shell.Browser.SelectedItems.Clear();
            shell.Browser.SelectedItems.Add(photos[0]);
            shell.IsPreviewPageOpen = true;
            for (var i = 0; i < 60 && shell.Preview.Image is null; i++)
                await Task.Delay(50);
            Assert.True(shell.Preview.HasImage);
        },
        afterShow: async window =>
        {
            var floating = window.GetVisualDescendants().OfType<MdFloatingWindow>().Single();
            Assert.True(floating.IsOpen);
            Assert.True(floating.IsEffectivelyVisible);
            Assert.False(floating.IsMinimized);

            // 用户把浮窗收成标题条。
            floating.IsMinimized = true;
            window.CaptureRenderedFrame();
            Assert.True(floating.IsMinimized);

            // 再点开另一张图：属性没变（IsPreviewPageOpen 一直是 true），内容必须自己重新展开。
            var shell = (ShellViewModel)window.GetVisualDescendants().OfType<MainView>().First().DataContext!;
            var next = shell.Browser.Items.Last(i => i.Kind == FileKind.Image);
            await shell.Preview.LoadAsync(null);
            shell.Browser.SelectedItems.Clear();
            shell.Browser.SelectedItems.Add(next);
            shell.OnFileOpened(next);
            for (var i = 0; i < 80 && (shell.Preview.Image is null || !shell.Preview.HasItem); i++)
            {
                window.CaptureRenderedFrame();
                await Task.Delay(25);
            }
            // 展开高度是动画；无头渲染只在 CaptureRenderedFrame 时推进时间轴，所以要主动推帧。
            for (var i = 0; i < 40; i++)
            {
                window.CaptureRenderedFrame();
                await Task.Delay(20);
            }

            Assert.False(floating.IsMinimized);
            Assert.True(floating.IsOpen);
            // 属性回到 false 不算修好：窗口必须真的回到展开高度，内容也必须在。
            Assert.True(floating.Bounds.Height > 400,
                $"浮窗展开后仍只有 {floating.Bounds.Height:0} 高");
            Assert.True(shell.Preview.HasItem);
            Assert.NotNull(shell.Preview.Image);
            Assert.Equal(next.Name, shell.Preview.Title);
        });

    [AvaloniaFact]
    public async Task Large_SearchIslandPanel() => await CaptureAsync("23-large-search-island.png", 1440, 900, async shell =>
    {
        shell.Browser.FilterText = "IMG_";
        shell.Browser.OpenSearchPanelCommand.Execute(null);
        await shell.Browser.WhenIdleAsync();
        Assert.True(shell.Browser.IsSearchPanelOpen);
        Assert.Equal("IMG_", shell.Browser.SearchIslandText);
    });

    [AvaloniaFact]
    public async Task Large_SearchCenter() => await CaptureAsync("25-large-search-center.png", 1440, 900, async shell =>
    {
        await shell.GoSearchCenterCommand.ExecuteAsync(null);
        Assert.True(await shell.AddSearchIndexRootAsync(SampleRoot));
        await shell.RebuildSearchIndexCommand.ExecuteAsync(null);
        shell.SearchCenterQuery = "IMG_";
        shell.SearchCenterKindFilter = "Image";
        await shell.RunSearchCenterSearchCommand.ExecuteAsync(null);
        shell.DismissSnackbarCommand.Execute(null);
        Assert.True(shell.SearchIndexIsCurrent);
        Assert.NotEmpty(shell.SearchCenterResults);
    });

    [AvaloniaFact]
    public async Task Compact_SearchCenter() => await CaptureAsync("26-compact-search-center.png", 390, 844, async shell =>
    {
        await shell.GoSearchCenterCommand.ExecuteAsync(null);
        Assert.True(await shell.AddSearchIndexRootAsync(SampleRoot));
        await shell.RebuildSearchIndexCommand.ExecuteAsync(null);
        shell.SearchCenterQuery = "IMG_";
        shell.SearchCenterKindFilter = "Image";
        await shell.RunSearchCenterSearchCommand.ExecuteAsync(null);
        shell.DismissSnackbarCommand.Execute(null);
        Assert.True(shell.SearchIndexIsCurrent);
        Assert.NotEmpty(shell.SearchCenterResults);
    });

    [AvaloniaFact]
    public async Task Large_StorageInsights() => await CaptureAsync("27-large-storage-insights.png", 1440, 900, async shell =>
    {
        await shell.GoStorageInsightsCommand.ExecuteAsync(null);
        Assert.True(shell.TrySetStorageRoot(SampleRoot));
        await shell.AnalyzeStorageCommand.ExecuteAsync(null);
        await shell.FindStorageDuplicatesCommand.ExecuteAsync(null);
        shell.DismissSnackbarCommand.Execute(null);
        Assert.True(shell.HasStorageAnalysis);
        Assert.True(shell.HasStorageDuplicateScan);
        Assert.NotEmpty(shell.StorageTopFolders);
        Assert.NotEmpty(shell.StorageLargestFiles);
    });

    [AvaloniaFact]
    public async Task Compact_StorageInsights() => await CaptureAsync("28-compact-storage-insights.png", 390, 844, async shell =>
    {
        await shell.GoStorageInsightsCommand.ExecuteAsync(null);
        Assert.True(shell.TrySetStorageRoot(SampleRoot));
        await shell.AnalyzeStorageCommand.ExecuteAsync(null);
        shell.DismissSnackbarCommand.Execute(null);
        Assert.True(shell.HasStorageAnalysis);
        Assert.NotEmpty(shell.StorageKindUsages);
        Assert.NotEmpty(shell.StorageLargestFiles);
    });

    [AvaloniaFact]
    public async Task Large_ArchiveWorkbench() => await CaptureAsync(
        "29-large-archive-workbench.png", 1440, 900, ArrangeArchiveWorkbenchAsync);

    [AvaloniaFact]
    public async Task Compact_ArchiveWorkbench() => await CaptureAsync(
        "30-compact-archive-workbench.png", 390, 844, ArrangeArchiveWorkbenchAsync);

    private static async Task ArrangeArchiveWorkbenchAsync(ShellViewModel shell)
    {
        var archivePath = Path.Combine(PicturesDir, "backup-2026.zip");
        await shell.OpenArchiveWorkbenchAsync(archivePath);
        var docs = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "docs");
        shell.NavigateArchiveFolder(docs.VirtualPath);

        var readme = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "readme.txt");
        await shell.SelectArchiveEntryAsync(readme);
        var config = Assert.Single(shell.ArchiveVisibleEntries, row => row.Name == "config.json");
        config.IsChecked = true;
        shell.DismissSnackbarCommand.Execute(null);

        Assert.True(shell.HasArchive);
        Assert.Equal("docs/", shell.ArchiveCurrentFolder);
        Assert.Contains("ZIP 工作台样例", shell.ArchivePreviewText, StringComparison.Ordinal);
        Assert.Equal(1, shell.ArchiveSelectedCount);
    }

    [AvaloniaFact]
    public async Task Compact_AndroidAccessManagement() => await CaptureAsync(
        "31-compact-android-access.png", 390, 844, ArrangeAndroidAccessAsync, androidAccess: true);

    [AvaloniaFact]
    public async Task Compact_AndroidAppTrash() => await CaptureAsync(
        "32-compact-android-app-trash.png", 390, 844, ArrangeAndroidAppTrashAsync, androidAccess: true);

    [AvaloniaFact]
    public async Task Large_TransferConflictDialog() => await CaptureAsync(
        "33-large-conflict-dialog.png", 1440, 900, ArrangeTransferConflictAsync);

    [AvaloniaFact]
    public async Task Compact_TransferConflictDialog() => await CaptureAsync(
        "34-compact-conflict-dialog.png", 390, 844, ArrangeTransferConflictAsync);

    [AvaloniaFact]
    public async Task Large_TaskDetails() => await CaptureAsync(
        "35-large-task-details.png", 1440, 900, ArrangeTaskDetailsAsync);

    [AvaloniaFact]
    public async Task Compact_TaskDetailsSheet() => await CaptureAsync(
        "36-compact-task-sheet.png", 390, 844, ArrangeTaskDetailsAsync);

    [AvaloniaFact]
    public async Task ExtraLarge_DualPane() => await CaptureAsync(
        "37-xlarge-dual-pane.png", 1920, 1080, ArrangeDualPaneAsync);

    private static async Task ArrangeDualPaneAsync(ShellViewModel shell)
    {
        var cameraDir = Path.Combine(PicturesDir, "Camera");
        shell.SettingsStore.Settings.DualPanePath = cameraDir;
        await shell.ToggleDualPaneCommand.ExecuteAsync(null);
        await shell.SecondaryBrowser.WhenIdleAsync();

        Assert.True(shell.ShowDualPane);
        Assert.Equal(cameraDir, shell.SecondaryBrowser.CurrentPath);
        Assert.Same(shell.SecondaryBrowser, shell.ActiveBrowser);

        var photo = shell.SecondaryBrowser.Items.First(
            item => item.Name.StartsWith("IMG_", StringComparison.Ordinal));
        shell.SecondaryBrowser.SelectedItems.Clear();
        shell.SecondaryBrowser.SelectedItems.Add(photo);
        for (var i = 0; i < 60 && shell.Preview.Image is null; i++)
            await Task.Delay(50);
        Assert.Equal(photo.FullPath, shell.Preview.CurrentItem?.FullPath);
    }

    private static Task ArrangeTransferConflictAsync(ShellViewModel shell)
    {
        _ = shell.AskConflictAsync(CreateConflictPrompt());
        Assert.True(shell.IsConflictDialog);
        Assert.NotNull(shell.CurrentConflict);
        Assert.True(shell.IsConflictKeepBoth);
        Assert.False(shell.ConflictApplyToRemaining);
        Assert.Equal(3, shell.PendingConflictCount);
        return Task.CompletedTask;
    }

    private static TransferConflictPrompt CreateConflictPrompt()
    {
        var sourcePath = Path.Combine(PicturesDir, "IMG_20261001.jpg");
        var targetPath = Path.Combine(PicturesDir, "Camera", "IMG_0001.png");
        FileItem MakeItem(string path) => new(
            FileLocator.FromPath(path),
            "IMG_20261001.jpg",
            FileKind.Image,
            new FileInfo(path).Length,
            new FileInfo(path).LastWriteTime,
            IsHidden: false,
            IsSymlink: false,
            ".jpg");
        string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        var source = new ConflictItemSnapshot(
            "来源",
            MakeItem(sourcePath),
            PicturesDir,
            Hash(sourcePath),
            new Bitmap(sourcePath));
        var target = new ConflictItemSnapshot(
            "目标已有",
            MakeItem(targetPath),
            Path.Combine(PicturesDir, "Camera"),
            Hash(targetPath),
            new Bitmap(targetPath));
        return new TransferConflictPrompt(source, target, Index: 0, TotalCount: 3, IsMove: false);
    }

    private static async Task ArrangeTaskDetailsAsync(ShellViewModel shell)
    {
        await shell.RunOperationAsync("正在解压 backup-2026.zip…", (_, _) =>
            Task.FromResult(new OperationOutcome(
                12,
                [],
                [Path.Combine(PicturesDir, "README.md")],
                SkippedCount: 1)));
        await shell.RunOperationAsync(
            "正在复制 4 个项目…",
            (_, _) => Task.FromResult(new OperationOutcome(
                2,
                [new OperationError(
                    "旅行视频.mp4",
                    "目标文件夹暂时不可写",
                    Path.Combine(PicturesDir, "旅行视频.mp4"))],
                [
                    Path.Combine(PicturesDir, "IMG_20261001.jpg"),
                    Path.Combine(PicturesDir, "screenshot_ide.png"),
                ],
                SkippedCount: 1)),
            _ => Task.CompletedTask);
        shell.IsTaskCenterOpen = true;
        shell.DismissSnackbarCommand.Execute(null);

        var failed = Assert.Single(shell.OperationTasks, task => task.IsFailed);
        Assert.True(failed.IsDetailsExpanded);
        Assert.True(failed.CanRetry);
        Assert.Equal(1, shell.FailedOperationTaskCount);
    }

    private static Task ArrangeAndroidAccessAsync(ShellViewModel shell)
    {
        shell.AuthorizedRoots.Clear();
        shell.AuthorizedRoots.Add(new AuthorizedRootModel(
            "Download",
            "content://com.android.externalstorage.documents/tree/primary%3ADownload",
            "bookmark-download",
            IsAvailable: true,
            IsPersistent: true));
        shell.AuthorizedRoots.Add(new AuthorizedRootModel(
            "相机备份",
            "content://com.android.externalstorage.documents/tree/primary%3ADCIM",
            Bookmark: null,
            IsAvailable: true,
            IsPersistent: false));
        shell.AuthorizedRoots.Add(new AuthorizedRootModel(
            "旧 SD 卡",
            string.Empty,
            "bookmark-stale",
            IsAvailable: false,
            IsPersistent: true));
        SeedAndroidTrashState(shell);
        shell.Page = ShellPage.Settings;

        Assert.True(shell.Capabilities.IsAndroid);
        Assert.Equal(2, shell.AvailableAuthorizedRootCount);
        Assert.Equal(1, shell.UnavailableAuthorizedRootCount);
        Assert.True(shell.HasTrashQuota);
        return Task.CompletedTask;
    }

    private static Task ArrangeAndroidAppTrashAsync(ShellViewModel shell)
    {
        SeedAndroidTrashState(shell);
        shell.Page = ShellPage.Trash;

        Assert.True(shell.Capabilities.UsesAppTrash);
        Assert.Equal("应用内回收站", shell.TrashTitle);
        Assert.Equal(4, shell.TrashItems.Count);
        Assert.True(shell.HasTrashQuota);
        return Task.CompletedTask;
    }

    private static void SeedAndroidTrashState(ShellViewModel shell)
    {
        shell.TrashItems.Clear();
        shell.TrashItems.Add(new TrashItemModel(
            "sample-video",
            "旅行视频.mp4",
            "content://com.android.externalstorage.documents/tree/primary%3ADownload/document/primary%3ADownload%2F旅行视频.mp4",
            "10月28日 09:42",
            IsFolder: false,
            SizeBytes: 512L * 1024 * 1024));
        shell.TrashItems.Add(new TrashItemModel(
            "sample-folder",
            "相机备份",
            "content://com.android.externalstorage.documents/tree/primary%3ADCIM/document/primary%3ADCIM%2F相机备份",
            "10月27日 21:18",
            IsFolder: true,
            SizeBytes: 210L * 1024 * 1024));
        shell.TrashItems.Add(new TrashItemModel(
            "sample-archive",
            "设计稿 v2.zip",
            "content://com.android.externalstorage.documents/tree/primary%3ADownload/document/primary%3ADownload%2F设计稿%20v2.zip",
            "10月26日 16:05",
            IsFolder: false,
            SizeBytes: 18L * 1024 * 1024));
        shell.TrashItems.Add(new TrashItemModel(
            "sample-note",
            "会议记录.md",
            "content://com.android.externalstorage.documents/tree/primary%3ADownload/document/primary%3ADownload%2F会议记录.md",
            "10月25日 11:30",
            IsFolder: false,
            SizeBytes: 24L * 1024));
        shell.TrashUsedBytes = shell.TrashItems.Sum(item => item.SizeBytes ?? 0);
        shell.TrashQuotaBytes = AppTrashService.DefaultQuotaBytes;
        shell.TrashRetentionDays = AppTrashService.DefaultRetentionDays;
        shell.TrashUsedPercent = shell.TrashUsedBytes * 100.0 / AppTrashService.DefaultQuotaBytes;
    }

    /// <summary>桌面溢出菜单：三列分组 + 限高滚动的呈现冒烟。</summary>
    [AvaloniaFact]
    public async Task Large_OverflowMenu_Open()
    {
        var shell = await CreateShellAsync();
        shell.SetViewportSize(1440, 900);
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        window.Show();
        try
        {
            await Task.Delay(150);
            var button = view.FindControl<Button>("OverflowButton");
            Assert.NotNull(button);
            Assert.NotNull(button!.Flyout);
            button.Flyout!.ShowAt(button);
            await Task.Delay(150);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(OutputDir, "24-large-overflow-menu.png"));
            button.Flyout.Hide();
        }
        finally
        {
            window.Close();
        }
    }
}
