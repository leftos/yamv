namespace Yamv.Core.Widths;

/// <summary>One segment of the content-width popover's Saved for control.</summary>
/// <param name="Label">What the segment shows.</param>
/// <param name="Folder">The folder a width set here is saved for, or <see langword="null"/> for All documents.</param>
/// <param name="IsSelected">True for the segment the slider edits.</param>
public sealed record WidthScope(string Label, string? Folder, bool IsSelected)
{
    /// <summary>True for All documents, the app default.</summary>
    public bool IsAllDocuments => Folder is null;
}

/// <summary>The Saved for segments of one document's content width. Pure: it never reads the file system.</summary>
public sealed class WidthScopes
{
    /// <summary>The label of the app default's segment.</summary>
    public const string AllDocumentsLabel = "All documents";

    private readonly string _documentFolder;

    private WidthScopes(IReadOnlyList<WidthScope> segments, WidthSource selectedSource, string clearAllRoot, string documentFolder)
    {
        Segments = segments;
        SelectedSource = selectedSource;
        ClearAllRoot = clearAllRoot;
        _documentFolder = documentFolder;
    }

    /// <summary>The segments, deepest folder first and All documents last.</summary>
    public IReadOnlyList<WidthScope> Segments { get; }

    /// <summary>The segment the slider edits.</summary>
    public WidthScope Selected => Segments.First(segment => segment.IsSelected);

    /// <summary>Where the selected segment's width comes from.</summary>
    public WidthSource SelectedSource { get; }

    /// <summary>The folder Clear all overrides here clears at and below: the group root.</summary>
    public string ClearAllRoot { get; }

    /// <summary>
    /// Where the selected segment inherits its width from: the folder's name, spelled as in the document's path (the group
    /// root without its "(repo)" mark), or All documents for the app default. <see langword="null"/> when the selected segment
    /// holds the width itself.
    /// </summary>
    public string? InheritedFrom
    {
        get
        {
            if (SameFolder(Selected.Folder, SelectedSource.Folder))
            {
                return null;
            }

            return SelectedSource.Folder is string folder ? FolderName(Spelled(folder, _documentFolder)) : AllDocumentsLabel;
        }
    }

    /// <summary>True when Reset to inherited has an override to remove: the selected segment is a folder with its own width.</summary>
    public bool CanReset => Selected.Folder is not null && SameFolder(Selected.Folder, SelectedSource.Folder);

    /// <summary>
    /// The segments for a document whose width resolves from <paramref name="source"/>, that source selected: the document's
    /// folder; the source's folder when it lies between the document's folder and the group root; the group root unless it is
    /// the document's folder; the source's folder when it lies above the group root; then All documents.
    /// </summary>
    /// <param name="documentPath">The document's full path.</param>
    /// <param name="groupRoot">The root of the document's group; for a viewer window, the document's own folder.</param>
    /// <param name="groupIsRepo">True when the group root is a git work tree, which its label says.</param>
    /// <param name="source">Where the document's width comes from.</param>
    /// <exception cref="ArgumentException">
    /// A path is not a full path, the group root is not at or above the document's folder, or the source is not.
    /// </exception>
    public static WidthScopes For(string documentPath, string groupRoot, bool groupIsRepo, WidthSource source)
    {
        string document = WidthKeys.Normalize(documentPath, nameof(documentPath));
        string folder =
            Path.GetDirectoryName(document) ?? throw new ArgumentException($"'{documentPath}' is a root, not a document.", nameof(documentPath));
        string root = WidthKeys.Normalize(groupRoot, nameof(groupRoot));
        if (!WidthKeys.IsAtOrBelow(folder, root))
        {
            throw new ArgumentException($"The group root '{groupRoot}' is not at or above the folder of '{documentPath}'.", nameof(groupRoot));
        }

        WidthSource normalizedSource = NormalizeSource(source, folder);
        List<string> folders = FolderOrder(folder, root, normalizedSource.Folder);
        List<string> spelled = [.. folders.Select(candidate => Spelled(candidate, folder))];
        List<WidthScope> segments = [];
        for (int index = 0; index < folders.Count; index++)
        {
            string label = Label(spelled, index, root, groupIsRepo);
            segments.Add(new WidthScope(label, folders[index], SameFolder(folders[index], normalizedSource.Folder)));
        }

        segments.Add(new WidthScope(AllDocumentsLabel, Folder: null, normalizedSource.IsAppDefault));
        return new WidthScopes(segments, normalizedSource, root, folder);
    }

    /// <summary>These segments with <paramref name="segment"/> selected, its width coming from <paramref name="segmentSource"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="segment"/> is not one of <see cref="Segments"/>, or <paramref name="segmentSource"/> is not at or above it.
    /// </exception>
    public WidthScopes Select(WidthScope segment, WidthSource segmentSource)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!Contains(segment))
        {
            throw new ArgumentException($"The segment '{segment.Label}' is not one of these segments.", nameof(segment));
        }

        WidthSource normalizedSource = segment.Folder is string folder ? NormalizeSource(segmentSource, folder) : WidthSource.AppDefault;
        List<WidthScope> segments = [.. Segments.Select(candidate => candidate with { IsSelected = SameFolder(candidate.Folder, segment.Folder) })];
        return new WidthScopes(segments, normalizedSource, ClearAllRoot, _documentFolder);
    }

    /// <summary>True when one of <see cref="Segments"/> is for the same folder as <paramref name="segment"/>, or both are All documents.</summary>
    public bool Contains(WidthScope segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return Segments.Any(candidate => SameFolder(candidate.Folder, segment.Folder));
    }

    /// <summary>
    /// True when a width set on the selected segment changes the document's width: no folder nearer the document than the
    /// selected segment holds a width of its own. <paramref name="documentSource"/> is where the document's width comes from.
    /// </summary>
    public bool Reaches(WidthSource documentSource)
    {
        if (documentSource.Folder is not string source)
        {
            return true;
        }

        string? key = WidthKeys.TryNormalize(source);
        return Selected.Folder is string selected && key is not null && WidthKeys.IsAtOrBelow(selected, key);
    }

    /// <summary>The folder segments in path order, deepest first.</summary>
    private static List<string> FolderOrder(string folder, string root, string? source)
    {
        (string? between, string? above) = SourceSegment(folder, root, source);
        List<string> folders = [folder];
        if (between is not null)
        {
            folders.Add(between);
        }

        if (!SameFolder(folder, root))
        {
            folders.Add(root);
        }

        if (above is not null)
        {
            folders.Add(above);
        }

        return folders;
    }

    /// <summary>
    /// Where the source gets a segment of its own: between the document's folder and the group root, or above the group root.
    /// Neither when it is the app default, the document's folder or the group root.
    /// </summary>
    private static (string? Between, string? Above) SourceSegment(string folder, string root, string? source)
    {
        if (source is null || SameFolder(source, folder) || SameFolder(source, root))
        {
            return (null, null);
        }

        return WidthKeys.IsAtOrBelow(source, root) ? (source, null) : (null, source);
    }

    /// <summary>
    /// The label of <c>folders[index]</c>: the group root by its name, a folder below it by its path below the next segment up,
    /// and a folder above it by its name.
    /// </summary>
    private static string Label(List<string> folders, int index, string root, bool groupIsRepo)
    {
        string folder = folders[index];
        if (SameFolder(folder, root))
        {
            return groupIsRepo ? $"{FolderName(folder)} (repo)" : FolderName(folder);
        }

        return WidthKeys.IsAtOrBelow(folder, root) ? RelativeLabel(folder, folders[index + 1]) : FolderName(folder);
    }

    /// <summary>
    /// <paramref name="folder"/> spelled as the document's folder spells it, when it is at or above that folder; else as given.
    /// </summary>
    private static string Spelled(string folder, string documentFolder) =>
        WidthKeys.IsAtOrBelow(documentFolder, folder) && folder.Length <= documentFolder.Length ? documentFolder[..folder.Length] : folder;

    /// <summary><paramref name="source"/> with its folder as a key, checked to be at or above <paramref name="folder"/>.</summary>
    private static WidthSource NormalizeSource(WidthSource source, string folder)
    {
        if (source.Folder is null)
        {
            return source;
        }

        string key = WidthKeys.Normalize(source.Folder, nameof(source));
        if (!WidthKeys.IsAtOrBelow(folder, key))
        {
            throw new ArgumentException($"The width source '{source.Folder}' is not at or above '{folder}'.", nameof(source));
        }

        return new WidthSource(key);
    }

    /// <summary><paramref name="folder"/> as a path below <paramref name="ancestor"/>, its parts joined by <c>/</c>.</summary>
    private static string RelativeLabel(string folder, string ancestor) =>
        folder[ancestor.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The folder's own name; a file-system root, which has none, is named by its path.</summary>
    private static string FolderName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    private static bool SameFolder(string? first, string? second) =>
        first is null ? second is null : second is not null && AppPaths.PathComparer.Equals(first, second);
}
