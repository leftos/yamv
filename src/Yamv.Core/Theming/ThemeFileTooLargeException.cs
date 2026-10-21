namespace Yamv.Core.Theming;

/// <summary>A theme file larger than <see cref="ThemeCatalog.MaxFileBytes"/>, which no theme file is.</summary>
internal sealed class ThemeFileTooLargeException()
    : IOException($"The file is larger than {ThemeCatalog.MaxFileSizeText}; a theme file is a few KB.");
