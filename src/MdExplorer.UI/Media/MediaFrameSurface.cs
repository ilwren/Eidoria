using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Media;

/// <summary>
/// 宿主帧呈现面（<see cref="MediaRendererKind.FramePush"/>）：后端把解码后的帧推进来，
/// 这里写成 <see cref="WriteableBitmap"/> 并按 Uniform 等比画进控件。
/// <para>
/// Wayland 下没有可交给解码器的窗口 id，所以 Linux 的 GStreamer 后端只能走 <c>appsink</c> 取帧；
/// 这条路径与 Android 的 <see cref="MediaSurfaceHost"/> 并列，互不冒充：
/// 原生画面由平台画，推帧画面由 Avalonia 画。
/// </para>
/// <para>
/// 线程约定：<see cref="PresentFrame"/> 可能从解码线程调用。实现先拷进自己的数组再切回 UI 线程，
/// 解码线程不会因为等待渲染而阻塞；UI 调度只保留最新待绘帧，避免渲染稍慢时积压过期画面。
/// </para>
/// </summary>
public sealed class MediaFrameSurface : Control, IMediaFrameSink
{
    public static readonly StyledProperty<IMediaVideoFrameSource?> SourceProperty =
        AvaloniaProperty.Register<MediaFrameSurface, IMediaVideoFrameSource?>(nameof(Source));

    /// <summary>当前画面尺寸；没有帧时为 0×0。截图与单测用它确认“真的收到了画面”。</summary>
    public PixelSize FrameSize { get; private set; }

    public bool HasFrame => FrameSize.Width > 0 && FrameSize.Height > 0 && _bitmap is not null;

    private WriteableBitmap? _bitmap;
    private IMediaVideoFrameSource? _attached;
    private readonly object _pendingFrameGate = new();
    private PendingFrame? _pendingFrame;
    private bool _frameDispatchPending;

    static MediaFrameSurface()
    {
        AffectsRender<MediaFrameSurface>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<MediaFrameSurface>((surface, _) => surface.Reattach());
    }

    public IMediaVideoFrameSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public void PresentFrame(ReadOnlySpan<byte> bgra8888, int width, int height, int strideBytes)
    {
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            return;
        var rowBytes = checked(width * 4);
        var frameLength = (long)rowBytes * height;
        var stride = strideBytes > 0 ? strideBytes : rowBytes;
        if (frameLength > 64L * 1024 * 1024 || stride < rowBytes ||
            bgra8888.Length < (long)stride * (height - 1) + rowBytes)
            return;

        // 解码线程只负责拷贝紧凑帧；UI 调度只保留最新待绘帧，不能让慢绘制把整段视频排成消息队列。
        var frame = new byte[(int)frameLength];
        if (stride == rowBytes)
        {
            bgra8888[..frame.Length].CopyTo(frame);
        }
        else
        {
            for (var y = 0; y < height; y++)
                bgra8888.Slice(y * stride, rowBytes).CopyTo(frame.AsSpan(y * rowBytes, rowBytes));
        }

        var runOnUiThread = false;
        var postToUiThread = false;
        lock (_pendingFrameGate)
        {
            _pendingFrame = new PendingFrame(frame, width, height);
            if (!_frameDispatchPending)
            {
                _frameDispatchPending = true;
                runOnUiThread = Dispatcher.UIThread.CheckAccess();
                postToUiThread = !runOnUiThread;
            }
        }

        if (runOnUiThread)
            DrainPendingFrame();
        else if (postToUiThread)
            Dispatcher.UIThread.Post(DrainPendingFrame);
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ClearCore();
            return;
        }
        Dispatcher.UIThread.Post(ClearCore);
    }

    public override void Render(DrawingContext context)
    {
        // 画面未到时保持黑底：占位而不是把后面的页面透出来。
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not { } bitmap || FrameSize.Width <= 0 || FrameSize.Height <= 0)
            return;

        var source = new Rect(0, 0, FrameSize.Width, FrameSize.Height);
        var scale = Math.Min(Bounds.Width / source.Width, Bounds.Height / source.Height);
        if (double.IsNaN(scale) || scale <= 0)
            return;

        var target = new Rect(
            (Bounds.Width - source.Width * scale) / 2,
            (Bounds.Height - source.Height * scale) / 2,
            source.Width * scale,
            source.Height * scale);
        context.DrawImage(bitmap, source, target);
    }

    private void PresentCore(byte[] frame, int width, int height)
    {
        EnsureBitmap(width, height);
        if (_bitmap is null)
            return;

        using (var buffer = _bitmap.Lock())
        {
            var rowBytes = width * 4;
            if (buffer.RowBytes == rowBytes)
            {
                // 常见媒体尺寸行距已对齐：一次连续复制，避免每帧按行跨越数百次托管/原生边界。
                Marshal.Copy(frame, 0, buffer.Address, frame.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    var destination = IntPtr.Add(buffer.Address, y * buffer.RowBytes);
                    Marshal.Copy(frame, y * rowBytes, destination, rowBytes);
                }
            }
        }

        FrameSize = new PixelSize(width, height);
        InvalidateVisual();
    }

    private void DrainPendingFrame()
    {
        PendingFrame? pending;
        lock (_pendingFrameGate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
            _frameDispatchPending = false;
        }
        if (pending is { } frame)
            PresentCore(frame.Pixels, frame.Width, frame.Height);
    }

    private void ClearCore()
    {
        lock (_pendingFrameGate)
            _pendingFrame = null;
        FrameSize = default;
        InvalidateVisual();
    }

    private readonly record struct PendingFrame(byte[] Pixels, int Width, int Height);

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is { } existing && FrameSize.Width == width && FrameSize.Height == height)
            return;

        var replaced = _bitmap;
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        replaced?.Dispose();
    }

    private void Reattach()
    {
        Detach();
        Attach();
    }

    private void Attach()
    {
        if (_attached is not null || Source is not { } source)
            return;
        _attached = source;
        if (!source.AttachFrameSink(this))
        {
            _attached = null;
            return;
        }
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_attached is not { } source)
            return;
        _attached = null;
        source.DetachFrameSink();
        ClearCore();
    }
}
