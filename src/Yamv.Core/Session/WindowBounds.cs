namespace Yamv.Core.Session;

/// <summary>
/// The window geometry YAMV restores on the next launch, in the device-independent pixels of the screen it was measured on:
/// <c>X * Scaling</c> is the left edge in physical screen pixels.
/// </summary>
/// <param name="X">The left edge of the restored rectangle.</param>
/// <param name="Y">The top edge of the restored rectangle.</param>
/// <param name="Width">The width of the restored rectangle.</param>
/// <param name="Height">The height of the restored rectangle.</param>
/// <param name="IsMaximized">True when the window was maximized when the state was saved.</param>
/// <param name="Scaling">The scaling of the screen the rectangle was measured on; 1 when unknown.</param>
/// <remarks>
/// The rectangle is always the window's normal (restored) rectangle, even while the window is maximized, so that
/// un-maximizing on the next launch returns the window to the size the user chose. Each screen has its own device-independent
/// space, and on Windows those spaces overlap when screens have different scalings, so a rectangle is only placed through its
/// physical pixels.
/// </remarks>
public sealed record WindowBounds(double X, double Y, double Width, double Height, bool IsMaximized, double Scaling = 1);
