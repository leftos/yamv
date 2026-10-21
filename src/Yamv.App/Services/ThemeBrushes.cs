using Avalonia.Controls;
using Avalonia.Media;
using Yamv.Core.Theming;

namespace Yamv.App.Services;

/// <summary>
/// Builds the Avalonia brushes and fonts of a resolved theme: one <see cref="SolidColorBrush"/> per colour token under
/// <see cref="ThemeBridge.ResourceKey"/>, built from the colour's channels and never by parsing its hex, and font families with
/// their bundled fallbacks.
/// </summary>
public static class ThemeBrushes
{
    private const string BundledSansFont = "fonts:Inter#Inter";
    private const string MonoFallbacks = "Cascadia Mono, Consolas, Menlo, monospace";

    /// <summary>
    /// Writes a brush for every chrome colour token of <paramref name="resolved"/> and the UI font under
    /// <see cref="ThemeBridge.UiFontKey"/> into <paramref name="resources"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A chrome colour token is missing or is not a colour.</exception>
    public static void FillChrome(IResourceDictionary resources, ResolvedTheme resolved)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resolved);
        var palette = ChromePalette.From(resolved);
        foreach ((string token, uint argb) in palette.Colors)
        {
            resources[ThemeBridge.ResourceKey(token)] = new SolidColorBrush(Color.FromUInt32(argb));
        }

        resources[ThemeBridge.UiFontKey] = SansFont(palette.UiFont);
    }

    /// <summary>The brush of the colour token <paramref name="token"/> of <paramref name="resolved"/>.</summary>
    /// <exception cref="InvalidOperationException">The token is missing or is not a colour.</exception>
    public static SolidColorBrush Brush(ResolvedTheme resolved, string token)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return resolved.Tokens.GetValueOrDefault(token) is ColorValue color
            ? new SolidColorBrush(Color.FromUInt32(color.ToArgb()))
            : throw new InvalidOperationException($"The resolved theme '{resolved.Id}' has no colour for the token '{token}'.");
    }

    /// <summary>The font token <paramref name="token"/> of <paramref name="resolved"/>, falling back to the bundled Inter.</summary>
    public static FontFamily SansFont(ResolvedTheme resolved, string token) => SansFont(FamilyOf(resolved, token) ?? "Inter");

    /// <summary>The font token <paramref name="token"/> of <paramref name="resolved"/>, falling back to the platform's monospace fonts.</summary>
    public static FontFamily MonoFont(ResolvedTheme resolved, string token) =>
        new(FamilyOf(resolved, token) is { } family ? $"{family}, {MonoFallbacks}" : MonoFallbacks);

    private static FontFamily SansFont(string family) => new($"{family}, {BundledSansFont}");

    private static string? FamilyOf(ResolvedTheme resolved, string token)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Tokens.GetValueOrDefault(token) is FontValue { Family: { } family } && !string.IsNullOrWhiteSpace(family) ? family : null;
    }
}
