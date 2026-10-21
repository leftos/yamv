namespace Yamv.Core.Theming;

/// <summary>What a theme file holds, read for the Import notice without copying it anywhere.</summary>
/// <param name="FileName">The file's name, without its folder.</param>
/// <param name="FilePath">The file's path, as it was given to <see cref="ThemeCatalog.Inspect"/>.</param>
/// <param name="Name">The theme's name, or null when the file is not a valid theme.</param>
/// <param name="Author">The theme's author, or null when it names none or the file is not a valid theme.</param>
/// <param name="HasLight">Whether the theme has a light half.</param>
/// <param name="HasDark">Whether the theme has a dark half.</param>
/// <param name="HasCss">Whether the shared, light or dark css holds anything but white space.</param>
/// <param name="Errors">Every error that would make the import fail; empty when it would succeed.</param>
/// <param name="Warnings">Every warning the theme file gives.</param>
/// <param name="AddedAsName">
/// The name the theme is imported under when its id is taken, "<c>&lt;Name&gt; (2)</c>" for <c>-2</c>; null when the id is free
/// or the file is not a valid theme.
/// </param>
/// <param name="LightChip">The light half's chip colours, or null when the theme has no light half or the file is not a valid theme.</param>
/// <param name="DarkChip">The dark half's chip colours, or null when the theme has no dark half or the file is not a valid theme.</param>
public sealed record ThemeInspection(
    string FileName,
    string FilePath,
    string? Name,
    string? Author,
    bool HasLight,
    bool HasDark,
    bool HasCss,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? AddedAsName,
    ThemeChip? LightChip,
    ThemeChip? DarkChip
);

/// <summary>
/// The two colours a theme half's chip shows: its page background and accent, each the half's own value, else the shared one, else
/// the default for that appearance.
/// </summary>
public sealed record ThemeChip(ColorValue PageBackground, ColorValue Accent);
