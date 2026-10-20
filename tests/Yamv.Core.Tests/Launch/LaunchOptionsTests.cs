using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class LaunchOptionsTests
{
    private static readonly string WorkDirectory = OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    [Fact]
    public void Parse_NoArguments_ReturnsMainLaunch() => Assert.IsType<MainLaunch>(LaunchOptions.Parse([], WorkDirectory, AlwaysExists));

    [Fact]
    public void Parse_OnlyProcessSerialNumber_ReturnsMainLaunch() =>
        Assert.IsType<MainLaunch>(LaunchOptions.Parse(["-psn_0_12345"], WorkDirectory, AlwaysExists));

    [Fact]
    public void Parse_ProcessSerialNumberBesideFile_DropsIt()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(LaunchOptions.Parse(["-psn_0_12345", "a.md"], WorkDirectory, AlwaysExists));

        Assert.Equal(Full("a.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_RegisterAlone_ReturnsRegisterLaunch() =>
        Assert.IsType<RegisterLaunch>(LaunchOptions.Parse(["--register"], WorkDirectory, AlwaysExists));

    [Fact]
    public void Parse_UnregisterAlone_ReturnsUnregisterLaunch() =>
        Assert.IsType<UnregisterLaunch>(LaunchOptions.Parse(["--unregister"], WorkDirectory, AlwaysExists));

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Parse_HelpOption_ReturnsHelpLaunch(string argument) =>
        Assert.IsType<HelpLaunch>(LaunchOptions.Parse([argument], WorkDirectory, AlwaysExists));

    [Fact]
    public void Parse_RegisterWithFile_ReturnsInvalidNamingTheOption()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["--register", "a.md"], WorkDirectory, AlwaysExists));

        Assert.Equal("--register cannot be combined with other arguments", invalid.Message);
    }

    [Fact]
    public void Parse_UnregisterWithFile_ReturnsInvalidNamingTheOption()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["--unregister", "a.md"], WorkDirectory, AlwaysExists));

        Assert.Equal("--unregister cannot be combined with other arguments", invalid.Message);
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsInvalidWithUsage()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["--bogus"], WorkDirectory, AlwaysExists));

        Assert.Equal($"Unknown option: --bogus\n\n{LaunchOptions.Usage}", invalid.Message);
    }

    [Fact]
    public void Parse_File_ReturnsViewerWithFullPath()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(LaunchOptions.Parse(["README.md"], WorkDirectory, AlwaysExists));

        Assert.Equal(Full("README.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_RelativeFile_ResolvesAgainstCurrentDirectory()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(LaunchOptions.Parse(["docs/MAIN.md"], WorkDirectory, AlwaysExists));

        Assert.Equal(Path.Combine(WorkDirectory, "docs", "MAIN.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_MultipleFiles_KeepsInputOrder()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(LaunchOptions.Parse(["b.md", "a.md"], WorkDirectory, AlwaysExists));

        Assert.Equal(new[] { Full("b.md"), Full("a.md") }, viewer.Files.ToArray());
    }

    [Fact]
    public void Parse_DuplicateFiles_KeepsOne()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(LaunchOptions.Parse(["a.md", Full("a.md")], WorkDirectory, AlwaysExists));

        Assert.Equal(Full("a.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_MissingFile_ReturnsInvalidListingIt()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(
            LaunchOptions.Parse(["ok.md", "missing.md"], WorkDirectory, path => path == Full("ok.md"))
        );

        Assert.Equal($"File not found: {Full("missing.md")}", invalid.Message);
    }

    [Fact]
    public void Parse_TwoMissingFiles_ListsEachOnItsOwnLine()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["one.md", "two.md"], WorkDirectory, _ => false));

        string[] lines = invalid.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"File not found: {Full("one.md")}", lines[0]);
        Assert.Equal($"File not found: {Full("two.md")}", lines[1]);
    }

    private static bool AlwaysExists(string path) => true;

    private static string Full(string name) => Path.GetFullPath(name, WorkDirectory);
}
