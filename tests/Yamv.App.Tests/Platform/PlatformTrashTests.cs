using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Yamv.App.Platform;

namespace Yamv.App.Tests.Platform;

public sealed class PlatformTrashTests : IDisposable
{
    /// <summary>The flag <c>SHFileOperationW</c> warns with before it destroys a file it cannot recycle.</summary>
    private const ushort WantNukeWarning = 0x4000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-trash-" + Guid.NewGuid().ToString("N"));

    public PlatformTrashTests() => Directory.CreateDirectory(_folder);

    private string ThemePath => Path.Combine(_folder, "a.yamv-theme.json");

    [Fact]
    public void MoveToTrash_of_a_missing_file_throws_before_the_shell_call()
    {
        int calls = 0;
        var trash = new PlatformTrash(fullPath =>
        {
            calls++;
            return new TrashResult(0, false);
        });

        Assert.Throws<FileNotFoundException>(() =>
        {
            trash.MoveToTrash(ThemePath);
        });

        Assert.Equal(0, calls);
    }

    [Fact]
    public void MoveToTrash_when_the_shell_fails_throws_an_IO_error_naming_the_path_and_the_code()
    {
        string path = WriteTheme("a");

        IOException failure = Assert.Throws<IOException>(() =>
        {
            new PlatformTrash(_ => new TrashResult(5, false)).MoveToTrash(path);
        });

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains("returned 0x5", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToTrash_when_the_shell_reports_aborted_throws_an_IO_error()
    {
        string path = WriteTheme("a");

        IOException failure = Assert.Throws<IOException>(() =>
        {
            new PlatformTrash(_ => new TrashResult(0, true)).MoveToTrash(path);
        });

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains("aborted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToTrash_when_the_shell_succeeds_passes_the_full_path_and_returns()
    {
        string path = WriteTheme("a");
        List<string> calls = [];
        var trash = new PlatformTrash(given =>
        {
            calls.Add(given);
            return new TrashResult(0, false);
        });

        trash.MoveToTrash(path);

        Assert.Equal([path], calls);
    }

    [Fact]
    public void MoveToTrash_of_a_relative_path_passes_the_full_path()
    {
        string name = "yamv-relative-" + Guid.NewGuid().ToString("N") + ".yamv-theme.json";
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), name);
        File.WriteAllText(fullPath, "{}");
        List<string> calls = [];
        var trash = new PlatformTrash(given =>
        {
            calls.Add(given);
            return new TrashResult(0, false);
        });

        try
        {
            trash.MoveToTrash(name);

            Assert.Equal([fullPath], calls);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    public void MoveToTrash_on_a_drive_without_a_recycle_bin_throws_before_the_shell_call(uint driveType)
    {
        string path = WriteTheme("a");
        int calls = 0;
        var trash = new PlatformTrash(
            new TrashSeams(
                (_, _) =>
                {
                    calls++;
                    return new TrashResult(0, false);
                },
                _ => driveType
            ),
            () => 0
        );

        IOException failure = Assert.Throws<IOException>(() =>
        {
            trash.MoveToTrash(path);
        });

        Assert.Contains("no Recycle Bin", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MoveToTrash_on_a_fixed_drive_calls_the_shell()
    {
        string path = WriteTheme("a");
        List<string> calls = [];
        var trash = new PlatformTrash(
            new TrashSeams(
                (given, _) =>
                {
                    calls.Add(given);
                    return new TrashResult(0, false);
                },
                _ => PlatformTrash.FixedDrive
            ),
            () => 0
        );

        trash.MoveToTrash(path);

        Assert.Equal([path], calls);
    }

    [Fact]
    public void The_delete_call_gets_the_owner_handle()
    {
        string path = WriteTheme("a");
        nint owner = 0;
        List<nint> owners = [];
        var trash = new PlatformTrash(
            new TrashSeams(
                (_, handle) =>
                {
                    owners.Add(handle);
                    return new TrashResult(0, false);
                },
                _ => PlatformTrash.FixedDrive
            ),
            () => owner
        );
        owner = 0x1234;

        trash.MoveToTrash(path);

        Assert.Equal([(nint)0x1234], owners);
    }

    [Fact]
    public void DeleteFlags_include_the_warning_before_a_file_is_destroyed() => Assert.NotEqual(0, PlatformTrash.DeleteFlags & WantNukeWarning);

    [Fact]
    public void Mac_round_trips_a_path_through_NSString_and_NSURL()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Only macOS has Foundation to ask.");
        }
        else
        {
            RoundTripThroughFoundation();
        }
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public void MoveToTrash_of_a_real_file_leaves_it_in_the_platform_trash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Only Windows' Recycle Bin and macOS' Trash are exercised here.");
        string path = WriteTheme("desktop");

        if (OperatingSystem.IsWindows())
        {
            new PlatformTrash(() => 0).MoveToTrash(path);

            Assert.False(File.Exists(path));
            Assert.True(RemoveFromRecycleBin(path), $"The Recycle Bin holds no item for {path}.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            string trashed = PlatformTrash.Mac.Delete(path);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(trashed), $"{Path.GetFileName(path)} is not at {trashed}.");
            File.Delete(trashed);
        }
    }

    [Fact]
    public void OriginalPathOf_reads_a_version_2_record()
    {
        byte[] record = Record(version: 2, @"C:\themes\a.yamv-theme.json");

        Assert.Equal(@"C:\themes\a.yamv-theme.json", OriginalPathOf(record));
    }

    [Fact]
    public void OriginalPathOf_reads_a_version_1_record()
    {
        byte[] record = Record(version: 1, @"C:\themes\a.yamv-theme.json");

        Assert.Equal(@"C:\themes\a.yamv-theme.json", OriginalPathOf(record));
    }

    [SupportedOSPlatform("macos")]
    private static void RoundTripThroughFoundation()
    {
        string path = "/Users/me/themes/a b.yamv-theme.json";
        Assert.NotEqual(0, PlatformTrash.Mac.LoadFoundation());
        nint pool = PlatformTrash.Mac.AutoreleasePoolPush();
        try
        {
            nint url = PlatformTrash.Mac.FileUrl(path);
            Assert.NotEqual(0, url);
            nint text = PlatformTrash.Mac.MsgSend(url, PlatformTrash.Mac.SelectorNamed("path"));
            nint utf8 = PlatformTrash.Mac.MsgSend(text, PlatformTrash.Mac.SelectorNamed("UTF8String"));
            Assert.Equal(path, Marshal.PtrToStringUTF8(utf8));
        }
        finally
        {
            PlatformTrash.Mac.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Finds the item recorded for <paramref name="path"/> in the current user's Recycle Bin folder, removes it, and says
    /// whether it was there: Windows keeps each item as a <c>$I</c> record of the original path beside the <c>$R</c> file.
    /// </summary>
    /// <param name="path">The path the file had before it was trashed.</param>
    [SupportedOSPlatform("windows")]
    private static bool RemoveFromRecycleBin(string path)
    {
        string? user = WindowsIdentity.GetCurrent().User?.Value;
        if (user is null)
        {
            return false;
        }

        string folder = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", user);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        foreach (string record in Directory.EnumerateFiles(folder, "$I*"))
        {
            if (!OriginalPathOf(File.ReadAllBytes(record)).Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(record);
            string content = Path.Combine(folder, "$R" + Path.GetFileName(record)[2..]);
            if (File.Exists(content))
            {
                File.Delete(content);
            }

            return true;
        }

        return false;
    }

    /// <summary>The original path a <c>$I</c> record names, in the layout its version number at offset 0 selects.</summary>
    /// <param name="record">The record's bytes: a length at 24 then the path at 28, version 2; a fixed path at 24, version 1.</param>
    private static string OriginalPathOf(byte[] record)
    {
        if (record.Length < 24)
        {
            return string.Empty;
        }

        if (BinaryPrimitives.ReadInt64LittleEndian(record) == 2)
        {
            return record.Length < 28 ? string.Empty : Text(record, 28, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(24)) * sizeof(char));
        }

        return Text(record, 24, 520);
    }

    /// <summary>The UTF-16 text of up to <paramref name="count"/> bytes from <paramref name="start"/>, up to a null.</summary>
    private static string Text(byte[] record, int start, int count)
    {
        int length = Math.Min(count, record.Length - start) & ~1;
        string text = Encoding.Unicode.GetString(record, start, length);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    /// <summary>A <c>$I</c> record of <paramref name="path"/> in the layout the version at offset 0 selects.</summary>
    private static byte[] Record(long version, string path)
    {
        byte[] text = Encoding.Unicode.GetBytes(path + "\0");
        if (version == 2)
        {
            byte[] record = new byte[28 + text.Length];
            BinaryPrimitives.WriteInt64LittleEndian(record, 2);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), path.Length + 1);
            text.CopyTo(record, 28);
            return record;
        }

        byte[] legacy = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(legacy, 1);
        text.CopyTo(legacy, 24);
        return legacy;
    }

    private string WriteTheme(string name)
    {
        string path = Path.Combine(_folder, name + ".yamv-theme.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
