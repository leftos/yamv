using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Tests.Session;
using Yamv.Core.Widths;

namespace Yamv.Core.Tests.Widths;

public sealed class WidthStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-widths-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string WidthsFile => Path.Combine(_directory, "widths.json");

    private string Docs => Path.Combine(_directory, "docs");

    private string FolderA => Path.Combine(Docs, "a");

    private string FolderB => Path.Combine(Docs, "b");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentWriterAndReader_ReaderNeverSeesTornFile()
    {
        WidthStore writer = CreateStore();
        for (int i = 0; i < 40; i++)
        {
            Assert.True(writer.Set(Path.Combine(Docs, "filler" + i), ContentWidth.FromPx(700)));
        }

        Assert.True(writer.Set(FolderA, ContentWidth.FromPx(600)));
        WidthStore reader = CreateStore();
        bool stop = false;
        var task = Task.Run(
            () =>
            {
                for (int i = 0; !Volatile.Read(ref stop); i++)
                {
                    _ = writer.Set(FolderA, ContentWidth.FromPx(i % 2 == 0 ? 1000 : 600));
                }
            },
            TestContext.Current.CancellationToken
        );

        const int WantedFlips = 20;
        var hangGuard = TimeSpan.FromMinutes(5);
        long started = TimeProvider.System.GetTimestamp();
        int flips = 0;
        double? previous = null;
        try
        {
            while (flips < WantedFlips)
            {
                Assert.True(TimeProvider.System.GetElapsedTime(started) < hangGuard, $"The reader saw only {flips} flips before the hang guard.");
                Assert.False(task.IsCompleted, "The writer stopped before the reader was done.");
                double? a = reader.Resolve(Path.Combine(FolderA, "doc.md")).Width.Px;
                double? filler = reader.Resolve(Path.Combine(Docs, "filler39", "doc.md")).Width.Px;
                Assert.True(a is 600 or 1000, $"Folder a resolved to {a}.");
                Assert.Equal(700, filler);
                if (previous is not null && a != previous)
                {
                    flips++;
                }

                previous = a;
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

    [Fact]
    public void TwoStores_SetDifferentFolders_BothSurvive()
    {
        WidthStore first = CreateStore();
        WidthStore second = CreateStore();
        _ = first.Resolve(Path.Combine(FolderA, "doc.md"));
        _ = second.Resolve(Path.Combine(FolderA, "doc.md"));

        Assert.True(first.Set(FolderA, ContentWidth.FromPx(600)));
        Assert.True(second.Set(FolderB, ContentWidth.FromPx(700)));

        WidthStore fresh = CreateStore();
        Assert.Equal(600, fresh.Get(FolderA)?.Px);
        Assert.Equal(700, fresh.Get(FolderB)?.Px);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_SameValue_DoesNotRewriteFile()
    {
        WidthStore store = CreateStore();
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));
        var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(WidthsFile, past);

        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));
        Assert.True(CreateStore().Set(FolderA, ContentWidth.FromPx(600)));

        Assert.Equal(past, File.GetLastWriteTimeUtc(WidthsFile));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Set_FileLockedWholeCall_ReturnsFalseKeepsOldFileNoTemp()
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        string before = File.ReadAllText(WidthsFile);
        int pauses = 0;
        WidthStore store = CreateStore(_ => pauses++);

        using (new FileStream(WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.Set(FolderA, ContentWidth.FromPx(700)));
        }

        Assert.Equal(5, pauses);
        Assert.Equal(before, File.ReadAllText(WidthsFile));
        Assert.Empty(TemporaryFiles());
        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.NotNull(error.Exception);
    }

    [Fact]
    public void Set_FileLockReleasedOnFirstRetry_Succeeds()
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        var held = new FileStream(WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int pauses = 0;
        WidthStore store = CreateStore(_ =>
        {
            pauses++;
            held.Dispose();
        });

        try
        {
            Assert.True(store.Set(FolderA, ContentWidth.FromPx(700)));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, pauses);
        Assert.Equal(700, CreateStore().Get(FolderA)?.Px);
        Assert.Empty(TemporaryFiles());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_DestinationSharedReadOnly_RetriesMoveThenSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        var held = new FileStream(WidthsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        int pauses = 0;
        WidthStore store = CreateStore(_ =>
        {
            pauses++;
            held.Dispose();
        });

        try
        {
            Assert.True(store.Set(FolderA, ContentWidth.FromPx(700)));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, pauses);
        Assert.Equal(700, CreateStore().Get(FolderA)?.Px);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Construct_SweepsOldTempKeepsFreshTemp()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_directory);
        string old = WidthsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string fresh = WidthsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string unrelated = Path.Combine(_directory, "session.json.tmp");
        foreach (string path in new[] { old, fresh, unrelated })
        {
            File.WriteAllText(path, "{}");
        }

        File.SetLastWriteTimeUtc(old, now.AddMinutes(-11).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, now.AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(unrelated, now.AddMinutes(-60).UtcDateTime);
        var time = new FakeTimeProvider(now);

        _ = new WidthStore(WidthsFile, _log, _ => { }, time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Resolve_TruncatedFile_KeepsLastGoodWidthsAndLeavesFile()
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        WidthStore store = CreateStore();
        string document = Path.Combine(FolderA, "doc.md");
        Assert.Equal(600, store.Resolve(document).Width.Px);
        string truncated = $$"""{ "folders": { {{Key(FolderA)}}: 7""";
        WriteFile(truncated);

        Assert.Equal(600, store.Resolve(document).Width.Px);
        Assert.Equal(600, store.Resolve(document).Width.Px);

        Assert.Equal(truncated, File.ReadAllText(WidthsFile));
        Assert.False(File.Exists(WidthsFile + ".bad"));
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(WidthsFile, warning.Message);
        Assert.NotNull(warning.Exception);
    }

    [Fact]
    public void Resolve_LockedFile_KeepsCacheNoBadFile()
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        string before = File.ReadAllText(WidthsFile);
        WidthStore store = CreateStore();
        string document = Path.Combine(FolderA, "doc.md");
        Assert.Equal(600, store.Resolve(document).Width.Px);

        using (new FileStream(WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(600, store.Resolve(document).Width.Px);
            Assert.Equal(600, store.Resolve(document).Width.Px);
        }

        Assert.Equal(600, store.Resolve(document).Width.Px);
        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.Equal(before, File.ReadAllText(WidthsFile));
        Assert.False(File.Exists(WidthsFile + ".bad"));
    }

    [Fact]
    public void Resolve_FileDeleted_UsesDefault()
    {
        WriteFile($$"""{ "default": 900, "folders": { {{Key(FolderA)}}: 600 } }""");
        WidthStore store = CreateStore();
        string document = Path.Combine(FolderA, "doc.md");
        Assert.Equal(600, store.Resolve(document).Width.Px);

        File.Delete(WidthsFile);

        (ContentWidth width, WidthSource source) = store.Resolve(document);
        Assert.Equal(ContentWidth.DefaultPx, width.Px);
        Assert.True(source.IsAppDefault);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Set_OnUnparseableFile_WritesBadCopyAndKeepsCachedWidths()
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        WidthStore store = CreateStore();
        Assert.Equal(600, store.Get(FolderA)?.Px);
        WriteFile("{ not json");

        Assert.True(store.Set(FolderB, ContentWidth.FromPx(700)));

        Assert.Equal("{ not json", File.ReadAllText(WidthsFile + ".bad"));
        WidthStore fresh = CreateStore();
        Assert.Equal(600, fresh.Get(FolderA)?.Px);
        Assert.Equal(700, fresh.Get(FolderB)?.Px);
        Assert.Empty(TemporaryFiles());
        Assert.Equal(2, _log.Entries.Count);
        Assert.All(_log.Entries, entry => Assert.Equal("warning", entry.Level));
        LogEntry rewritten = _log.Entries[1];
        Assert.Contains(WidthsFile + ".bad", rewritten.Message);
        Assert.Contains("wrote the last good widths", rewritten.Message);
    }

    [Fact]
    public void Remove_NoOpOverUnparseableFile_LeavesFileAndNoBadCopy()
    {
        WriteFile("{ nope");
        WidthStore store = CreateStore();

        Assert.True(store.Remove(FolderA));

        Assert.Equal("{ nope", File.ReadAllText(WidthsFile));
        Assert.False(File.Exists(WidthsFile + ".bad"));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void Set_BadCopyCannotBeWritten_FailsTheChange()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile("{ not json");
        string badCopy = WidthsFile + ".bad";
        File.WriteAllText(badCopy, "older");
        WidthStore store = CreateStore();

        using (new FileStream(badCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Set(FolderB, ContentWidth.FromPx(700)));
        }

        Assert.Equal("{ not json", File.ReadAllText(WidthsFile));
        Assert.Equal("older", File.ReadAllText(badCopy));
        Assert.Empty(TemporaryFiles());
        Assert.Contains(_log.Entries, entry => entry.Level == "error");
    }

    [Theory]
    [InlineData("\"wide\"")]
    [InlineData("true")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Load_OneBadEntry_DropsOnlyItWithOneWarning(string badValue)
    {
        string folderC = Path.Combine(Docs, "c");
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600, {{Key(FolderB)}}: {{badValue}}, {{Key(folderC)}}: "full" } }""");
        WidthStore store = CreateStore();

        Assert.Equal(600, store.Get(FolderA)?.Px);
        Assert.Null(store.Get(FolderB));
        Assert.True(store.Get(folderC)?.IsFull);
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);

        Assert.True(store.Set(Path.Combine(Docs, "d"), ContentWidth.FromPx(640)));
        Assert.DoesNotContain(Key(FolderB), File.ReadAllText(WidthsFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/folder")]
    public void Load_OneBadKey_DropsOnlyItWithOneWarning(string badKey)
    {
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600, {{Key(badKey)}}: 700 } }""");
        WidthStore store = CreateStore();

        Assert.Equal(600, store.Get(FolderA)?.Px);
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
    }

    [Fact]
    public void Load_UnknownTopLevelKey_KeptThroughRewrite()
    {
        WriteFile("""{ "default": 800, "folders": {}, "future": { "x": 1 } }""");
        WidthStore store = CreateStore();

        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));

        using var written = JsonDocument.Parse(File.ReadAllText(WidthsFile));
        Assert.Equal(1, written.RootElement.GetProperty("future").GetProperty("x").GetInt32());
        Assert.Equal(600, CreateStore().Get(FolderA)?.Px);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_PxBelowMinimum_RaisedTo480()
    {
        WriteFile($$"""{ "default": 100, "folders": { {{Key(FolderA)}}: 300 } }""");
        WidthStore store = CreateStore();

        Assert.Equal(ContentWidth.MinimumPx, store.Get(FolderA)?.Px);
        Assert.Equal(ContentWidth.MinimumPx, store.Default.Px);
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData("\"wide\"")]
    [InlineData("-1")]
    [InlineData("true")]
    [InlineData("[800]")]
    public void Load_BadDefault_Is800(string badDefault)
    {
        WriteFile($$"""{ "default": {{badDefault}}, "folders": { {{Key(FolderA)}}: 600 } }""");
        WidthStore store = CreateStore();

        Assert.Equal(ContentWidth.DefaultPx, store.Default.Px);
        Assert.Equal(600, store.Get(FolderA)?.Px);
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
    }

    [Fact]
    public void Resolve_OtherStoreWritesSameLength_SeesChange()
    {
        WidthStore writer = CreateStore();
        WidthStore reader = CreateStore();
        string document = Path.Combine(FolderA, "doc.md");
        Assert.True(writer.Set(FolderA, ContentWidth.FromPx(600)));
        Assert.Equal(600, reader.Resolve(document).Width.Px);
        long length = new FileInfo(WidthsFile).Length;
        DateTime firstWrite = File.GetLastWriteTimeUtc(WidthsFile);

        Assert.True(writer.Set(FolderA, ContentWidth.FromPx(700)));
        File.SetLastWriteTimeUtc(WidthsFile, firstWrite);

        Assert.Equal(length, new FileInfo(WidthsFile).Length);
        Assert.Equal(700, reader.Resolve(document).Width.Px);
    }

    [Fact]
    public void Set_FailedWrite_ResolveReturnsDiskValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds without delete sharing.");
        WriteFile($$"""{ "folders": { {{Key(FolderA)}}: 600 } }""");
        WidthStore store = CreateStore();
        string document = Path.Combine(FolderA, "doc.md");

        using (new FileStream(WidthsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Set(FolderA, ContentWidth.FromPx(700)));
            Assert.Equal(600, store.Resolve(document).Width.Px);
        }

        Assert.Equal(600, store.Resolve(document).Width.Px);
        Assert.Empty(TemporaryFiles());
        Assert.Equal("error", Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void Resolve_InheritsFromParentThenGrandparentThenDefault_ReportsSource()
    {
        string own = Path.Combine(FolderA, "b");
        string document = Path.Combine(own, "doc.md");
        WidthStore store = CreateStore();
        Assert.True(store.Set(Docs, ContentWidth.FromPx(900)));
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));
        Assert.True(store.Set(own, ContentWidth.FromPx(720)));

        AssertResolves(store, document, 720, own);
        Assert.True(store.Remove(own));
        AssertResolves(store, document, 600, FolderA);
        Assert.True(store.Remove(FolderA));
        AssertResolves(store, document, 900, Docs);
        Assert.True(store.Remove(Docs));

        (ContentWidth width, WidthSource source) = store.Resolve(document);
        Assert.Equal(ContentWidth.DefaultPx, width.Px);
        Assert.True(source.IsAppDefault);
        Assert.Null(source.Folder);
    }

    [Fact]
    public void Resolve_WalksPastGroupRootToAnyAncestor()
    {
        WidthStore store = CreateStore();
        Assert.True(store.Set(_directory, ContentWidth.Full));

        (ContentWidth width, WidthSource source) = store.Resolve(Path.Combine(FolderA, "deep", "doc.md"));

        Assert.True(width.IsFull);
        Assert.Equal(_directory, source.Folder);
    }

    [Fact]
    public void Resolve_FolderWithoutWidth_UsesTheStoredDefault()
    {
        WidthStore store = CreateStore();
        Assert.True(store.SetDefault(ContentWidth.FromPx(640)));

        (ContentWidth width, WidthSource source) = CreateStore().Resolve(Path.Combine(FolderA, "doc.md"));

        Assert.Equal(640, width.Px);
        Assert.True(source.IsAppDefault);
    }

    [Fact]
    public void ClearAtOrBelow_SiblingPrefix_Untouched()
    {
        string b = Path.Combine(FolderA, "b");
        string bc = Path.Combine(FolderA, "bc");
        string below = Path.Combine(b, "c");
        WidthStore store = CreateStore();
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(900)));
        Assert.True(store.Set(b, ContentWidth.FromPx(600)));
        Assert.True(store.Set(bc, ContentWidth.FromPx(700)));
        Assert.True(store.Set(below, ContentWidth.FromPx(650)));

        Assert.True(store.ClearAtOrBelow(b + Path.DirectorySeparatorChar));

        WidthStore fresh = CreateStore();
        Assert.Null(fresh.Get(b));
        Assert.Null(fresh.Get(below));
        Assert.Equal(700, fresh.Get(bc)?.Px);
        Assert.Equal(900, fresh.Get(FolderA)?.Px);
    }

    [Fact]
    public void ClearAtOrBelow_FileSystemRoot_ClearsEveryEntry()
    {
        WidthStore store = CreateStore();
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));
        Assert.True(store.Set(_directory, ContentWidth.Full));
        string root = Path.GetPathRoot(FolderA) ?? throw new InvalidOperationException($"{FolderA} has no root.");

        Assert.True(store.ClearAtOrBelow(root));

        WidthStore fresh = CreateStore();
        Assert.Null(fresh.Get(FolderA));
        Assert.Null(fresh.Get(_directory));
    }

    [Fact]
    public void Remove_TrailingSeparatorOrOtherCase_RemovesEntry()
    {
        WidthStore store = CreateStore();
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));

        Assert.True(store.Remove(FolderA + Path.DirectorySeparatorChar));
        Assert.Null(CreateStore().Get(FolderA));

        if (AppPaths.PathComparison == StringComparison.OrdinalIgnoreCase)
        {
            Assert.True(store.Set(FolderA, ContentWidth.FromPx(600)));
            Assert.True(store.Remove(FolderA.ToUpperInvariant()));
            Assert.Null(CreateStore().Get(FolderA));
        }
    }

    [Fact]
    public void Load_KeysDifferingOnlyByCase_Merge()
    {
        Assert.SkipUnless(AppPaths.PathComparison == StringComparison.OrdinalIgnoreCase, "Folder paths are case-sensitive here.");
        string lower = FolderA.ToLowerInvariant();
        string upper = FolderA.ToUpperInvariant();
        WriteFile($$"""{ "folders": { {{Key(lower)}}: 600, {{Key(upper + Path.DirectorySeparatorChar)}}: 700 } }""");
        WidthStore store = CreateStore();

        Assert.Equal(700, store.Get(FolderA)?.Px);
        Assert.Equal(lower, store.Resolve(Path.Combine(FolderA, "doc.md")).Source.Folder);

        Assert.True(store.Set(FolderB, ContentWidth.FromPx(640)));
        using var written = JsonDocument.Parse(File.ReadAllText(WidthsFile));
        List<string> keys = [.. written.RootElement.GetProperty("folders").EnumerateObject().Select(property => property.Name)];
        Assert.Equal(2, keys.Count);
        Assert.Contains(lower, keys);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Resolve_MissingOrBadFile_NeverCreatesOrRewritesFile()
    {
        WidthStore store = CreateStore();
        _ = store.Resolve(Path.Combine(FolderA, "doc.md"));
        _ = store.Get(FolderA);
        _ = store.Default;
        Assert.True(store.SetDefault(ContentWidth.AppDefault));
        Assert.True(store.Remove(FolderA));
        Assert.False(Directory.Exists(_directory));

        WriteFile("{ nope");
        var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(WidthsFile, past);
        _ = store.Resolve(Path.Combine(FolderA, "doc.md"));
        _ = CreateStore().Get(FolderA);

        Assert.Equal("{ nope", File.ReadAllText(WidthsFile));
        Assert.Equal(past, File.GetLastWriteTimeUtc(WidthsFile));
        Assert.Equal([WidthsFile], Directory.GetFiles(_directory));
    }

    [Fact]
    public void SaveThenLoad_FullAndPxRoundTripAsNumberAndFullString()
    {
        WidthStore store = CreateStore();
        Assert.True(store.SetDefault(ContentWidth.Full));
        Assert.True(store.Set(FolderA, ContentWidth.FromPx(760)));
        Assert.True(store.Set(FolderB, ContentWidth.Full));

        string text = File.ReadAllText(WidthsFile);

        Assert.Contains("\"default\": \"full\"", text);
        Assert.Contains($"{Key(FolderA)}: 760", text);
        Assert.Contains($"{Key(FolderB)}: \"full\"", text);
        WidthStore fresh = CreateStore();
        Assert.True(fresh.Default.IsFull);
        Assert.Equal(760, fresh.Get(FolderA)?.Px);
        Assert.True(fresh.Get(FolderB)?.IsFull);
        Assert.Empty(_log.Entries);
    }

    private static void AssertResolves(WidthStore store, string document, double px, string folder)
    {
        (ContentWidth width, WidthSource source) = store.Resolve(document);
        Assert.Equal(px, width.Px);
        Assert.Equal(folder, source.Folder);
    }

    private static string Key(string path) => "\"" + JsonEncodedText.Encode(path) + "\"";

    private WidthStore CreateStore(Action<TimeSpan>? pause = null) => new(WidthsFile, _log, pause ?? (_ => { }), TimeProvider.System);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(WidthsFile, json);
    }

    private string[] TemporaryFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.tmp") : [];
}
