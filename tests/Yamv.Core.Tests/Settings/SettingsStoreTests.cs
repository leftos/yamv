using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Settings;
using Yamv.Core.Tests.Session;

namespace Yamv.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly DateTime Past = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-settings-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_IsDefaults()
    {
        SettingsStore store = CreateStore();

        Assert.Equal(AppSettings.Default, store.Load());
        Assert.Equal(AppSettings.Default, CreateStore().Load());

        Assert.False(Directory.Exists(_directory));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_ValidFile_ReadsBothValues()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");

        AppSettings settings = CreateStore().Load();

        Assert.Equal("my-theme", settings.ThemeId);
        Assert.Equal("dark", settings.Appearance);
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData("{}", "studio", "system")]
    [InlineData("""{ "themeId": "my-theme" }""", "my-theme", "system")]
    [InlineData("""{ "appearance": "light" }""", "studio", "light")]
    public void Load_MissingMember_GetsItsDefault(string json, string themeId, string appearance)
    {
        WriteFile(json);

        AppSettings settings = CreateStore().Load();

        Assert.Equal(themeId, settings.ThemeId);
        Assert.Equal(appearance, settings.Appearance);
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("""[ "studio" ]""")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void Load_BadThemeId_FallsBackWithOneWarningKeepsAppearance(string badThemeId)
    {
        WriteFile($$"""{ "themeId": {{badThemeId}}, "appearance": "dark" }""");

        AppSettings settings = CreateStore().Load();

        Assert.Equal(AppSettings.Default.ThemeId, settings.ThemeId);
        Assert.Equal(AppSettings.DarkAppearance, settings.Appearance);
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(SettingsFile, warning.Message);
    }

    [Theory]
    [InlineData("\"blue\"")]
    [InlineData("\"System\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Load_BadAppearance_FallsBackToSystemWithOneWarningKeepsTheme(string badAppearance)
    {
        WriteFile($$"""{ "themeId": "my-theme", "appearance": {{badAppearance}} }""");

        AppSettings settings = CreateStore().Load();

        Assert.Equal("my-theme", settings.ThemeId);
        Assert.Equal(AppSettings.SystemAppearance, settings.Appearance);
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(SettingsFile, warning.Message);
    }

    [Fact]
    public void Set_UnknownTopLevelKeys_SurviveTheRewrite()
    {
        WriteFile("""{ "themeId": "studio", "appearance": "system", "future": { "x": 1 } }""");
        SettingsStore store = CreateStore();

        Assert.True(store.SetAppearance(AppSettings.DarkAppearance));

        using var written = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        Assert.Equal(1, written.RootElement.GetProperty("future").GetProperty("x").GetInt32());
        Assert.Equal(AppSettings.DarkAppearance, CreateStore().Load().Appearance);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_SameValues_WritesNothing()
    {
        WriteFile("""{ "themeId": "studio", "appearance": "system" }""");
        File.SetLastWriteTimeUtc(SettingsFile, Past);
        string before = File.ReadAllText(SettingsFile);

        Assert.True(CreateStore().SetThemeId("studio"));
        Assert.True(CreateStore().SetAppearance(AppSettings.SystemAppearance));

        Assert.Equal(Past, File.GetLastWriteTimeUtc(SettingsFile));
        Assert.Equal(before, File.ReadAllText(SettingsFile));
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_OverMissingFile_CreatesIt()
    {
        SettingsStore store = CreateStore();

        Assert.True(store.SetThemeId("my-theme"));

        AppSettings fresh = CreateStore().Load();
        Assert.Equal("my-theme", fresh.ThemeId);
        Assert.Equal(AppSettings.SystemAppearance, fresh.Appearance);
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void TwoStores_EachChangeDifferentValue_BothSurvive()
    {
        SettingsStore first = CreateStore();
        SettingsStore second = CreateStore();
        _ = first.Load();
        _ = second.Load();

        Assert.True(first.SetThemeId("my-theme"));
        Assert.True(second.SetAppearance(AppSettings.DarkAppearance));

        AppSettings fresh = CreateStore().Load();
        Assert.Equal("my-theme", fresh.ThemeId);
        Assert.Equal(AppSettings.DarkAppearance, fresh.Appearance);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_FileDeletedAfterGoodRead_KeepsLastValuesAndChangeRecreatesFile()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");
        SettingsStore store = CreateStore();
        Assert.Equal("my-theme", store.Load().ThemeId);

        File.Delete(SettingsFile);

        AppSettings kept = store.Load();
        Assert.Equal("my-theme", kept.ThemeId);
        Assert.Equal(AppSettings.DarkAppearance, kept.Appearance);
        Assert.False(File.Exists(SettingsFile));
        Assert.Empty(_log.Entries);

        Assert.True(store.SetAppearance(AppSettings.LightAppearance));

        AppSettings fresh = CreateStore().Load();
        Assert.Equal("my-theme", fresh.ThemeId);
        Assert.Equal(AppSettings.LightAppearance, fresh.Appearance);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Set_OverMissingFile_WritesEvenWhenUnchanged()
    {
        WriteFile("""{ "themeId": "studio", "appearance": "system" }""");
        SettingsStore store = CreateStore();
        Assert.Equal(AppSettings.Default, store.Load());
        File.Delete(SettingsFile);

        Assert.True(store.SetAppearance(AppSettings.SystemAppearance));

        Assert.True(File.Exists(SettingsFile));
        Assert.Equal(AppSettings.Default, CreateStore().Load());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_LockedFile_KeepsLastValuesAndLogsOnce()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");
        string before = File.ReadAllText(SettingsFile);
        SettingsStore store = CreateStore();
        Assert.Equal("my-theme", store.Load().ThemeId);

        using (new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("my-theme", store.Load().ThemeId);
            Assert.Equal("my-theme", store.Load().ThemeId);
        }

        Assert.Equal("my-theme", store.Load().ThemeId);
        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".bad"));
    }

    [Fact]
    public void Set_OnUnparseableFile_KeepsLastGoodValuesAndWritesBadCopyFirst()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");
        SettingsStore store = CreateStore();
        Assert.Equal("my-theme", store.Load().ThemeId);
        WriteFile("{ not json");

        AppSettings kept = store.Load();
        Assert.Equal("my-theme", kept.ThemeId);
        Assert.Equal(AppSettings.DarkAppearance, kept.Appearance);
        Assert.False(File.Exists(SettingsFile + ".bad"));

        Assert.True(store.SetAppearance(AppSettings.LightAppearance));

        Assert.Equal("{ not json", File.ReadAllText(SettingsFile + ".bad"));
        AppSettings fresh = CreateStore().Load();
        Assert.Equal("my-theme", fresh.ThemeId);
        Assert.Equal(AppSettings.LightAppearance, fresh.Appearance);
        Assert.Empty(TemporaryFiles());
        Assert.Equal(2, _log.Entries.Count);
        Assert.All(_log.Entries, entry => Assert.Equal("warning", entry.Level));
        Assert.Contains(SettingsFile + ".bad", _log.Entries[1].Message);
        Assert.Contains("wrote the last good settings", _log.Entries[1].Message);
    }

    [Fact]
    public void Set_NoOpOverUnparseableFile_LeavesFileAndWritesNoBadCopy()
    {
        WriteFile("{ nope");
        SettingsStore store = CreateStore();

        Assert.True(store.SetThemeId(AppSettings.Default.ThemeId));
        Assert.True(store.SetAppearance(AppSettings.SystemAppearance));

        Assert.Equal("{ nope", File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".bad"));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Set_OnUnparseableFile_BadCopyCannotBeWritten_FailsTheChange()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile("{ not json");
        string badCopy = SettingsFile + ".bad";
        File.WriteAllText(badCopy, "older");
        SettingsStore store = CreateStore();

        using (new FileStream(badCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.SetAppearance(AppSettings.DarkAppearance));
        }

        Assert.Equal("{ not json", File.ReadAllText(SettingsFile));
        Assert.Equal("older", File.ReadAllText(badCopy));
        Assert.Empty(TemporaryFiles());
        Assert.Contains(_log.Entries, entry => entry.Level == "error");
    }

    [Fact]
    public void Set_FileLockedWholeCall_ReturnsFalseKeepsOldFileNoTemp()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "system" }""");
        string before = File.ReadAllText(SettingsFile);
        int pauses = 0;
        SettingsStore store = CreateStore(_ => pauses++);

        using (new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.SetAppearance(AppSettings.DarkAppearance));
        }

        Assert.Equal(5, pauses);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
        Assert.Empty(TemporaryFiles());
        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.NotNull(error.Exception);
    }

    [Fact]
    public void Set_FileLockReleasedOnFirstRetry_Succeeds()
    {
        WriteFile("""{ "themeId": "my-theme", "appearance": "system" }""");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int pauses = 0;
        SettingsStore store = CreateStore(_ =>
        {
            pauses++;
            held.Dispose();
        });

        try
        {
            Assert.True(store.SetAppearance(AppSettings.DarkAppearance));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, pauses);
        Assert.Equal(AppSettings.DarkAppearance, CreateStore().Load().Appearance);
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_DestinationSharedReadOnly_RetriesTheReplaceThenSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile("""{ "themeId": "my-theme", "appearance": "system" }""");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        int pauses = 0;
        SettingsStore store = CreateStore(_ =>
        {
            pauses++;
            held.Dispose();
        });

        try
        {
            Assert.True(store.SetAppearance(AppSettings.DarkAppearance));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, pauses);
        Assert.Equal(AppSettings.DarkAppearance, CreateStore().Load().Appearance);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Set_FailedWrite_ReturnsFalseKeepsDiskValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile("""{ "themeId": "my-theme", "appearance": "system" }""");
        SettingsStore store = CreateStore();
        Assert.Equal("my-theme", store.Load().ThemeId);
        string before = File.ReadAllText(SettingsFile);

        using (new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.SetAppearance(AppSettings.DarkAppearance));
            Assert.Equal(AppSettings.SystemAppearance, store.Load().Appearance);
        }

        Assert.Equal(before, File.ReadAllText(SettingsFile));
        Assert.Equal(AppSettings.SystemAppearance, store.Load().Appearance);
        Assert.Empty(TemporaryFiles());
        Assert.Equal("error", Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void Construct_SweepsOldTempKeepsFreshTemp()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_directory);
        string old = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string fresh = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string unrelated = Path.Combine(_directory, "widths.json.tmp");
        foreach (string path in new[] { old, fresh, unrelated })
        {
            File.WriteAllText(path, "{}");
        }

        File.SetLastWriteTimeUtc(old, now.AddMinutes(-11).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, now.AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(unrelated, now.AddMinutes(-60).UtcDateTime);
        var time = new FakeTimeProvider(now);

        _ = new SettingsStore(SettingsFile, _log, _ => { }, time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_OtherStoreWrites_SeesTheChangeEvenAtTheSameLength()
    {
        SettingsStore writer = CreateStore();
        SettingsStore reader = CreateStore();
        Assert.True(writer.SetThemeId("aaaaa"));
        Assert.Equal("aaaaa", reader.Load().ThemeId);
        Assert.True(writer.SetAppearance(AppSettings.DarkAppearance));
        long length = new FileInfo(SettingsFile).Length;
        DateTime firstWrite = File.GetLastWriteTimeUtc(SettingsFile);

        Assert.True(writer.SetThemeId("bbbbb"));
        File.SetLastWriteTimeUtc(SettingsFile, firstWrite);

        Assert.Equal(length, new FileInfo(SettingsFile).Length);
        Assert.Equal("bbbbb", reader.Load().ThemeId);
        Assert.Equal(AppSettings.DarkAppearance, reader.Load().Appearance);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task ConcurrentWriterAndReader_ReaderNeverSeesTornFile()
    {
        string padding = string.Join(",", Enumerable.Range(0, 200));
        WriteFile($$"""{ "themeId": "theme-a", "appearance": "system", "future": [ {{padding}} ] }""");
        SettingsStore writer = CreateStore();
        SettingsStore reader = CreateStore();
        bool stop = false;
        var task = Task.Run(
            () =>
            {
                for (int i = 0; !Volatile.Read(ref stop); i++)
                {
                    _ = writer.SetThemeId(i % 2 == 0 ? "theme-b" : "theme-a");
                }
            },
            TestContext.Current.CancellationToken
        );

        const int WantedFlips = 20;
        var hangGuard = TimeSpan.FromMinutes(5);
        long started = TimeProvider.System.GetTimestamp();
        int flips = 0;
        string? previous = null;
        try
        {
            while (flips < WantedFlips)
            {
                Assert.True(TimeProvider.System.GetElapsedTime(started) < hangGuard, $"The reader saw only {flips} flips before the hang guard.");
                Assert.False(task.IsCompleted, "The writer stopped before the reader was done.");
                string themeId = reader.Load().ThemeId;
                Assert.True(themeId is "theme-a" or "theme-b", $"The reader saw {themeId}.");
                if (previous is not null && themeId != previous)
                {
                    flips++;
                }

                previous = themeId;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await task;
        }

        Assert.Equal(WantedFlips, flips);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level == "warning");
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("System")]
    [InlineData("")]
    public void Set_BadAppearance_ThrowsAndWritesNothing(string appearance)
    {
        WriteFile("""{ "themeId": "studio", "appearance": "system" }""");
        File.SetLastWriteTimeUtc(SettingsFile, Past);
        SettingsStore store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.SetAppearance(appearance));

        Assert.Equal(Past, File.GetLastWriteTimeUtc(SettingsFile));
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Set_BlankThemeId_ThrowsAndWritesNothing(string themeId)
    {
        WriteFile("""{ "themeId": "studio", "appearance": "system" }""");
        File.SetLastWriteTimeUtc(SettingsFile, Past);
        SettingsStore store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.SetThemeId(themeId));

        Assert.Equal(Past, File.GetLastWriteTimeUtc(SettingsFile));
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    private SettingsStore CreateStore(Action<TimeSpan>? pause = null) => new(SettingsFile, _log, pause ?? (_ => { }), TimeProvider.System);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, json);
    }

    private string[] TemporaryFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.tmp") : [];
}
