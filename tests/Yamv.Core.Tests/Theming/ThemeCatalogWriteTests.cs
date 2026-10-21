using Yamv.Core.Tests.Session;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeCatalogWriteTests : IDisposable
{
    private const string Commented = """
        {
          // my theme
          "light": {
            "tokens": {
              "accent": "#123456", /* blue */
              "text": "#222222"
            }
          },
          "extra": { "keep": true },
          "name": "Mine"
        }

        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-theme-writes-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string UserFolder => Path.Combine(_root, "themes");

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

    private static ThemeDefinition Load(ThemeCatalog catalog, string id) => catalog.LoadAll().Single(entry => entry.Id == id).Result.Theme!;

    private string[] UserFileNames() => [.. Directory.GetFiles(UserFolder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Save_ChangedValue_KeepsCommentsOrderAndUnknownFields()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "accent", new ColorValue("#ABCDEF")));

        Assert.Equal(Commented.Replace("#123456", "#abcdef", StringComparison.Ordinal), File.ReadAllText(path));
        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Save_AddedTokens_GoBesideTheirNeighbours()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "line", new ColorValue("#444444"));
        theme = ThemeEditing.SetToken(theme, ThemeTab.Light, "heading", new ColorValue("#555555"));

        catalog.Save(theme);

        Assert.Equal(
            """
            {
              // my theme
              "light": {
                "tokens": {
                  "accent": "#123456", /* blue */
                  "line": "#444444",
                  "text": "#222222",
                  "heading": "#555555"
                }
              },
              "extra": { "keep": true },
              "name": "Mine"
            }

            """,
            File.ReadAllText(path)
        );
    }

    [Fact]
    public void Save_TokenInAMissingDarkSection_CreatesTheSection()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeEditing.AddVariant(Load(catalog, "mine"), Appearance.Dark);

        catalog.Save(ThemeEditing.SetToken(theme, ThemeTab.Dark, "accent", new ColorValue("#abcdef")));

        Assert.Equal(
            """
            {
              // my theme
              "light": {
                "tokens": {
                  "accent": "#123456", /* blue */
                  "text": "#222222"
                }
              },
              "dark": {
                "tokens": {
                  "accent": "#abcdef"
                }
              },
              "extra": { "keep": true },
              "name": "Mine"
            }

            """,
            File.ReadAllText(path)
        );
    }

    [Fact]
    public void Save_EmptyNewHalf_IsAnEmptyObject()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.AddVariant(Load(catalog, "mine"), Appearance.Dark));

        Assert.Equal(Commented.Replace("  },\n  \"extra\"", "  },\n  \"dark\": {},\n  \"extra\"", StringComparison.Ordinal), File.ReadAllText(path));
        Assert.NotNull(Load(catalog, "mine").Dark);
    }

    [Fact]
    public void Save_SingleLineFile_InsertsOnTheSameLine()
    {
        string path = WriteUserFile("mine", """{ "name": "Mine", "light": { "tokens": { "accent": "#123456" } } }""");
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Shared, "text", new ColorValue("#222222")));

        Assert.Equal(
            """{ "name": "Mine", "tokens": { "text": "#222222" }, "light": { "tokens": { "accent": "#123456" } } }""",
            File.ReadAllText(path)
        );
    }

    private const string ThreeTokens = """
        {
          "name": "Mine",
          "light": {
            "tokens": {
              "text": "#111111",
              "heading": "#222222",
              "accent": "#333333"
            }
          }
        }

        """;

    [Theory]
    [InlineData("text", "      \"heading\": \"#222222\",\n      \"accent\": \"#333333\"\n")]
    [InlineData("heading", "      \"text\": \"#111111\",\n      \"accent\": \"#333333\"\n")]
    [InlineData("accent", "      \"text\": \"#111111\",\n      \"heading\": \"#222222\"\n")]
    public void Save_RemovedToken_GoesWithItsComma(string token, string expectedTokens)
    {
        string path = WriteUserFile("mine", ThreeTokens);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.ResetToken(Load(catalog, "mine"), ThemeTab.Light, token));

        string expected = "{\n  \"name\": \"Mine\",\n  \"light\": {\n    \"tokens\": {\n" + expectedTokens + "    }\n  }\n}\n";
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Save_ChangedNameAndAddedAuthor_ArePatched()
    {
        string path = WriteUserFile("mine", ThreeTokens);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(Load(catalog, "mine") with { Name = "Other", Author = "Ann" });

        Assert.Equal(
            ThreeTokens.Replace("\"name\": \"Mine\",", "\"name\": \"Other\",\n  \"author\": \"Ann\",", StringComparison.Ordinal),
            File.ReadAllText(path)
        );
    }

    [Fact]
    public void Save_DefinitionThePatchCannotExpress_ThrowsAndLeavesTheFile()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = Load(catalog, "mine");
        var tokens = new Dictionary<string, ThemeValue>(theme.Light!.Tokens) { ["notAToken"] = new ColorValue("#ffffff") };

        ThemeException exception = Assert.Throws<ThemeException>(() => catalog.Save(theme with { Light = theme.Light with { Tokens = tokens } }));

        Assert.Contains("could not be updated in place", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Commented, File.ReadAllText(path));
        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Save_TokenTwiceInItsSection_ThrowsAndLeavesTheFile()
    {
        const string twice = """{ "name": "Mine", "light": { "tokens": { "accent": "#111111", "accent": "#222222" } } }""";
        string path = WriteUserFile("mine", twice);
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeParser.Parse(ThreeTokens, "mine", ThemeSource.User, path).Theme!;

        ThemeException exception = Assert.Throws<ThemeException>(() => catalog.Save(theme));

        Assert.Contains("could not be updated in place: 'accent' appears twice in 'light.tokens'.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(twice, File.ReadAllText(path));
    }

    [Fact]
    public void Save_RepeatedKeyThatIsNotEdited_DoesNotBlockTheSave()
    {
        const string text = """{ "_comment": "a", "_comment": "b", "name": "Mine", "light": { "tokens": { "accent": "#123456" } } }""";
        string path = WriteUserFile("mine", text);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "accent", new ColorValue("#abcdef")));

        Assert.Equal(text.Replace("#123456", "#abcdef", StringComparison.Ordinal), File.ReadAllText(path));
    }

    [Fact]
    public void CheckPatch_TextThatDropsAnUnknownField_IsRefused()
    {
        string path = Path.Combine(UserFolder, "mine.yamv-theme.json");
        ThemeDefinition theme = ThemeParser.Parse(Commented, "mine", ThemeSource.User, path).Theme!;
        string dropped = Commented.Replace("  \"extra\": { \"keep\": true },\n", "", StringComparison.Ordinal);

        ThemeException exception = Assert.Throws<ThemeException>(() => ThemeCatalog.CheckPatch(Commented, dropped, path, theme));

        Assert.Contains("could not be updated in place", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Commented, ThemeCatalog.CheckPatch(Commented, Commented, path, theme));
    }

    [Fact]
    public void Save_OverAFileThatIsNotAValidTheme_ThrowsAndLeavesItByteForByte()
    {
        const string broken = """{ "name": "Mine", "light": { "tokens": { "accent": "#12" } } }""";
        string path = WriteUserFile("mine", broken);
        byte[] before = File.ReadAllBytes(path);
        ThemeDefinition theme = ThemeParser.Parse(ThreeTokens, "mine", ThemeSource.User, path).Theme!;

        ThemeException exception = Assert.Throws<ThemeException>(() => CreateCatalog().Save(theme));

        Assert.Contains("the file on disk is not a valid theme", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_CrlfFile_KeepsCrlf()
    {
        string path = WriteUserFile("mine", Commented.ReplaceLineEndings("\r\n"));
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "line", new ColorValue("#444444"));

        catalog.Save(ThemeEditing.SetToken(theme, ThemeTab.Light, "heading", new ColorValue("#555555")));

        string expected = """
            {
              // my theme
              "light": {
                "tokens": {
                  "accent": "#123456", /* blue */
                  "line": "#444444",
                  "text": "#222222",
                  "heading": "#555555"
                }
              },
              "extra": { "keep": true },
              "name": "Mine"
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\r\n"), File.ReadAllText(path));
    }

    [Fact]
    public void Save_TabIndentedFile_IndentsWithTabs()
    {
        string path = WriteUserFile(
            "mine",
            "{\n\t\"name\": \"Mine\",\n\t\"light\": {\n\t\t\"tokens\": {\n\t\t\t\"accent\": \"#123456\"\n\t\t}\n\t}\n}\n"
        );
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "text", new ColorValue("#222222"));
        theme = ThemeEditing.SetToken(ThemeEditing.AddVariant(theme, Appearance.Dark), ThemeTab.Dark, "accent", new ColorValue("#abcdef"));

        catalog.Save(theme);

        Assert.Equal(
            "{\n\t\"name\": \"Mine\",\n\t\"light\": {\n\t\t\"tokens\": {\n\t\t\t\"text\": \"#222222\",\n\t\t\t\"accent\": \"#123456\"\n\t\t}\n\t},\n"
                + "\t\"dark\": {\n\t\t\"tokens\": {\n\t\t\t\"accent\": \"#abcdef\"\n\t\t}\n\t}\n}\n",
            File.ReadAllText(path)
        );
    }

    [Fact]
    public void Save_TrailingCommas_SurviveARemovalAndAnInsert()
    {
        string path = WriteUserFile(
            "mine",
            "{\n  \"name\": \"Mine\",\n  \"light\": {\n    \"tokens\": {\n      \"text\": \"#111111\",\n"
                + "      \"accent\": \"#333333\",\n    },\n  },\n}\n"
        );
        ThemeCatalog catalog = CreateCatalog();
        ThemeDefinition theme = ThemeEditing.ResetToken(Load(catalog, "mine"), ThemeTab.Light, "accent");

        catalog.Save(ThemeEditing.SetToken(theme, ThemeTab.Light, "heading", new ColorValue("#555555")));

        Assert.Equal(
            "{\n  \"name\": \"Mine\",\n  \"light\": {\n    \"tokens\": {\n      \"text\": \"#111111\",\n"
                + "      \"heading\": \"#555555\",\n    },\n  },\n}\n",
            File.ReadAllText(path)
        );
    }

    [Fact]
    public void Save_InsertionPointInsideABlockComment_ThrowsAndLeavesTheFile()
    {
        const string text =
            "{\n  \"name\": \"Mine\",\n  \"light\": {\n    \"tokens\": {\n      \"accent\": \"#123456\", /* first line\n      second line */\n"
            + "      \"text\": \"#222222\"\n    }\n  }\n}\n";
        string path = WriteUserFile("mine", text);
        ThemeCatalog catalog = CreateCatalog();

        ThemeDefinition theme = ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "line", new ColorValue("#444444"));

        Assert.Throws<ThemeException>(() => catalog.Save(theme));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void SaveAndRename_KeepAByteOrderMark()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        Directory.CreateDirectory(UserFolder);
        string path = Path.Combine(UserFolder, "mine" + ThemeCatalog.FileSuffix);
        File.WriteAllBytes(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(Commented)]);
        ThemeCatalog catalog = CreateCatalog();

        catalog.Save(ThemeEditing.SetToken(Load(catalog, "mine"), ThemeTab.Light, "accent", new ColorValue("#abcdef")));
        ThemeDefinition renamed = catalog.Rename("mine", "Other");

        string expected = Commented
            .Replace("#123456", "#abcdef", StringComparison.Ordinal)
            .Replace("\"name\": \"Mine\"", "\"name\": \"Other\"", StringComparison.Ordinal);
        Assert.Equal([.. bom, .. System.Text.Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(renamed.FilePath!));
    }

    [Fact]
    public void Rename_MovesTheFileAndKeepsItsComments()
    {
        string old = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        ThemeDefinition renamed = catalog.Rename("mine", "My Theme!");

        string path = Path.Combine(UserFolder, "my-theme.yamv-theme.json");
        Assert.Equal(("my-theme", "My Theme!", path), (renamed.Id, renamed.Name, renamed.FilePath));
        Assert.False(File.Exists(old));
        Assert.Equal(["my-theme.yamv-theme.json"], UserFileNames());
        Assert.Equal(Commented.Replace("\"name\": \"Mine\"", "\"name\": \"My Theme!\"", StringComparison.Ordinal), File.ReadAllText(path));
        Assert.True(Load(catalog, "my-theme").ContentEquals(renamed));
    }

    [Fact]
    public void Rename_TakenSlug_GetsANumber()
    {
        WriteUserFile("my-theme", Commented);
        WriteUserFile("mine", Commented);

        ThemeDefinition renamed = CreateCatalog().Rename("mine", "My theme");

        Assert.Equal("my-theme-2", renamed.Id);
        Assert.Equal(["my-theme-2.yamv-theme.json", "my-theme.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Rename_ToStudio_GetsStudio2()
    {
        WriteUserFile("mine", Commented);

        Assert.Equal("studio-2", CreateCatalog().Rename("mine", "Studio").Id);
        Assert.Equal(["studio-2.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Rename_UserFileShadowingABuiltIn_MovesItAndTheBuiltInReturns()
    {
        WriteUserFile("studio", Commented);
        ThemeCatalog catalog = CreateCatalog();

        Assert.Equal("night", catalog.Rename("studio", "Night").Id);

        Assert.Equal(["night.yamv-theme.json"], UserFileNames());
        Assert.Equal(ThemeSource.BuiltIn, catalog.LoadAll().Single(entry => entry.Id == "studio").Source);
    }

    [Fact]
    public void Rename_ToANameThatSlugsToItsOwnId_KeepsTheFile()
    {
        string path = WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        Assert.Equal("mine", catalog.Rename("mine", "MINE").Id);

        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
        Assert.Equal(Commented.Replace("\"name\": \"Mine\"", "\"name\": \"MINE\"", StringComparison.Ordinal), File.ReadAllText(path));
    }

    [Fact]
    public void Rename_ThemeFiledUnderANumberedSlug_KeepsItsNumber()
    {
        WriteUserFile("mine", Commented);
        WriteUserFile("mine-3", Commented.Replace("\"Mine\"", "\"Old\"", StringComparison.Ordinal));

        Assert.Equal("mine-3", CreateCatalog().Rename("mine-3", "Mine").Id);
        Assert.Equal(["mine-3.yamv-theme.json", "mine.yamv-theme.json"], UserFileNames());
    }

    [Fact]
    public void Rename_UnchangedName_DoesNotRewriteTheFile()
    {
        string path = WriteUserFile("mine", Commented);
        DateTime written = DateTime.UtcNow - TimeSpan.FromDays(1);
        File.SetLastWriteTimeUtc(path, written);

        Assert.Equal("mine", CreateCatalog().Rename("mine", "Mine").Id);

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Rename_MoveFails_StaysRenamedUnderItsOldId()
    {
        string path = WriteUserFile("mine", Commented);
        Directory.CreateDirectory(Path.Combine(UserFolder, "other" + ThemeCatalog.FileSuffix));
        ThemeCatalog catalog = CreateCatalog();

        ThemeDefinition renamed = catalog.Rename("mine", "Other");

        Assert.Equal(("mine", "Other", path), (renamed.Id, renamed.Name, renamed.FilePath));
        Assert.Equal("Other", Load(catalog, "mine").Name);
        Assert.Contains(_log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("cannot move it", StringComparison.Ordinal));
    }

    [Fact]
    public void Rename_MissingTheme_ThrowsAndBlankName_IsRefused()
    {
        WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        Assert.Throws<FileNotFoundException>(() => catalog.Rename("other", "X"));
        Assert.Throws<ThemeException>(() => catalog.Rename("mine", " "));
        Assert.Equal(["mine.yamv-theme.json"], UserFileNames());
    }

    [Theory]
    [InlineData("My Theme!", "my-theme")]
    [InlineData("  Ink -- Night 2 ", "ink-night-2")]
    [InlineData("!!!", "theme")]
    [InlineData("Ελληνικά", "theme")]
    [InlineData("Con", "con")]
    public void Slug_LowerCasesAndJoinsWithDashes(string name, string slug) => Assert.Equal(slug, ThemeIds.Slug(name));

    [Fact]
    public void Slug_LongName_IsCutTo64WithNoTrailingDash()
    {
        string name = new string('A', 63) + " " + new string('b', 236);

        Assert.Equal(300, name.Length);
        Assert.Equal(new string('a', 63), ThemeIds.Slug(name));
        Assert.Equal(ThemeIds.MaxSlugLength, ThemeIds.Slug(new string('c', 300)).Length);
    }

    [Theory]
    [InlineData("!!!", "theme")]
    [InlineData("Con", "con-2")]
    public void Rename_EmptyOrDeviceSlug_IsFiledSafely(string name, string id)
    {
        WriteUserFile("mine", Commented);
        ThemeCatalog catalog = CreateCatalog();

        Assert.Equal(id, catalog.Rename("mine", name).Id);
        Assert.Equal(name, Load(catalog, id).Name);
    }
}
