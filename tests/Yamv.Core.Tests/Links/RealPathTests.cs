using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
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

    [Theory]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\a.png")]
    [InlineData(@"\??\C:\Windows\notepad.exe")]
    [InlineData(@"\??\UNC\192.0.2.1\share\a.png")]
    public void Resolve_DevicePath_IsUnresolvableWithoutTouchingIt(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Device paths exist only on Windows.");
        List<string> touched = [];

        Assert.Null(RealPath.Resolve(path, touched.Add));

        Assert.Empty(touched);
    }

    [Fact]
    public void Resolve_LinkToAGlobalRootPath_IsUnresolvable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GLOBALROOT device paths exist only on Windows.");
        string link = Path.Combine(tempRoot, "mup");
        Assert.SkipUnless(TryLink(link, @"\\?\GLOBALROOT\Device\Mup\192.0.2.1\share"), "This machine does not let the test make a directory link.");
        List<string> touched = [];

        Assert.Null(RealPath.Resolve(Path.Combine(link, "a.png"), touched.Add));

        Assert.DoesNotContain(
            touched,
            path => path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase) || path.Contains("192.0.2.1", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Resolve_RelativeLink_ResolvesAgainstItsFolder()
    {
        string sub = Path.Combine(tempRoot, "sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(tempRoot, "target"));
        string link = Path.Combine(sub, "link");
        Assert.SkipUnless(TryLink(link, Path.Combine("..", "target")), "This machine does not let the test make a directory link.");

        Assert.Equal(Path.Combine(realRoot, "target", "a.png"), RealPath.Resolve(Path.Combine(link, "a.png"))?.Path);
    }

    [Fact]
    public void Resolve_RootRelativeLink_ResolvesOnTheLinksDrive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows stores a link target rooted at the link's own drive.");
        string target = Path.Combine(tempRoot, "target-dir");
        Directory.CreateDirectory(target);
        string rootRelative = target[(Path.GetPathRoot(target)!.Length - 1)..];
        string link = Path.Combine(tempRoot, "root-relative");
        Assert.SkipUnless(TryLink(link, rootRelative), "This machine does not let the test make a directory link.");

        Assert.Equal(Path.Combine(realRoot, "target-dir", "a.png"), RealPath.Resolve(Path.Combine(link, "a.png"))?.Path);
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("junction")]
    public async Task Resolve_EachLinkShape_EqualsTheOsFinalPath(string shape)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GetFinalPathNameByHandleW exists only on Windows.");
        string target = Path.Combine(tempRoot, "shape-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.png"), "png", TestContext.Current.CancellationToken);
        string link = Path.Combine(tempRoot, "shape-link");
        bool made = shape switch
        {
            "absolute" => TryLink(link, target),
            "relative" => TryLink(link, "shape-target"),
            _ => await TryJunction(link, target),
        };
        Assert.SkipUnless(made, $"This machine does not let the test make a {shape} link.");
        try
        {
            string path = Path.Combine(link, "a.png");

            Assert.Equal(FinalPath(path), RealPath.Resolve(path)?.Path, ignoreCase: true);
        }
        finally
        {
            // The recursive delete in Dispose is denied access to a junction; removing the link on its own takes only the link.
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData(@"a\b.md")]
    [InlineData(@"\rooted-no-drive.md")]
    [InlineData("a/b.md")]
    public void Resolve_RelativePath_IsUnresolvableWithoutTouching(string path)
    {
        List<string> touched = [];

        Assert.Null(RealPath.Resolve(path, touched.Add));

        Assert.Empty(touched);
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
    public void FullPath_LeavesAShortNameUnexpanded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names exist only on Windows.");
        string longPath = Path.Combine(tempRoot, "long-name-file.png");
        File.WriteAllText(longPath, "png");
        string shortName = Path.GetFileName(ShortPath(longPath));
        Assert.SkipWhen(shortName == "long-name-file.png", "This volume has 8.3 short names turned off.");
        string shortPath = Path.Combine(tempRoot, shortName);

        Assert.Equal(shortPath, LinkIo.FullPath(shortPath, static _ => { }));
    }

    [Theory]
    [MemberData(nameof(ReparseReplies))]
    public void ReparseTarget_CraftedReply_GivesTheCheckedTarget(string shape, byte[] data, bool isLink, string? target)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reparse buffers and drive-letter targets exist only on Windows.");

        (bool IsLink, string? Target) actual = RealPath.ReparseTarget(@"C:\repo\sub\link", data);

        Assert.True(actual == (isLink, target), $"{shape}: expected ({isLink}, {target}), got {actual}");
    }

    [Fact]
    public void RelativeTarget_RootRelative_IsRootedAtTheLinksDrive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows roots a link target at the link's own drive.");
        char current = char.ToUpperInvariant(Environment.CurrentDirectory[0]);
        char drive = "QRSTUVWXYZ".First(letter => letter != current);

        Assert.Equal($@"{drive}:\t", RealPath.RelativeTarget($@"{drive}:\a\link", @"\t"));
    }

    public static TheoryData<string, byte[], bool, string?> ReparseReplies() =>
        new()
        {
            { "reply shorter than a tag", [0x0C, 0x00], true, null },
            { "header shorter than 8 bytes", Symlink(@"\??\C:\x", 0)[..6], true, null },
            { "odd name length", WithUInt16(Symlink(@"\??\C:\x", 0), 10, 3), true, null },
            { "name offset past the end", WithUInt16(Symlink(@"\??\C:\x", 0), 8, 200), true, null },
            { "name length past the end", WithUInt16(Symlink(@"\??\C:\x", 0), 10, 400), true, null },
            { "symlink to a drive path", Symlink(@"\??\C:\x", 0), true, @"C:\x" },
            { "symlink to a share", Symlink(@"\??\UNC\h\s", 0), true, null },
            { "symlink to a volume", Symlink(@"\??\Volume{01234567-89ab-cdef-0123-456789abcdef}\", 0), true, null },
            { "symlink to GLOBALROOT", Symlink(@"\??\GLOBALROOT\Device\Mup\h\s", 0), true, null },
            { "junction to a drive path", ReparseBuffer(0xA000_0003, @"\??\D:\y", flags: null), true, @"D:\y" },
            { "junction to a share", ReparseBuffer(0xA000_0003, @"\??\UNC\h\s", flags: null), true, null },
            { "relative-flag target rooted with a drive", Symlink(@"C:\x", 1), true, null },
            { "relative-flag target", Symlink(@"..\t", 1), true, @"C:\repo\sub\..\t" },
            { "another reparse tag", ReparseBuffer(0x8000_0017, @"\??\C:\x", flags: null), false, null },
        };

    private static byte[] Symlink(string substituteName, uint flags) => ReparseBuffer(0xA000_000C, substituteName, flags);

    /// <summary>A <c>REPARSE_DATA_BUFFER</c>: a symbolic link's layout when <paramref name="flags"/> is given, else a junction's.</summary>
    private static byte[] ReparseBuffer(uint tag, string substituteName, uint? flags)
    {
        byte[] name = Encoding.Unicode.GetBytes(substituteName);
        int pathBuffer = flags is null ? 16 : 20;
        byte[] data = new byte[pathBuffer + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(data.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 0);
        if (flags is uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), value);
        }

        name.CopyTo(data, pathBuffer);
        return data;
    }

    private static byte[] WithUInt16(byte[] data, int offset, ushort value)
    {
        byte[] copy = [.. data];
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), value);
        return copy;
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

    [Fact]
    public async Task Resolve_NameMatchingMoreThanOneEntryInACaseSensitiveFolder_IsUnresolvable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows makes a single folder case-sensitive.");
        string folder = Path.Combine(tempRoot, "sensitive");
        Directory.CreateDirectory(folder);
        string? refusal = await TryCaseSensitive(folder);
        Assert.SkipWhen(refusal is not null, $"This machine does not let the test make a folder case-sensitive: {refusal}");
        File.WriteAllText(Path.Combine(folder, "a.png"), "lower");
        File.WriteAllText(Path.Combine(folder, "A.PNG"), "upper");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Directory.CreateDirectory(Path.Combine(folder, "SUB"));
        Assert.SkipWhen(Directory.EnumerateFileSystemEntries(folder).Count() < 4, "The folder did not keep its names apart.");
        string real = Path.Combine(realRoot, "sensitive");

        Assert.Equal(
            new ResolvedPath(Path.Combine(real, "a.png"), Exists: true, IsDirectory: false),
            RealPath.Resolve(Path.Combine(folder, "a.png"))
        );
        Assert.Equal(
            new ResolvedPath(Path.Combine(real, "A.PNG"), Exists: true, IsDirectory: false),
            RealPath.Resolve(Path.Combine(folder, "A.PNG"))
        );
        Assert.Null(RealPath.Resolve(Path.Combine(folder, "a.PNG")));
        Assert.Null(RealPath.Resolve(Path.Combine(folder, "Sub", "x.png")));
    }

    [Fact]
    public void ChooseMatch_ExactNameAmongSeveralDifferingByCase_IsTheExactOne() =>
        Assert.Equal((1, RealPath.MatchOutcome.One), RealPath.ChooseMatch(["a.png", "A.PNG", "b.png"], "A.PNG"));

    [Fact]
    public void ChooseMatch_OneEntryMatchingIgnoringCase_IsThatEntry() =>
        Assert.Equal((0, RealPath.MatchOutcome.One), RealPath.ChooseMatch(["a.png"], "A.PNG"));

    [Fact]
    public void ChooseMatch_NoEntryMatching_IsNone() => Assert.Equal((-1, RealPath.MatchOutcome.None), RealPath.ChooseMatch(["a.png"], "b.png"));

    [Fact]
    public void ChooseMatch_MoreThanOneMatchingIgnoringCase_IsAmbiguous() =>
        Assert.Equal((-1, RealPath.MatchOutcome.Ambiguous), RealPath.ChooseMatch(["a.png", "A.PNG"], "a.PNG"));

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

    /// <summary>Makes <paramref name="folder"/> case-sensitive: null when fsutil did it, else fsutil's own reason.</summary>
    private static async Task<string?> TryCaseSensitive(string folder)
    {
        var start = new ProcessStartInfo("fsutil.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in (string[])["file", "setCaseSensitiveInfo", folder, "enable"])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("fsutil.exe did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string reason = string.Join(" ", await output, await error).Trim();
        if (process.ExitCode == 0)
        {
            return null;
        }

        return reason.Length == 0 ? $"fsutil.exe exited with code {process.ExitCode}" : reason;
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

    /// <summary>The path Windows opens for <paramref name="path"/>, every link followed, without its <c>\\?\</c> prefix.</summary>
    private static string FinalPath(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path);
        char[] buffer = new char[short.MaxValue];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new InvalidOperationException($"GetFinalPathNameByHandleW failed for '{path}' (error {Marshal.GetLastPInvokeError()}).");
        }

        string final = new(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] filePath, uint length, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
