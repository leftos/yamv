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
    // The card's shadow is not a theme token: the width-popover mock's values for light and dark.
    private static readonly BoxShadows LightShadow = BoxShadows.Parse("0 10 22 0 #421F2328, 0 1 3 0 #291F2328");
    private static readonly BoxShadows DarkShadow = BoxShadows.Parse("0 10 22 0 #B3000000, 0 1 3 0 #8C000000");

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
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ClearAllConfirm.PropertyChanged += OnClearAllConfirmPropertyChanged;
        ClearAllLink.PropertyChanged += OnClearAllLinkPropertyChanged;
        UpdateShadow();
    }

    /// <summary>
    /// The Clear all question replaced the link that had focus, or went away with the button that had it: focus moves to Cancel,
    /// or back to the link, or to the selected segment's button when the link is disabled (the slider when no segment is
    /// selected), so keys such as Esc still reach the popover.
    /// </summary>
    private void OnClearAllConfirmPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && TopLevel.GetTopLevel(this) is not null)
        {
            Dispatcher.UIThread.Post(FocusClearAllLine);
        }
    }

    private void FocusClearAllLine()
    {
        if (ClearAllConfirm.IsEffectivelyVisible)
        {
            ClearAllNo.Focus();
        }
        else if (ClearAllLink.IsEffectivelyEnabled && ClearAllLink.IsEffectivelyVisible)
        {
            ClearAllLink.Focus();
        }
        else if (SelectedSegmentButton() is { } segment)
        {
            segment.Focus();
        }
        else
        {
            WidthSlider.Focus();
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
            FocusClearAllLine();
        }
    }

    /// <summary>The selected Saved for segment's button, which takes focus when the Clear all link cannot; null when none is selected.</summary>
    private Button? SelectedSegmentButton() =>
        ViewModel?.SelectedSegment is { } selected
            ? Segments.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.DataContext, selected))
            : null;

    private ContentWidthViewModel? ViewModel => DataContext as ContentWidthViewModel;

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => UpdateShadow();

    private void UpdateShadow() => Card.BoxShadow = ActualThemeVariant == ThemeVariant.Dark ? DarkShadow : LightShadow;

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
