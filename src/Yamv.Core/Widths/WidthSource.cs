namespace Yamv.Core.Widths;

/// <summary>Where a resolved width comes from: the folder that holds it, or the app default.</summary>
/// <param name="Folder">The folder as it is stored in the widths file, or <see langword="null"/> for the app default.</param>
public readonly record struct WidthSource(string? Folder)
{
    /// <summary>The app default, used when no folder at or above the document has a width.</summary>
    public static WidthSource AppDefault => default;

    public bool IsAppDefault => Folder is null;
}
