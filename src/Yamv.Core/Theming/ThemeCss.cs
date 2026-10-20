using System.Globalization;
using System.Text;

namespace Yamv.Core.Theming;

/// <summary>Builds the stylesheet the document page reads: every token as a <c>--yamv-*</c> custom property, then the theme's css.</summary>
public static class ThemeCss
{
    private const string BundledSans = "Inter V";
    private const string BundledMono = "JetBrains Mono V";

    /// <summary>
    /// The <c>:root</c> block followed by the theme's css, safe to place inside a <c>&lt;style&gt;</c> element:
    /// every <c>&lt;/</c> is written as <c>&lt;\/</c>, so nothing in a theme can close the element.
    /// </summary>
    public static string Build(ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var builder = new StringBuilder(":root {\n");
        foreach (TokenInfo token in ThemeTokens.All)
        {
            builder.Append("  ").Append(token.CssVariable).Append(": ").Append(Format(token, theme.Tokens[token.Name])).Append(";\n");
        }

        builder.Append("  color-scheme: ").Append(theme.Appearance == Appearance.Dark ? "dark" : "light").Append(";\n}\n");
        builder.Append(theme.Css);
        return builder.ToString().Replace("</", "<\\/", StringComparison.Ordinal);
    }

    private static string Format(TokenInfo token, ThemeValue value) =>
        value switch
        {
            ColorValue color => color.Hex,
            FontValue font => FontStack(token, font.Family),
            NumberValue number => number.Value.ToString(CultureInfo.InvariantCulture) + token.Unit,
            BoolValue flag => flag.Value ? "1px solid var(--yamv-line)" : "none",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown theme value type."),
        };

    private static string FontStack(TokenInfo token, string family) =>
        token.Name == "monoFont" ? $"{Quote(family)}, \"{BundledMono}\", monospace" : $"{Quote(family)}, \"{BundledSans}\", sans-serif";

    /// <summary>A CSS string: <c>"</c> and <c>\</c> backslash-escaped, control characters (below 0x20, and 0x7F) as <c>\HH </c>.</summary>
    private static string Quote(string family)
    {
        var builder = new StringBuilder("\"");
        foreach (char character in family)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\').Append(character);
            }
            else if (character < 0x20 || character == 0x7F)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\{(int)character:x2} ");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.Append('"').ToString();
    }
}
