using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yamv.Core.Theming;
using AvaloniaColor = Avalonia.Media.Color;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>
/// The colour picker's body: a saturation and value square over the hue, the hue and alpha bars, the old and new colours side
/// by side with the hex field, and the caption naming the colour it opened with. Every change (a press or drag on the square
/// or a bar, a full <c>#rrggbb</c> or <c>#rrggbbaa</c> typed) raises <see cref="ColorChanged"/> at once; <c>#rgb</c> applies on
/// Enter, and an invalid hex shows its error and raises nothing. Esc raises <see cref="CancelRequested"/> and Enter, with a
/// valid hex, <see cref="AcceptRequested"/>.
/// </summary>
public partial class ColorPickerPopup : UserControl
{
    private const double KnobRadius = 7;

    /// <summary>The length of <c>#rgb</c>, which the hex field applies only on Enter: it is also the start of a longer hex.</summary>
    private const int ShortHexLength = 4;

    private Hsv _hsv;
    private byte _alpha = byte.MaxValue;
    private bool _writingHex;

    public ColorPickerPopup()
    {
        InitializeComponent();
        foreach (Panel area in new[] { PickerSquare, PickerHue, PickerAlpha })
        {
            area.PointerPressed += OnAreaPressed;
            area.PointerMoved += OnAreaMoved;
            area.PointerReleased += OnAreaReleased;
            area.SizeChanged += (_, _) => PlaceKnobs();
        }

        PickerHex.TextChanged += OnHexChanged;
        AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The colour shown as new.</summary>
    public ColorValue Color { get; private set; } = new("#000000");

    /// <summary>The colour changed by a press, a drag or a valid hex.</summary>
    public event EventHandler<ColorValue>? ColorChanged;

    /// <summary>Esc was pressed in the picker.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Enter was pressed in the picker while its hex is valid.</summary>
    public event EventHandler? AcceptRequested;

    /// <summary>Shows <paramref name="was"/> as both the old and the new colour, and names it in the caption.</summary>
    public void Show(ColorValue was)
    {
        ArgumentNullException.ThrowIfNull(was);
        _hsv = ColorMath.ToHsv(was, greyHue: 0);
        _alpha = was.A;
        Color = was;
        PickerOld.Background = new SolidColorBrush(ToAvalonia(was));
        PickerCaption.Text = $"Was {was.Hex}. Esc puts it back.";
        ShowColor(writeHex: true);
    }

    /// <summary>Focuses the hex field with its text selected.</summary>
    public void FocusHex()
    {
        PickerHex.Focus();
        PickerHex.SelectAll();
    }

    /// <summary>Where <paramref name="position"/> lies in an area of <paramref name="size"/>, as fractions from 0 to 1 of each side.</summary>
    private static (double X, double Y) FractionOf(Point position, Size size) =>
        (size.Width > 0 ? Math.Clamp(position.X / size.Width, 0, 1) : 0, size.Height > 0 ? Math.Clamp(position.Y / size.Height, 0, 1) : 0);

    private static AvaloniaColor ToAvalonia(ColorValue color) => AvaloniaColor.FromUInt32(color.ToArgb());

    private void OnAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel area || !e.GetCurrentPoint(area).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(area);
        e.Handled = true;
        if (!IsKeyboardFocusWithin)
        {
            PickerHex.Focus();
        }

        Pick(area, e.GetPosition(area));
    }

    private void OnAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Panel area && ReferenceEquals(e.Pointer.Captured, area))
        {
            Pick(area, e.GetPosition(area));
        }
    }

    private void OnAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Panel area && ReferenceEquals(e.Pointer.Captured, area))
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <summary>Takes the colour under <paramref name="position"/> in <paramref name="area"/>: the square, the hue bar or the alpha bar.</summary>
    private void Pick(Panel area, Point position)
    {
        (double x, double y) = FractionOf(position, area.Bounds.Size);
        if (ReferenceEquals(area, PickerSquare))
        {
            _hsv = _hsv with { S = x, V = 1 - y };
        }
        else if (ReferenceEquals(area, PickerHue))
        {
            _hsv = _hsv with { H = x * 360 };
        }
        else
        {
            _alpha = (byte)Math.Round(x * byte.MaxValue, MidpointRounding.AwayFromZero);
        }

        Change(ColorMath.FromHsv(_hsv, _alpha), writeHex: true);
    }

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_writingHex)
        {
            return;
        }

        string text = (PickerHex.Text ?? "").Trim();
        if (ColorValue.Normalize(text) is not { } hex)
        {
            PickerError.Text = ColorValue.NotAColour(text);
            PickerError.IsVisible = true;
            return;
        }

        PickerError.IsVisible = false;
        if (text.Length != ShortHexLength)
        {
            ApplyHex(hex);
        }
    }

    /// <summary>Takes a typed hex as the colour, keeping the hue when it is a grey.</summary>
    private void ApplyHex(string hex)
    {
        var color = new ColorValue(hex);
        _hsv = ColorMath.ToHsv(color, greyHue: _hsv.H);
        _alpha = color.A;
        Change(color, writeHex: false);
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ColorValue.Normalize((PickerHex.Text ?? "").Trim()) is { } hex)
            {
                ApplyHex(hex);
                AcceptRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Change(ColorValue color, bool writeHex)
    {
        bool changed = color != Color;
        Color = color;
        ShowColor(writeHex);
        if (changed)
        {
            ColorChanged?.Invoke(this, color);
        }
    }

    /// <summary>Shows <see cref="Color"/> and the hue in the boxes, the gradients and the knobs, and clears any error.</summary>
    private void ShowColor(bool writeHex)
    {
        AvaloniaColor color = ToAvalonia(Color);
        AvaloniaColor hue = ToAvalonia(ColorMath.FromHsv(_hsv with { S = 1, V = 1 }, byte.MaxValue));
        PickerNew.Background = new SolidColorBrush(color);
        ((LinearGradientBrush)SquareHue.Background!).GradientStops[1].Color = hue;
        GradientStops ramp = ((LinearGradientBrush)AlphaRamp.Background!).GradientStops;
        ramp[0].Color = AvaloniaColor.FromArgb(0, color.R, color.G, color.B);
        ramp[1].Color = AvaloniaColor.FromArgb(byte.MaxValue, color.R, color.G, color.B);
        SquareKnob.Background = new SolidColorBrush(color);
        HueKnob.Background = new SolidColorBrush(hue);
        AlphaKnob.Background = new SolidColorBrush(color);
        PickerError.IsVisible = false;
        if (writeHex)
        {
            _writingHex = true;
            PickerHex.Text = Color.Hex;
            _writingHex = false;
        }

        PlaceKnobs();
    }

    private void PlaceKnobs()
    {
        Size square = PickerSquare.Bounds.Size;
        Canvas.SetLeft(SquareKnob, (_hsv.S * square.Width) - KnobRadius);
        Canvas.SetTop(SquareKnob, ((1 - _hsv.V) * square.Height) - KnobRadius);
        Canvas.SetLeft(HueKnob, (_hsv.H / 360 * PickerHue.Bounds.Width) - KnobRadius);
        Canvas.SetLeft(AlphaKnob, (_alpha / 255d * PickerAlpha.Bounds.Width) - KnobRadius);
    }
}
