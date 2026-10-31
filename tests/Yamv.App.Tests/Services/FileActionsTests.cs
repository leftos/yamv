using System.ComponentModel;
using System.Diagnostics;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Platform;

namespace Yamv.App.Tests.Services;

public sealed class FileActionsTests
{
    private readonly RecordingAppLog _log = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly RecordingToasts _toasts = new();

    [Fact]
    public void Reveal_of_a_path_with_no_existing_folder_logs_a_warning_toasts_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a Windows path can have no existing root.");
        string folder = Path.Combine(MissingDriveRoot(), "nowhere");
        string path = Path.Combine(folder, "a.md");

        Actions().Reveal(path, RevealKind.File);

        Assert.Empty(_started);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", $"Reveal: no folder of {path} exists."), (entry.Level, entry.Message));
        Assert.Equal((ToastKind.Warning, $"`{folder}` no longer exists"), Assert.Single(_toasts.Shown));
    }

    [Theory]
    [InlineData(false, "Explorer")]
    [InlineData(true, "Finder")]
    public void Reveal_whose_file_manager_cannot_start_logs_a_warning_and_toasts(bool mac, string fileManager)
    {
        string path = Path.Combine(Path.GetTempPath(), "a.md");
        FileActionSeams seams = SeamsOn(mac ? FilePlatform.MacOS : FilePlatform.Windows);
        var actions = new AvaloniaFileActions(_log, _toasts, _ => throw new Win32Exception(2), seams);

        actions.Reveal(path, RevealKind.File);

        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("warning", entry.Level);
        Assert.StartsWith("Reveal: could not start ", entry.Message, StringComparison.Ordinal);
        Assert.EndsWith($" for {path}.", entry.Message, StringComparison.Ordinal);
        Assert.IsType<Win32Exception>(entry.Exception);
        Assert.Equal((ToastKind.Warning, $"Couldn't open {fileManager} for `a.md`"), Assert.Single(_toasts.Shown));
    }

    [Fact]
    public void Reveal_of_a_deleted_folder_starts_the_file_manager_on_its_parent()
    {
        string parent = Path.Combine(Path.GetTempPath(), "yamv-actions-" + Guid.NewGuid().ToString("N"));
        string gone = Path.Combine(parent, "gone");
        Directory.CreateDirectory(gone);
        Directory.Delete(gone);
        try
        {
            Actions().Reveal(gone, RevealKind.Folder);

            ProcessStartInfo start = Assert.Single(_started);
            Assert.False(start.UseShellExecute);
            Assert.Contains(parent, start.Arguments + string.Join(" ", start.ArgumentList), StringComparison.Ordinal);
            Assert.Empty(_log.Entries);
            Assert.Empty(_toasts.Shown);
        }
        finally
        {
            Directory.Delete(parent);
        }
    }

    [Fact]
    public void OpenWithDefaultApp_on_windows_shell_opens_the_file_itself()
    {
        string path = ThemePath();

        WindowsActions().OpenWithDefaultApp(path);

        ProcessStartInfo start = Assert.Single(_started);
        Assert.True(start.UseShellExecute);
        Assert.Equal(path, start.FileName);
        Assert.Empty(start.ArgumentList);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void OpenWithDefaultApp_on_mac_runs_open_with_the_path_as_one_argument()
    {
        string path = ThemePath();
        List<ProcessStartInfo> runs = [];
        var log = new RecordingAppLog();
        AvaloniaFileActions actions = MacActions(
            log,
            (start, _) =>
            {
                runs.Add(start);
                return new ProcessOutcome(false, 0, string.Empty);
            }
        );

        actions.OpenWithDefaultApp(path);

        ProcessStartInfo start = Assert.Single(runs);
        Assert.Equal("/usr/bin/open", start.FileName);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void OpenWithDefaultApp_on_mac_when_open_fails_logs_its_error_and_throws()
    {
        string path = ThemePath();
        var log = new RecordingAppLog();
        AvaloniaFileActions actions = MacActions(log, (_, _) => new ProcessOutcome(false, 1, "no application knows how to open"));

        IOException failure = Assert.Throws<IOException>(() =>
        {
            actions.OpenWithDefaultApp(path);
        });

        Assert.Contains("no application knows how to open", failure.Message, StringComparison.Ordinal);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("warning", entry.Level);
        Assert.Contains("no application knows how to open", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWithDefaultApp_on_mac_when_open_outlives_the_wait_logs_and_does_not_throw()
    {
        string path = ThemePath();
        var log = new RecordingAppLog();
        AvaloniaFileActions actions = MacActions(log, (_, _) => new ProcessOutcome(true, 0, string.Empty));

        actions.OpenWithDefaultApp(path);

        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("warning", entry.Level);
    }

    [Fact]
    public void OpenWithDefaultApp_on_an_unknown_platform_throws_platform_not_supported()
    {
        string path = ThemePath();
        FileActionSeams seams = SeamsOn(FilePlatform.Other);
        var actions = new AvaloniaFileActions(_log, _toasts, _started.Add, seams);

        Assert.Throws<PlatformNotSupportedException>(() =>
        {
            actions.OpenWithDefaultApp(path);
        });

        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("a.yamv-theme.json")]
    [InlineData("themes/a.yamv-theme.json")]
    public void OpenWithDefaultApp_of_a_relative_path_throws_argument_exception(string path)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            WindowsActions().OpenWithDefaultApp(path);
        });

        Assert.Empty(_started);
    }

    [Fact]
    public void OpenWithDefaultApp_of_a_file_that_is_not_json_throws_argument_exception()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv themes", "a.txt");

        Assert.Throws<ArgumentException>(() =>
        {
            WindowsActions().OpenWithDefaultApp(path);
        });

        Assert.Empty(_started);
    }

    [Fact]
    public void OpenWithDefaultApp_when_the_start_throws_logs_it_and_surfaces_the_failure()
    {
        string path = ThemePath();
        var log = new RecordingAppLog();
        var actions = new AvaloniaFileActions(log, _toasts, _ => throw new Win32Exception(1155), WindowsSeams);

        IOException failure = Assert.Throws<IOException>(() =>
        {
            actions.OpenWithDefaultApp(path);
        });

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.IsType<Win32Exception>(failure.InnerException);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal(("warning", $"Edit as text: could not open {path}."), (entry.Level, entry.Message));
    }

    [Theory]
    [InlineData("{temp}tool.exe")]
    [InlineData("{temp}notes.md")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("//server/share/a.png")]
    [InlineData(@"\\?\C:\a.png")]
    [InlineData(@"\??\C:\a.png")]
    [InlineData("a.png")]
    [InlineData("docs/a.png")]
    [InlineData("{temp}folder.{sep}a.png")]
    [InlineData("{temp}folder {sep}a.png")]
    [InlineData(@"/\server\share\a.png")]
    [InlineData(@"\/server\share\a.png")]
    [InlineData("C:\\a\\b.exe\0.png")]
    [InlineData(@"C:\a\b.png:stream")]
    public void OpenViewable_refuses_exe_unc_device_relative_and_trailing_dot_paths(string template)
    {
        string path = template
            .Replace("{temp}", Path.GetTempPath(), StringComparison.Ordinal)
            .Replace("{sep}", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
        {
            WindowsActions().OpenViewable(path);
        });

        Assert.Empty(_started);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenConfirmed_starts_any_type_with_no_verb_or_arguments(bool mac)
    {
        Assert.SkipUnless(mac || OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = mac ? Path.Combine(Path.GetTempPath(), "yamv links", "set up.PS1") : @"C:\yamv links\set up.PS1";
        List<ProcessStartInfo> runs = [];
        var log = new RecordingAppLog();
        AvaloniaFileActions actions = mac
            ? MacActions(
                log,
                (start, _) =>
                {
                    runs.Add(start);
                    return new ProcessOutcome(false, 0, string.Empty);
                }
            )
            : new AvaloniaFileActions(log, _toasts, runs.Add, WindowsSeams);

        actions.OpenConfirmed(path, isDirectory: false);

        ProcessStartInfo start = Assert.Single(runs);
        Assert.Equal(string.Empty, start.Verb);
        if (mac)
        {
            Assert.Equal("/usr/bin/open", start.FileName);
            Assert.Equal(path, Assert.Single(start.ArgumentList));
        }
        else
        {
            Assert.True(start.UseShellExecute);
            Assert.Equal(path, start.FileName);
            Assert.Empty(start.ArgumentList);
            Assert.Equal(string.Empty, start.Arguments);
        }
    }

    [Theory]
    [InlineData(@"\\server\share\a.ps1")]
    [InlineData("//server/share/a.ps1")]
    [InlineData(@"\\?\C:\a.ps1")]
    [InlineData(@"\??\C:\a.ps1")]
    [InlineData("a.ps1")]
    [InlineData("docs/a.ps1")]
    [InlineData("{temp}folder.{sep}a.ps1")]
    [InlineData("{temp}folder {sep}a.ps1")]
    [InlineData("C:\\a\\b.exe\0.ps1")]
    [InlineData(@"C:\a\b.ps1:stream")]
    public void OpenConfirmed_refuses_a_path_that_is_not_plain_local(string template)
    {
        string path = template
            .Replace("{temp}", Path.GetTempPath(), StringComparison.Ordinal)
            .Replace("{sep}", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
        {
            WindowsActions().OpenConfirmed(path, isDirectory: false);
        });

        Assert.Empty(_started);
    }

    [Fact]
    public void OpenConfirmed_opens_a_windows_folder_with_explorer_and_no_shell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string folder = @"C:\yamv links\docs";

        WindowsActions().OpenConfirmed(folder, isDirectory: true);

        ProcessStartInfo start = Assert.Single(_started);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Path.Combine(FakeWindowsFolder, "explorer.exe"), start.FileName);
        Assert.Equal($"\"{folder}\"", start.Arguments);
    }

    [Theory]
    [InlineData(".pif")]
    [InlineData(".com")]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".lnk")]
    public void OpenConfirmed_refuses_a_file_with_a_program_twin(string extension)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = @"C:\yamv links\setup";

        IOException refused = Assert.Throws<IOException>(() => ActionsWithFile(path + extension).OpenConfirmed(path, isDirectory: false));

        Assert.Equal($"{path} has a program of the same name beside it, which Windows might run instead.", refused.Message);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("release.1 notes")]
    [InlineData("a.b c")]
    [InlineData("a. b")]
    [InlineData("a b.c d")]
    public void OpenConfirmed_refuses_a_program_twin_behind_an_extension_holding_a_space(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = @"C:\yamv links\" + name;

        IOException refused = Assert.Throws<IOException>(() => ActionsWithFile(path + ".cmd").OpenConfirmed(path, isDirectory: false));

        Assert.Equal($"{path} has a program of the same name beside it, which Windows might run instead.", refused.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public void OpenConfirmed_opens_a_file_whose_extension_the_shell_sees_beside_a_same_named_program()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = @"C:\yamv links\a.zzz";

        ActionsWithFile(path + ".cmd").OpenConfirmed(path, isDirectory: false);

        Assert.Equal(path, Assert.Single(_started).FileName);
    }

    [Fact]
    public void A_file_with_its_own_extension_opens_beside_a_same_named_program()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = @"C:\yamv links\chart.png";

        ActionsWithFile(path + ".cmd").OpenViewable(path);

        Assert.Equal(path, Assert.Single(_started).FileName);
    }

    [Fact]
    public void A_file_with_no_twin_opens()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = @"C:\yamv links\setup";

        ActionsWithFile(path).OpenConfirmed(path, isDirectory: false);

        Assert.Equal(path, Assert.Single(_started).FileName);
    }

    [Fact]
    public void OpenViewable_still_refuses_an_unlisted_type()
    {
        List<ProcessStartInfo> runs = [];
        AvaloniaFileActions actions = MacActions(
            new RecordingAppLog(),
            (start, _) =>
            {
                runs.Add(start);
                return new ProcessOutcome(false, 0, string.Empty);
            }
        );

        Assert.Throws<ArgumentException>(() =>
        {
            actions.OpenViewable(Path.Combine(Path.GetTempPath(), "setup.ps1"));
        });

        Assert.Empty(runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenViewable_starts_a_viewable_file_with_no_verb_or_arguments(bool mac)
    {
        Assert.SkipUnless(mac || OperatingSystem.IsWindows(), "Only Windows reads a drive-letter path as fully qualified.");
        string path = mac ? Path.Combine(Path.GetTempPath(), "yamv links", "a chart.PNG") : @"C:\yamv links\a chart.PNG";
        List<ProcessStartInfo> runs = [];
        var log = new RecordingAppLog();
        AvaloniaFileActions actions = mac
            ? MacActions(
                log,
                (start, _) =>
                {
                    runs.Add(start);
                    return new ProcessOutcome(false, 0, string.Empty);
                }
            )
            : new AvaloniaFileActions(log, _toasts, runs.Add, WindowsSeams);

        actions.OpenViewable(path);

        ProcessStartInfo start = Assert.Single(runs);
        if (mac)
        {
            Assert.Equal("/usr/bin/open", start.FileName);
            Assert.Equal(path, Assert.Single(start.ArgumentList));
        }
        else
        {
            Assert.True(start.UseShellExecute);
            Assert.Equal(path, start.FileName);
            Assert.Empty(start.ArgumentList);
        }

        Assert.Empty(start.Verb);
        Assert.Empty(start.Arguments);
        Assert.Empty(log.Entries);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public async Task Copy_path_with_no_clipboard_logs_and_toasts()
    {
        await Actions().CopyTextToAsync(null, "x");

        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", "Copy path: the window has no clipboard."), (entry.Level, entry.Message));
        Assert.Equal((ToastKind.Warning, "Couldn't copy the path: no clipboard"), Assert.Single(_toasts.Shown));
    }

    [Fact]
    public void A_picked_file_with_no_local_path_is_refused_by_name()
    {
        List<string> paths = Actions().KeepLocal([("a.md", null)]);

        Assert.Empty(paths);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", "Pick: a.md has no local path and is not opened."), (entry.Level, entry.Message));
        Assert.Equal((ToastKind.Refusal, "YAMV can't open `a.md`: it isn't a local file"), Assert.Single(_toasts.Shown));
    }

    [Fact]
    public void A_picked_file_name_with_a_line_break_cannot_break_the_log_line()
    {
        List<string> paths = Actions().KeepLocal([("evil\nFORGED.md", null)]);

        Assert.Empty(paths);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", "Pick: evil\uFFFDFORGED.md has no local path and is not opened."), (entry.Level, entry.Message));
    }

    [Fact]
    public void Several_picked_files_with_no_local_path_are_refused_once_with_their_count()
    {
        List<string> paths = Actions().KeepLocal([("a.md", null), ("b.md", null), ("c.md", null)]);

        Assert.Empty(paths);
        Assert.Equal(3, _log.Entries.Count);
        Assert.Equal((ToastKind.Refusal, "YAMV can't open 3 picked files: they aren't local files"), Assert.Single(_toasts.Shown));
    }

    [Fact]
    public void A_picked_file_with_a_local_path_shows_no_toast()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv-actions", "a.md");

        List<string> paths = Actions().KeepLocal([("a.md", path)]);

        Assert.Equal([path], paths);
        Assert.Empty(_log.Entries);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void A_window_with_no_toasts_only_logs_a_picked_non_local_file()
    {
        var actions = new AvaloniaFileActions(_log, null, _started.Add, FileActionSeams.Default);

        Assert.Empty(actions.KeepLocal([("a.md", null)]));

        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", "Pick: a.md has no local path and is not opened."), (entry.Level, entry.Message));
    }

    [Fact]
    public void A_folder_name_with_a_backtick_stays_inside_the_code_span()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a Windows path can have no existing root.");
        string folder = Path.Combine(MissingDriveRoot(), "a`b");
        string path = Path.Combine(folder, "notes.md");

        Actions().Reveal(path, RevealKind.File);

        (ToastKind kind, string text) = Assert.Single(_toasts.Shown);
        Assert.Equal(ToastKind.Warning, kind);
        Assert.Contains('ˋ', text);
        Assert.Equal(2, text.Count(character => character == '`'));
        Assert.Equal($"`{folder.Replace('`', 'ˋ')}` no longer exists", text);
    }

    [Theory]
    [InlineData(false, "Explorer")]
    [InlineData(true, "Finder")]
    public void A_file_name_with_a_backtick_stays_inside_the_code_span(bool mac, string fileManager)
    {
        string path = Path.Combine(Path.GetTempPath(), "a`b.md");
        FileActionSeams seams = SeamsOn(mac ? FilePlatform.MacOS : FilePlatform.Windows);
        var actions = new AvaloniaFileActions(_log, _toasts, _ => throw new Win32Exception(2), seams);

        actions.Reveal(path, RevealKind.File);

        (ToastKind kind, string text) = Assert.Single(_toasts.Shown);
        Assert.Equal(ToastKind.Warning, kind);
        Assert.Contains('ˋ', text);
        Assert.Equal(2, text.Count(character => character == '`'));
        Assert.Equal($"Couldn't open {fileManager} for `aˋb.md`", text);
    }

    private const string FakeWindowsFolder = @"C:\FakeWindows";

    private static FileActionSeams WindowsSeams { get; } = SeamsOn(FilePlatform.Windows);

    private static FileActionSeams SeamsOn(FilePlatform platform) =>
        new(platform, (_, _) => new ProcessOutcome(false, 0, string.Empty), _ => false, FakeWindowsFolder);

    private static string ThemePath() => Path.Combine(Path.GetTempPath(), "yamv themes", "a b.yamv-theme.json");

    private AvaloniaFileActions Actions() => new(_log, _toasts, _started.Add, FileActionSeams.Default);

    private AvaloniaFileActions WindowsActions() => new(_log, _toasts, _started.Add, WindowsSeams);

    private AvaloniaFileActions ActionsWithFile(string existing) =>
        new(_log, _toasts, _started.Add, WindowsSeams with { FileExists = path => path == existing });

    private static AvaloniaFileActions MacActions(RecordingAppLog log, Func<ProcessStartInfo, TimeSpan, ProcessOutcome> run) =>
        new(log, null, _ => { }, SeamsOn(FilePlatform.MacOS) with { Run = run });

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
}
