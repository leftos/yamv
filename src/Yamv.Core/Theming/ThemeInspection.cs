namespace Yamv.Core.Theming;

/// <summary>What a theme file holds, read for the Import notice without copying it anywhere.</summary>
/// <param name="FileName">The file's name, without its folder.</param>
/// <param name="Name">The theme's name, or null when the file is not a valid theme.</param>
/// <param name="Author">The theme's author, or null when it names none or the file is not a valid theme.</param>
/// <param name="HasLight">Whether the theme has a light half.</param>
/// <param name="HasDark">Whether the theme has a dark half.</param>
/// <param name="HasCss">Whether the shared, light or dark css holds anything but white space.</param>
/// <param name="Errors">Every error that would make the import fail; empty when it would succeed.</param>
/// <param name="Warnings">Every warning the theme file gives.</param>
public sealed record ThemeInspection(
    string FileName,
    string? Name,
    string? Author,
    bool HasLight,
    bool HasDark,
    bool HasCss,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
);
