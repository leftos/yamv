namespace Yamv.Core.Session;

/// <summary>
/// The frame a window's platform draws around its client, in the physical pixels of one scaling: where the client begins
/// inside the outer window rectangle, and the visible outline drawn along the client's left, right and bottom edges.
/// </summary>
/// <param name="ClientLeft">The physical pixels from the outer window corner to the client's left edge at one scaling.</param>
/// <param name="ClientTop">The physical pixels from the outer window corner to the client's top edge at one scaling.</param>
/// <param name="Outline">
/// The visible outline's width in physical pixels on the client's left, right and bottom sides; there is none on top.
/// </param>
public sealed record WindowFrame(double ClientLeft, double ClientTop, double Outline)
{
    /// <summary>A window whose client begins at its outer corner, with no visible outline.</summary>
    public static WindowFrame None { get; } = new(ClientLeft: 0, ClientTop: 0, Outline: 0);
}
