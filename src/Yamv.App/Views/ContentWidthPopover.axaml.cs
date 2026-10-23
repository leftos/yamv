using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.ViewModels;
using Yamv.Core.Widths;

namespace Yamv.App.Views;

/// <summary>
/// The content of the content-width popover over a <see cref="ContentWidthViewModel"/>: the readout and slider, the Saved for
/// segments, Reset to inherited, Clear all overrides here with its question, and the foot's failure note. The slider ends a
/// drag when the pointer is released or loses capture, and its keys move it as <see cref="ContentWidthViewModel.StepSlider"/>
/// does, ending the drag on the key's release, so a held key saves once.
/// </summary>
public partial class ContentWidthPopover : UserControl
{
    // The shadow is not a theme token: the width-popover-arrow mock's values, a far and a near shadow in each theme.
    private static readonly Color LightShadowColor = Color.Parse("#1F2328");

    private const double CardRadius = 8;

    // The rim's centre line runs half a px inside the outline, so its 1 px stroke lies on whole pixels inside the fill.
    private const double RimInset = 0.5;

    // The pinned cap's hatch, as the wide-stored-width mock draws it: 2 px stripes 4.5 px apart, rising at 55°.
    private const int HatchStripes = 20;
    private const double HatchHeight = 6;
    private const double HatchRise = 4.2;
    private const double HatchPitch = 5.49;

    // Focus is already null when the Clear all question's panel hides the link, so the last focus's kind is kept here instead.
    private bool _keyboardFocus;

    /// <summary>The hatch of the slider's cap while the stored width is wider than the window; enough stripes for a 100 px cap.</summary>
    public static Geometry PinnedHatch { get; } = HatchGeometry();

    public ContentWidthPopover()
    {
        InitializeComponent();
        WidthSlider.AddHandler(KeyDownEvent, OnSliderKeyDown, RoutingStrategies.Tunnel);
        WidthSlider.AddHandler(KeyUpEvent, OnSliderKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        WidthSlider.AddHandler(
            PointerReleasedEvent,
            OnSliderPointerEnded,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true
        );
        WidthSlider.AddHandler(PointerCaptureLostEvent, OnSliderPointerEnded, RoutingStrategies.Direct, handledEventsToo: true);
        WidthSlider.AddHandler(Thumb.DragCompletedEvent, OnSliderPointerEnded, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnPopoverGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ClearAllConfirm.PropertyChanged += OnClearAllConfirmPropertyChanged;
        ClearAllLink.PropertyChanged += OnClearAllLinkPropertyChanged;
        Card.SizeChanged += (_, _) => UpdateRim();
        UpdateShadow();
    }

    /// <summary>
    /// The Clear all question replaced the link that had focus, or went away with the button that had it: focus moves to Cancel,
    /// or back to the link, or to the selected segment's button when the link is disabled (the slider when no segment is
    /// selected), so keys such as Esc still reach the popover. The focus ring follows the move when the focus it replaces was
    /// keyboard-given; a pointer's focus stays ringless.
    /// </summary>
    private void OnClearAllConfirmPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && TopLevel.GetTopLevel(this) is not null)
        {
            Dispatcher.UIThread.Post(() => FocusClearAllLine(_keyboardFocus));
        }
    }

    /// <summary>Remembers how the focus inside the popover was given: a keyboard navigation draws the ring that a move keeps.</summary>
    private void OnPopoverGotFocus(object? sender, FocusChangedEventArgs e) =>
        _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;

    /// <summary>Moves focus into the Clear all line, keeping the ring when <paramref name="keyboard"/> says the focus it replaces had one.</summary>
    private void FocusClearAllLine(bool keyboard)
    {
        NavigationMethod method = keyboard ? NavigationMethod.Tab : NavigationMethod.Pointer;
        if (ClearAllConfirm.IsEffectivelyVisible)
        {
            ClearAllNo.Focus(method);
        }
        else if (ClearAllLink.IsEffectivelyEnabled && ClearAllLink.IsEffectivelyVisible)
        {
            ClearAllLink.Focus(method);
        }
        else if (SelectedSegmentButton() is { } segment)
        {
            segment.Focus(method);
        }
        else
        {
            WidthSlider.Focus(method);
        }
    }

    /// <summary>
    /// The Clear all link was disabled, as the clear it asked for left nothing to clear: it may have had the focus, so focus
    /// goes back into the popover, to the selected segment's button, while nothing in the popover holds the focus or the link
    /// still does. Opening the popover runs nothing, because the link is then disabled already or becomes enabled.
    /// </summary>
    private void OnClearAllLinkPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.IsEffectivelyEnabledProperty && !e.GetNewValue<bool>())
        {
            Dispatcher.UIThread.Post(RefocusWhenClearAllLinkDisabled);
        }
    }

    private void RefocusWhenClearAllLinkDisabled()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IInputElement? focused = top.FocusManager?.GetFocusedElement();
        if (focused is null || ReferenceEquals(focused, ClearAllLink))
        {
            FocusClearAllLine(_keyboardFocus);
        }
    }

    /// <summary>The selected Saved for segment's button, which takes focus when the Clear all link cannot; null when none is selected.</summary>
    private Button? SelectedSegmentButton() =>
        ViewModel?.SelectedSegment is { } selected
            ? Segments.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.DataContext, selected))
            : null;

    private ContentWidthViewModel? ViewModel => DataContext as ContentWidthViewModel;

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => UpdateShadow();

    /// <summary>
    /// Gives the arrow and the card their shadow for the theme: a far one (0 10 22) and a near one (0 1 3), cast by the panels
    /// that hold both, so they cast one silhouette.
    /// </summary>
    private void UpdateShadow()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        Color color = dark ? Colors.Black : LightShadowColor;
        FarShadow.Effect = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 10,
            BlurRadius = 22,
            Color = color,
            Opacity = dark ? 0.70 : 0.26,
        };
        NearShadow.Effect = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 1,
            BlurRadius = 3,
            Color = color,
            Opacity = dark ? 0.55 : 0.16,
        };
    }

    /// <summary>The hatch's stripes, from 5 px left of the cap's start and 1 px past its 4 px height at each end.</summary>
    private static StreamGeometry HatchGeometry()
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        for (int stripe = 0; stripe < HatchStripes; stripe++)
        {
            double x = -5 + (stripe * HatchPitch);
            context.BeginFigure(new Point(x, HatchHeight - 1), isFilled: false);
            context.LineTo(new Point(x + HatchRise, -1));
            context.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>Outlines the arrow and the card again, as the card's size changed.</summary>
    private void UpdateRim()
    {
        if (BoundsInRim(Arrow) is { } arrow && BoundsInRim(Card) is { } card)
        {
            Rim.Data = RimGeometry(arrow, card);
        }
    }

    /// <summary><paramref name="control"/>'s bounds in the rim's coordinates; null while it is not in the rim's visual tree.</summary>
    private Rect? BoundsInRim(Control control) => control.TranslatePoint(default, Rim) is { } origin ? new Rect(origin, control.Bounds.Size) : null;

    /// <summary>
    /// The rim's outline: the card's rounded rectangle with the arrow rising from its top edge, as one closed figure, so the
    /// stroke has no seam where the arrow meets the card. Both rectangles are in the rim's coordinates.
    /// </summary>
    private static StreamGeometry RimGeometry(Rect arrow, Rect card)
    {
        double inset = RimInset;
        double top = card.Top + inset;
        double bottom = card.Bottom - inset;
        double left = card.Left + inset;
        double right = card.Right - inset;
        var corner = new Size(CardRadius - inset, CardRadius - inset);
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(new Point(card.Left + CardRadius, top), isFilled: false);
        context.LineTo(new Point(arrow.Left + inset, top));
        // The arrow's sides rise at 45°, so its tip moves down by the inset times √2.
        context.LineTo(new Point(arrow.Center.X, arrow.Top + (inset * Math.Sqrt(2))));
        context.LineTo(new Point(arrow.Right - inset, top));
        context.LineTo(new Point(Math.Max(arrow.Right - inset, card.Right - CardRadius), top));
        context.ArcTo(new Point(right, card.Top + CardRadius), corner, 0, false, SweepDirection.Clockwise);
        context.LineTo(new Point(right, card.Bottom - CardRadius));
        context.ArcTo(new Point(card.Right - CardRadius, bottom), corner, 0, false, SweepDirection.Clockwise);
        context.LineTo(new Point(card.Left + CardRadius, bottom));
        context.ArcTo(new Point(left, card.Bottom - CardRadius), corner, 0, false, SweepDirection.Clockwise);
        context.LineTo(new Point(left, card.Top + CardRadius));
        context.ArcTo(new Point(card.Left + CardRadius, top), corner, 0, false, SweepDirection.Clockwise);
        context.EndFigure(isClosed: true);
        return geometry;
    }

    private void OnSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WidthScope segment })
        {
            ViewModel?.SelectSegment(segment);
        }
    }

    /// <summary>
    /// Every arrow, Page, Home and End key moves the slider on the width grid, whatever the modifiers, so none falls through to
    /// the template slider's own step over its 0..1 range.
    /// </summary>
    private void OnSliderKeyDown(object? sender, KeyEventArgs e)
    {
        if (StepOf(e.Key) is not SliderStep step)
        {
            return;
        }

        ViewModel?.StepSlider(step);
        e.Handled = true;
    }

    private void OnSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (StepOf(e.Key) is not null)
        {
            ViewModel?.EndDrag();
        }
    }

    private void OnSliderPointerEnded(object? sender, RoutedEventArgs e) => ViewModel?.EndDrag();

    private static SliderStep? StepOf(Key key) =>
        key switch
        {
            Key.Left or Key.Down or Key.PageDown => SliderStep.Narrower,
            Key.Right or Key.Up or Key.PageUp => SliderStep.Wider,
            Key.Home => SliderStep.Narrowest,
            Key.End => SliderStep.Full,
            _ => null,
        };
}

/// <summary>
/// Lays the Saved for segments out in one row across the whole width, as the width-popover mock's flex row does: an equal
/// share each while every label fits one, else each label's width plus 6 px at each side and an equal share of what is left.
/// </summary>
public sealed class SegmentPanel : Panel
{
    private const double CellPadding = 6;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width + (2 * CellPadding);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double[] widths = CellWidths(finalSize.Width);
        double x = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(new Rect(x, 0, widths[index], finalSize.Height));
            x += widths[index];
        }

        return finalSize;
    }

    private double[] CellWidths(double total)
    {
        int count = Children.Count;
        double equal = total / count;
        if (Children.All(child => child.DesiredSize.Width <= equal))
        {
            return [.. Enumerable.Repeat(equal, count)];
        }

        double padding = 2 * CellPadding;
        double share = (total - Children.Sum(child => child.DesiredSize.Width + padding)) / count;
        return [.. Children.Select(child => Math.Max(0, child.DesiredSize.Width + padding + share))];
    }
}
