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
        var seams = new FileActionSeams(mac ? FilePlatform.MacOS : FilePlatform.Windows, (_, _) => new ProcessOutcome(false, 0, string.Empty));
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
        var seams = new FileActionSeams(FilePlatform.Other, (_, _) => new ProcessOutcome(false, 0, string.Empty));
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

    private static FileActionSeams WindowsSeams { get; } = new(FilePlatform.Windows, (_, _) => new ProcessOutcome(false, 0, string.Empty));

    private static string ThemePath() => Path.Combine(Path.GetTempPath(), "yamv themes", "a b.yamv-theme.json");

    private AvaloniaFileActions Actions() => new(_log, _toasts, _started.Add, FileActionSeams.Default);

    private AvaloniaFileActions WindowsActions() => new(_log, _toasts, _started.Add, WindowsSeams);

    private static AvaloniaFileActions MacActions(RecordingAppLog log, Func<ProcessStartInfo, TimeSpan, ProcessOutcome> run) =>
        new(log, null, _ => { }, new FileActionSeams(FilePlatform.MacOS, run));

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
