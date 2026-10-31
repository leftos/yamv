namespace Yamv.Core.Documents;

/// <summary>
/// The file types YAMV opens in its own window, in one list the pickers, drag-drop and links all share: markdown, MDX and
/// plain text.
/// </summary>
public static class DocumentFileTypes
{
    /// <summary>The extensions a document may have, with the leading dot, in the order the pickers list them.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".md", ".markdown", ".mdx", ".txt"];

    /// <summary>The picker patterns of <see cref="Extensions"/>, such as <c>*.md</c>.</summary>
    public static IReadOnlyList<string> Patterns { get; } = [.. Extensions.Select(extension => "*" + extension)];

    /// <summary>What a drag holding no document is told, naming every one of <see cref="Extensions"/>.</summary>
    public static string DropHint { get; } = $"Drop {string.Join(", ", Extensions.SkipLast(1))} or {Extensions[^1]} files";

    /// <summary>
    /// Splits dropped paths into the documents, in drop order, and a count of the rest. A folder is never a document, whatever
    /// its name. This touches the disk, so a slow drive makes it slow.
    /// </summary>
    /// <param name="paths">The dropped paths; an empty string stands for a dropped file with no local path.</param>
    public static DroppedFiles Sort(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> documents = [.. paths.Where(path => IsDocument(path) && !Directory.Exists(path))];
        return new DroppedFiles(documents, paths.Count - documents.Count);
    }

    /// <summary>
    /// Whether <paramref name="path"/> has one of <see cref="Extensions"/>, compared case-insensitively whatever the platform.
    /// A path with no extension, or one that ends in a dot, is not a document. The file need not exist.
    /// </summary>
    /// <param name="path">The file path to test.</param>
    public static bool IsDocument(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        return extension.Length > 1 && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What <see cref="DocumentFileTypes.Sort"/> made of a drop.</summary>
/// <param name="Documents">The paths that open as documents, in drop order.</param>
/// <param name="Refused">How many of the dropped paths do not.</param>
public sealed record DroppedFiles(IReadOnlyList<string> Documents, int Refused);
