using System.Globalization;

namespace Yamv.Core.Theming;

/// <summary>The value of one theme token.</summary>
public abstract record ThemeValue;

/// <summary>
/// A colour, held normalised as lower-case <c>#rrggbb</c> or <c>#rrggbbaa</c>. <see cref="Hex"/> is in CSS order, alpha last;
/// Avalonia's <c>Color.Parse</c> reads eight digits as <c>#aarrggbb</c>, so the app chrome builds its colours from
/// <see cref="R"/>, <see cref="G"/>, <see cref="B"/> and <see cref="A"/> or <see cref="ToArgb"/>, never from <see cref="Hex"/>.
/// </summary>
/// <remarks><c>#rgb</c> expands to <c>#rrggbb</c>. Anything else throws <see cref="ArgumentException"/>.</remarks>
public sealed record ColorValue(string Hex) : ThemeValue
{
    public string Hex { get; } = Normalize(Hex) ?? throw new ArgumentException(NotAColour(Hex), nameof(Hex));

    public byte R => Channel(0);

    public byte G => Channel(1);

    public byte B => Channel(2);

    /// <summary>The alpha channel: the last two digits of an eight-digit <see cref="Hex"/>, else 255 (opaque).</summary>
    public byte A => Hex.Length == 9 ? Channel(3) : byte.MaxValue;

    /// <summary>The colour as <c>0xAARRGGBB</c>.</summary>
    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>The message for <paramref name="text"/> that is not <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.</summary>
    public static string NotAColour(string text) => $"'{text}' is not a colour (#rgb, #rrggbb or #rrggbbaa).";

    private byte Channel(int index) => byte.Parse(Hex.AsSpan(1 + (index * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the normalised form of <paramref name="text"/>, or null when it is not <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.
    /// </summary>
    public static string? Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is not (4 or 7 or 9) || text[0] != '#' || !text.Skip(1).All(char.IsAsciiHexDigit))
        {
            return null;
        }

        string digits = text[1..].ToLowerInvariant();
        if (digits.Length == 3)
        {
            digits = string.Create(CultureInfo.InvariantCulture, $"{digits[0]}{digits[0]}{digits[1]}{digits[1]}{digits[2]}{digits[2]}");
        }

        return "#" + digits;
    }
}

/// <summary>A font family name, tried as named before the role's bundled fallback.</summary>
public sealed record FontValue(string Family) : ThemeValue;

/// <summary>A number; its unit (px, em or none) comes from the token.</summary>
public sealed record NumberValue(double Value) : ThemeValue;

/// <summary>An on/off switch.</summary>
public sealed record BoolValue(bool Value) : ThemeValue;
