using System.Text;
using Yamv.Core.Tests.Session;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeCatalogTests : IDisposable
{
    private const string ValidTheme = """{ "name": "Mine", "light": { "tokens": { "accent": "#123456" } } }""";

    private const string SizeError = "The file is larger than 1 MB; a theme file is a few KB.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-themes-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string UserFolder => Path.Combine(_root, "themes");

    private string SourceFolder => Path.Combine(_root, "source");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ThemeCatalog CreateCatalog() => new(UserFolder, _log);

    private string WriteUserFile(string id, string text)
    {
        Directory.CreateDirectory(UserFolder);
        string path = Path.Combine(UserFolder, id + ThemeCatalog.FileSuffix);
        File.WriteAllText(path, text);
        return path;
    }

    private string WriteSourceFile(string fileName, string text)
    {
        Directory.CreateDirectory(SourceFolder);
        string path = Path.Combine(SourceFolder, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void LoadAll_NoUserFolder_ListsTheBuiltIns()
    {
        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        Assert.Equal(["paper", "studio", "ink"], entries.Select(entry => entry.Id));
        Assert.All(entries, entry => Assert.Equal(ThemeSource.BuiltIn, entry.Source));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void LoadAll_ListsBuiltInsThenUserThemesSortedByName()
    {
        string zebra = WriteUserFile("z", """{ "name": "Alpha", "dark": {} }""");
        WriteUserFile("a", """{ "name": "Beta", "dark": {} }""");
        File.WriteAllText(Path.Combine(UserFolder, "notes.json"), "{}");

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        Assert.Equal(["paper", "studio", "ink", "z", "a"], entries.Select(entry => entry.Id));
        ThemeEntry first = entries[3];
        Assert.Equal((ThemeSource.User, zebra), (first.Source, first.FilePath));
        Assert.Equal("Alpha", first.Result.Theme!.Name);
        Assert.Equal(ThemeSource.User, first.Result.Theme.Source);
    }

    [Fact]
    public void LoadAll_UserThemeWithBuiltInId_ShadowsIt()
    {
        WriteUserFile("studio", """{ "name": "My Studio", "light": {} }""");

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        ThemeEntry studio = Assert.Single(entries, entry => entry.Id == "studio");
        Assert.Equal(ThemeSource.User, studio.Source);
        Assert.Equal("My Studio", studio.Result.Theme!.Name);
        Assert.Equal(["paper", "ink", "studio"], entries.Select(entry => entry.Id));
    }

    [Fact]
    public void LoadAll_InvalidUserFile_IsListedWithItsErrors()
    {
        WriteUserFile("broken", """{ "name": "Broken", "light": { "tokens": { "accent": "#12" } } }""");

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        ThemeEntry broken = Assert.Single(entries, entry => entry.Id == "broken");
        Assert.Null(broken.Result.Theme);
        Assert.Equal("Token 'accent' in light: '#12' is not a colour (#rgb, #rrggbb or #rrggbbaa).", Assert.Single(broken.Result.Errors));
        Assert.Contains(_log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_CopiesTheFileVerbatimUnderItsId()
    {
        string text = "// keep me\n" + ValidTheme;
        string source = WriteSourceFile("Mine.yamv-theme.json", text);

        ThemeEntry entry = CreateCatalog().Import(source);

        Assert.Equal("Mine", entry.Id);
        Assert.Equal(Path.Combine(UserFolder, "Mine.yamv-theme.json"), entry.FilePath);
        Assert.Equal(text, File.ReadAllText(entry.FilePath!));
        Assert.Equal("Mine", entry.Result.Theme!.Id);
    }

    [Fact]
    public void Import_Clash_GetsANumberedId()
    {
        WriteUserFile("mine", ValidTheme);
        WriteUserFile("mine-2", ValidTheme);
        string source = WriteSourceFile("mine.yamv-theme.json", ValidTheme);

        ThemeEntry entry = CreateCatalog().Import(source);

        Assert.Equal("mine-3", entry.Id);
        Assert.True(File.Exists(Path.Combine(UserFolder, "mine-3.yamv-theme.json")));
        Assert.Equal(ValidTheme, File.ReadAllText(Path.Combine(UserFolder, "mine.yamv-theme.json")));
    }

    [Fact]
    public void Import_InvalidFile_ThrowsWithItsErrorsAndWritesNothing()
    {
        string source = WriteSourceFile("bad.yamv-theme.json", """{ "light": {} }""");

        ThemeException exception = Assert.Throws<ThemeException>(() => CreateCatalog().Import(source));

        Assert.Equal(["The theme has no 'name'."], exception.Errors);
        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Inspect_TwoHalvesWithCss_ListsThemAndCopiesNothing()
    {
        string source = WriteSourceFile(
            "both.yamv-theme.json",
            """{ "name": "Both", "author": "Ann", "light": {}, "dark": { "css": "p { }" }, "extra": 1 }"""
        );

        ThemeInspection inspection = ThemeCatalog.Inspect(source);

        Assert.Equal(
            ("both.yamv-theme.json", "Both", "Ann", true, true, true),
            (inspection.FileName, inspection.Name, inspection.Author, inspection.HasLight, inspection.HasDark, inspection.HasCss)
        );
        Assert.Empty(inspection.Errors);
        Assert.Equal(["Unknown field 'extra' is ignored."], inspection.Warnings);
        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Inspect_OneHalfWithoutCss()
    {
        ThemeInspection inspection = ThemeCatalog.Inspect(WriteSourceFile("mine.yamv-theme.json", ValidTheme));

        Assert.Equal(
            ("Mine", null, true, false, false),
            (inspection.Name, inspection.Author, inspection.HasLight, inspection.HasDark, inspection.HasCss)
        );
        Assert.Empty(inspection.Errors);
    }

    [Fact]
    public void Inspect_WhiteSpaceCss_IsNoCss()
    {
        ThemeInspection inspection = ThemeCatalog.Inspect(WriteSourceFile("ws.yamv-theme.json", """{ "name": "W", "css": "  ", "light": {} }"""));

        Assert.False(inspection.HasCss);
    }

    [Fact]
    public void Inspect_InvalidFile_ListsItsErrors()
    {
        string source = WriteSourceFile("bad.yamv-theme.json", """{ "light": { "tokens": { "accent": "#12" } } }""");

        ThemeInspection inspection = ThemeCatalog.Inspect(source);

        Assert.Null(inspection.Name);
        Assert.Equal(["The theme has no 'name'.", "Token 'accent' in light: '#12' is not a colour (#rgb, #rrggbb or #rrggbbaa)."], inspection.Errors);
    }

    [Fact]
    public void Inspect_FileNamedOnlyTheSuffix_ListsTheIdError()
    {
        ThemeInspection inspection = ThemeCatalog.Inspect(WriteSourceFile(ThemeCatalog.FileSuffix, ValidTheme));

        Assert.Contains("gives no usable theme id", Assert.Single(inspection.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_MissingFile_ListsTheReadError()
    {
        ThemeInspection inspection = ThemeCatalog.Inspect(Path.Combine(_root, "missing.yamv-theme.json"));

        Assert.Equal("missing.yamv-theme.json", inspection.FileName);
        Assert.StartsWith("The file cannot be read: ", Assert.Single(inspection.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_WritesALoadableCopy()
    {
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition ink = catalog.LoadAll().Single(entry => entry.Id == "ink").Result.Theme!;

        ThemeDefinition copy = catalog.Duplicate(ink);
        ThemeDefinition second = catalog.Duplicate(ink);

        Assert.Equal(("ink-2", "Ink (copy)", ThemeSource.User), (copy.Id, copy.Name, copy.Source));
        Assert.Equal(Path.Combine(UserFolder, "ink-2.yamv-theme.json"), copy.FilePath);
        Assert.Equal(("ink-3", "Ink (copy 2)"), (second.Id, second.Name));
        ThemeEntry loaded = catalog.LoadAll().Single(entry => entry.Id == "ink-2");
        Assert.Empty(loaded.Result.Errors);
        Assert.Equal("Ink (copy)", loaded.Result.Theme!.Name);
        Assert.Equal("Ink (copy 2)", catalog.LoadAll().Single(entry => entry.Id == "ink-3").Result.Theme!.Name);
        Assert.Equal(ink.SharedCss, loaded.Result.Theme.SharedCss);
        Assert.Equal(ink.Dark!.Tokens.OrderBy(pair => pair.Key), loaded.Result.Theme.Dark!.Tokens.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Duplicate_OfAUserThemeWhoseFileIsGone_WritesTheCopy()
    {
        string path = Path.Combine(UserFolder, "mine" + ThemeCatalog.FileSuffix);
        ThemeDefinition gone = ThemeParser.Parse(ValidTheme, "mine", ThemeSource.User, path).Theme!;

        ThemeDefinition copy = CreateCatalog().Duplicate(gone);

        Assert.Equal(("mine-2", "Mine (copy)"), (copy.Id, copy.Name));
        Assert.Equal(["mine-2.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Duplicate_OfStudio_IsStudio2ThenStudio3()
    {
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition studio = catalog.LoadAll().Single(entry => entry.Id == "studio").Result.Theme!;

        ThemeDefinition copy = catalog.Duplicate(studio);
        ThemeDefinition second = catalog.Duplicate(studio);

        Assert.Equal(("studio-2", "Studio (copy)"), (copy.Id, copy.Name));
        Assert.Equal(("studio-3", "Studio (copy 2)"), (second.Id, second.Name));
    }

    [Fact]
    public void Duplicate_OfADuplicate_GetsAnotherUniqueId()
    {
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition studio = catalog.LoadAll().Single(entry => entry.Id == "studio").Result.Theme!;
        ThemeDefinition copy = catalog.Duplicate(studio);

        ThemeDefinition copyOfCopy = catalog.Duplicate(copy);

        Assert.Equal(("studio-2-2", "Studio (copy) (copy)"), (copyOfCopy.Id, copyOfCopy.Name));
        Assert.Equal(["studio-2-2.yamv-theme.json", "studio-2.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Save_WritesAtomicallyAndLeavesNoTemporaryFile()
    {
        ThemeCatalog catalog = CreateCatalog();
        WriteUserFile("mine", ValidTheme);
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;

        catalog.Save(theme with { Name = "Renamed" });

        Assert.Equal(["mine.yamv-theme.json"], Directory.GetFiles(UserFolder).Select(Path.GetFileName));
        Assert.Equal("Renamed", catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!.Name);
    }

    [Fact]
    public void SaveAndDelete_OfABuiltIn_Throw()
    {
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition paper = catalog.LoadAll()[0].Result.Theme!;

        var trash = new RecordingTrash();
        Assert.Throws<InvalidOperationException>(() => catalog.Save(paper));
        Assert.Throws<InvalidOperationException>(() => catalog.Delete("paper", trash));
        Assert.Throws<InvalidOperationException>(() => catalog.Rename("paper", "Mine"));
        Assert.Empty(trash.Paths);
        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Delete_MovesTheUserFileToTheTrash()
    {
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        var trash = new RecordingTrash();

        catalog.Delete("mine", trash);

        Assert.Equal([path], trash.Paths);
        Assert.Throws<FileNotFoundException>(() => catalog.Delete("other", trash));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("")]
    public void Delete_IdThatIsNotAPlainFileName_Throws(string id) =>
        Assert.Throws<ArgumentException>(() => CreateCatalog().Delete(id, new RecordingTrash()));

    [Fact]
    public void Export_OfABuiltIn_IsItsEmbeddedText()
    {
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition ink = catalog.LoadAll().Single(entry => entry.Id == "ink").Result.Theme!;
        string destination = Path.Combine(_root, "out", "ink.yamv-theme.json");

        catalog.Export(ink, destination);

        Assert.Equal(ThemeCatalog.ReadBuiltInText("ink"), File.ReadAllText(destination));
        Assert.Contains("//", File.ReadAllText(destination), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_OfAUserTheme_IsTheSerializerOutput()
    {
        ThemeCatalog catalog = CreateCatalog();
        WriteUserFile("mine", "// dropped\n" + ValidTheme);
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;
        string destination = Path.Combine(_root, "mine.yamv-theme.json");

        catalog.Export(theme, destination);

        Assert.Equal(ThemeSerializer.Serialize(theme), File.ReadAllText(destination));
    }

    [Fact]
    public void LoadAll_InvalidUserFileWithBuiltInId_KeepsTheBuiltInListed()
    {
        WriteUserFile("paper", """{ "light": {} }""");

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        ThemeEntry builtIn = Assert.Single(entries, entry => entry.Id == "paper" && entry.Source == ThemeSource.BuiltIn);
        Assert.NotNull(builtIn.Result.Theme);
        ThemeEntry user = Assert.Single(entries, entry => entry.Id == "paper" && entry.Source == ThemeSource.User);
        Assert.Equal(["The theme has no 'name'."], user.Result.Errors);
    }

    [Fact]
    public void Import_FileWithABuiltInId_GetsANumberedId()
    {
        string source = WriteSourceFile("paper.yamv-theme.json", ValidTheme);

        ThemeEntry entry = CreateCatalog().Import(source);

        Assert.Equal("paper-2", entry.Id);
        Assert.False(File.Exists(Path.Combine(UserFolder, "paper.yamv-theme.json")));
    }

    [Fact]
    public void Import_FileNamedOnlyTheSuffix_Throws()
    {
        string source = WriteSourceFile(ThemeCatalog.FileSuffix, ValidTheme);

        ThemeException exception = Assert.Throws<ThemeException>(() => CreateCatalog().Import(source));

        Assert.Contains("gives no usable theme id", Assert.Single(exception.Errors), StringComparison.Ordinal);
        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Import_PathLikeNames_StayInsideTheUserFolder()
    {
        Directory.CreateDirectory(SourceFolder);
        string parent = Path.Combine(SourceFolder, "..", "x" + ThemeCatalog.FileSuffix);
        File.WriteAllText(parent, ValidTheme);
        Assert.Equal(Path.Combine(UserFolder, "x" + ThemeCatalog.FileSuffix), CreateCatalog().Import(parent).FilePath);

        string backslash = Path.Combine(SourceFolder, "a\\b" + ThemeCatalog.FileSuffix);
        Directory.CreateDirectory(Path.GetDirectoryName(backslash)!);
        File.WriteAllText(backslash, ValidTheme);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(UserFolder, "b" + ThemeCatalog.FileSuffix), CreateCatalog().Import(backslash).FilePath);
        }
        else
        {
            Assert.Throws<ThemeException>(() => CreateCatalog().Import(backslash));
        }

        string device = Path.Combine(Path.GetFullPath(SourceFolder), "con" + ThemeCatalog.FileSuffix);
        string devicePath = OperatingSystem.IsWindows() ? @"\\?\" + device : device;
        File.WriteAllText(devicePath, ValidTheme);
        try
        {
            Assert.Throws<ThemeException>(() => CreateCatalog().Import(devicePath));
        }
        finally
        {
            File.Delete(devicePath);
        }

        string[] expected = OperatingSystem.IsWindows() ? ["b.yamv-theme.json", "x.yamv-theme.json"] : ["x.yamv-theme.json"];
        Assert.Equal(expected, UserFileNames());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a\\b")]
    [InlineData("con")]
    public void Save_PathLikeOrDeviceId_ThrowsAndWritesNothing(string id)
    {
        ThemeDefinition theme = ThemeParser.Parse(ValidTheme, id, ThemeSource.User, null).Theme!;

        Assert.Throws<ArgumentException>(() => CreateCatalog().Save(theme));

        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a\\b")]
    public void Duplicate_PathLikeId_ThrowsAndWritesNothing(string id)
    {
        ThemeDefinition theme = ThemeParser.Parse(ValidTheme, id, ThemeSource.User, null).Theme!;

        Assert.Throws<ArgumentException>(() => CreateCatalog().Duplicate(theme));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Duplicate_DeviceNameId_WritesAPlainCopyInsideTheUserFolder()
    {
        ThemeDefinition theme = ThemeParser.Parse(ValidTheme, "con", ThemeSource.User, null).Theme!;

        ThemeDefinition copy = CreateCatalog().Duplicate(theme);

        Assert.Equal(Path.Combine(UserFolder, "con-2" + ThemeCatalog.FileSuffix), copy.FilePath);
        Assert.Equal(["con-2.yamv-theme.json"], UserFileNames());
    }

    public static TheoryData<string, ThemeValue> InvalidValues =>
        new()
        {
            { "accent", new NumberValue(3) },
            { "bodySize", new NumberValue(99) },
            { "bodyFont", new FontValue("Inter\nBold") },
            { "h2Rule", new ColorValue("#fff") },
        };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Save_InvalidTheme_ThrowsAndLeavesTheFileUnchanged(string token, ThemeValue value)
    {
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;
        var tokens = new Dictionary<string, ThemeValue>(theme.SharedTokens) { [token] = value };

        ThemeException exception = Assert.Throws<ThemeException>(() => catalog.Save(theme with { SharedTokens = tokens }));

        Assert.StartsWith($"Token '{token}' in shared: ", Assert.Single(exception.Errors), StringComparison.Ordinal);
        Assert.Equal(ValidTheme, File.ReadAllText(path));
    }

    [Fact]
    public void Save_EmptyNameOrNoVariant_Throws()
    {
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;

        Assert.Equal(["The theme has no 'name'."], Assert.Throws<ThemeException>(() => catalog.Save(theme with { Name = " " })).Errors);
        Assert.Equal(
            ["The theme has neither a 'light' nor a 'dark' variant."],
            Assert.Throws<ThemeException>(() => catalog.Save(theme with { Light = null })).Errors
        );
        Assert.Equal(ValidTheme, File.ReadAllText(path));
    }

    [Fact]
    public void Duplicate_InvalidTheme_ThrowsAndWritesNothing()
    {
        ThemeDefinition theme = ThemeParser.Parse(ValidTheme, "mine", ThemeSource.User, null).Theme!;

        Assert.Throws<ThemeException>(() => CreateCatalog().Duplicate(theme with { Light = null }));

        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Save_MoveFails_SurfacesTheErrorAndLeavesTheOriginal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle holds open.");
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;

        Exception exception;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            exception = Assert.ThrowsAny<Exception>(() => catalog.Save(theme with { Name = "Renamed" }));
        }

        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());
        Assert.Equal(ValidTheme, File.ReadAllText(path));
        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
        Assert.Contains(_log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("retrying", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAll_UnreadableUserFile_IsListedWithTheReadError()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces FileShare.None against other readers.");
        string path = WriteUserFile("mine", ValidTheme);

        IReadOnlyList<ThemeEntry> entries;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            entries = CreateCatalog().LoadAll();
        }

        ThemeEntry mine = Assert.Single(entries, entry => entry.Id == "mine");
        Assert.Null(mine.Result.Theme);
        Assert.StartsWith("The file cannot be read: ", Assert.Single(mine.Result.Errors), StringComparison.Ordinal);
        Assert.Contains(_log.Entries, entry => entry.Level == "error" && entry.Exception is IOException);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Save_RejectsNonFiniteNumber(double number)
    {
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme!;
        var tokens = new Dictionary<string, ThemeValue>(theme.SharedTokens) { ["bodySize"] = new NumberValue(number) };

        ThemeException exception = Assert.Throws<ThemeException>(() => catalog.Save(theme with { SharedTokens = tokens }));

        Assert.StartsWith("Token 'bodySize' in shared: ", Assert.Single(exception.Errors), StringComparison.Ordinal);
        Assert.Equal(ValidTheme, File.ReadAllText(path));
    }

    [Fact]
    public void Save_RejectsLoneSurrogateInName()
    {
        string path = WriteUserFile("mine", ValidTheme);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = catalog.LoadAll().Single(entry => entry.Id == "mine").Result.Theme! with { Name = "Bad \ud800" };

        ThemeException saved = Assert.Throws<ThemeException>(() => catalog.Save(theme));
        ThemeException duplicated = Assert.Throws<ThemeException>(() => catalog.Duplicate(theme));

        Assert.Equal(["The theme's 'name' is not valid text."], saved.Errors);
        Assert.Equal(["The theme's 'name' is not valid text."], duplicated.Errors);
        Assert.Equal(ValidTheme, File.ReadAllText(path));
        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Import_LongValidFileName_Succeeds()
    {
        string id = new('t', 255 - ThemeCatalog.FileSuffix.Length);
        string source = WriteSourceFile(id + ThemeCatalog.FileSuffix, ValidTheme);

        ThemeEntry entry = CreateCatalog().Import(source);

        Assert.Equal(id, entry.Id);
        Assert.Equal([id + ThemeCatalog.FileSuffix], UserFileNames());
    }

    [Fact]
    public void LoadAll_SweepsStaleTempsOnly()
    {
        Directory.CreateDirectory(UserFolder);
        DateTime stale = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        string staleMatch = WriteTempFile("." + Guid.NewGuid().ToString("N") + ".tmp", stale);
        string freshMatch = WriteTempFile("." + Guid.NewGuid().ToString("N") + ".tmp", DateTime.UtcNow);
        string staleOther = WriteTempFile("x.tmp", stale);

        CreateCatalog().LoadAll();

        Assert.False(File.Exists(staleMatch));
        Assert.True(File.Exists(freshMatch));
        Assert.True(File.Exists(staleOther));
        Assert.Contains(_log.Entries, entry => entry.Level == "info" && entry.Message.Contains(staleMatch, StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAll_DeviceNamedFile_IsInvalid()
    {
        Directory.CreateDirectory(UserFolder);
        string device = Path.Combine(Path.GetFullPath(UserFolder), "con" + ThemeCatalog.FileSuffix);
        string devicePath = OperatingSystem.IsWindows() ? @"\\?\" + device : device;
        File.WriteAllText(devicePath, ValidTheme);
        try
        {
            IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

            ThemeEntry con = Assert.Single(entries, entry => entry.Source == ThemeSource.User);
            Assert.Equal("con", con.Id);
            Assert.Null(con.Result.Theme);
            Assert.Equal(["\"con.yamv-theme.json\" uses a name Windows reserves for a device; rename the file."], con.Result.Errors);
        }
        finally
        {
            File.Delete(devicePath);
        }
    }

    [Fact]
    public void LoadAll_FileOverTheCap_IsInvalidWithTheSizeError()
    {
        string big = ThemeOverTheCap();
        Assert.Equal(ThemeCatalog.MaxFileBytes + 1, Encoding.UTF8.GetByteCount(big));
        string path = WriteUserFile("big", big);

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        ThemeEntry entry = Assert.Single(entries, entry => entry.Id == "big");
        Assert.Null(entry.Result.Theme);
        Assert.Equal([$"The file cannot be read: {SizeError}"], entry.Result.Errors);
        Assert.Contains(
            _log.Entries,
            logged => logged.Level == "warning" && logged.Message == $"Cannot read the theme file {path}: it is larger than 1 MB."
        );
        Assert.DoesNotContain(_log.Entries, logged => logged.Level == "error");
    }

    [Fact]
    public void LoadAll_FileExactlyAtTheCap_IsRead()
    {
        string padded = ValidTheme + new string(' ', ThemeCatalog.MaxFileBytes - ValidTheme.Length);
        Assert.Equal(ThemeCatalog.MaxFileBytes, Encoding.UTF8.GetByteCount(padded));
        WriteUserFile("mine", padded);

        ThemeEntry entry = Assert.Single(CreateCatalog().LoadAll(), entry => entry.Id == "mine");

        Assert.Empty(entry.Result.Errors);
        Assert.Equal("Mine", entry.Result.Theme!.Name);
    }

    [Fact]
    public void LoadAll_FileOverTheCapInBytesOnly_IsInvalidWithTheSizeError()
    {
        string padded = ValidTheme + new string('é', (ThemeCatalog.MaxFileBytes - ValidTheme.Length) / 2 + 1);
        Assert.True(padded.Length < ThemeCatalog.MaxFileBytes);
        Assert.True(Encoding.UTF8.GetByteCount(padded) > ThemeCatalog.MaxFileBytes);
        WriteUserFile("wide", padded);

        ThemeEntry entry = Assert.Single(CreateCatalog().LoadAll(), entry => entry.Id == "wide");

        Assert.Null(entry.Result.Theme);
        Assert.Equal([$"The file cannot be read: {SizeError}"], entry.Result.Errors);
    }

    [Fact]
    public void Import_SourceOpenForWritingByAnotherHandle_StillImports()
    {
        string source = WriteSourceFile("mine.yamv-theme.json", ValidTheme);

        ThemeEntry entry;
        using (new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            entry = CreateCatalog().Import(source);
        }

        Assert.Equal("mine", entry.Id);
        Assert.Equal(ValidTheme, File.ReadAllText(Path.Combine(UserFolder, "mine.yamv-theme.json")));
    }

    [Fact]
    public void Import_FileWithABom_ImportsItWithoutTheBom()
    {
        string source = WriteSourceFile("mine.yamv-theme.json", ValidTheme);
        File.WriteAllBytes(source, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(ValidTheme)]);

        ThemeEntry entry = CreateCatalog().Import(source);

        Assert.Equal(Encoding.UTF8.GetBytes(ValidTheme), File.ReadAllBytes(entry.FilePath!));
    }

    [Fact]
    public void Import_FileOverTheCap_ThrowsAndWritesNothing()
    {
        string source = WriteSourceFile("big.yamv-theme.json", ThemeOverTheCap());

        ThemeException exception = Assert.Throws<ThemeException>(() => CreateCatalog().Import(source));

        string error = $"{source} is larger than 1 MB; a theme file is a few KB.";
        Assert.Equal(error, exception.Message);
        Assert.Equal([error], exception.Errors);
        Assert.False(Directory.Exists(UserFolder));
    }

    [Fact]
    public void Inspect_FileOverTheCap_ReportsTheSizeError()
    {
        ThemeInspection inspection = ThemeCatalog.Inspect(WriteSourceFile("big.yamv-theme.json", ThemeOverTheCap()));

        Assert.Equal("big.yamv-theme.json", inspection.FileName);
        Assert.Null(inspection.Name);
        Assert.Equal([$"The file cannot be read: {SizeError}"], inspection.Errors);
    }

    [Fact]
    public void LoadAll_FileThatVanishesBeforeItIsRead_IsSkipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only Unix lets a symbolic link point at a file that is not there.");
        Directory.CreateDirectory(UserFolder);
        string gone = Path.Combine(UserFolder, "gone" + ThemeCatalog.FileSuffix);
        File.CreateSymbolicLink(gone, Path.Combine(UserFolder, "missing" + ThemeCatalog.FileSuffix));

        IReadOnlyList<ThemeEntry> entries = CreateCatalog().LoadAll();

        Assert.DoesNotContain(entries, entry => entry.Id == "gone");
        Assert.Contains(
            _log.Entries,
            logged => logged.Level == "info" && logged.Message == $"The theme file {gone} went away while it was being read; skipping it."
        );
        Assert.DoesNotContain(_log.Entries, logged => logged.Level == "error");
    }

    [Fact]
    public void LoadAll_IdWithBackslash_IsInvalidWithTheRenameError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only Unix lets a file name hold a backslash.");
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, "a\\b" + ThemeCatalog.FileSuffix), ValidTheme);

        ThemeEntry entry = Assert.Single(CreateCatalog().LoadAll(), entry => entry.Source == ThemeSource.User);

        Assert.Equal("a\\b", entry.Id);
        Assert.Null(entry.Result.Theme);
        Assert.StartsWith(
            "The file name 'a\\b.yamv-theme.json' gives no usable theme id",
            Assert.Single(entry.Result.Errors),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void LoadAll_IdWithControlCharacter_IsInvalidWithTheRenameError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only Unix lets a file name hold a control character.");
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, "a\u0001b" + ThemeCatalog.FileSuffix), ValidTheme);

        ThemeEntry entry = Assert.Single(CreateCatalog().LoadAll(), entry => entry.Source == ThemeSource.User);

        Assert.Equal("a\u0001b", entry.Id);
        Assert.Null(entry.Result.Theme);
        Assert.StartsWith(
            "The file name 'a\u0001b.yamv-theme.json' gives no usable theme id",
            Assert.Single(entry.Result.Errors),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(".yamv-theme.json", "")]
    [InlineData("..yamv-theme.json", ".")]
    public void LoadAll_FileWhoseIdIsNotValid_IsInvalidWithTheRenameError(string fileName, string id)
    {
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, fileName), "not a theme");

        ThemeEntry entry = Assert.Single(CreateCatalog().LoadAll(), entry => entry.Source == ThemeSource.User);

        Assert.Equal(id, entry.Id);
        Assert.Null(entry.Result.Theme);
        Assert.StartsWith($"The file name '{fileName}' gives no usable theme id", Assert.Single(entry.Result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAtMost_EndlessStream_StopsOneByteOverTheCap()
    {
        var stream = new EndlessStream();

        Assert.Throws<ThemeFileTooLargeException>(() => ThemeCatalog.ReadAtMost(stream));

        Assert.Equal(ThemeCatalog.MaxFileBytes + 1L, stream.BytesServed);
    }

    [Fact]
    public void ReadAtMost_StreamOfExactlyTheCap_ReturnsEveryByte()
    {
        byte[] content = new byte[ThemeCatalog.MaxFileBytes];

        Assert.Equal(content, ThemeCatalog.ReadAtMost(new MemoryStream(content)));
    }

    /// <summary>A stream that hands out its byte forever and cannot tell its length or seek.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesServed += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }
    }

    /// <summary>A valid theme file text one byte over the cap, its description carrying the padding.</summary>
    private static string ThemeOverTheCap()
    {
        const string Prefix = "{ \"name\": \"Big\", \"description\": \" ";
        const string Suffix = "\", \"light\": {} }";
        return Prefix + new string('x', ThemeCatalog.MaxFileBytes + 1 - Prefix.Length - Suffix.Length) + Suffix;
    }

    private string WriteTempFile(string fileName, DateTime lastWriteUtc)
    {
        string path = Path.Combine(UserFolder, fileName);
        File.WriteAllText(path, "partial");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private sealed class RecordingTrash : IFileTrash
    {
        public List<string> Paths { get; } = [];

        public void MoveToTrash(string path) => Paths.Add(path);
    }

    private string[] UserFileNames() => [.. Directory.GetFiles(UserFolder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
}
