namespace Yamv.Core.Theming;

/// <summary>A tab of the theme editor: the tokens both halves share, or the tokens of one half.</summary>
public enum ThemeTab
{
    Shared,
    Light,
    Dark,
}

/// <summary>Where a token's resolved value comes from.</summary>
public enum TokenLayer
{
    /// <summary>The built-in default (Studio) for the appearance.</summary>
    Default,

    /// <summary>The theme's shared tokens.</summary>
    Shared,

    /// <summary>The theme's light or dark half.</summary>
    Variant,
}

/// <summary>One token as a tab of the theme editor shows it.</summary>
/// <param name="Token">The token, with its friendly label.</param>
/// <param name="Value">The value the token resolves to in this tab.</param>
/// <param name="Layer">The layer that set <paramref name="Value"/>.</param>
/// <param name="SetHere">Whether the tab's own layer holds the token, so it can be reset; otherwise the value is inherited.</param>
public sealed record ThemeTokenRow(TokenInfo Token, ThemeValue Value, TokenLayer Layer, bool SetHere);

/// <summary>Builds the token rows of one theme editor tab.</summary>
public static class ThemeTokenView
{
    /// <summary>
    /// One row per token in <see cref="ThemeTokens.All"/> order. The Shared tab layers the defaults of <paramref name="shown"/>, the
    /// appearance the app shows now, under the shared tokens. The Light and Dark tabs layer that appearance's defaults, the shared
    /// tokens and that half, as <see cref="ThemeResolver.Resolve"/> does, and ignore <paramref name="shown"/>; a half the theme lacks
    /// gives rows that are all inherited.
    /// </summary>
    public static IReadOnlyList<ThemeTokenRow> Build(ThemeDefinition theme, ThemeTab tab, Appearance shown)
    {
        ArgumentNullException.ThrowIfNull(theme);
        (Appearance appearance, ThemeVariant? variant, TokenLayer own) = tab switch
        {
            ThemeTab.Shared => (shown, null, TokenLayer.Shared),
            ThemeTab.Light => (Appearance.Light, theme.Light, TokenLayer.Variant),
            ThemeTab.Dark => (Appearance.Dark, theme.Dark, TokenLayer.Variant),
            _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, "Unknown theme tab."),
        };
        Dictionary<string, (ThemeValue Value, TokenLayer Layer)> layered = ThemeResolver.Layer(appearance, theme.SharedTokens, variant?.Tokens);
        return
        [
            .. ThemeTokens.All.Select(token =>
            {
                (ThemeValue value, TokenLayer layer) = layered[token.Name];
                return new ThemeTokenRow(token, value, layer, layer == own);
            }),
        ];
    }
}
