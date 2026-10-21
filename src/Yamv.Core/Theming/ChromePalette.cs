namespace Yamv.Core.Theming;

/// <summary>The colours and font the app chrome (title bar, sidebar, panes) is drawn with, taken from a resolved theme.</summary>
/// <param name="Colors">Each chrome colour token's value as <c>0xAARRGGBB</c>, keyed by token name.</param>
/// <param name="UiFont">The font family of the chrome's text.</param>
public sealed record ChromePalette(IReadOnlyDictionary<string, uint> Colors, string UiFont)
{
    private const string FallbackUiFont = "Inter";

    /// <summary>The colour tokens the chrome uses.</summary>
    public static IReadOnlyList<string> ColorTokens { get; } =
    [
        "chromeBackground",
        "sidebarBackground",
        "pageBackground",
        "codeBackground",
        "text",
        "textSecondary",
        "textMuted",
        "heading",
        "accent",
        "accentSoft",
        "line",
        "selectionBackground",
        "selectionText",
        "hoverBackground",
        "warning",
        "warningBackground",
        "warningLine",
    ];

    /// <summary>
    /// Reads the chrome colours and font from <paramref name="theme"/>. The font falls back to Inter when the theme's
    /// <c>uiFont</c> is absent or blank.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A chrome colour token is missing or is not a colour; a resolved theme sets every token.
    /// </exception>
    public static ChromePalette From(ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var colors = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (string token in ColorTokens)
        {
            if (!theme.Tokens.TryGetValue(token, out ThemeValue? value) || value is not ColorValue color)
            {
                throw new InvalidOperationException($"The resolved theme '{theme.Id}' has no colour for the chrome token '{token}'.");
            }

            colors[token] = color.ToArgb();
        }

        string font =
            theme.Tokens.GetValueOrDefault("uiFont") is FontValue { Family: { } family } && !string.IsNullOrWhiteSpace(family)
                ? family
                : FallbackUiFont;
        return new ChromePalette(colors, font);
    }
}
