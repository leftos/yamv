using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Yamv.App.ViewModels;

namespace Yamv.App.Views;

/// <summary>
/// The cards of a window's <see cref="ToastCenter"/>, stacked oldest first, each with its icon, its line, the count of its
/// repeats, a × that takes it down and the line of its time left. It is the content of the window's toast popup.
/// </summary>
public partial class ToastHost : UserControl
{
    // The shadow is not a theme token: the toast mock's values, a far and a near shadow in each theme.
    private static readonly Color LightShadowColor = Color.Parse("#1F2328");

    public ToastHost()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => UpdateShadow();
        UpdateShadow();
    }

    /// <summary>Gives the cards their shadows for the theme: a far one (0 10 22) and a near one (0 1 3).</summary>
    private void UpdateShadow()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        Color color = dark ? Colors.Black : LightShadowColor;
        double opacity = dark ? 0.6 : 0.18;
        Resources["ToastFarShadow"] = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 10,
            BlurRadius = 22,
            Color = color,
            Opacity = opacity,
        };
        Resources["ToastNearShadow"] = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 1,
            BlurRadius = 3,
            Color = color,
            Opacity = opacity,
        };
    }

    /// <summary>True when <paramref name="point"/>, in the host's coordinates, lies on a card.</summary>
    /// <param name="point">The point, relative to the host's top-left corner.</param>
    public bool HasCardAt(Point point) =>
        Cards
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("card"))
            .Any(card => card.TranslatePoint(default, this) is { } origin && new Rect(origin, card.Bounds.Size).Contains(point));

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItem item } && DataContext is ToastCenter center)
        {
            center.Dismiss(item);
        }
    }
}

/// <summary>A toast's one line: its plain pieces as text, and its inline code in a rounded code box.</summary>
public sealed class ToastText : TextBlock
{
    /// <summary>Defines the <see cref="Segments"/> property.</summary>
    public static readonly StyledProperty<IReadOnlyList<ToastSegment>?> SegmentsProperty = AvaloniaProperty.Register<
        ToastText,
        IReadOnlyList<ToastSegment>?
    >(nameof(Segments));

    /// <summary>The narrowest a code box gets, however narrow the card is.</summary>
    private const double MinCodeWidth = 60;

    /// <summary>The left and right padding of a <c>Border.code</c>, which its text does not have.</summary>
    private const double CodeChrome = 10;

    /// <summary>The text of each inline code box, whose width is capped so the box keeps a visible piece of its name.</summary>
    private readonly List<TextBlock> _codeBlocks = [];

    /// <summary>The line's plain pieces joined, whose width the code boxes leave room for.</summary>
    private string _plainText = "";

    /// <summary>The pieces of the line, in order.</summary>
    public IReadOnlyList<ToastSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>
    /// Caps each code box at the width left once the plain pieces are paid for before laying the line out, so a long name is cut
    /// inside its box instead of the box collapsing as a whole or taking the line.
    /// </summary>
    /// <param name="availableSize">The width the card gives the line.</param>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && _codeBlocks.Count > 0)
        {
            double maxWidth = Math.Max(MinCodeWidth, (availableSize.Width - PlainWidth() - (_codeBlocks.Count * CodeChrome)) / _codeBlocks.Count);
            foreach (TextBlock block in _codeBlocks)
            {
                block.MaxWidth = maxWidth;
            }
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>The width the plain pieces take on one line, so the code boxes are capped to what is left of the line.</summary>
    private double PlainWidth()
    {
        if (_plainText.Length == 0)
        {
            return 0;
        }

        using var layout = new TextLayout(_plainText, new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground);
        return layout.WidthIncludingTrailingWhitespace;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
        {
            _codeBlocks.Clear();
            _plainText = string.Concat((Segments ?? []).Where(segment => !segment.IsCode).Select(segment => segment.Text));
            Inlines = InlinesOf(Segments ?? []);
        }
    }

    private InlineCollection InlinesOf(IReadOnlyList<ToastSegment> segments)
    {
        var inlines = new InlineCollection();
        foreach (ToastSegment segment in segments)
        {
            inlines.Add(segment.IsCode ? CodeBox(segment.Text) : new Run(segment.Text));
        }

        return inlines;
    }

    private InlineUIContainer CodeBox(string text)
    {
        var inner = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        _codeBlocks.Add(inner);
        var box = new Border { Child = inner };
        box.Classes.Add("code");
        return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
    }
}

/// <summary>A line along a toast's foot, as long as the share of its 5 s still to run.</summary>
public sealed class ToastTimerLine : Control
{
    /// <summary>Defines the <see cref="Fraction"/> property.</summary>
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<ToastTimerLine, double>(nameof(Fraction), 1);

    /// <summary>Defines the <see cref="Fill"/> property.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ToastTimerLine, IBrush?>(nameof(Fill));

    static ToastTimerLine()
    {
        AffectsRender<ToastTimerLine>(FractionProperty, FillProperty);
    }

    /// <summary>The share of the line drawn, from its left end: 1 is the whole width, 0 nothing.</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>The line's brush.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is { } fill)
        {
            context.FillRectangle(fill, new Rect(0, 0, Bounds.Width * Math.Clamp(Fraction, 0, 1), Bounds.Height));
        }
    }
}
