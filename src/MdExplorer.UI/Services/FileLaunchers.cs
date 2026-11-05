using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>桌面：经 Shell 打开（Windows UseShellExecute、Linux xdg-open）。</summary>
public sealed class ProcessFileLauncher : IFileLauncherService
{
    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            }
            else
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> RevealAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add($"/select,{path}");
                Process.Start(start);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var start = new ProcessStartInfo("open") { UseShellExecute = false };
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(path);
                Process.Start(start);
            }
            else if (OperatingSystem.IsLinux())
            {
                // xdg-open 没有跨桌面统一的“选中文件”语义，能稳定承诺的是打开所在文件夹。
                var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (target is null)
                    return Task.FromResult(false);
                Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false });
            }
            else
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Avalonia Launcher 实现（Android 走系统 Intent；桌面亦可）。
/// 需要活的 TopLevel，由 MainView 装配时注入并替换默认启动器。
/// </summary>
public sealed class AvaloniaFileLauncher(Func<TopLevel?> topLevelProvider) : IFileLauncherService
{
    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var topLevel = topLevelProvider();
        if (topLevel is null) return false;
        try
        {
            var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true);
            if (file is null) return false;
            return await topLevel.Launcher.LaunchFileAsync(file).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<bool> RevealAsync(string path, CancellationToken cancellationToken) =>
        // Android 没有可移植的“在系统文件管理器中选中此文件”Intent；Eidoria 内定位由 Browser 承担。
        Task.FromResult(false);
}
