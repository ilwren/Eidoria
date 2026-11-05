using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Media;

/// <summary>
/// Hosts a renderer-specific Avalonia control only for the active preview surface. Preview content
/// is duplicated in the sidebar and the compact floating window, but a Control can have only one
/// visual parent; inactive hosts therefore never construct or retain the native renderer view.
/// </summary>
public sealed class MediaControlHost : ContentControl
{
    public static readonly StyledProperty<IMediaPlaybackService?> ServiceProperty =
        AvaloniaProperty.Register<MediaControlHost, IMediaPlaybackService?>(nameof(Service));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<MediaControlHost, bool>(nameof(IsActive));

    private bool _isAttached;
    private bool _creationAttempted;
    private Control? _hostedControl;
    private IMediaPlaybackService? _hostedService;
    private IMediaPlaybackService? _attemptedService;

    static MediaControlHost()
    {
        ServiceProperty.Changed.AddClassHandler<MediaControlHost>((host, _) => host.UpdateContent());
        IsActiveProperty.Changed.AddClassHandler<MediaControlHost>((host, _) => host.UpdateContent());
    }

    public IMediaPlaybackService? Service
    {
        get => GetValue(ServiceProperty);
        set => SetValue(ServiceProperty, value);
    }

    /// <summary>Only the one preview currently on screen may own the mpv renderer.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Reports view-construction failures to the shared preview diagnostics.</summary>
    public string? InitializationError { get; private set; }
    public event EventHandler? InitializationErrorChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateContent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateContent()
    {
        if (!_isAttached || !IsActive || Service is not { RendererKind: MediaRendererKind.AvaloniaControl } service)
        {
            Deactivate();
            return;
        }

        if (ReferenceEquals(_hostedService, service) && _hostedControl is not null)
            return;
        if (_creationAttempted && ReferenceEquals(_attemptedService, service))
            return;

        Deactivate();
        _attemptedService = service;
        _creationAttempted = true;
        var factory = MediaControlBridge.Factory;
        if (factory is null)
            return;

        try
        {
            _hostedControl = factory(service);
            if (_hostedControl is null)
            {
                SetInitializationError("媒体 renderer 没有创建画面控件。");
                return;
            }

            _hostedService = service;
            Content = _hostedControl;
        }
        catch (Exception ex)
        {
            SetInitializationError($"视频画面控件初始化失败：{ex.GetBaseException().Message}");
        }
    }

    private void Deactivate()
    {
        // Removing the control from the visual tree lets the platform view hand its native context
        // back to the playback service before another preview host becomes active.
        Content = null;
        _hostedControl = null;
        _hostedService = null;
        _attemptedService = null;
        _creationAttempted = false;
        SetInitializationError(null);
    }

    private void SetInitializationError(string? error)
    {
        if (string.Equals(InitializationError, error, StringComparison.Ordinal))
            return;

        InitializationError = error;
        InitializationErrorChanged?.Invoke(this, EventArgs.Empty);
    }
}
