using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using HanumanInstitute.LibMpv;

namespace MdExplorer.Desktop;

/// <summary>
/// Keeps the wrapper-created mpv context connected to the playback service for exactly as long as
/// the Avalonia renderer is attached. The renderer/control owns its native context lifetime.
/// </summary>
internal sealed partial class LibMpvVideoControl : UserControl
{
    private readonly LibMpvMediaPlaybackService _service;
    private MpvContext? _context;

    public LibMpvVideoControl(LibMpvMediaPlaybackService service)
    {
        _service = service;
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>Target of the MpvView OneWayToSource binding.</summary>
    public MpvContext? Context
    {
        get => _context;
        set
        {
            if (ReferenceEquals(_context, value))
                return;

            _service.DetachContext(_context);
            _context = value;
            if (value is not null)
                _service.AttachContext(value);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_context is not null)
            _service.AttachContext(_context);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.DetachContext(_context);
        base.OnDetachedFromVisualTree(e);
    }
}
