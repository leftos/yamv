using Yamv.Core.Documents;
using Yamv.Core.Grouping;

namespace Yamv.Core.Tests.Documents;

public sealed class DocumentWindowStateTests
{
    private static readonly GroupingOptions Plain = new(null, StringComparison.OrdinalIgnoreCase);
    private static readonly string Repo = P("dev", "yamv");
    private static readonly string RepoDocA = Path.Combine(Repo, "docs", "a.md");
    private static readonly string RepoDocB = Path.Combine(Repo, "docs", "b.md");
    private static readonly string RepoRootDoc = Path.Combine(Repo, "root.md");
    private static readonly string Alpha = P("dev", "alpha");
    private static readonly string AlphaDoc = Path.Combine(Alpha, "x.md");

    [Fact]
    public void Add_TwoDocuments_SelectsTheFirstAndKeepsTheOrder()
    {
        DocumentWindowState state = Create();

        state.Add([RepoDocA, RepoRootDoc]);

        string[] expected = [RepoDocA, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(RepoDocA, state.SelectedPath);
    }

    [Fact]
    public void Add_PathAlreadyOpen_SelectsItWithoutAddingItTwice()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);

        state.Add([RepoRootDoc]);

        string[] expected = [RepoDocA, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(RepoRootDoc, state.SelectedPath);
    }

    [Fact]
    public void Add_PathAlreadySelected_RaisesNoChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Add([RepoDocA]);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Add_NewDocument_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Add([RepoDocA]);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Add_MissingDocument_FlagsItInTheTree()
    {
        DocumentWindowState state = Create(missing: [RepoDocA]);

        state.Add([RepoDocA, RepoRootDoc]);

        Assert.True(DocumentItemOf(state, RepoDocA).IsMissing);
        Assert.False(DocumentItemOf(state, RepoRootDoc).IsMissing);
    }

    [Fact]
    public void Add_DocumentInACollapsedGroup_ExpandsTheGroup()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        state.SetGroupExpanded(Repo, expanded: false);

        state.Add([RepoRootDoc]);

        Assert.True(RootGroup(state).IsExpanded);
    }

    [Fact]
    public void Select_DocumentInACollapsedGroup_ExpandsTheGroup()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        state.SetGroupExpanded(Repo, expanded: false);
        Assert.False(RootGroup(state).IsExpanded);

        state.Select(RepoRootDoc);

        Assert.True(RootGroup(state).IsExpanded);
    }

    [Fact]
    public void Select_SelectionAndExpansion_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        state.SetGroupExpanded(Repo, expanded: false);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Select(RepoRootDoc);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Select_DocumentThatIsNotOpen_ChangesNothing()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Select(Path.Combine(Repo, "other.md"));

        Assert.Equal(RepoDocA, state.SelectedPath);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Select_DoesNotCheckTheDisk()
    {
        var disk = new CountingFileSystem();
        var state = new DocumentWindowState(true, FindRepo, disk.Exists, Plain);
        state.Add([RepoDocA, RepoRootDoc]);
        disk.Reset();

        state.Select(RepoRootDoc);

        Assert.Equal(0, disk.Checks);
    }

    [Fact]
    public void GroupOf_DocumentInAGroup_ReturnsThatGroup()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);

        GroupNode? group = state.GroupOf(RepoDocA);

        Assert.NotNull(group);
        Assert.Equal(Repo, group.Path);
    }

    [Fact]
    public void Close_SelectedDocumentInTreeOrder_SelectsTheNext()
    {
        DocumentWindowState state = Create();
        state.Add([RepoRootDoc, RepoDocA, AlphaDoc]);
        state.Select(RepoDocA);

        state.Close(RepoDocA);

        Assert.Equal(RepoRootDoc, state.SelectedPath);
    }

    [Fact]
    public void Close_LastDocumentInTreeOrder_SelectsThePrevious()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc, AlphaDoc]);
        state.Select(RepoRootDoc);

        state.Close(RepoRootDoc);

        Assert.Equal(RepoDocA, state.SelectedPath);
    }

    [Fact]
    public void Close_OnlyDocument_LeavesNothingSelectedAndRemovesItsGroup()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);

        state.Close(RepoDocA);

        Assert.True(state.IsEmpty);
        Assert.Null(state.SelectedPath);
        Assert.Empty(state.Tree.Items);
    }

    [Fact]
    public void Close_UnselectedDocument_KeepsTheSelection()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);

        state.Close(RepoRootDoc);

        Assert.Equal(RepoDocA, state.SelectedPath);
    }

    [Fact]
    public void Close_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Close(RepoRootDoc);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Close_LandingInACollapsedGroup_ExpandsTheGroup()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoDocB]);
        state.SetGroupExpanded(Repo, expanded: false);

        state.Close(RepoDocA);

        Assert.True(RootGroup(state).IsExpanded);
    }

    [Fact]
    public void Close_GroupEmptied_IsRemovedFromTheCollapsedGroups()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        state.SetGroupExpanded(Repo, expanded: false);

        state.Close(RepoDocA);

        Assert.Empty(state.CollapsedGroups);
    }

    [Fact]
    public void Close_ViewerWindowEmptied_ClosesTheWindow()
    {
        DocumentWindowState state = Create(persist: false);
        state.Add([RepoDocA]);
        Assert.False(state.WindowShouldClose);

        state.Close(RepoDocA);

        Assert.True(state.WindowShouldClose);
    }

    [Fact]
    public void Close_MainWindowEmptied_KeepsItOpen()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);

        state.Close(RepoDocA);

        Assert.False(state.WindowShouldClose);
    }

    [Fact]
    public void Add_AfterAnEmptyingClose_ClearsWindowShouldClose()
    {
        DocumentWindowState state = Create(persist: false);
        state.Add([RepoDocA]);
        state.Close(RepoDocA);
        Assert.True(state.WindowShouldClose);

        state.Add([RepoDocA]);

        Assert.False(state.WindowShouldClose);
    }

    [Fact]
    public void CloseGroup_SelectsTheNextInTreeOrder()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoRootDoc, RepoDocA]);

        state.CloseGroup(Alpha);

        Assert.Equal(RepoDocA, state.SelectedPath);
    }

    [Fact]
    public void CloseGroup_SelectionOutside_KeepsIt()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);
        state.Select(RepoDocA);

        state.CloseGroup(Alpha);

        Assert.Equal(RepoDocA, state.SelectedPath);
    }

    [Fact]
    public void CloseGroup_LandingInACollapsedGroup_ExpandsTheGroup()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);
        state.SetGroupExpanded(Repo, expanded: false);

        state.CloseGroup(Alpha);

        Assert.True(RootGroup(state).IsExpanded);
    }

    [Fact]
    public void CloseGroup_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.CloseGroup(Alpha);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void CloseGroup_GroupEmptied_IsRemovedFromTheCollapsedGroups()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);
        state.SetGroupExpanded(Alpha, expanded: false);

        state.CloseGroup(Alpha);

        Assert.Empty(state.CollapsedGroups);
    }

    [Fact]
    public void CloseGroup_ViewerWindowEmptied_ClosesTheWindow()
    {
        DocumentWindowState state = Create(persist: false);
        state.Add([AlphaDoc]);
        Assert.False(state.WindowShouldClose);

        state.CloseGroup(Alpha);

        Assert.True(state.WindowShouldClose);
    }

    [Fact]
    public void SelectNext_FollowsTreeOrder()
    {
        DocumentWindowState state = Create();
        state.Add([RepoRootDoc, RepoDocA, AlphaDoc]);

        state.SelectNext();

        Assert.Equal(AlphaDoc, state.SelectedPath);
    }

    [Fact]
    public void SelectPrevious_FollowsTreeOrder()
    {
        DocumentWindowState state = Create();
        state.Add([RepoRootDoc, RepoDocA, AlphaDoc]);
        state.Select(AlphaDoc);

        state.SelectPrevious();

        Assert.Equal(RepoRootDoc, state.SelectedPath);
    }

    [Fact]
    public void SelectNext_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.SelectNext();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void SelectNext_DoesNotCheckTheDisk()
    {
        var disk = new CountingFileSystem();
        var state = new DocumentWindowState(true, FindRepo, disk.Exists, Plain);
        state.Add([RepoDocA, RepoRootDoc]);
        Assert.Equal(2, disk.Checks);
        disk.Reset();

        state.SelectNext();

        Assert.Equal(0, disk.Checks);
    }

    [Fact]
    public void SelectNext_WithNoDocuments_DoesNothing()
    {
        DocumentWindowState state = Create();

        state.SelectNext();
        state.SelectPrevious();

        Assert.Null(state.SelectedPath);
    }

    [Fact]
    public void ToggleSidebar_HidesThenShowsAndRaisesOneChangeEach()
    {
        DocumentWindowState state = Create();
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.ToggleSidebar();
        Assert.True(state.SidebarHidden);

        state.ToggleSidebar();
        Assert.False(state.SidebarHidden);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void SetGroupExpanded_Unchanged_RaisesNoChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.SetGroupExpanded(Repo, expanded: true);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void SetGroupExpanded_PathWithATrailingSeparator_IsNormalized()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);

        state.SetGroupExpanded(Repo + Path.DirectorySeparatorChar, expanded: false);

        Assert.False(RootGroup(state).IsExpanded);
        string[] expected = [Repo];
        Assert.Equal(expected, state.CollapsedGroups);
    }

    [Fact]
    public void SetGroupExpanded_GroupNotInTheTree_IsRememberedForTheRestore()
    {
        DocumentWindowState state = Create();

        state.SetGroupExpanded(Alpha, expanded: false);

        string[] expected = [Alpha];
        Assert.Equal(expected, state.CollapsedGroups);
    }

    [Fact]
    public void Replace_KeepsThePositionAndFollowsTheSelection()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);

        state.Replace(RepoDocA, RepoDocB);

        string[] expected = [RepoDocB, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(RepoDocB, state.SelectedPath);
    }

    [Fact]
    public void Replace_NewPathAlreadyOpen_DropsTheOtherEntry()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc, RepoDocB]);
        state.Select(RepoRootDoc);

        state.Replace(RepoDocA, RepoDocB);

        string[] expected = [RepoDocB, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(RepoRootDoc, state.SelectedPath);
    }

    [Fact]
    public void Replace_DuplicateBeforeTheReplacedIndex_KeepsThePosition()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocB, RepoRootDoc, RepoDocA]);
        state.Select(RepoDocA);

        state.Replace(RepoDocA, RepoDocB);

        string[] expected = [RepoRootDoc, RepoDocB];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(RepoDocB, state.SelectedPath);
    }

    [Fact]
    public void Replace_SamePath_ChangesNothingAndRaisesNothing()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Replace(RepoDocA, RepoDocA);

        string[] expected = [RepoDocA, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Replace_RaisesOneChange()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA, RepoRootDoc]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        state.Replace(RepoDocA, RepoDocB);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Replace_LandingInACollapsedGroup_ExpandsTheGroup()
    {
        DocumentWindowState state = Create();
        state.Add([RepoRootDoc]);
        state.SetGroupExpanded(Repo, expanded: false);

        state.Replace(RepoRootDoc, RepoDocA);

        Assert.True(RootGroup(state).IsExpanded);
    }

    [Fact]
    public void Replace_ChecksOnlyTheNewPath()
    {
        var disk = new CountingFileSystem();
        var state = new DocumentWindowState(true, FindRepo, disk.Exists, Plain);
        state.Add([RepoDocA, RepoRootDoc]);
        disk.Reset();

        state.Replace(RepoDocA, RepoDocB);

        Assert.Equal(1, disk.Checks);
    }

    [Fact]
    public void Replace_CaseOnlyRename_IsApplied()
    {
        DocumentWindowState state = Create();
        string upper = Path.Combine(Repo, "README.md");
        string lower = Path.Combine(Repo, "readme.md");
        state.Add([lower, RepoRootDoc]);

        state.Replace(lower, upper);

        string[] expected = [upper, RepoRootDoc];
        Assert.Equal(expected, state.Documents);
        Assert.Equal(upper, state.SelectedPath);
    }

    [Fact]
    public void Replace_GroupEmptied_IsRemovedFromTheCollapsedGroups()
    {
        DocumentWindowState state = Create();
        state.Add([AlphaDoc, RepoDocA]);
        state.Select(RepoDocA);
        state.SetGroupExpanded(Alpha, expanded: false);

        state.Replace(AlphaDoc, RepoDocB);

        Assert.Empty(state.CollapsedGroups);
    }

    [Fact]
    public void RefreshMissing_FileReappears_ReportsTheChange()
    {
        HashSet<string> absent = [RepoDocA];
        var state = new DocumentWindowState(true, FindRepo, path => !absent.Contains(path), Plain);
        state.Add([RepoDocA, RepoRootDoc]);
        Assert.True(DocumentItemOf(state, RepoDocA).IsMissing);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        absent.Clear();

        Assert.True(state.RefreshMissing());
        Assert.False(DocumentItemOf(state, RepoDocA).IsMissing);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RefreshMissing_NothingChanged_ReportsFalseAndRaisesNothing()
    {
        DocumentWindowState state = Create();
        state.Add([RepoDocA]);
        int changes = 0;
        state.Changed += (_, _) => changes++;

        Assert.False(state.RefreshMissing());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ShowTree_ViewerWindow_IsHiddenUntilTwoDocuments()
    {
        DocumentWindowState state = Create(persist: false);
        Assert.False(state.ShowTree);

        state.Add([RepoDocA]);
        Assert.False(state.ShowTree);

        state.Add([RepoRootDoc]);
        Assert.True(state.ShowTree);
    }

    [Fact]
    public void ShowTree_MainWindow_IsShownWhileItHoldsADocument()
    {
        DocumentWindowState state = Create();
        Assert.False(state.ShowTree);

        state.Add([RepoDocA]);
        Assert.True(state.ShowTree);

        state.Close(RepoDocA);
        Assert.False(state.ShowTree);
    }

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static string? FindRepo(string path) =>
        path.StartsWith(Repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Repo
        : path.StartsWith(Alpha + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Alpha
        : null;

    private static DocumentWindowState Create(bool persist = true, string[]? missing = null)
    {
        HashSet<string> absent = new(missing ?? [], StringComparer.OrdinalIgnoreCase);
        return new DocumentWindowState(persist, FindRepo, path => !absent.Contains(path), Plain);
    }

    private static GroupItem RootGroup(DocumentWindowState state) =>
        state.Tree.Items.OfType<GroupItem>().Single(item => string.Equals(item.Path, Repo, StringComparison.OrdinalIgnoreCase));

    private static DocumentItem DocumentItemOf(DocumentWindowState state, string path) =>
        state.Tree.Documents.Single(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

    private sealed class CountingFileSystem
    {
        public int Checks { get; private set; }

        public bool Exists(string _)
        {
            Checks++;
            return true;
        }

        public void Reset() => Checks = 0;
    }
}
