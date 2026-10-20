using System.Text;
using Yamv.Core.Grouping;

namespace Yamv.Core.Tests.Grouping;

public sealed class DocumentGrouperTests
{
    private static readonly GroupingOptions Plain = new(null, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Group_RepoFiles_ShowFoldersThenDocuments()
    {
        string repo = P("dev", "yamv");
        string readme = Path.Combine(repo, "README.md");
        string main = Path.Combine(repo, "docs", "plans", "MAIN.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([readme, main], RepoRoots((readme, repo), (main, repo)), Plain);

        Assert.Equal("yamv [git]\n  docs/plans/\n    MAIN.md\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_FolderChainOfThree_CompactsIntoOneNode()
    {
        string repo = P("dev", "yamv");
        string file = Path.Combine(repo, "a", "b", "c", "x.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], RepoRoots((file, repo)), Plain);

        Assert.Equal("yamv [git]\n  a/b/c/\n    x.md\n", Render(groups));
    }

    [Fact]
    public void Group_FolderHoldingOneFile_IsNotCompacted()
    {
        string repo = P("dev", "yamv");
        string file = Path.Combine(repo, "docs", "MAIN.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], RepoRoots((file, repo)), Plain);

        Assert.Equal("yamv [git]\n  docs/\n    MAIN.md\n", Render(groups));
    }

    [Fact]
    public void Group_LooseFile_IsGroupedByItsFolder()
    {
        string folder = P("work", "notes");
        string file = Path.Combine(folder, "README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], NoRepository, Plain);

        Assert.Equal($"{folder}\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_DuplicatePaths_AreShownOnce()
    {
        string repo = P("dev", "yamv");
        string file = Path.Combine(repo, "README.md");
        string sameFile = Path.Combine(repo, ".", "README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file, sameFile], RepoRoots((file, repo)), Plain);

        Assert.Equal("yamv [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_FoldersAndDocuments_AreSortedByName()
    {
        string repo = P("dev", "yamv");
        string[] files =
        [
            Path.Combine(repo, "b.md"),
            Path.Combine(repo, "a.md"),
            Path.Combine(repo, "zeta", "z.md"),
            Path.Combine(repo, "alpha", "one.md"),
            Path.Combine(repo, "alpha", "two.md"),
        ];

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group(files, AllInRepo(files, repo), Plain);

        Assert.Equal("yamv [git]\n  alpha/\n    one.md\n    two.md\n  zeta/\n    z.md\n  a.md\n  b.md\n", Render(groups));
    }

    [Fact]
    public void Group_HomeFolderInFolderGroupName_IsShownAsTilde()
    {
        GroupingOptions options = new(P("Users", "lefto"), StringComparison.Ordinal);
        string file = P("Users", "lefto", "docs", "x.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], NoRepository, options);

        Assert.Equal($"~{Path.DirectorySeparatorChar}docs\n  x.md\n", Render(groups));
    }

    [Fact]
    public void Group_HomeFolderPrefixInsideAnotherName_IsNotReplaced()
    {
        GroupingOptions options = new(P("Users", "lefto"), StringComparison.Ordinal);
        string folder = P("Users", "lefto2", "docs");
        string file = Path.Combine(folder, "x.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], NoRepository, options);

        Assert.Equal($"{folder}\n  x.md\n", Render(groups));
    }

    [Fact]
    public void Group_SameRepoNameInTwoFolders_AppendsTheParentFolder()
    {
        string dev = P("dev", "yamv");
        string old = P("old", "yamv");
        string devFile = Path.Combine(dev, "README.md");
        string oldFile = Path.Combine(old, "README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([devFile, oldFile], RepoRoots((devFile, dev), (oldFile, old)), Plain);

        Assert.Equal("yamv (dev) [git]\n  README.md\nyamv (old) [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_SameRepoNameAndParentName_UsesFullPaths()
    {
        string first = P("a", "dev", "yamv");
        string second = P("b", "dev", "yamv");
        string firstFile = Path.Combine(first, "README.md");
        string secondFile = Path.Combine(second, "README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([firstFile, secondFile], RepoRoots((firstFile, first), (secondFile, second)), Plain);

        Assert.Equal($"{first} [git]\n  README.md\n{second} [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_RepoAndLooseFiles_AreSeparateGroups()
    {
        string repo = P("dev", "yamv");
        string repoFile = Path.Combine(repo, "README.md");
        string loose = P("work", "loose");
        string looseFile = Path.Combine(loose, "notes.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([repoFile, looseFile], RepoRoots((repoFile, repo)), Plain);

        Assert.Equal($"{loose}\n  notes.md\nyamv [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_LooseFilesInSiblingFolders_AreTwoGroups()
    {
        string one = P("one");
        string two = P("two");
        string oneFile = Path.Combine(one, "a.md");
        string twoFile = Path.Combine(two, "b.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([oneFile, twoFile], NoRepository, Plain);

        Assert.Equal($"{one}\n  a.md\n{two}\n  b.md\n", Render(groups));
    }

    [Fact]
    public void Group_LooseFileAndItsSubfolderFile_AreTwoGroups()
    {
        string downloads = P("Downloads");
        string sub = Path.Combine(downloads, "sub");
        string top = Path.Combine(downloads, "a.md");
        string nested = Path.Combine(sub, "b.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([top, nested], NoRepository, Plain);

        Assert.Equal($"{downloads}\n  a.md\n{sub}\n  b.md\n", Render(groups));
    }

    [Fact]
    public void Group_FileInDriveRoot_IsItsOwnGroup()
    {
        string file = P("README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], NoRepository, Plain);

        Assert.Equal($"{PlatformRoot}\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_PathsDifferingOnlyInCase_AreDeduplicatedWhenComparisonIgnoresCase()
    {
        string repo = P("dev", "yamv");
        string spelledUpper = Path.Combine(repo, "README.md");
        string spelledLower = Path.Combine(repo, "readme.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group(
            [spelledUpper, spelledLower],
            RepoRoots((spelledUpper, repo), (spelledLower, repo)),
            Plain
        );

        Assert.Equal("yamv [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_PathsDifferingOnlyInCase_AreKeptApartUnderOrdinalComparison()
    {
        GroupingOptions ordinal = new(null, StringComparison.Ordinal);
        string repo = P("dev", "yamv");
        string spelledUpper = Path.Combine(repo, "README.md");
        string spelledLower = Path.Combine(repo, "readme.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group(
            [spelledUpper, spelledLower],
            RepoRoots((spelledUpper, repo), (spelledLower, repo)),
            ordinal
        );

        Assert.Equal("yamv [git]\n  README.md\n  readme.md\n", Render(groups));
    }

    [Fact]
    public void Group_FolderHoldingOneFolderAndOneFile_IsNotCompacted()
    {
        string repo = P("dev", "yamv");
        string loose = Path.Combine(repo, "a", "x.md");
        string nested = Path.Combine(repo, "a", "b", "y.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([loose, nested], AllInRepo([loose, nested], repo), Plain);

        Assert.Equal("yamv [git]\n  a/\n    b/\n      y.md\n    x.md\n", Render(groups));
    }

    [Fact]
    public void Group_FileDirectlyInHomeFolder_IsGroupedAsTilde()
    {
        GroupingOptions options = new(P("Users", "lefto"), StringComparison.Ordinal);
        string file = P("Users", "lefto", "x.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group([file], NoRepository, options);

        Assert.Equal("~\n  x.md\n", Render(groups));
    }

    [Fact]
    public void Group_SuffixedNameMatchingAnotherRepoName_UsesFullPaths()
    {
        string dev = P("dev", "yamv");
        string old = P("old", "yamv");
        string literal = P("other", "yamv (dev)");
        string devFile = Path.Combine(dev, "README.md");
        string oldFile = Path.Combine(old, "README.md");
        string literalFile = Path.Combine(literal, "README.md");

        IReadOnlyList<GroupNode> groups = DocumentGrouper.Group(
            [devFile, oldFile, literalFile],
            RepoRoots((devFile, dev), (oldFile, old), (literalFile, literal)),
            Plain
        );

        Assert.Equal($"{dev} [git]\n  README.md\n{literal} [git]\n  README.md\nyamv (old) [git]\n  README.md\n", Render(groups));
    }

    [Fact]
    public void Group_NoFiles_ReturnsNoGroups() => Assert.Empty(DocumentGrouper.Group([], NoRepository, Plain));

    private static string PlatformRoot => OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] parts) => Path.Combine(PlatformRoot, Path.Combine(parts));

    private static string? NoRepository(string path) => null;

    private static Func<string, string?> RepoRoots(params (string File, string Root)[] entries)
    {
        Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string file, string root) in entries)
        {
            roots[file] = root;
        }

        return path => roots.TryGetValue(path, out string? root) ? root : null;
    }

    private static Func<string, string?> AllInRepo(IEnumerable<string> files, string repo) => RepoRoots([.. files.Select(file => (file, repo))]);

    private static string Render(IReadOnlyList<GroupNode> groups)
    {
        StringBuilder builder = new();
        foreach (GroupNode group in groups)
        {
            builder.Append(group.Name).Append(group.IsRepo ? " [git]\n" : "\n");
            AppendChildren(builder, group.Children, 1);
        }

        return builder.ToString();
    }

    private static void AppendChildren(StringBuilder builder, IReadOnlyList<TreeNode> children, int depth)
    {
        foreach (TreeNode child in children)
        {
            builder.Append(' ', depth * 2).Append(child.Name);
            if (child is FolderNode folder)
            {
                builder.Append('/').Append('\n');
                AppendChildren(builder, folder.Children, depth + 1);
            }
            else
            {
                builder.Append('\n');
            }
        }
    }
}
