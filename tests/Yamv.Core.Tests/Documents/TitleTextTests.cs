using Yamv.Core.Documents;
using Yamv.Core.Grouping;

namespace Yamv.Core.Tests.Documents;

public sealed class TitleTextTests
{
    [Fact]
    public void For_FileAtTheGroupRoot_ShowsTheGroupNameAsTheLocation()
    {
        string root = P("dev", "yamv");
        GroupNode group = new("yamv", root, true, []);

        (string name, string location) = TitleText.For(Path.Combine(root, "README.md"), group);

        Assert.Equal("README.md", name);
        Assert.Equal("yamv", location);
    }

    [Fact]
    public void For_NestedFile_JoinsEachFolderWithASlash()
    {
        string root = P("dev", "yamv");
        GroupNode group = new("yamv", root, true, []);

        (string name, string location) = TitleText.For(Path.Combine(root, "docs", "plans", "MAIN.md"), group);

        Assert.Equal("MAIN.md", name);
        Assert.Equal("yamv / docs / plans", location);
    }

    [Fact]
    public void For_NoDocument_ShowsTheAppNameAndNoLocation()
    {
        (string name, string location) = TitleText.For(null, null);

        Assert.Equal("YAMV", name);
        Assert.Equal(string.Empty, location);
    }

    [Fact]
    public void For_DocumentWithoutAGroup_ShowsTheNameOnly()
    {
        string file = Path.Combine(P("elsewhere"), "x.md");

        (string name, string location) = TitleText.For(file, null);

        Assert.Equal("x.md", name);
        Assert.Equal(string.Empty, location);
    }

    [Fact]
    public void For_DocumentOutsideTheGroupRoot_ShowsTheNameOnly()
    {
        GroupNode group = new("yamv", P("dev", "yamv"), true, []);

        (string name, string location) = TitleText.For(Path.Combine(P("elsewhere"), "x.md"), group);

        Assert.Equal("x.md", name);
        Assert.Equal(string.Empty, location);
    }

    [Fact]
    public void For_LooseGroupUnderTheHomeFolder_KeepsTheTildeInTheLocation()
    {
        string home = P("Users", "lefto");
        string file = Path.Combine(home, "docs", "a", "x.md");
        GroupingOptions options = new(home, StringComparison.OrdinalIgnoreCase);

        GroupNode group = Assert.Single(DocumentGrouper.Group([file], _ => null, options));

        (string name, string location) = TitleText.For(file, group);

        Assert.Equal("x.md", name);
        Assert.Equal($"~{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}a", location);
    }

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));
}
