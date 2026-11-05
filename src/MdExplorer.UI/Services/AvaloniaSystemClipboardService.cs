using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

/// <summary>
/// Avalonia 系统剪贴板（文件格式）实现。TopLevel 在视图挂载后由 Shell 注入；
/// 剪贴板在不同平台/会话下可能不可用，失败时安静退回应用内剪贴板。
/// </summary>
public sealed class AvaloniaSystemClipboardService : ISystemClipboardService
{
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    public async Task SetFilesAsync(IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        if (TopLevelProvider?.Invoke() is not { Clipboard: { } clipboard } topLevel)
            return;

        try
        {
            var items = new List<IStorageItem>();
            foreach (var path in localPaths.Where(p => !FileLocator.IsStorageUri(p)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IStorageItem? item = Directory.Exists(path)
                    ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(path).ConfigureAwait(true)
                    : await topLevel.StorageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true);
                if (item is not null)
                    items.Add(item);
            }

            if (items.Count > 0)
                await clipboard.SetFilesAsync(items).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // X11/Wayland/远程会话等环境可能拒绝或缺少文件格式；内部剪贴板仍已生效。
        }
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (TopLevelProvider?.Invoke() is not { Clipboard: { } clipboard })
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 与文件格式相同：部分桌面会话可能拒绝剪贴板写入，调用方只给短提示，不升级为错误弹窗。
        }
    }

    public async Task<IReadOnlyList<string>> TryGetFilesAsync(CancellationToken cancellationToken)
    {
        if (TopLevelProvider?.Invoke() is not { Clipboard: { } clipboard })
            return [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await clipboard.TryGetFilesAsync().ConfigureAwait(true);
            if (files is null)
                return [];

            return files
                .Select(item => item.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }
}
