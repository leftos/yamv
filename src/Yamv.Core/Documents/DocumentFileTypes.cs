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
