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

    /// <summary>后端接受了宿主的帧呈现面（v0.55）：自动播放靠它确认「画面管道已接通」。</summary>
    public event EventHandler? SinkAttached;

    public bool IsSinkAttached => _attached is not null;

    private WriteableBitmap? _bitmap;
    private IMediaVideoFrameSource? _attached;
    // v0.55：后端有时还没准备好（解码器仍在初始化）就返回 false。此前只在 Source 变化或控件
    // 重新进树时才重试，于是「第一次预览不出画面、动一下才出来」——这里按固定间隔自己重试几次。
    private Avalonia.Threading.DispatcherTimer? _attachRetry;
    private int _attachAttempts;
    private const int MaxAttachAttempts = 20;
    private readonly object _pendingFrameGate = new();
    private PendingFrame? _pendingFrame;
    private bool _frameDispatchPending;
    // v0.46：帧缓冲池。此前每帧都 new byte[]——1080p 一帧 8 MB，60 fps 就是每秒 500 MB 垃圾，
    // 表现为 CPU 高、内存抖动、画面一顿一顿。现在生产者从池里取、UI 消费完再还回池：稳态零分配。
    private byte[]? _spareFrame;

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
        var frame = RentFrame((int)frameLength);
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
            // 上一帧还没画就被新帧顶掉时，把它的缓冲区直接还回池，别扔给 GC。
            if (_pendingFrame is { } superseded && superseded.Pixels.Length == frame.Length)
                _spareFrame = superseded.Pixels;
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

    /// <summary>从池里取一块够大的缓冲；尺寸变了就换新（旧的自然被丢弃）。只能由解码线程调用。</summary>
    private byte[] RentFrame(int length)
    {
        // v0.55：空闲缓冲是解码线程和 UI 线程共用的，取还都要在同一把锁里。
        // 此前两边各写各的，偶尔会把同一块缓冲同时交给两帧，画面上就是撕裂/抖一下。
        lock (_pendingFrameGate)
        {
            var spare = _spareFrame;
            if (spare is not null && spare.Length == length)
            {
                _spareFrame = null;
                return spare;
            }
        }
        return new byte[length];
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
        if (pending is not { } frame)
            return;
        PresentCore(frame.Pixels, frame.Width, frame.Height);
        // 画完把缓冲还给解码线程；只留一块，多余的自然回收。
        lock (_pendingFrameGate)
            _spareFrame = frame.Pixels;
    }

    private void ClearCore()
    {
        lock (_pendingFrameGate)
        {
            _pendingFrame = null;
            _spareFrame = null;
        }
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
            ScheduleAttachRetry();
            return;
        }

        StopAttachRetry();
        _attachAttempts = 0;
        InvalidateVisual();
        SinkAttached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 后端暂时拒收就隔 200 ms 再试。上限 20 次（约 4 秒）后放弃——那说明这个后端确实交不出画面，
    /// 界面照实说「画面未接入」，而不是无限重试。
    /// </summary>
    private void ScheduleAttachRetry()
    {
        if (_attachRetry is not null || _attachAttempts >= MaxAttachAttempts)
            return;
        _attachAttempts++;
        _attachRetry = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _attachRetry.Tick += (_, _) =>
        {
            StopAttachRetry();
            if (_attached is null)
                Attach();
        };
        _attachRetry.Start();
    }

    private void StopAttachRetry()
    {
        _attachRetry?.Stop();
        _attachRetry = null;
    }

    private void Detach()
    {
        StopAttachRetry();
        _attachAttempts = 0;
        if (_attached is not { } source)
            return;
        _attached = null;
        source.DetachFrameSink();
        ClearCore();
    }
}
