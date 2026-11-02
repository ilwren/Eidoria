using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;

namespace MdExplorer.LibMpvAvaloniaSpike;

public sealed partial class MainWindow : Window
{
    private MpvContext? _mpv;
    private string? _pendingFile;

    public MainWindow()
    {
        var nativeRoot = Environment.GetEnvironmentVariable("MDEXPLORER_LIBMPV_ROOT");
        if (!string.IsNullOrWhiteSpace(nativeRoot) && Directory.Exists(nativeRoot))
            MpvApi.RootPath = Path.GetFullPath(nativeRoot);

        InitializeComponent();
        DataContext = this;

        OpenButton.Click += OpenClicked;
        PlayPauseButton.Click += PlayPauseClicked;
        StopButton.Click += StopClicked;
        BackButton.Click += (_, _) => Seek(-10);
        ForwardButton.Click += (_, _) => Seek(10);
    }

    /// <summary>Bound OneWayToSource from MpvView; the Avalonia renderer owns this native context.</summary>
    public MpvContext? Mpv
    {
        get => _mpv;
        set
        {
            _mpv = value;
            if (value is null)
                return;

            StatusText.Text = "libmpv 已初始化；实际解码能力由所安装的 LGPL-only 原生构建决定。";
            if (_pendingFile is { } path)
            {
                _pendingFile = null;
                _ = LoadAsync(path);
            }
        }
    }

    private async void OpenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择媒体文件",
        });
        if (files.Count == 0)
            return;

        var path = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "此 Spike 只处理本地文件路径；该存储提供程序没有返回本地路径。";
            return;
        }

        if (Mpv is null)
        {
            _pendingFile = path;
            StatusText.Text = "媒体文件已选，等待 libmpv 渲染视图初始化…";
            return;
        }
        await LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            StatusText.Text = $"正在打开：{Path.GetFileName(path)}";
            await Mpv!.LoadFile(path).InvokeAsync();
            StatusText.Text = $"已发送给 libmpv：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"libmpv 打开失败：{ex.Message}";
        }
    }

    private void PlayPauseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Mpv is null)
            return;
        var paused = Mpv.Pause.Get() ?? false;
        Mpv.Pause.Set(!paused);
    }

    private void StopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Mpv is null)
            return;
        _ = Mpv.Stop().InvokeAsync();
    }

    private void Seek(double seconds)
    {
        if (Mpv is null)
            return;
        _ = Mpv.Seek(seconds, SeekOption.Relative).InvokeAsync();
    }
}
