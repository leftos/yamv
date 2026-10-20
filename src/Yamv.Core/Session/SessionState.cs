namespace Yamv.Core.Session;

/// <summary>
/// The state YAMV restores on the next launch: which files are open, where each one is scrolled,
/// and how the window and sidebar were left.
/// </summary>
public sealed record SessionState
{
    /// <summary>The first-run state: nothing open, default sidebar and theme.</summary>
    public static SessionState Empty { get; } = new();

    /// <summary>The full paths of the open files, in tab order.</summary>
    public IReadOnlyList<string> OpenFiles { get; init; } = [];

    /// <summary>The full path of the file the viewer shows, or null when it shows none.</summary>
    public string? SelectedFile { get; init; }

    /// <summary>Scroll position per full file path, as a fraction of the document from 0 to 1.</summary>
    public IReadOnlyDictionary<string, double> ScrollPositions { get; init; } = new Dictionary<string, double>();

    /// <summary>The window geometry to restore, or null when nothing was saved.</summary>
    public WindowBounds? Window { get; init; }

    /// <summary>The sidebar width in device-independent pixels.</summary>
    public double SidebarWidth { get; init; } = 280;

    /// <summary>The root paths of the tree groups the user collapsed.</summary>
    public IReadOnlyList<string> CollapsedGroups { get; init; } = [];

    /// <summary>The chosen theme: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; init; } = "system";
}
