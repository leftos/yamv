using Yamv.Core.Documents;

namespace Yamv.Core.Session;

/// <summary>Turns a document window's live state into the state the session file stores.</summary>
public static class SessionSnapshot
{
    /// <summary>
    /// Builds the session state for <paramref name="state"/>: its documents in list order, its selection, the scroll
    /// positions of the documents it holds, its collapsed groups, the sidebar width and hidden flag.
    /// </summary>
    /// <param name="state">The window's live state.</param>
    /// <param name="scrollPositions">
    /// Scroll positions per full path, as a fraction of the document; entries for documents the window does not hold are dropped.
    /// </param>
    /// <param name="bounds">The window's restored rectangle, or null when none is known.</param>
    /// <param name="sidebarWidth">The sidebar width in device-independent pixels.</param>
    public static SessionState Build(
        DocumentWindowState state,
        IReadOnlyDictionary<string, double> scrollPositions,
        WindowBounds? bounds,
        double sidebarWidth
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scrollPositions);

        var open = new HashSet<string>(state.Documents, state.PathComparer);
        Dictionary<string, double> positions = new(state.PathComparer);
        foreach ((string path, double position) in scrollPositions)
        {
            if (open.Contains(path))
            {
                positions[path] = position;
            }
        }

        return new SessionState
        {
            OpenFiles = [.. state.Documents],
            SelectedFile = state.SelectedPath,
            ScrollPositions = positions,
            Window = bounds,
            SidebarWidth = sidebarWidth,
            SidebarHidden = state.SidebarHidden,
            CollapsedGroups = [.. state.CollapsedGroups],
        };
    }
}
