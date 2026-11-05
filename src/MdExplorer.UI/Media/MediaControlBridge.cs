using Avalonia.Controls;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Media;

/// <summary>
/// Optional host hook for media engines that render through their own Avalonia control instead of
/// a native surface or pushed frames. The desktop host installs the factory before creating the shell;
/// headless tests and platforms without such an engine leave it unset.
/// </summary>
public static class MediaControlBridge
{
    /// <summary>Creates the visual control for a service using <see cref="MediaRendererKind.AvaloniaControl"/>.</summary>
    public static Func<IMediaPlaybackService, Control?>? Factory { get; set; }
}
