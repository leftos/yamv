using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class SessionSnapshotTests : IDisposable
{
    private static readonly string Repo = Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", "dev", "yamv");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string SessionFile => Path.Combine(_directory, "session.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Build_UsesTheDocumentsInListOrderAndTheSelection()
    {
        string first = Path.Combine(Repo, "a.md");
        string second = Path.Combine(Repo, "b.md");
        DocumentWindowState state = CreateState();
        state.Add([first, second]);

        SessionState snapshot = SessionSnapshot.Build(state, new Dictionary<string, double>(), null, 280, "studio", "system");

        string[] expected = [first, second];
        Assert.Equal(expected, snapshot.OpenFiles);
        Assert.Equal(first, snapshot.SelectedFile);
    }

    [Fact]
    public void Build_DropsScrollPositionsOfDocumentsTheWindowDoesNotHold()
    {
        string open = Path.Combine(Repo, "a.md");
        string closed = Path.Combine(Repo, "b.md");
        DocumentWindowState state = CreateState();
        state.Add([open]);
        var scroll = new Dictionary<string, double> { [open] = 0.5, [closed] = 0.9 };

        SessionState snapshot = SessionSnapshot.Build(state, scroll, null, 280, "studio", "system");

        Assert.Equal(0.5, snapshot.ScrollPositions[open]);
        Assert.False(snapshot.ScrollPositions.ContainsKey(closed));
    }

    [Fact]
    public void Build_FiltersScrollPositionsWithTheStatesComparer()
    {
        string open = Path.Combine(Repo, "a.md");
        var state = new DocumentWindowState(true, _ => Repo, _ => true, new GroupingOptions(null, StringComparison.Ordinal));
        state.Add([open]);
        var scroll = new Dictionary<string, double> { [open.ToUpperInvariant()] = 0.5 };

        SessionState snapshot = SessionSnapshot.Build(state, scroll, null, 280, "studio", "system");

        Assert.Empty(snapshot.ScrollPositions);
    }

    [Fact]
    public void Build_CarriesCollapsedGroupsSidebarWidthAndTheHiddenFlag()
    {
        DocumentWindowState state = CreateState();
        state.Add([Path.Combine(Repo, "a.md")]);
        state.SetGroupExpanded(Repo, expanded: false);
        state.ToggleSidebar();
        WindowBounds bounds = new(10, 20, 1200, 800, IsMaximized: true);

        SessionState snapshot = SessionSnapshot.Build(state, new Dictionary<string, double>(), bounds, 320, "noir", "dark");

        string[] groups = [Repo];
        Assert.Equal(groups, snapshot.CollapsedGroups);
        Assert.Equal(320, snapshot.SidebarWidth);
        Assert.True(snapshot.SidebarHidden);
        Assert.Equal(bounds, snapshot.Window);
        Assert.Equal("noir", snapshot.ThemeId);
        Assert.Equal("dark", snapshot.Appearance);
    }

    [Fact]
    public void SaveThenLoad_GivesTheSameState()
    {
        string first = Path.Combine(Repo, "docs", "a.md");
        string second = Path.Combine(Repo, "root.md");
        DocumentWindowState state = CreateState();
        state.Add([first, second]);
        state.Select(second);
        state.SetGroupExpanded(Repo, expanded: false);
        state.ToggleSidebar();
        WindowBounds bounds = new(10, 20, 1200, 800, IsMaximized: true);
        var scroll = new Dictionary<string, double> { [first] = 0.25, [second] = 0.75 };

        SessionState snapshot = SessionSnapshot.Build(state, scroll, bounds, 320, "noir", "dark");
        new SessionStore(SessionFile, _log).Save(snapshot);

        SessionState loaded = new SessionStore(SessionFile, _log).Load();

        Assert.Equal(snapshot.OpenFiles, loaded.OpenFiles);
        Assert.Equal(snapshot.SelectedFile, loaded.SelectedFile);
        Assert.Equal(snapshot.ScrollPositions.Count, loaded.ScrollPositions.Count);
        foreach ((string path, double position) in snapshot.ScrollPositions)
        {
            Assert.Equal(position, loaded.ScrollPositions[path]);
        }

        Assert.Equal(snapshot.Window, loaded.Window);
        Assert.Equal(snapshot.SidebarWidth, loaded.SidebarWidth);
        Assert.Equal(snapshot.SidebarHidden, loaded.SidebarHidden);
        Assert.Equal(snapshot.CollapsedGroups, loaded.CollapsedGroups);
        Assert.Equal(snapshot.ThemeId, loaded.ThemeId);
        Assert.Equal(snapshot.Appearance, loaded.Appearance);
        Assert.Empty(_log.Entries);
    }

    private static DocumentWindowState CreateState() =>
        new(
            true,
            path => path.StartsWith(Repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Repo : null,
            _ => true,
            new GroupingOptions(null, StringComparison.OrdinalIgnoreCase)
        );
}
