namespace Yamv.Core.Theming;

/// <summary>Turns a theme family into the full token set for one appearance.</summary>
public static class ThemeResolver
{
    /// <summary>
    /// Picks the <paramref name="requested"/> variant, or the only one the theme has, then layers the defaults for that variant's
    /// appearance, the shared tokens and the variant's tokens. The css is the shared css followed by the variant's.
    /// </summary>
    public static ResolvedTheme Resolve(ThemeDefinition theme, Appearance requested)
    {
        ArgumentNullException.ThrowIfNull(theme);
        (ThemeVariant variant, Appearance appearance) = Pick(theme, requested);
        var tokens = new Dictionary<string, ThemeValue>(ThemeTokens.DefaultsFor(appearance), StringComparer.Ordinal);
        foreach ((string name, ThemeValue value) in theme.SharedTokens)
        {
            tokens[name] = value;
        }

        foreach ((string name, ThemeValue value) in variant.Tokens)
        {
            tokens[name] = value;
        }

        string css = string.Join("\n", new[] { theme.SharedCss.Trim(), variant.Css.Trim() }.Where(part => part.Length > 0));
        return new ResolvedTheme(theme.Id, theme.Name, appearance, tokens, css);
    }

    private static (ThemeVariant Variant, Appearance Appearance) Pick(ThemeDefinition theme, Appearance requested)
    {
        ThemeVariant? wanted = requested == Appearance.Dark ? theme.Dark : theme.Light;
        if (wanted is not null)
        {
            return (wanted, requested);
        }

        if (theme.Light is not null)
        {
            return (theme.Light, Appearance.Light);
        }

        return theme.Dark is not null
            ? (theme.Dark, Appearance.Dark)
            : throw new ArgumentException($"Theme '{theme.Id}' has neither a light nor a dark variant.", nameof(theme));
    }
}
