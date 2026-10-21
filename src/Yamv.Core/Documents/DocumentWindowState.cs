using Yamv.Core.Grouping;

namespace Yamv.Core.Documents;

/// <summary>
/// One document window's state: the documents it holds, which one is selected, which groups are collapsed, whether the
/// window persists and whether its sidebar is hidden. Every operation that changes the state raises
/// <see cref="Changed"/> exactly once.
/// </summary>
public sealed class DocumentWindowState
{
    private readonly bool persist;
    private readonly GroupingOptions groupingOptions;
    private readonly Func<string, string?> repoFinder;
    private readonly Func<string, bool> existsOnDisk;
    private readonly StringComparison pathComparison;
    private readonly StringComparer pathComparer;
    private readonly List<string> documents = [];
    private readonly HashSet<string> openPaths;
    private readonly HashSet<string> collapsedGroups;
    private readonly HashSet<string> missing;
    private IReadOnlyList<GroupNode> groups = [];
    private DocumentTree tree = DocumentTree.Empty;
    private string? selectedPath;
    private bool windowShouldClose;

    /// <summary>Creates the state of a window that holds no documents.</summary>
    /// <param name="persist">True for the main window, which saves its session; false for a viewer window.</param>
    /// <param name="findRepoRoot">Reports the git work-tree root that contains a file, or null when it is in none.</param>
    /// <param name="fileExists">
    /// Reports whether a document is still on disk. Only adding a document, replacing it with another,
    /// <see cref="RefreshMissing()"/> (for every document or for some) and <see cref="ExistsOnDisk"/> ask it; every other
    /// operation rebuilds the tree from the answers it already has.
    /// </param>
    /// <param name="options">The home folder and path comparison the document tree is built with.</param>
    public DocumentWindowState(bool persist, Func<string, string?> findRepoRoot, Func<string, bool> fileExists, GroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(findRepoRoot);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(options);
        this.persist = persist;
        groupingOptions = options;
        repoFinder = findRepoRoot;
        existsOnDisk = fileExists;
        pathComparison = options.PathComparison;
        pathComparer = StringComparer.FromComparison(options.PathComparison);
        openPaths = new HashSet<string>(pathComparer);
        collapsedGroups = new HashSet<string>(pathComparer);
        missing = new HashSet<string>(pathComparer);
    }

    /// <summary>Raised once after each operation that changed the state.</summary>
    public event EventHandler? Changed;

    /// <summary>True for the main window, which saves its session; false for a viewer window.</summary>
    public bool Persist => persist;

    /// <summary>True when the sidebar is hidden.</summary>
    public bool SidebarHidden { get; private set; }

    /// <summary>The open documents' full paths, in the order they were opened.</summary>
    public IReadOnlyList<string> Documents => documents;

    /// <summary>How the window compares two document paths.</summary>
    public StringComparer PathComparer => pathComparer;

    /// <summary>The selected document's full path, or null when no document is selected.</summary>
    public string? SelectedPath => selectedPath;

    /// <summary>The root paths of the collapsed groups, in ordinal order.</summary>
    public IReadOnlyList<string> CollapsedGroups => [.. collapsedGroups.OrderBy(path => path, StringComparer.Ordinal)];

    /// <summary>The groups the tree is built from, as the grouper built them.</summary>
    public IReadOnlyList<GroupNode> Groups => groups;

    /// <summary>The rows the window's tree shows.</summary>
    public DocumentTree Tree => tree;

    /// <summary>
    /// True when the window shows its tree: it holds two or more documents, or it persists and holds at least one. An empty
    /// main window shows only its empty state.
    /// </summary>
    public bool ShowTree => documents.Count >= 2 || (persist && documents.Count > 0);

    /// <summary>True when the window holds no document.</summary>
    public bool IsEmpty => documents.Count == 0;

    /// <summary>True when the last Close emptied a window that does not persist, so the window closes too.</summary>
    public bool WindowShouldClose => windowShouldClose;

    /// <summary>
    /// Adds <paramref name="paths"/>, normalized and de-duplicated, and selects the first one given, expanding the group
    /// that holds it. A path that is already open is selected rather than added again.
    /// </summary>
    public void Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string? first = null;
        List<string> added = [];
        foreach (string rawPath in paths)
        {
            string path = Path.GetFullPath(rawPath);
            first ??= path;
            if (!openPaths.Add(path))
            {
                continue;
            }

            documents.Add(path);
            added.Add(path);
        }

        if (first is null)
        {
            return;
        }

        if (added.Count > 0)
        {
            ProbeMissing(added);
            windowShouldClose = false;
            Rebuild();
        }

        bool changed = SetSelection(StoredPath(first) ?? first);
        if (added.Count == 0 && !changed)
        {
            return;
        }

        Rebuild();
        RaiseChanged();
    }

    /// <summary>Selects an open document, expanding the group that holds it.</summary>
    public void Select(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? stored = StoredPath(Path.GetFullPath(path));
        if (stored is null || !SetSelection(stored))
        {
            return;
        }

        Rebuild();
        RaiseChanged();
    }

    /// <summary>
    /// Closes a document, selecting the next document in tree order or, when it was the last, the previous one, and
    /// expanding the group that holds it. A group left with no document disappears from the tree and is forgotten as a
    /// collapsed group.
    /// </summary>
    public void Close(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? stored = StoredPath(Path.GetFullPath(path));
        if (stored is null)
        {
            return;
        }

        bool wasSelected = SamePath(selectedPath, stored);
        string? emptiedGroup = GroupOf(stored)?.Path;
        string? next = NeighbouringPath([stored]);
        RemoveDocument(stored);
        Rebuild();
        if (wasSelected)
        {
            SetSelection(next);
        }

        PruneEmptiedGroup(emptiedGroup);
        UpdateWindowShouldClose();
        Rebuild();
        RaiseChanged();
    }

    /// <summary>Closes every document of a group, selecting as <see cref="Close"/> does when the selection was in it.</summary>
    public void CloseGroup(string groupPath)
    {
        ArgumentNullException.ThrowIfNull(groupPath);
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(groupPath));
        GroupItem? item = tree.Items.OfType<GroupItem>().FirstOrDefault(group => string.Equals(group.Path, normalized, pathComparison));
        if (item is null)
        {
            return;
        }

        List<string> members =
        [
            .. tree.Documents.Where(document => string.Equals(document.GroupPath, item.Path, pathComparison)).Select(document => document.Path),
        ];
        if (members.Count == 0)
        {
            return;
        }

        bool selectionInside = selectedPath is not null && members.Contains(selectedPath, pathComparer);
        string? next = NeighbouringPath(members);
        foreach (string member in members)
        {
            RemoveDocument(member);
        }

        Rebuild();
        if (selectionInside)
        {
            SetSelection(next);
        }

        PruneEmptiedGroup(item.Path);
        UpdateWindowShouldClose();
        Rebuild();
        RaiseChanged();
    }

    /// <summary>Selects the next document in tree order, wrapping around to the first.</summary>
    public void SelectNext() => Step(1);

    /// <summary>Selects the previous document in tree order, wrapping around to the last.</summary>
    public void SelectPrevious() => Step(-1);

    /// <summary>Hides the sidebar when it is shown, and shows it when it is hidden.</summary>
    public void ToggleSidebar()
    {
        SidebarHidden = !SidebarHidden;
        RaiseChanged();
    }

    /// <summary>Expands or collapses a group, recording the choice for the session.</summary>
    public void SetGroupExpanded(string groupPath, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(groupPath);
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(groupPath));
        bool changed = expanded ? collapsedGroups.Remove(normalized) : collapsedGroups.Add(normalized);
        if (!changed)
        {
            return;
        }

        Rebuild();
        RaiseChanged();
    }

    /// <summary>
    /// Replaces a document's path, keeping its position in the list. A path that is already open as another document
    /// replaces that entry instead, so the list never holds it twice. The selection follows the document when it was
    /// selected, and the group that held the old path is forgotten when the replacement empties it.
    /// </summary>
    public void Replace(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        string from = Path.GetFullPath(oldPath);
        string to = Path.GetFullPath(newPath);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }

        int index = IndexOfDocument(from);
        if (index < 0)
        {
            return;
        }

        string? emptiedGroup = GroupOf(from)?.Path;
        int duplicate = IndexOfDocument(to);
        if (duplicate >= 0 && duplicate != index)
        {
            RemoveDocument(documents[duplicate]);
            if (duplicate < index)
            {
                index--;
            }
        }

        bool wasSelected = SamePath(selectedPath, from);
        openPaths.Remove(from);
        documents[index] = to;
        openPaths.Add(to);
        missing.Remove(from);
        ProbeMissing([to]);
        Rebuild();
        if (wasSelected)
        {
            SetSelection(to);
        }

        PruneEmptiedGroup(emptiedGroup);
        Rebuild();
        RaiseChanged();
    }

    /// <summary>Re-reads which documents are on disk, and returns whether any document's missing flag changed.</summary>
    public bool RefreshMissing() => Refresh(documents);

    /// <summary>
    /// Re-reads whether the given open documents are on disk, leaving every other document's missing flag as it is, and returns
    /// whether any flag changed. A path that is not open is ignored.
    /// </summary>
    /// <param name="paths">The full paths of the documents to check.</param>
    public bool RefreshMissing(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Refresh([.. paths.Where(openPaths.Contains)]);
    }

    /// <summary>
    /// Asks whether <paramref name="path"/> is on disk now, leaving its missing flag and every other part of the state as it
    /// is, and raising no <see cref="Changed"/>.
    /// </summary>
    /// <param name="path">The full path of the document to check.</param>
    public bool ExistsOnDisk(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return existsOnDisk(path);
    }

    private bool Refresh(IEnumerable<string> paths)
    {
        HashSet<string> before = MissingDocuments();
        ProbeMissing(paths);
        Rebuild();
        bool changed = !before.SetEquals(MissingDocuments());
        if (changed)
        {
            RaiseChanged();
        }

        return changed;
    }

    /// <summary>Finds the group that holds a document, or null when the document is not open.</summary>
    public GroupNode? GroupOf(string documentPath)
    {
        ArgumentNullException.ThrowIfNull(documentPath);
        string normalized = Path.GetFullPath(documentPath);
        string? groupPath = tree.Documents.FirstOrDefault(document => string.Equals(document.Path, normalized, pathComparison))?.GroupPath;
        return groupPath is null ? null : groups.FirstOrDefault(group => string.Equals(group.Path, groupPath, pathComparison));
    }

    /// <summary>Sets the selected document, expanding the group that holds it, and reports whether anything changed.</summary>
    private bool SetSelection(string? path)
    {
        bool changed = !SamePath(selectedPath, path);
        selectedPath = path;
        if (path is null)
        {
            return changed;
        }

        GroupNode? group = GroupOf(path);
        if (group is not null && collapsedGroups.Remove(group.Path))
        {
            changed = true;
        }

        return changed;
    }

    private string? NeighbouringPath(IReadOnlyList<string> block)
    {
        IReadOnlyList<string> order = tree.DocumentOrder;
        int first = -1;
        int last = -1;
        for (int index = 0; index < order.Count; index++)
        {
            if (block.Contains(order[index], pathComparer))
            {
                first = first < 0 ? index : first;
                last = index;
            }
        }

        return last < 0 ? null : FirstOutside(order, block, last + 1, 1) ?? FirstOutside(order, block, first - 1, -1);
    }

    private string? FirstOutside(IReadOnlyList<string> order, IReadOnlyList<string> block, int start, int step)
    {
        for (int index = start; index >= 0 && index < order.Count; index += step)
        {
            if (!block.Contains(order[index], pathComparer))
            {
                return order[index];
            }
        }

        return null;
    }

    private void Step(int delta)
    {
        IReadOnlyList<string> order = tree.DocumentOrder;
        if (order.Count == 0)
        {
            return;
        }

        int current = selectedPath is null ? -1 : IndexOfOrder(order, selectedPath);
        int index = current < 0 ? (delta > 0 ? 0 : order.Count - 1) : (current + delta + order.Count) % order.Count;
        Select(order[index]);
    }

    private void Rebuild()
    {
        groups = DocumentGrouper.Group(documents, repoFinder, groupingOptions);
        tree = DocumentTree.Build(groups, path => !missing.Contains(path), collapsedGroups);
    }

    private void ProbeMissing(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (existsOnDisk(path))
            {
                missing.Remove(path);
            }
            else
            {
                missing.Add(path);
            }
        }
    }

    private HashSet<string> MissingDocuments()
    {
        HashSet<string> missingDocuments = new(pathComparer);
        foreach (DocumentItem document in tree.Documents)
        {
            if (document.IsMissing)
            {
                missingDocuments.Add(document.Path);
            }
        }

        return missingDocuments;
    }

    private void PruneEmptiedGroup(string? groupPath)
    {
        if (groupPath is null || groups.Any(group => string.Equals(group.Path, groupPath, pathComparison)))
        {
            return;
        }

        collapsedGroups.Remove(groupPath);
    }

    private void UpdateWindowShouldClose() => windowShouldClose = !persist && documents.Count == 0;

    private bool SamePath(string? first, string? second) => first is not null && second is not null && string.Equals(first, second, pathComparison);

    private string? StoredPath(string normalizedPath) => documents.FirstOrDefault(path => string.Equals(path, normalizedPath, pathComparison));

    private int IndexOfDocument(string normalizedPath) => documents.FindIndex(path => string.Equals(path, normalizedPath, pathComparison));

    private int IndexOfOrder(IReadOnlyList<string> order, string path)
    {
        for (int index = 0; index < order.Count; index++)
        {
            if (string.Equals(order[index], path, pathComparison))
            {
                return index;
            }
        }

        return -1;
    }

    private void RemoveDocument(string path)
    {
        int index = IndexOfDocument(path);
        if (index >= 0)
        {
            documents.RemoveAt(index);
        }

        openPaths.Remove(path);
        missing.Remove(path);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
