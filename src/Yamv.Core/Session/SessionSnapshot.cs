using Yamv.Core.Documents;

namespace Yamv.Core.Session;

/// <summary>Turns a document window's live state into the state the session file stores.</summary>
public static class SessionSnapshot
{
    /// <summary>
    /// Builds the session state for <paramref name="state"/>: its documents in list order, its selection, the scroll
    /// positions and anchors of the documents it holds, its collapsed groups, the sidebar width and hidden flag.
    /// </summary>
    /// <param name="state">The window's live state.</param>
    /// <param name="scrollPositions">
    /// Scroll positions per full path, as a fraction of the document; entries for documents the window does not hold are dropped.
    /// </param>
    /// <param name="scrollAnchors">
    /// Scroll anchors per full path; entries for documents the window does not hold are dropped.
    /// </param>
    /// <param name="bounds">The window's restored rectangle, or null when none is known.</param>
    /// <param name="sidebarWidth">The sidebar width in device-independent pixels.</param>
    public static SessionState Build(
        DocumentWindowState state,
        IReadOnlyDictionary<string, double> scrollPositions,
        IReadOnlyDictionary<string, ScrollAnchor> scrollAnchors,
        WindowBounds? bounds,
        double sidebarWidth
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scrollPositions);
        ArgumentNullException.ThrowIfNull(scrollAnchors);

        var open = new HashSet<string>(state.Documents, state.PathComparer);
        return new SessionState
        {
            OpenFiles = [.. state.Documents],
            SelectedFile = state.SelectedPath,
            ScrollPositions = OfOpenDocuments(scrollPositions, open),
            ScrollAnchors = OfOpenDocuments(scrollAnchors, open),
            Window = bounds,
            SidebarWidth = sidebarWidth,
            SidebarHidden = state.SidebarHidden,
            CollapsedGroups = [.. state.CollapsedGroups],
        };
    }

    private static Dictionary<string, T> OfOpenDocuments<T>(IReadOnlyDictionary<string, T> byPath, HashSet<string> open)
    {
        Dictionary<string, T> kept = new(open.Comparer);
        foreach ((string path, T value) in byPath)
        {
            if (open.Contains(path))
            {
                kept[path] = value;
            }
        }

        return kept;
    }
}
