using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Media;

/// <summary>
/// 把后端自带的画面控件（<see cref="MediaRendererKind.HostedControl"/>，如 libmpv）托管进 Avalonia。
/// <para>
/// 与 <see cref="MediaSurfaceHost"/>（平台原生窗口）和 <see cref="MediaFrameSurface"/>（后端推帧）
/// 并列：三者互斥，一个媒体卡片在同一时刻只会有一个可见的画面面。
/// </para>
/// <para>
/// 本控件只做三件事：向后端要控件、把控件摆进内容区、在控件离开视觉树时立刻还回去。
/// 它不猜后端、不自己解码，也**不会**在创建失败时留一块黑底假装有画面——失败就显示一句原因。
/// </para>
/// </summary>
public sealed class MediaVideoControlHost : ContentControl
{
    /// <summary>画面控件来源（后端实现 <see cref="IMediaVideoControlSource"/>）。</summary>
    public static readonly StyledProperty<IMediaVideoControlSource?> SourceProperty =
        AvaloniaProperty.Register<MediaVideoControlHost, IMediaVideoControlSource?>(nameof(Source));

    /// <summary>
    /// 是否要真正的视频画面。false 表示当前只播音频（或视频画面没被放行）：
    /// 后端仍会建立播放上下文（否则没有解码器），但应挑一条不需要 GPU 表面的渲染路线。
    /// </summary>
    public static readonly StyledProperty<bool> IncludeVideoSurfaceProperty =
        AvaloniaProperty.Register<MediaVideoControlHost, bool>(nameof(IncludeVideoSurface));

    private IMediaVideoControlSource? _attached;
    private string? _errorText;
    private bool _rebuilding;
    private bool _inVisualTree;

    static MediaVideoControlHost()
    {
        SourceProperty.Changed.AddClassHandler<MediaVideoControlHost>((host, _) => host.Rebuild());
        IncludeVideoSurfaceProperty.Changed.AddClassHandler<MediaVideoControlHost>((host, _) => host.Rebuild());
    }

    public IMediaVideoControlSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IncludeVideoSurface
    {
        get => GetValue(IncludeVideoSurfaceProperty);
        set => SetValue(IncludeVideoSurfaceProperty, value);
    }

    /// <summary>最近一次创建失败的原因；没失败时为 null。</summary>
    public string? ErrorText => _errorText;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _inVisualTree = true;
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 离开视觉树就还回去：预览栏收起、切走目的地、换后端都走这条。
        // 后端的“续播”语义由它自己处理（见 MpvMediaPlaybackService.ReleaseVideoControl）。
        _inVisualTree = false;
        Release();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild()
    {
        // 绑定在控件挂上视觉树之前就会落地：那时建控件只会白建一次（还要在挂上时再建一次），
        // 所以只信“已挂上”这个状态，挂上时再统一建。
        if (_rebuilding || !_inVisualTree)
            return;
        _rebuilding = true;
        try
        {
            Release();

            var source = Source;
            if (source is null)
            {
                _errorText = null;
                return;
            }

            MediaVideoControl session;
            try
            {
                session = source.CreateVideoControl(IncludeVideoSurface);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 原生库缺失、AOT 下包装层不受支持等都会在这里露头：如实显示，不闪退。
                session = new MediaVideoControl(null, ex.Message);
            }

            if (session.Control is Control control)
            {
                _attached = source;
                _errorText = null;
                Content = control;
                return;
            }

            _errorText = session.Error ?? "后端没有交出画面控件。";
            Content = new TextBlock
            {
                Text = _errorText,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12),
                Foreground = Avalonia.Media.Brushes.Gainsboro,
            };
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void Release()
    {
        if (_attached is { } attached)
        {
            _attached = null;
            try
            {
                attached.ReleaseVideoControl();
            }
            catch (Exception)
            {
                // 释放失败仍然把内容摘掉：界面不该被一个卡死的后端拖住。
            }
        }

        Content = null;
    }
}
