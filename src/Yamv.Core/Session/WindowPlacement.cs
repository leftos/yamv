namespace Yamv.Core.Session;

/// <summary>A screen's working area in physical screen pixels, with the screen's scaling.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Scaling">The screen's scaling: physical pixels per device-independent pixel.</param>
public sealed record ScreenArea(double X, double Y, double Width, double Height, double Scaling);

/// <summary>Fits a saved window rectangle onto the screens there are now, working in physical pixels.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// The rectangle to restore <paramref name="saved"/> as. Its physical pixels (its device-independent pixels times its
    /// <see cref="WindowBounds.Scaling"/>) are shrunk when larger than, then moved inside, the working area they overlap most
    /// (ties and no overlap go to the first area given); the result is in that screen's device-independent pixels, with its
    /// scaling. The client and the visible outline drawn along its left, right and bottom edges are what move inside the
    /// area: <paramref name="frameAt"/> says where the client begins inside the outer rectangle and how wide that outline is,
    /// and it is read at <see cref="WindowBounds.Scaling"/> for the rectangle as it was saved and at the area's scaling for
    /// where it lands. <see cref="WindowBounds.IsMaximized"/> is kept.
    /// </summary>
    /// <param name="saved">The saved rectangle.</param>
    /// <param name="workingAreas">The screens' working areas, the primary screen first; with none, <paramref name="saved"/> is returned.</param>
    /// <param name="frameAt">The window's frame at a scaling, in physical pixels; <see cref="WindowFrame.None"/> for none.</param>
    public static WindowBounds Fit(WindowBounds saved, IReadOnlyList<ScreenArea> workingAreas, Func<double, WindowFrame> frameAt)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(workingAreas);
        ArgumentNullException.ThrowIfNull(frameAt);
        if (workingAreas.Count == 0)
        {
            return saved;
        }

        var pixels = new ScreenArea(saved.X * saved.Scaling, saved.Y * saved.Scaling, saved.Width * saved.Scaling, saved.Height * saved.Scaling, 1);
        ScreenArea area = workingAreas[TargetArea(pixels, workingAreas)];
        WindowFrame savedFrame = frameAt(saved.Scaling);
        WindowFrame areaFrame = frameAt(area.Scaling);
        double width = Math.Min(pixels.Width, area.Width - (2 * areaFrame.Outline));
        double height = Math.Min(pixels.Height, area.Height - areaFrame.Outline);
        double visibleWidth = width + (2 * areaFrame.Outline);
        double visibleHeight = height + areaFrame.Outline;
        double visibleX = Math.Clamp(pixels.X + savedFrame.ClientLeft - savedFrame.Outline, area.X, area.X + area.Width - visibleWidth);
        double visibleY = Math.Clamp(pixels.Y + savedFrame.ClientTop, area.Y, area.Y + area.Height - visibleHeight);
        return saved with
        {
            X = (visibleX + areaFrame.Outline - areaFrame.ClientLeft) / area.Scaling,
            Y = (visibleY - areaFrame.ClientTop) / area.Scaling,
            Width = width / area.Scaling,
            Height = height / area.Scaling,
            Scaling = area.Scaling,
        };
    }

    private static int TargetArea(ScreenArea pixels, IReadOnlyList<ScreenArea> workingAreas)
    {
        int best = 0;
        double bestOverlap = 0;
        for (int index = 0; index < workingAreas.Count; index++)
        {
            double overlap = Overlap(pixels, workingAreas[index]);
            if (overlap > bestOverlap)
            {
                best = index;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static double Overlap(ScreenArea first, ScreenArea second)
    {
        double width = Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X);
        double height = Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}
