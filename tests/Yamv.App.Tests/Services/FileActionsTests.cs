using System.Diagnostics;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Platform;

namespace Yamv.App.Tests.Services;

public sealed class FileActionsTests
{
    private readonly RecordingAppLog _log = new();
    private readonly List<ProcessStartInfo> _started = [];

    [Fact]
    public void Reveal_of_a_path_with_no_existing_folder_logs_a_warning_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a Windows path can have no existing root.");
        string path = Path.Combine(MissingDriveRoot(), "nowhere", "a.md");

        Actions().Reveal(path, RevealKind.File);

        Assert.Empty(_started);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(("warning", $"Reveal: no folder of {path} exists."), (entry.Level, entry.Message));
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
        }
        finally
        {
            Directory.Delete(parent);
        }
    }

    private AvaloniaFileActions Actions() => new(_log, _started.Add);

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
