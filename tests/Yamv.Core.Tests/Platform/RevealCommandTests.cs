using Yamv.Core.Platform;

namespace Yamv.Core.Tests.Platform;

public sealed class RevealCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-reveal " + Guid.NewGuid().ToString("N"));

    public RevealCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "a.md"), "# A");
    }

    private string Docs => Path.Combine(_root, "docs");

    private string DocA => Path.Combine(_root, "docs", "a.md");

    [Fact]
    public void For_FileOnWindows_SelectsItInExplorer()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(DocA, RevealKind.File, isMac: false));

        Assert.Equal("explorer.exe", command.Program);
        Assert.Equal($"/select,\"{DocA}\"", command.Arguments);
        Assert.Empty(command.ArgumentList);
    }

    [Fact]
    public void For_FolderOnWindows_OpensItInExplorer()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(Docs, RevealKind.Folder, isMac: false));

        Assert.Equal("explorer.exe", command.Program);
        Assert.Equal($"\"{Docs}\"", command.Arguments);
        Assert.Empty(command.ArgumentList);
    }

    [Fact]
    public void For_FileOnMac_RevealsItInFinder()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(DocA, RevealKind.File, isMac: true));

        Assert.Equal("/usr/bin/open", command.Program);
        Assert.Null(command.Arguments);
        Assert.Equal(["-R", DocA], command.ArgumentList);
    }

    [Fact]
    public void For_FolderOnMac_OpensItInFinder()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(Docs, RevealKind.Folder, isMac: true));

        Assert.Equal("/usr/bin/open", command.Program);
        Assert.Null(command.Arguments);
        Assert.Equal([Docs], command.ArgumentList);
    }

    [Fact]
    public void For_PathWithoutSpaces_IsQuotedTheSameWay()
    {
        string plain = Path.Combine(Path.GetTempPath(), "yamv-reveal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try
        {
            RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(plain, RevealKind.Folder, isMac: false));

            Assert.Equal($"\"{plain}\"", command.Arguments);
        }
        finally
        {
            Directory.Delete(plain);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void For_MissingFile_OpensItsNearestExistingFolder(bool isMac)
    {
        string gone = Path.Combine(Docs, "deep", "er", "gone.md");

        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(gone, RevealKind.File, isMac));

        if (isMac)
        {
            Assert.Equal([Docs], command.ArgumentList);
        }
        else
        {
            Assert.Equal($"\"{Docs}\"", command.Arguments);
        }
    }

    [Fact]
    public void For_MissingFolder_OpensItsNearestExistingFolder()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(Path.Combine(Docs, "gone"), RevealKind.Folder, isMac: false));

        Assert.Equal($"\"{Docs}\"", command.Arguments);
    }

    [Fact]
    public void For_PathOnADriveThatDoesNotExist_IsNoCommand()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a Windows path can have no existing root.");

        string path = Path.Combine(MissingDriveRoot(), "nowhere", "a.md");

        Assert.Null(RevealCommand.For(path, RevealKind.File, isMac: false));
    }

    [Fact]
    public void For_FileBelowAnExistingFile_OpensTheFolderHoldingThatFile()
    {
        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(Path.Combine(DocA, "b.md"), RevealKind.File, isMac: false));

        Assert.Equal($"\"{Docs}\"", command.Arguments);
    }

    [Fact]
    public void For_DriveRootOnWindows_IsPassedUnquoted()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(root, RevealKind.Folder, isMac: false));

        Assert.Equal(root, command.Arguments);
    }

    [Fact]
    public void For_MissingFileWhoseNearestFolderIsTheRoot_PassesTheRootUnquoted()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string gone = Path.Combine(root, "nowhere-" + Guid.NewGuid().ToString("N"), "a.md");

        RevealCommand command = Assert.IsType<RevealCommand>(RevealCommand.For(gone, RevealKind.File, isMac: false));

        Assert.Equal(root, command.Arguments);
    }

    [Theory]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share name", @"""\\server\share name""")]
    [InlineData(@"\\server\share\docs", @"""\\server\share\docs""")]
    public void ExplorerFolderArgument_QuotesAUncShareRootOnlyWithASpace(string folder, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a UNC share as a root.");

        Assert.Equal(expected, RevealCommand.ExplorerFolderArgument(folder));
    }

    /// <summary>The root of a drive letter no volume is mounted on, Z: first.</summary>
    private static string MissingDriveRoot()
    {
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            string root = $"{letter}:\\";
            if (!Directory.Exists(root))
            {
                return root;
            }
        }

        throw new InvalidOperationException("Every drive letter from D: to Z: is in use; this test needs a free one.");
    }

    [Fact]
    public void NearestExistingFolder_OfAnExistingFile_IsItsFolder() => Assert.Equal(Docs, RevealCommand.NearestExistingFolder(DocA));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
