using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MdExplorer.UI.Services;
using MdExplorer.UI.Views;

namespace MdExplorer.UI;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = AppHost.Build();
        var shell = AppHost.CreateShell(services);

        // 主题：先按已存设置落地一次；系统明暗变化时重算 HCT 深色板
        ThemeService.Apply(services.SettingsStore.Settings);
        shell.ThemeSettingsChanged += (_, _) => ThemeService.Apply(services.SettingsStore.Settings);
        ActualThemeVariantChanged += (_, _) =>
        {
            if (string.Equals(services.SettingsStore.Settings.ThemeMode, "System", StringComparison.Ordinal))
                ThemeService.Apply(services.SettingsStore.Settings);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = shell };
        }

        _ = shell.InitializeAsync();
        base.OnFrameworkInitializationCompleted();
    }
}
