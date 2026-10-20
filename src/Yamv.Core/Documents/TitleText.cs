using Yamv.Core.Grouping;

namespace Yamv.Core.Documents;

/// <summary>The document name and location a window's title bar shows.</summary>
public static class TitleText
{
    private const string NoDocumentName = "YAMV";
    private const string Separator = " / ";

    /// <summary>
    /// The title bar's two texts for a document: its file name, and the group name followed by each folder between the
    /// group root and the document's folder. A window with no document shows the app name and no location; a document
    /// with no group, or one outside its group root, shows its name only.
    /// </summary>
    /// <param name="documentPath">The document's full path, or null when the window holds none.</param>
    /// <param name="group">The group that holds the document, or null when it is in none.</param>
    public static (string Name, string Location) For(string? documentPath, GroupNode? group)
    {
        if (string.IsNullOrEmpty(documentPath))
        {
            return (NoDocumentName, string.Empty);
        }

        string name = Path.GetFileName(documentPath);
        if (name.Length == 0)
        {
            name = documentPath;
        }

        if (group is null)
        {
            return (name, string.Empty);
        }

        List<string>? folders = Folders(documentPath, group.Path);
        return folders is null ? (name, string.Empty) : (name, string.Join(Separator, [group.Name, .. folders]));
    }

    private static List<string>? Folders(string documentPath, string groupRoot)
    {
        string? folder = Path.GetDirectoryName(documentPath);
        if (folder is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(groupRoot, folder);
        if (IsOutside(relative))
        {
            return null;
        }

        return
        [
            .. relative
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part != "."),
        ];
    }

    private static bool IsOutside(string relative) =>
        Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
