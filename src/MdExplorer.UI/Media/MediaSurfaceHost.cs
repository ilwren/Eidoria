using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Media;

/// <summary>
/// 宿主侧原生画面（Android 的 TextureView 等）。桥接对象由平台宿主注册，
/// UI 层只认这个接口，不引用任何平台程序集。
/// </summary>
public interface IMediaSurfaceAttachment : IDisposable
{
    /// <summary>交给 Avalonia 的原生控件句柄；<see cref="NativeControlHost"/> 会把它挂进视觉树并随控件定位。</summary>
    IPlatformHandle Handle { get; }

    /// <summary>原生画面创建成功；参数是平台画面对象（Android：<c>Android.Views.Surface</c>）。</summary>
    event EventHandler<object>? SurfaceReady;

    /// <summary>原生画面被系统收回（切后台、旋转、或控件销毁）。</summary>
    event EventHandler? SurfaceLost;
}

/// <summary>
/// 平台注入点。与 <c>AppHost.MediaPlaybackFactory</c> 同一套模式：
/// Android 宿主在 Activity 创建时注册，桌面与无头测试不注册——此时视频只剩“系统应用打开”。
/// </summary>
public static class MediaSurfaceBridge
{
    /// <summary>参数是 Avalonia 交来的父句柄；返回 null 表示这一端没有原生画面能力。</summary>
    public static Func<IPlatformHandle, IMediaSurfaceAttachment?>? Factory { get; set; }
}

/// <summary>
/// 把后端需要的原生画面窗口托管进 Avalonia 的容器。
/// <para>
/// 这里刻意用 <see cref="NativeControlHost"/> 而不是自绘：视频解码输出在平台侧，
/// 宿主能做的只有“给出一个窗口并把它摆到正确位置”。窗口就绪后通过
/// <see cref="IMediaVideoRenderer"/> 把平台画面对象交给后端。
/// </para>
/// <para>
/// 没有注册 <see cref="MediaSurfaceBridge.Factory"/>（桌面、无头测试）时，
/// 本控件回落成 <see cref="NativeControlHost"/> 的默认空子控件，绝不假装有画面。
/// </para>
/// </summary>
public sealed class MediaSurfaceHost : NativeControlHost
{
    public static readonly StyledProperty<IMediaVideoRenderer?> RendererProperty =
        AvaloniaProperty.Register<MediaSurfaceHost, IMediaVideoRenderer?>(nameof(Renderer));

    private IMediaSurfaceAttachment? _attachment;
    private object? _surface;

    public IMediaVideoRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    static MediaSurfaceHost()
    {
        RendererProperty.Changed.AddClassHandler<MediaSurfaceHost>((host, _) => host.Reattach());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var factory = MediaSurfaceBridge.Factory;
        if (factory is null)
            return base.CreateNativeControlCore(parent);

        _attachment = factory(parent);
        if (_attachment is null)
            return base.CreateNativeControlCore(parent);

        _attachment.SurfaceReady += OnSurfaceReady;
        _attachment.SurfaceLost += OnSurfaceLost;
        return _attachment.Handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_attachment is not null)
        {
            _attachment.SurfaceReady -= OnSurfaceReady;
            _attachment.SurfaceLost -= OnSurfaceLost;
            DetachSurface();
            _attachment.Dispose();
            _attachment = null;
            return;
        }

        base.DestroyNativeControlCore(control);
    }

    private void OnSurfaceReady(object? sender, object surface)
    {
        _surface = surface;
        Renderer?.AttachVideoSurface(surface);
    }

    private void OnSurfaceLost(object? sender, EventArgs e)
    {
        // 系统收回画面时后端必须知道，否则会继续往一个已失效的 Surface 解码。
        DetachSurface();
    }

    private void DetachSurface()
    {
        if (_surface is null)
            return;
        _surface = null;
        Renderer?.DetachVideoSurface();
    }

    /// <summary>切换后端时把已就绪的画面交给新后端，避免切文件后画面停在旧解码器上。</summary>
    private void Reattach()
    {
        if (_surface is { } surface)
            Renderer?.AttachVideoSurface(surface);
    }
}
