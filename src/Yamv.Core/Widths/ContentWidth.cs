namespace Yamv.Core.Widths;

/// <summary>How wide the document's text column is: a number of px, or Full (the whole pane).</summary>
/// <remarks>
/// <see langword="default"/> is Full. A px width is never below <see cref="MinimumPx"/>; it may be wider than the pane,
/// in which case the column is the pane's width and the stored value is left as it is.
/// </remarks>
public readonly record struct ContentWidth
{
    /// <summary>The narrowest column, in px.</summary>
    public const double MinimumPx = 480;

    /// <summary>The app default before the user picks one, in px.</summary>
    public const double DefaultPx = 800;

    /// <summary>The grid Ctrl+[ and Ctrl+] step on, in px; the grid starts at <see cref="MinimumPx"/>.</summary>
    public const double StepPx = 40;

    /// <summary>
    /// The page's side padding around the text, in px: the two 44 px paddings of <c>.yamv-content</c> in <c>yamv.css</c>.
    /// A width is the text measure inside them.
    /// </summary>
    public const double SidePaddingPx = 88;

    private const double FullFraction = 0.95;

    private ContentWidth(double? px)
    {
        Px = px;
    }

    /// <summary>The width in px, or <see langword="null"/> for Full.</summary>
    public double? Px { get; }

    public bool IsFull => Px is null;

    /// <summary>The whole pane.</summary>
    public static ContentWidth Full => default;

    /// <summary>The app default before the user picks one.</summary>
    public static ContentWidth AppDefault => FromPx(DefaultPx);

    /// <summary>A px width, raised to <see cref="MinimumPx"/> when narrower.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="px"/> is not a finite number above zero.</exception>
    public static ContentWidth FromPx(double px)
    {
        if (!double.IsFinite(px) || px <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(px), px, "A content width must be a finite number of px above zero.");
        }

        return new ContentWidth(Math.Max(px, MinimumPx));
    }

    /// <summary>
    /// The width a slider position stands for: <paramref name="fraction"/> runs from 0 (480 px) to 1 (the pane's width),
    /// and the last 5% of the travel is Full. The px value is rounded to a whole px.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fraction"/> is not finite, or <paramref name="paneWidth"/> is not a finite number above zero.
    /// </exception>
    public static ContentWidth FromSlider(double fraction, double paneWidth)
    {
        if (!double.IsFinite(fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A slider position must be a finite number.");
        }

        double span = SliderSpan(paneWidth);
        double position = Math.Clamp(fraction, 0, 1);
        return position >= FullFraction ? Full : FromPx(Math.Round(MinimumPx + (position * span)));
    }

    /// <summary>The slider position for this width, from 0 to 1; Full is 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paneWidth"/> is not a finite number above zero.</exception>
    public double ToSlider(double paneWidth)
    {
        double span = SliderSpan(paneWidth);
        if (Px is not { } px)
        {
            return 1;
        }

        return span <= 0 ? 0 : Math.Clamp((px - MinimumPx) / span, 0, 1);
    }

    /// <summary>
    /// One step wider: the next grid value above this width, or Full when that is past the pane's width. Full stays Full.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paneWidth"/> is not a finite number above zero.</exception>
    public ContentWidth StepUp(double paneWidth)
    {
        RequirePaneWidth(paneWidth);
        if (Px is not { } px)
        {
            return this;
        }

        double next = MinimumPx + ((Math.Floor((px - MinimumPx) / StepPx) + 1) * StepPx);
        return next > paneWidth ? Full : FromPx(next);
    }

    /// <summary>
    /// One step narrower: the previous grid value below this width, never below <see cref="MinimumPx"/>. Full, or a px
    /// width at or past the pane's width, goes to the largest grid value below the pane's width.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paneWidth"/> is not a finite number above zero.</exception>
    public ContentWidth StepDown(double paneWidth)
    {
        RequirePaneWidth(paneWidth);
        if (Px is not { } px || px >= paneWidth)
        {
            return FromPx(LargestStepBelow(paneWidth));
        }

        double previous = MinimumPx + ((Math.Ceiling((px - MinimumPx) / StepPx) - 1) * StepPx);
        return FromPx(Math.Max(previous, MinimumPx));
    }

    /// <summary>
    /// The room a pane of <paramref name="paneWidth"/> px leaves for the text: the pane less <see cref="SidePaddingPx"/>, never
    /// below 1 px. The pane is the width the page lays the text column out in: the document column less a classic scrollbar
    /// when the page shows one. The steps, the slider and Full compare a width against this, not against the pane.
    /// </summary>
    public static double TextSpace(double paneWidth) => Math.Max(paneWidth - SidePaddingPx, 1);

    private static double LargestStepBelow(double paneWidth) =>
        paneWidth <= MinimumPx ? MinimumPx : MinimumPx + ((Math.Ceiling((paneWidth - MinimumPx) / StepPx) - 1) * StepPx);

    private static double SliderSpan(double paneWidth)
    {
        RequirePaneWidth(paneWidth);
        return Math.Max(paneWidth - MinimumPx, 0);
    }

    private static void RequirePaneWidth(double paneWidth)
    {
        if (!double.IsFinite(paneWidth) || paneWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paneWidth), paneWidth, "The pane width must be a finite number of px above zero.");
        }
    }
}
