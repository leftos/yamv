namespace Yamv.Core.Theming;

/// <summary>Where a theme came from.</summary>
public enum ThemeSource
{
    BuiltIn,
    User,
}

/// <summary>Which variant of a theme family is shown.</summary>
public enum Appearance
{
    Light,
    Dark,
}

/// <summary>The light or dark half of a theme family, holding only the tokens the file sets.</summary>
public sealed record ThemeVariant(IReadOnlyDictionary<string, ThemeValue> Tokens, string Css);

/// <summary>A parsed theme file. Token dictionaries hold only what the file sets; <see cref="ThemeResolver"/> fills in the rest.</summary>
public sealed record ThemeDefinition(
    string Id,
    string Name,
    string? Author,
    string? Description,
    ThemeSource Source,
    string? FilePath,
    IReadOnlyDictionary<string, ThemeValue> SharedTokens,
    string SharedCss,
    ThemeVariant? Light,
    ThemeVariant? Dark
)
{
    /// <summary>
    /// Whether <paramref name="other"/> holds the same name, author, description, tokens and css, whatever its id, source and file;
    /// record equality compares the token dictionaries by reference, so two reads of one file are not <c>==</c>.
    /// </summary>
    public bool ContentEquals(ThemeDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Name == other.Name
            && Author == other.Author
            && Description == other.Description
            && SharedCss == other.SharedCss
            && SameTokens(SharedTokens, other.SharedTokens)
            && SameVariant(Light, other.Light)
            && SameVariant(Dark, other.Dark);
    }

    /// <summary>Whether two token sets hold the same tokens with equal values.</summary>
    internal static bool SameTokens(IReadOnlyDictionary<string, ThemeValue> a, IReadOnlyDictionary<string, ThemeValue> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out ThemeValue? other) && Equals(pair.Value, other));

    private static bool SameVariant(ThemeVariant? a, ThemeVariant? b) =>
        a is null || b is null ? a is null && b is null : a.Css == b.Css && SameTokens(a.Tokens, b.Tokens);
}

/// <summary>The outcome of reading a theme file: the theme when it is valid, and every error and warning found.</summary>
public sealed record ThemeLoadResult(ThemeDefinition? Theme, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>A theme with every token set, for one appearance.</summary>
public sealed record ResolvedTheme(string Id, string Name, Appearance Appearance, IReadOnlyDictionary<string, ThemeValue> Tokens, string Css);

/// <summary>
/// A theme the catalog lists. Invalid themes are listed too, with <see cref="ThemeLoadResult.Theme"/> null. Ids can repeat: an invalid
/// user file that shadows a built-in is listed beside the built-in. Look a theme up by id preferring the valid entry.
/// </summary>
public sealed record ThemeEntry(string Id, ThemeSource Source, string? FilePath, ThemeLoadResult Result);

/// <summary>A theme file that cannot be used, with every error that makes it so.</summary>
public sealed class ThemeException : Exception
{
    public ThemeException(string message, IReadOnlyList<string> errors)
        : base(message) => Errors = errors;

    public ThemeException(string message, IReadOnlyList<string> errors, Exception innerException)
        : base(message, innerException) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}
