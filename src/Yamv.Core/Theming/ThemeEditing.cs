namespace Yamv.Core.Theming;

/// <summary>The theme editor's edits: each returns a new <see cref="ThemeDefinition"/> and leaves the one given unchanged.</summary>
public static class ThemeEditing
{
    /// <summary>Sets <paramref name="name"/> to <paramref name="value"/> in the layer <paramref name="tab"/> edits.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a token in <see cref="ThemeTokens.All"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="tab"/> is a half the theme lacks; add it first.</exception>
    /// <exception cref="ThemeException">The value is not valid for the token, worded as the theme file parser words it.</exception>
    public static ThemeDefinition SetToken(ThemeDefinition theme, ThemeTab tab, string name, ThemeValue value)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(value);
        TokenInfo token = ThemeTokens.Find(name) ?? throw new ArgumentException($"'{name}' is not a theme token.", nameof(name));
        string? problem = ThemeParser.ProblemWith(token, value, Where(tab));
        if (problem is not null)
        {
            throw new ThemeException(problem, [problem]);
        }

        return WithTokens(theme, tab, tokens => tokens[name] = value);
    }

    /// <summary>
    /// Removes <paramref name="name"/> from the layer <paramref name="tab"/> edits, so it inherits again. A token the layer does not
    /// hold returns <paramref name="theme"/> itself.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="tab"/> is a half the theme lacks.</exception>
    public static ThemeDefinition ResetToken(ThemeDefinition theme, ThemeTab tab, string name)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (ThemeTokens.Find(name) is null)
        {
            throw new ArgumentException($"'{name}' is not a theme token.", nameof(name));
        }

        IReadOnlyDictionary<string, ThemeValue> layer = tab == ThemeTab.Shared ? theme.SharedTokens : RequireVariant(theme, tab).Tokens;
        return layer.ContainsKey(name) ? WithTokens(theme, tab, tokens => tokens.Remove(name)) : theme;
    }

    /// <summary>Adds an empty light or dark half, so every token in it inherits until set.</summary>
    /// <exception cref="InvalidOperationException">The theme already has that half.</exception>
    public static ThemeDefinition AddVariant(ThemeDefinition theme, Appearance appearance)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (VariantOf(theme, appearance) is not null)
        {
            throw new InvalidOperationException($"The theme '{theme.Id}' already has a {Where(appearance)} half.");
        }

        var empty = new ThemeVariant(new Dictionary<string, ThemeValue>(StringComparer.Ordinal), "");
        return appearance == Appearance.Dark ? theme with { Dark = empty } : theme with { Light = empty };
    }

    /// <summary>Gives the theme a new display name; the file keeps its id (<see cref="ThemeCatalog.Rename"/> moves the file).</summary>
    /// <exception cref="ThemeException"><paramref name="name"/> is empty or white space.</exception>
    public static ThemeDefinition Rename(ThemeDefinition theme, string name)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrWhiteSpace(name))
        {
            const string error = "The theme has no 'name'.";
            throw new ThemeException(error, [error]);
        }

        return theme with
        {
            Name = name,
        };
    }

    private static ThemeDefinition WithTokens(ThemeDefinition theme, ThemeTab tab, Action<Dictionary<string, ThemeValue>> edit)
    {
        if (tab == ThemeTab.Shared)
        {
            var shared = new Dictionary<string, ThemeValue>(theme.SharedTokens, StringComparer.Ordinal);
            edit(shared);
            return theme with { SharedTokens = shared };
        }

        ThemeVariant variant = RequireVariant(theme, tab);
        var tokens = new Dictionary<string, ThemeValue>(variant.Tokens, StringComparer.Ordinal);
        edit(tokens);
        ThemeVariant edited = variant with { Tokens = tokens };
        return tab == ThemeTab.Dark ? theme with { Dark = edited } : theme with { Light = edited };
    }

    private static ThemeVariant RequireVariant(ThemeDefinition theme, ThemeTab tab)
    {
        Appearance appearance = tab == ThemeTab.Dark ? Appearance.Dark : Appearance.Light;
        return VariantOf(theme, appearance)
            ?? throw new InvalidOperationException($"The theme '{theme.Id}' has no {Where(appearance)} half; add it before editing it.");
    }

    private static ThemeVariant? VariantOf(ThemeDefinition theme, Appearance appearance) => appearance == Appearance.Dark ? theme.Dark : theme.Light;

    private static string Where(ThemeTab tab) =>
        tab switch
        {
            ThemeTab.Shared => "shared",
            ThemeTab.Light => "light",
            ThemeTab.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, "Unknown theme tab."),
        };

    private static string Where(Appearance appearance) => appearance == Appearance.Dark ? "dark" : "light";
}
