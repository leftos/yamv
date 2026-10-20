using Yamv.Core.Documents;
using Yamv.Core.Grouping;

namespace Yamv.Core.Tests.Documents;

public sealed class DocumentTreeTests
{
    private static readonly string Repo = P("dev", "yamv");

    [Fact]
    public void Build_DocumentMissingFromDisk_IsFlaggedMissing()
    {
        string present = Path.Combine(Repo, "a.md");
        string absent = Path.Combine(Repo, "b.md");
        GroupNode group = new("yamv", Repo, true, [new DocumentNode("a.md", present), new DocumentNode("b.md", absent)]);

        var tree = DocumentTree.Build([group], path => path == present, Collapsed());

        Assert.False(Document(tree, present).IsMissing);
        Assert.True(Document(tree, absent).IsMissing);
    }

    [Fact]
    public void Build_CollapsedGroup_IsNotExpanded()
    {
        GroupNode group = new("yamv", Repo, true, []);

        var tree = DocumentTree.Build([group], _ => true, Collapsed(Repo));

        Assert.False(Group(tree).IsExpanded);
    }

    [Fact]
    public void Build_GroupNotCollapsed_IsExpanded()
    {
        GroupNode group = new("yamv", Repo, true, []);

        var tree = DocumentTree.Build([group], _ => true, Collapsed());

        Assert.True(Group(tree).IsExpanded);
    }

    [Fact]
    public void Build_CollapsedPathDifferingInCase_IsNotCollapsedUnderOrdinal()
    {
        GroupNode group = new("yamv", Repo, true, []);
        HashSet<string> collapsed = new([Repo.ToUpperInvariant()], StringComparer.Ordinal);

        var tree = DocumentTree.Build([group], _ => true, collapsed);

        Assert.True(Group(tree).IsExpanded);
    }

    [Fact]
    public void Build_TwoGroupsWithNestedFolders_ListsDocumentsInTreeOrder()
    {
        string alphaRoot = P("dev", "alpha");
        string alphaDoc = Path.Combine(alphaRoot, "docs", "a.md");
        string alphaLast = Path.Combine(alphaRoot, "z.md");
        string yamvDoc = Path.Combine(Repo, "b.md");
        GroupNode alpha = new(
            "alpha",
            alphaRoot,
            true,
            [new FolderNode("docs", Path.Combine(alphaRoot, "docs"), [new DocumentNode("a.md", alphaDoc)]), new DocumentNode("z.md", alphaLast)]
        );
        GroupNode yamv = new("yamv", Repo, true, [new DocumentNode("b.md", yamvDoc)]);

        var tree = DocumentTree.Build([alpha, yamv], _ => true, Collapsed());

        string[] expected = [alphaDoc, alphaLast, yamvDoc];
        Assert.Equal(expected, tree.DocumentOrder);
        Assert.Equal(expected, tree.Documents.Select(document => document.Path));
        Assert.Equal(alphaRoot, tree.Documents[0].GroupPath);
    }

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static HashSet<string> Collapsed(params string[] paths) => new(paths, StringComparer.Ordinal);

    private static GroupItem Group(DocumentTree tree) => Assert.Single(tree.Items.OfType<GroupItem>());

    private static DocumentItem Document(DocumentTree tree, string path) =>
        tree.Documents.Single(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
}
