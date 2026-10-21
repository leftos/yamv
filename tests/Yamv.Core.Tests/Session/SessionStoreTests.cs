using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-session-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string SessionFile => Path.Combine(_directory, "session.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var state = new SessionState
        {
            OpenFiles = ["C:/notes/a.md", "C:/notes/b.md"],
            SelectedFile = "C:/notes/b.md",
            ScrollPositions = new Dictionary<string, double> { ["C:/notes/a.md"] = 0.25, ["C:/notes/b.md"] = 0.75 },
            Window = new WindowBounds(10, 20, 1200, 800, IsMaximized: true),
            SidebarWidth = 340,
            CollapsedGroups = ["C:/notes"],
            ThemeId = "noir",
            Appearance = "dark",
        };

        CreateStore().Save(state);
        SessionState loaded = CreateStore().Load();

        Assert.Equal(state.OpenFiles, loaded.OpenFiles);
        Assert.Equal(state.SelectedFile, loaded.SelectedFile);
        Assert.Equal(0.25, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.Equal(0.75, loaded.ScrollPositions["C:/notes/b.md"]);
        Assert.Equal(state.Window, loaded.Window);
        Assert.Equal(340, loaded.SidebarWidth);
        Assert.Equal(state.CollapsedGroups, loaded.CollapsedGroups);
        Assert.Equal("noir", loaded.ThemeId);
        Assert.Equal("dark", loaded.Appearance);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void SaveLoad_SidebarHidden_RoundTrips()
    {
        CreateStore().Save(new SessionState { SidebarHidden = true });

        Assert.True(CreateStore().Load().SidebarHidden);
    }

    [Fact]
    public void Load_FileWithoutSidebarHidden_IsVisible()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, """{ "openFiles": ["C:/notes/a.md"] }""");

        Assert.False(CreateStore().Load().SidebarHidden);
    }

    [Fact]
    public void Load_WindowWithoutScaling_HasScalingOne()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, """{ "window": { "x": 10, "y": 20, "width": 900, "height": 700, "isMaximized": false } }""");

        Assert.Equal(new WindowBounds(10, 20, 900, 700, IsMaximized: false, Scaling: 1), CreateStore().Load().Window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SaveThenLoad_RepairsAnUnusableScalingToOne(double scaling)
    {
        CreateStore().Save(new SessionState { Window = new WindowBounds(10, 20, 900, 700, IsMaximized: false, scaling) });

        Assert.Equal(1, CreateStore().Load().Window!.Scaling);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Same(SessionState.Empty, CreateStore().Load());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_CorruptFile_MovesItAsideAndLogsOneWarning()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, "{ not json");

        Assert.Same(SessionState.Empty, CreateStore().Load());

        Assert.False(File.Exists(SessionFile));
        Assert.True(File.Exists(SessionFile + ".bad"));
        Assert.Equal("{ not json", File.ReadAllText(SessionFile + ".bad"));
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(SessionFile, warning.Message);
        Assert.NotNull(warning.Exception);
    }

    [Fact]
    public void Load_JsonNull_MovesItAsideAndLogsOneWarning()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, "null");

        Assert.Same(SessionState.Empty, CreateStore().Load());

        Assert.False(File.Exists(SessionFile));
        Assert.Equal("null", File.ReadAllText(SessionFile + ".bad"));
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.NotNull(warning.Exception);
    }

    [Fact]
    public void Load_DropsScrollAndSelectionOutsideOpenFiles()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SessionFile,
            """
            {
              "openFiles": ["C:/notes/a.md"],
              "selectedFile": "C:/notes/b.md",
              "scrollPositions": { "C:/notes/a.md": 0.5, "C:/notes/b.md": 0.9 }
            }
            """
        );

        SessionState loaded = CreateStore().Load();

        Assert.Null(loaded.SelectedFile);
        Assert.Equal(0.5, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.False(loaded.ScrollPositions.ContainsKey("C:/notes/b.md"));
    }

    [Fact]
    public void Save_DropsScrollAndSelectionOutsideOpenFiles()
    {
        CreateStore()
            .Save(
                new SessionState
                {
                    OpenFiles = ["C:/notes/a.md"],
                    SelectedFile = "C:/notes/b.md",
                    ScrollPositions = new Dictionary<string, double> { ["C:/notes/a.md"] = 0.5, ["C:/notes/b.md"] = 0.9 },
                }
            );

        string json = File.ReadAllText(SessionFile);

        Assert.Contains("C:/notes/a.md", json);
        Assert.DoesNotContain("C:/notes/b.md", json);
    }

    [Fact]
    public void Load_ClampsScrollPositions()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SessionFile,
            """
            {
              "openFiles": ["C:/notes/a.md", "C:/notes/b.md"],
              "scrollPositions": { "C:/notes/a.md": 1.5, "C:/notes/b.md": -0.25 }
            }
            """
        );

        SessionState loaded = CreateStore().Load();

        Assert.Equal(1.0, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.Equal(0.0, loaded.ScrollPositions["C:/notes/b.md"]);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        CreateStore().Save(SessionState.Empty);

        Assert.True(File.Exists(SessionFile));
        Assert.False(File.Exists(SessionFile + ".tmp"));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        Assert.False(Directory.Exists(_directory));

        CreateStore().Save(SessionState.Empty);

        Assert.True(Directory.Exists(_directory));
        Assert.True(File.Exists(SessionFile));
    }

    [Theory]
    [InlineData("""{ "openFiles": null }""")]
    [InlineData("""{ "themeId": null }""")]
    [InlineData("""{ "appearance": null }""")]
    public void Load_ExplicitNullForANonNullableField_MovesTheFileAside(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, json);

        Assert.Same(SessionState.Empty, CreateStore().Load());

        Assert.False(File.Exists(SessionFile));
        Assert.True(File.Exists(SessionFile + ".bad"));
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.NotNull(warning.Exception);
    }

    [Fact]
    public void Load_DropsNullAndBlankEntriesFromLists()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SessionFile,
            """
            {
              "openFiles": ["C:/notes/a.md", null, "  "],
              "collapsedGroups": ["C:/notes/group", null, ""],
              "scrollPositions": { "C:/notes/a.md": 0.5 }
            }
            """
        );

        SessionState loaded = CreateStore().Load();

        string[] expectedFiles = ["C:/notes/a.md"];
        string[] expectedGroups = ["C:/notes/group"];
        Assert.Equal(expectedFiles, loaded.OpenFiles);
        Assert.Equal(expectedGroups, loaded.CollapsedGroups);
        Assert.Equal(0.5, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Load_UnknownAppearanceAndEmptyThemeId_FallBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, """{ "themeId": "", "appearance": "sepia" }""");

        SessionState loaded = CreateStore().Load();

        Assert.Equal("studio", loaded.ThemeId);
        Assert.Equal("system", loaded.Appearance);
    }

    [Fact]
    public void SaveThenLoad_RepairsNaNValues()
    {
        CreateStore()
            .Save(
                new SessionState
                {
                    OpenFiles = ["C:/notes/a.md"],
                    ScrollPositions = new Dictionary<string, double> { ["C:/notes/a.md"] = double.NaN },
                    Window = new WindowBounds(double.NaN, 0, 1200, 800, false),
                    SidebarWidth = double.NaN,
                }
            );

        SessionState loaded = CreateStore().Load();

        Assert.Equal(0.0, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.Null(loaded.Window);
        Assert.Equal(280.0, loaded.SidebarWidth);
    }

    [Fact]
    public void SaveThenLoad_RepairsInfiniteValues()
    {
        CreateStore()
            .Save(
                new SessionState
                {
                    OpenFiles = ["C:/notes/a.md"],
                    ScrollPositions = new Dictionary<string, double> { ["C:/notes/a.md"] = double.PositiveInfinity },
                    Window = new WindowBounds(0, 0, double.PositiveInfinity, 800, true),
                    SidebarWidth = double.NegativeInfinity,
                }
            );

        SessionState loaded = CreateStore().Load();

        Assert.Equal(0.0, loaded.ScrollPositions["C:/notes/a.md"]);
        Assert.Null(loaded.Window);
        Assert.Equal(280.0, loaded.SidebarWidth);
    }

    [Fact]
    public void SaveThenLoad_RepairsNonPositiveSizes()
    {
        CreateStore().Save(new SessionState { Window = new WindowBounds(10, 20, 0, -5, false), SidebarWidth = 0 });

        SessionState loaded = CreateStore().Load();

        Assert.Null(loaded.Window);
        Assert.Equal(280.0, loaded.SidebarWidth);
    }

    [Fact]
    public void Load_LockedFile_LogsAnErrorAndLeavesTheFileInPlace()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, "{}");

        using (var held = new FileStream(SessionFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Same(SessionState.Empty, CreateStore().Load());
        }

        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.True(File.Exists(SessionFile));
        Assert.False(File.Exists(SessionFile + ".bad"));
    }

    private SessionStore CreateStore() => new(SessionFile, _log);

    [Fact]
    public void LoadReadOnly_CorruptFile_LeavesItInPlaceAndLogsOneWarning()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, "{ not json");

        Assert.Same(SessionState.Empty, CreateStore().LoadReadOnly());

        Assert.Equal("{ not json", File.ReadAllText(SessionFile));
        Assert.False(File.Exists(SessionFile + ".bad"));
        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(SessionFile, warning.Message);
        Assert.NotNull(warning.Exception);
    }

    [Fact]
    public void LoadReadOnly_MissingFile_ReturnsEmptyAndCreatesNothing()
    {
        Assert.Same(SessionState.Empty, CreateStore().LoadReadOnly());

        Assert.False(Directory.Exists(_directory));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void LoadReadOnly_SavedState_ReturnsTheThemeItHolds()
    {
        CreateStore().Save(new SessionState { ThemeId = "ink", Appearance = "dark" });

        SessionState loaded = CreateStore().LoadReadOnly();

        Assert.Equal("ink", loaded.ThemeId);
        Assert.Equal("dark", loaded.Appearance);
    }

    [Fact]
    public void LoadReadOnly_NormalizesTheStateItReads()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SessionFile, "{}");

        SessionState loaded = CreateStore().LoadReadOnly();

        Assert.Equal(280, loaded.SidebarWidth);
        Assert.Equal("studio", loaded.ThemeId);
        Assert.Equal("system", loaded.Appearance);
    }
}
