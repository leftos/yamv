using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Yamv.Core.Links;

namespace Yamv.Core.Tests.Links;

public sealed class RealPathTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"yamv-realpath-{Guid.NewGuid():N}");

    private readonly string realRoot;

    public RealPathTests()
    {
        Directory.CreateDirectory(tempRoot);
        realRoot = RealPath.Resolve(tempRoot)?.Path ?? throw new InvalidOperationException($"The test folder '{tempRoot}' did not resolve.");
    }

    public void Dispose() => Directory.Delete(tempRoot, recursive: true);

    [Fact]
    public void Resolve_DotDotSegment_CollapsesIt()
    {
        string path = Path.Combine(tempRoot, "sub", "..", "a.png");

        Assert.Equal(new ResolvedPath(Path.Combine(realRoot, "a.png"), Exists: false, IsDirectory: false), RealPath.Resolve(path));
    }

    [Fact]
    public void Resolve_MissingTail_IsKeptAsItIs()
    {
        string path = Path.Combine(tempRoot, "missing", "deeper", "a.png");

        Assert.Equal(
            new ResolvedPath(Path.Combine(realRoot, "missing", "deeper", "a.png"), Exists: false, IsDirectory: false),
            RealPath.Resolve(path)
        );
    }

    [Fact]
    public void Resolve_DirectoryLink_ReturnsThePathThroughItsTarget()
    {
        string target = Path.Combine(tempRoot, "real");
        Directory.CreateDirectory(target);
        string link = Path.Combine(tempRoot, "link");
        Assert.SkipUnless(TryLink(link, target), "This machine does not let the test make a directory link.");

        Assert.Equal(Path.Combine(realRoot, "real", "a.png"), RealPath.Resolve(Path.Combine(link, "a.png"))?.Path);
    }

    [Fact]
    public void Resolve_FolderTheUserCannotList_IsUnresolvable()
    {
        string locked = Path.Combine(tempRoot, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        string path = Path.Combine(locked, "inner", "a.png");
        Lock(locked, true);
        try
        {
            Assert.SkipWhen(CanList(locked), "The test runs with rights that list any folder.");

            Assert.Null(RealPath.Resolve(path));
        }
        finally
        {
            Lock(locked, false);
        }
    }

    [Fact]
    public void Resolve_TwoLinkLoop_IsUnresolvable()
    {
        string first = Path.Combine(tempRoot, "first");
        string second = Path.Combine(tempRoot, "second");
        Assert.SkipUnless(TryLink(first, second) && TryLink(second, first), "This machine does not let the test make a directory link.");

        Assert.Null(RealPath.Resolve(Path.Combine(first, "a.png")));
    }

    [Fact]
    public void Resolve_ChainLongerThanTheHopLimit_IsUnresolvable()
    {
        string target = Path.Combine(tempRoot, "real");
        Directory.CreateDirectory(target);
        bool made = true;
        for (int index = RealPath.MaxLinkHops; index >= 0 && made; index--)
        {
            string next = index == RealPath.MaxLinkHops ? target : Path.Combine(tempRoot, $"link{index + 1}");
            made = TryLink(Path.Combine(tempRoot, $"link{index}"), next);
        }

        Assert.SkipUnless(made, "This machine does not let the test make a directory link.");

        Assert.Null(RealPath.Resolve(Path.Combine(tempRoot, "link0", "a.png")));
    }

    [Fact]
    public void Resolve_LinkToANetworkShare_IsUnresolvableWithoutTouchingTheShare()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share paths exist only on Windows.");
        string link = Path.Combine(tempRoot, "share");
        Assert.SkipUnless(TryLink(link, @"\\192.0.2.1\share"), "This machine does not let the test make a directory link.");
        List<string> touched = [];

        Assert.Null(RealPath.Resolve(Path.Combine(link, "a.png"), touched.Add));

        Assert.NotEmpty(touched);
        Assert.DoesNotContain(touched, path => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_JunctionToAnInsideFolder_ResolvesThroughIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions exist only on Windows.");
        string target = Path.Combine(tempRoot, "inside");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.png"), "png", TestContext.Current.CancellationToken);
        string junction = Path.Combine(tempRoot, "junction");
        Assert.SkipUnless(await TryJunction(junction, target), "mklink /J could not make a junction here.");
        try
        {
            Assert.Equal(
                new ResolvedPath(Path.Combine(realRoot, "inside", "a.png"), Exists: true, IsDirectory: false),
                RealPath.Resolve(Path.Combine(junction, "a.png"))
            );
        }
        finally
        {
            // The recursive delete in Dispose is denied access to a junction; removing the junction on its own takes only the link.
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void Resolve_ShortName_ResolvesToTheLongName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names exist only on Windows.");
        string longPath = Path.Combine(tempRoot, "evil.png.exe");
        File.WriteAllText(longPath, "exe");
        string shortName = Path.GetFileName(ShortPath(longPath));
        Assert.SkipWhen(shortName == "evil.png.exe", "This volume has 8.3 short names turned off.");

        Assert.Equal(
            new ResolvedPath(Path.Combine(realRoot, "evil.png.exe"), Exists: true, IsDirectory: false),
            RealPath.Resolve(Path.Combine(tempRoot, shortName))
        );
    }

    [Fact]
    public void Resolve_InputInAnotherCase_ResolvesToTheOnDiskCase()
    {
        Assert.SkipUnless(AppPaths.PathComparison == StringComparison.OrdinalIgnoreCase, "Folder paths are case-sensitive here.");
        Directory.CreateDirectory(Path.Combine(tempRoot, "CaseFolder"));
        File.WriteAllText(Path.Combine(tempRoot, "CaseFolder", "file.png"), "png");

        Assert.Equal(
            new ResolvedPath(Path.Combine(realRoot, "CaseFolder", "file.png"), Exists: true, IsDirectory: false),
            RealPath.Resolve(Path.Combine(tempRoot, "casefolder", "FILE.PNG"))
        );
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> TryJunction(string junction, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in (string[])["/c", "mklink", "/J", junction, target])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0;
    }

    private static bool CanList(string folder)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(folder).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Takes away, or gives back, the current user's right to list <paramref name="folder"/>.</summary>
    private static void Lock(string folder, bool locked)
    {
        if (OperatingSystem.IsWindows())
        {
            LockOnWindows(folder, locked);
        }
        else
        {
            File.SetUnixFileMode(folder, locked ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void LockOnWindows(string folder, bool locked)
    {
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no SID.");
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        if (locked)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        info.SetAccessControl(security);
    }

    private static string ShortPath(string longPath)
    {
        char[] buffer = new char[1024];
        uint length = GetShortPathName(longPath, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
        {
            throw new InvalidOperationException($"GetShortPathNameW failed for '{longPath}' (error {Marshal.GetLastPInvokeError()}).");
        }

        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
