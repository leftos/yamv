namespace Yamv.Core.Session;

/// <summary>Where a document's view stands: the top-level block at the top of the view, and how far down that block the view starts.</summary>
/// <param name="Line">The zero-based source line the block starts on.</param>
/// <param name="Offset">The fraction, 0 to 1, of the block's height above the top of the view.</param>
public sealed record ScrollAnchor(int Line, double Offset);
