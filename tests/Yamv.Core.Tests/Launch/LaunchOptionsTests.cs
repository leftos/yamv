using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class LaunchOptionsTests
{
    private static readonly string WorkDirectory = OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    [Fact]
    public void Parse_NoArguments_ReturnsMainLaunch() => Assert.IsType<MainLaunch>(Parse());

    [Fact]
    public void Parse_OnlyProcessSerialNumber_ReturnsMainLaunch() => Assert.IsType<MainLaunch>(Parse("-psn_0_12345"));

    [Fact]
    public void Parse_ProcessSerialNumberBesideFile_DropsIt()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("-psn_0_12345", "a.md"));

        Assert.Equal(Full("a.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_RegisterAlone_ReturnsRegisterLaunch() => Assert.IsType<RegisterLaunch>(Parse("--register"));

    [Fact]
    public void Parse_UnregisterAlone_ReturnsUnregisterLaunch() => Assert.IsType<UnregisterLaunch>(Parse("--unregister"));

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Parse_HelpOption_ReturnsHelpLaunch(string argument) => Assert.IsType<HelpLaunch>(Parse(argument));

    [Fact]
    public void Parse_RegisterWithFile_ReturnsInvalidNamingTheOption()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(Parse("--register", "a.md"));

        Assert.Equal("--register cannot be combined with other arguments", invalid.Message);
    }

    [Fact]
    public void Parse_UnregisterWithFile_ReturnsInvalidNamingTheOption()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(Parse("--unregister", "a.md"));

        Assert.Equal("--unregister cannot be combined with other arguments", invalid.Message);
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsInvalidWithUsage()
    {
        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(Parse("--bogus"));

        Assert.Equal($"Unknown option: --bogus\n\n{LaunchOptions.Usage}", invalid.Message);
    }

    [Fact]
    public void Parse_File_ReturnsViewerWithFullPath()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("README.md"));

        Assert.Equal(Full("README.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_RelativeFile_ResolvesAgainstCurrentDirectory()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("docs/MAIN.md"));

        Assert.Equal(Path.Combine(WorkDirectory, "docs", "MAIN.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_MultipleFiles_KeepsInputOrder()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("b.md", "a.md"));

        Assert.Equal(new[] { Full("b.md"), Full("a.md") }, viewer.Files.ToArray());
    }

    [Fact]
    public void Parse_DuplicateFiles_KeepsOne()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("a.md", Full("a.md")));

        Assert.Equal(Full("a.md"), Assert.Single(viewer.Files));
    }

    [Fact]
    public void Parse_MissingFile_ReturnsViewerHoldingEveryPath()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("ok.md", "missing.md"));

        Assert.Equal(new[] { Full("ok.md"), Full("missing.md") }, viewer.Files.ToArray());
    }

    [Fact]
    public void Parse_TwoMissingFiles_KeepsEachInOrder()
    {
        ViewerLaunch viewer = Assert.IsType<ViewerLaunch>(Parse("one.md", "two.md"));

        Assert.Equal(new[] { Full("one.md"), Full("two.md") }, viewer.Files.ToArray());
    }

    [Fact]
    public void Parse_Directory_ReturnsInvalidNamingIt()
    {
        string folder = Full(".");

        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["."], WorkDirectory, path => path == folder));

        Assert.Equal($"Not a file: {folder}", invalid.Message);
    }

    [Fact]
    public void Parse_EmptyArgument_ResolvesToTheCurrentDirectoryAndIsInvalid()
    {
        string folder = Path.GetFullPath("", WorkDirectory);

        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse([""], WorkDirectory, path => path == folder));

        Assert.Equal($"Not a file: {folder}", invalid.Message);
    }

    [Fact]
    public void Parse_FileBesideDirectory_ReturnsInvalidListingTheDirectory()
    {
        string folder = Full("docs");

        InvalidLaunch invalid = Assert.IsType<InvalidLaunch>(LaunchOptions.Parse(["ok.md", "docs"], WorkDirectory, path => path == folder));

        Assert.Equal($"Not a file: {folder}", invalid.Message);
    }

    private static LaunchRequest Parse(params string[] args) => LaunchOptions.Parse(args, WorkDirectory, _ => false);

    private static string Full(string name) => Path.GetFullPath(name, WorkDirectory);
}
