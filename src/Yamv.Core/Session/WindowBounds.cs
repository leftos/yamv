namespace Yamv.Core.Session;

/// <summary>The window geometry YAMV restores on the next launch, in device-independent pixels.</summary>
/// <param name="X">The left edge of the restored rectangle.</param>
/// <param name="Y">The top edge of the restored rectangle.</param>
/// <param name="Width">The width of the restored rectangle.</param>
/// <param name="Height">The height of the restored rectangle.</param>
/// <param name="IsMaximized">True when the window was maximized when the state was saved.</param>
/// <remarks>
/// The rectangle is always the window's normal (restored) rectangle, even while the window is maximized, so that
/// un-maximizing on the next launch returns the window to the size the user chose.
/// </remarks>
public sealed record WindowBounds(double X, double Y, double Width, double Height, bool IsMaximized);
