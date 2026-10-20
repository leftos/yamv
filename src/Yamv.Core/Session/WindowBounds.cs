namespace Yamv.Core.Session;

/// <summary>The window geometry YAMV restores on the next launch, in device-independent pixels.</summary>
public sealed record WindowBounds(double X, double Y, double Width, double Height, bool IsMaximized);
