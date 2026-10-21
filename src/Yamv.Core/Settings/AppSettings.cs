using System.Diagnostics.CodeAnalysis;
using Yamv.Core.Theming;

namespace Yamv.Core.Settings;

/// <summary>The app-wide settings: the theme every window shows and the light or dark preference.</summary>
/// <param name="ThemeId">The id of the theme the app uses; whether it still exists is the app's business.</param>
/// <param name="Appearance">One of <see cref="SystemAppearance"/>, <see cref="LightAppearance"/> or <see cref="DarkAppearance"/>.</param>
public sealed record AppSettings(string ThemeId, string Appearance)
{
    /// <summary>The appearance that follows the platform's light or dark preference.</summary>
    public const string SystemAppearance = "system";

    /// <summary>The appearance that always uses the theme's light colours.</summary>
    public const string LightAppearance = "light";

    /// <summary>The appearance that always uses the theme's dark colours.</summary>
    public const string DarkAppearance = "dark";

    /// <summary>The first-run settings: the default theme, following the system.</summary>
    public static AppSettings Default { get; } = new(ThemeCatalog.DefaultThemeId, SystemAppearance);

    /// <summary>Whether <paramref name="appearance"/> is one of the three values.</summary>
    internal static bool IsAppearance([NotNullWhen(true)] string? appearance) => appearance is SystemAppearance or LightAppearance or DarkAppearance;
}
