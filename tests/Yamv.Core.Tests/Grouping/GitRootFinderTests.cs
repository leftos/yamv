using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Yamv.Core.Diagnostics;
using Yamv.Core.Grouping;

namespace Yamv.Core.Tests.Grouping;

public sealed class GitRootFinderTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"yamv-gitroot-{Guid.NewGuid():N}");
    private readonly GitRootFinder finder;

    public GitRootFinderTests()
    {
        Directory.CreateDirectory(tempRoot);
        finder = new GitRootFinder(new CaptureLog());
    }

    public void Dispose() => Directory.Delete(tempRoot, recursive: true);

    [Fact]
    public void FindWorkTreeRoot_FileAtRepoRoot_ReturnsRepoDirectory()
    {
        string repo = CreateGitDirectory("repo");
        string file = WriteFile(repo, "README.md");

        Assert.Equal(repo, finder.FindWorkTreeRoot(file));
    }

    [Fact]
    public void FindWorkTreeRoot_FileThreeLevelsDeep_ReturnsRepoDirectory()
    {
        string repo = CreateGitDirectory("repo");
        string file = WriteFile(Path.Combine(repo, "a", "b"), "x.md");

        Assert.Equal(repo, finder.FindWorkTreeRoot(file));
    }

    [Fact]
    public void FindWorkTreeRoot_GitFile_ReturnsItsDirectory()
    {
        string repo = CreateDirectory("worktree");
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: ../main/.git/worktrees/worktree\n");
        string file = WriteFile(Path.Combine(repo, "docs"), "x.md");

        Assert.Equal(repo, finder.FindWorkTreeRoot(file));
    }

    [Fact]
    public void FindWorkTreeRoot_Submodule_ReturnsInnermostRoot()
    {
        CreateGitDirectory("repo");
        string submodule = CreateDirectory("repo", "sub");
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/sub\n");
        string file = WriteFile(Path.Combine(submodule, "x"), "a.md");

        Assert.Equal(submodule, finder.FindWorkTreeRoot(file));
    }

    [Fact]
    public void FindWorkTreeRoot_NoRepository_ReturnsNull()
    {
        string plain = CreateDirectory("plain", "deep");
        if (finder.FindWorkTreeRoot(WriteFile(tempRoot, "probe.md")) is not null)
        {
            Assert.Skip("The machine's temp folder is inside a git repository.");
        }

        Assert.Null(finder.FindWorkTreeRoot(WriteFile(plain, "x.md")));
    }

    [Fact]
    public void FindWorkTreeRoot_SecondFileUsesCachedWalk()
    {
        string repo = CreateGitDirectory("repo");
        string nested = Path.Combine(repo, "a", "b");
        Assert.Equal(repo, finder.FindWorkTreeRoot(WriteFile(nested, "x.md")));

        Directory.Delete(Path.Combine(repo, ".git"), recursive: true);
        string second = WriteFile(Path.Combine(repo, "a"), "y.md");
        Assert.Equal(repo, finder.FindWorkTreeRoot(second));

        GitRootFinder fresh = new(new CaptureLog());
        Assert.NotEqual(repo, fresh.FindWorkTreeRoot(second));
    }

    [Fact]
    public void FindWorkTreeRoot_UnreadableFolder_LogsWarningAndProbesAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            RunUnreadableFolderCase();
            return;
        }

        Assert.Skip("Unix file modes are needed to make a folder unreadable.");
    }

    [UnsupportedOSPlatform("windows")]
    private void RunUnreadableFolderCase()
    {
        CaptureLog log = new();
        GitRootFinder probing = new(log);
        string repo = CreateGitDirectory("repo");
        string locked = CreateDirectory("repo", "locked");
        string file = WriteFile(locked, "x.md");

        try
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);
            string? answer = probing.FindWorkTreeRoot(file);
            if (log.WarningCount == 0)
            {
                Assert.Skip("The process can read a folder with no permissions, so no probe fails.");
            }

            Assert.Equal(repo, answer);
            Assert.Equal(repo, probing.FindWorkTreeRoot(file));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(2, log.WarningCount);
    }

    private string CreateDirectory(params string[] parts) => Directory.CreateDirectory(Path.Combine(tempRoot, Path.Combine(parts))).FullName;

    private string CreateGitDirectory(string name)
    {
        string directory = CreateDirectory(name);
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        return directory;
    }

    private static string WriteFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class CaptureLog : IAppLog
    {
        private readonly ConcurrentQueue<string> warnings = new();

        public int WarningCount => warnings.Count;

        public void LogInfo(string message) { }

        public void LogWarning(string message, Exception? exception = null) => warnings.Enqueue(message);

        public void LogError(string message, Exception? exception = null) { }
    }
}
