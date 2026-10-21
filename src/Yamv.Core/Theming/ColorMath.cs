using System.Globalization;

namespace Yamv.Core.Theming;

/// <summary>A colour as hue, saturation and value.</summary>
/// <param name="H">The hue in degrees, 0 to 360; 360 is the same red as 0.</param>
/// <param name="S">The saturation, 0 (grey) to 1.</param>
/// <param name="V">The value, 0 (black) to 1.</param>
public readonly record struct Hsv(double H, double S, double V);

/// <summary>
/// Conversions between <see cref="Hsv"/>, RGB bytes and <see cref="ColorValue"/>, for the colour picker. A grey has no hue, so
/// <see cref="ToHsv(byte, byte, byte, double)"/> takes the hue to give it from the caller: the picker's hue bar stays where it was.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// The RGB bytes of <paramref name="hsv"/>. A hue outside 0 to 360 wraps round; a saturation or value outside 0 to 1 is
    /// clamped to it.
    /// </summary>
    public static (byte R, byte G, byte B) ToRgb(Hsv hsv)
    {
        double hue = hsv.H % 360;
        if (hue < 0)
        {
            hue += 360;
        }

        double value = Math.Clamp(hsv.V, 0, 1);
        double chroma = value * Math.Clamp(hsv.S, 0, 1);
        double sector = hue / 60;
        double middle = chroma * (1 - Math.Abs((sector % 2) - 1));
        double floor = value - chroma;
        (double r, double g, double b) = Math.Min((int)sector, 5) switch
        {
            0 => (chroma, middle, 0d),
            1 => (middle, chroma, 0d),
            2 => (0d, chroma, middle),
            3 => (0d, middle, chroma),
            4 => (middle, 0d, chroma),
            _ => (chroma, 0d, middle),
        };
        return (ToByte(r + floor), ToByte(g + floor), ToByte(b + floor));
    }

    /// <summary>The hue, saturation and value of an RGB colour; a grey (no saturation) gets <paramref name="greyHue"/>.</summary>
    public static Hsv ToHsv(byte r, byte g, byte b, double greyHue)
    {
        double red = r / 255d;
        double green = g / 255d;
        double blue = b / 255d;
        double max = Math.Max(red, Math.Max(green, blue));
        double delta = max - Math.Min(red, Math.Min(green, blue));
        if (delta == 0)
        {
            return new Hsv(greyHue, 0, max);
        }

        double sector =
            max == red ? (((green - blue) / delta) + 6) % 6
            : max == green ? ((blue - red) / delta) + 2
            : ((red - green) / delta) + 4;
        return new Hsv(sector * 60, delta / max, max);
    }

    /// <summary>The hue, saturation and value of <paramref name="color"/>, without its alpha; a grey gets <paramref name="greyHue"/>.</summary>
    public static Hsv ToHsv(ColorValue color, double greyHue)
    {
        ArgumentNullException.ThrowIfNull(color);
        return ToHsv(color.R, color.G, color.B, greyHue);
    }

    /// <summary><paramref name="hsv"/> with <paramref name="alpha"/> as a colour value.</summary>
    public static ColorValue FromHsv(Hsv hsv, byte alpha)
    {
        (byte r, byte g, byte b) = ToRgb(hsv);
        return FromRgba(r, g, b, alpha);
    }

    /// <summary>The colour value of four bytes: lower-case <c>#rrggbb</c> when opaque, else <c>#rrggbbaa</c>.</summary>
    public static ColorValue FromRgba(byte r, byte g, byte b, byte a) =>
        new(
            a == byte.MaxValue
                ? string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}")
                : string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}{a:x2}")
        );

    private static byte ToByte(double channel) => (byte)Math.Round(channel * 255, MidpointRounding.AwayFromZero);
}
