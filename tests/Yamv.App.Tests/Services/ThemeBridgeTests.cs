using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Theming;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;

namespace Yamv.App.Tests.Services;

public sealed class ThemeBridgeTests : IDisposable
{
    private readonly string _themesFolder = Path.Combine(Path.GetTempPath(), "yamv-bridge-themes-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_themesFolder))
        {
            Directory.Delete(_themesFolder, recursive: true);
        }
    }

    [Fact]
    public Task Apply_writes_brushes_for_every_chrome_token_from_argb_channels() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();

            bridge.Apply("studio", "dark");

            var palette = ChromePalette.From(Resolve("studio", Appearance.Dark));
            AssertColoursOf(Resolve("studio", Appearance.Dark));
            SolidColorBrush warning = Assert.IsType<SolidColorBrush>(Lookup("YamvWarningBackground"));
            Assert.Equal(Color.FromArgb(0x26, 0xbb, 0x80, 0x09), warning.Color);
            FontFamily font = Assert.IsType<FontFamily>(Lookup(ThemeBridge.UiFontKey));
            Assert.Equal(palette.UiFont, font.FamilyNames.PrimaryFamilyName);
            Assert.Equal(2, font.FamilyNames.Count);
        });

    [Fact]
    public Task Apply_replaces_brush_instances_on_theme_change() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            bridge.Apply("studio", "light");
            object? before = Lookup("YamvAccent");

            bridge.Apply("paper", "light");

            SolidColorBrush after = Assert.IsType<SolidColorBrush>(Lookup("YamvAccent"));
            Assert.NotSame(before, after);
            Assert.Equal(Color.FromUInt32(ChromePalette.From(Resolve("paper", Appearance.Light)).Colors["accent"]), after.Color);
            Assert.Single(
                Application.Current!.Resources.MergedDictionaries,
                provider => provider is ResourceDictionary { } dictionary && dictionary.ContainsKey("YamvAccent")
            );
        });

    [Fact]
    public Task Apply_prefers_the_valid_entry_when_an_invalid_user_file_shares_the_id() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            Directory.CreateDirectory(_themesFolder);
            File.WriteAllText(Path.Combine(_themesFolder, "paper" + ThemeCatalog.FileSuffix), "{ this is not a theme");
            var catalog = new ThemeCatalog(_themesFolder, _log);
            Assert.Equal(2, catalog.LoadAll().Count(entry => entry.Id == "paper"));
            using var bridge = new ThemeBridge(catalog, Application.Current!, _log);

            bridge.Apply("paper", "light");

            AssertColoursOf(Resolve("paper", Appearance.Light));
            Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("instead", StringComparison.Ordinal));
        });

    [Fact]
    public Task Unknown_theme_id_falls_back_to_studio_and_logs_warning() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();

            bridge.Apply("no-such-theme", "light");

            AssertColoursOf(Resolve("studio", Appearance.Light));
            Assert.Contains(_log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("no-such-theme", StringComparison.Ordinal));
        });

    [Fact]
    public Task Apply_sends_theme_css_to_registered_surfaces() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            var surface = new FakeDocumentSurface();
            bridge.Register(surface);

            bridge.Apply("ink", "dark");

            Assert.Equal([ThemeCss.Build(Resolve("ink", Appearance.Dark))], surface.ThemeCss);
        });

    [Fact]
    public Task Register_sends_current_css_immediately() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            bridge.Apply("studio", "light");
            var surface = new FakeDocumentSurface();

            bridge.Register(surface);

            Assert.Equal([ThemeCss.Build(Resolve("studio", Appearance.Light))], surface.ThemeCss);
        });

    [Fact]
    public Task Light_and_dark_appearance_set_requested_theme_variant() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();

            bridge.Apply("studio", "light");
            Assert.Equal(AvaloniaThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            bridge.Apply("studio", "dark");
            Assert.Equal(AvaloniaThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        });

    /// <summary>Removes the brushes an earlier test's bridge left, so they cannot satisfy this test's lookups.</summary>
    private static void RemoveYamvResources()
    {
        IList<IResourceProvider> merged = Application.Current!.Resources.MergedDictionaries;
        foreach (IResourceProvider provider in merged.ToList())
        {
            if (provider is ResourceDictionary dictionary && dictionary.ContainsKey("YamvAccent"))
            {
                merged.Remove(provider);
            }
        }

        Assert.False(Application.Current.TryGetResource("YamvAccent", null, out _));
    }

    private static object? Lookup(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value), $"No resource {key}.");
        return value;
    }

    private static void AssertColoursOf(ResolvedTheme expected)
    {
        var palette = ChromePalette.From(expected);
        foreach (string token in ChromePalette.ColorTokens)
        {
            SolidColorBrush brush = Assert.IsType<SolidColorBrush>(Lookup(ThemeBridge.ResourceKey(token)));
            Assert.Equal(Color.FromUInt32(palette.Colors[token]), brush.Color);
        }
    }

    [Fact]
    public Task Requested_theme_before_any_apply_is_studio_and_system() =>
        Headless.Run(() =>
        {
            using ThemeBridge bridge = CreateBridge();

            Assert.Equal("studio", bridge.RequestedThemeId);
            Assert.Equal("system", bridge.RequestedAppearance);
        });

    [Fact]
    public Task Requested_theme_keeps_an_unknown_id_that_resolved_to_studio() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();

            bridge.Apply("nope", "dark");

            Assert.Equal("nope", bridge.RequestedThemeId);
            Assert.Equal("dark", bridge.RequestedAppearance);
            Assert.Equal("studio", bridge.Current!.Id);
        });

    [Fact]
    public Task Requested_appearance_other_than_light_or_dark_is_system() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();

            bridge.Apply("studio", "LIGHT");

            Assert.Equal("studio", bridge.RequestedThemeId);
            Assert.Equal("system", bridge.RequestedAppearance);
        });

    [Fact]
    public Task RequestChanged_is_raised_once_after_an_apply_that_changes_the_theme_id() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            List<(string ThemeId, string Appearance)> seen = [];
            bridge.RequestChanged += (_, _) => seen.Add((bridge.RequestedThemeId, bridge.RequestedAppearance));

            bridge.Apply("paper", "system");

            Assert.Equal([("paper", "system")], seen);
        });

    [Fact]
    public Task RequestChanged_is_raised_once_after_an_apply_that_changes_only_the_appearance() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            List<(string ThemeId, string Appearance)> seen = [];
            bridge.RequestChanged += (_, _) => seen.Add((bridge.RequestedThemeId, bridge.RequestedAppearance));

            bridge.Apply("studio", "dark");

            Assert.Equal([("studio", "dark")], seen);
        });

    [Fact]
    public Task RequestChanged_is_not_raised_after_an_apply_with_the_same_requested_values() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            int raised = 0;
            bridge.RequestChanged += (_, _) => raised++;

            bridge.Apply("studio", "system");
            bridge.Apply("studio", "LIGHT");
            Assert.Equal(0, raised);

            bridge.Apply("ink", "light");
            bridge.Apply("ink", "light");
            Assert.Equal(1, raised);
        });

    [Fact]
    public Task RequestChanged_for_an_unknown_id_sees_that_id_requested_while_studio_shows() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            List<(string ThemeId, string? Shown)> seen = [];
            bridge.RequestChanged += (_, _) => seen.Add((bridge.RequestedThemeId, bridge.Current?.Id));

            bridge.Apply("nope", "dark");

            Assert.Equal([("nope", "studio")], seen);
        });

    [Fact]
    public Task Reload_applies_a_changed_good_file_to_brushes_and_css() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge();
            var surface = new FakeDocumentSurface();
            bridge.Register(surface);
            bridge.Apply("mine", "light");

            WriteTheme("mine", "#445566");
            bridge.Reload();

            ResolvedTheme expected = Resolve("mine", Appearance.Light);
            AssertColoursOf(expected);
            Assert.Equal(Color.Parse("#445566"), Assert.IsType<SolidColorBrush>(Lookup("YamvAccent")).Color);
            Assert.Equal(2, surface.ThemeCss.Count);
            Assert.Equal(ThemeCss.Build(expected), surface.ThemeCss[^1]);
            Assert.Equal("mine", bridge.Current!.Id);
        });

    [Fact]
    public Task Reload_with_unchanged_content_swaps_no_brushes_and_sends_no_css() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge();
            var surface = new FakeDocumentSurface();
            bridge.Register(surface);
            bridge.Apply("mine", "light");
            ResolvedTheme? current = bridge.Current;
            object? brush = Lookup("YamvAccent");

            WriteTheme("mine", "#112233");
            bridge.Reload();

            Assert.Same(current, bridge.Current);
            Assert.Same(brush, Lookup("YamvAccent"));
            Assert.Single(surface.ThemeCss);
        });

    [Fact]
    public Task Reload_keeps_the_last_good_theme_when_the_file_turns_invalid() =>
        Headless.Run(() => AssertKeepsLastGood("mine", () => File.WriteAllText(ThemePath("mine"), "{ this is not a theme")));

    [Fact]
    public Task Reload_keeps_the_last_good_theme_when_the_file_is_deleted() =>
        Headless.Run(() => AssertKeepsLastGood("mine", () => File.Delete(ThemePath("mine"))));

    [Fact]
    public Task Reload_keeps_the_last_good_theme_when_the_themes_folder_is_deleted() =>
        Headless.Run(() => AssertKeepsLastGood("mine", () => Directory.Delete(_themesFolder, recursive: true)));

    [Fact]
    public Task Reload_switches_to_the_requested_theme_when_it_appears_after_a_studio_fallback() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            var surface = new FakeDocumentSurface();
            bridge.Register(surface);
            bridge.Apply("mine", "light");
            Assert.Equal("studio", bridge.Current!.Id);

            WriteTheme("mine", "#445566");
            bridge.Reload();

            ResolvedTheme expected = Resolve("mine", Appearance.Light);
            Assert.Equal("mine", bridge.Current!.Id);
            AssertColoursOf(expected);
            Assert.Equal(ThemeCss.Build(expected), surface.ThemeCss[^1]);
            Assert.Equal("mine", bridge.RequestedThemeId);
        });

    [Fact]
    public Task Reload_falls_back_to_studio_only_for_an_id_with_no_good_version() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge unknown = CreateBridge();
            unknown.Apply("nope", "light");

            unknown.Reload();

            Assert.Equal("studio", unknown.Current!.Id);
            AssertColoursOf(Resolve("studio", Appearance.Light));
            Assert.Single(_log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("nope", StringComparison.Ordinal));

            WriteTheme("mine", "#112233");
            using ThemeBridge known = CreateBridge();
            known.Apply("mine", "light");
            File.Delete(ThemePath("mine"));

            known.Reload();

            Assert.Equal("mine", known.Current!.Id);
            Assert.Equal(Color.Parse("#112233"), Assert.IsType<SolidColorBrush>(Lookup("YamvAccent")).Color);
        });

    [Fact]
    public Task Reload_keeps_a_user_override_of_a_builtin_when_it_turns_invalid() =>
        Headless.Run(() => AssertKeepsLastGood("paper", () => File.WriteAllText(ThemePath("paper"), "{ this is not a theme")));

    [Fact]
    public Task Reload_keeps_a_user_override_of_a_builtin_when_it_is_deleted() =>
        Headless.Run(() => AssertKeepsLastGood("paper", () => File.Delete(ThemePath("paper"))));

    [Fact]
    public Task Reload_logs_one_warning_per_bad_streak_and_a_recovery_line() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge();
            bridge.Apply("mine", "light");

            File.WriteAllText(ThemePath("mine"), "{ this is not a theme");
            bridge.Reload();
            bridge.Reload();
            File.Delete(ThemePath("mine"));
            bridge.Reload();
            Assert.Equal(1, MineEntries("warning"));
            Assert.Equal(0, MineEntries("info"));

            WriteTheme("mine", "#445566");
            bridge.Reload();
            bridge.Reload();
            Assert.Equal(1, MineEntries("warning"));
            Assert.Equal(1, MineEntries("info"));

            File.Delete(ThemePath("mine"));
            bridge.Reload();
            Assert.Equal(2, MineEntries("warning"));

            // A valid version applied through Apply ends the streak with the same line.
            WriteTheme("mine", "#778899");
            bridge.Apply("mine", "dark");
            Assert.Equal(2, MineEntries("warning"));
            Assert.Equal(2, MineEntries("info"));
        });

    [Fact]
    public Task Reload_does_not_raise_RequestChanged_nor_change_the_requested_values() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            using ThemeBridge bridge = CreateBridge();
            bridge.Apply("mine", "dark");
            int raised = 0;
            bridge.RequestChanged += (_, _) => raised++;

            WriteTheme("mine", "#112233");
            bridge.Reload();
            WriteTheme("mine", "#445566");
            bridge.Reload();
            File.WriteAllText(ThemePath("mine"), "{ this is not a theme");
            bridge.Reload();
            File.Delete(ThemePath("mine"));
            bridge.Reload();

            Assert.Equal(0, raised);
            Assert.Equal("mine", bridge.RequestedThemeId);
            Assert.Equal("dark", bridge.RequestedAppearance);
            Assert.Equal("mine", bridge.Current!.Id);
        });

    [Fact]
    public Task ThemesReloaded_is_raised_after_every_reload_with_the_new_current_theme() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge();
            bridge.Apply("mine", "light");
            List<ResolvedTheme?> seen = [];
            bridge.ThemesReloaded += (_, _) => seen.Add(bridge.Current);

            WriteTheme("mine", "#445566");
            bridge.Reload();

            ResolvedTheme shown = Assert.Single(seen)!;
            Assert.Equal(ThemeCss.Build(Resolve("mine", Appearance.Light)), ThemeCss.Build(shown));

            bridge.Reload();
            File.Delete(ThemePath("mine"));
            bridge.Reload();

            Assert.Equal(3, seen.Count);
            Assert.All(seen, current => Assert.Same(shown, current));
        });

    [Fact]
    public Task Reload_after_Dispose_does_nothing() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            ThemeBridge bridge = CreateBridge();
            bridge.Apply("mine", "light");
            ResolvedTheme? current = bridge.Current;
            object? brush = Lookup("YamvAccent");
            int raised = 0;
            bridge.ThemesReloaded += (_, _) => raised++;
            bridge.Dispose();

            WriteTheme("mine", "#445566");
            bridge.Reload();

            Assert.Same(current, bridge.Current);
            Assert.Same(brush, Lookup("YamvAccent"));
            Assert.Equal(0, raised);
        });

    [Fact]
    public Task Reload_keeps_following_the_system_appearance() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge(() => Appearance.Dark);
            bridge.Apply("mine", "system");

            WriteTheme("mine", "#445566");
            bridge.Reload();

            Assert.Equal(Appearance.Dark, bridge.Current!.Appearance);
            AssertColoursOf(Resolve("mine", Appearance.Dark));
            Assert.Equal(Color.Parse("#ddeeff"), Assert.IsType<SolidColorBrush>(Lookup("YamvAccent")).Color);
            Assert.Equal("system", bridge.RequestedAppearance);
        });

    [Fact]
    public Task Reload_keeps_a_fixed_dark_appearance() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge(() => Appearance.Light);
            bridge.Apply("mine", "dark");

            WriteTheme("mine", "#445566");
            bridge.Reload();

            Assert.Equal(Appearance.Dark, bridge.Current!.Appearance);
            AssertColoursOf(Resolve("mine", Appearance.Dark));
            Assert.Equal(Color.Parse("#ddeeff"), Assert.IsType<SolidColorBrush>(Lookup("YamvAccent")).Color);
            Assert.Equal("dark", bridge.RequestedAppearance);
        });

    [Fact]
    public Task FollowPlatform_reresolves_the_kept_theme_when_the_platform_flips() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            Appearance platform = Appearance.Light;
            using ThemeBridge bridge = CreateBridge(() => platform);
            bridge.Apply("mine", "system");
            ResolvedTheme keptDark = Resolve("mine", Appearance.Dark);
            File.WriteAllText(ThemePath("mine"), "{ this is not a theme");
            bridge.Reload();

            platform = Appearance.Dark;
            bridge.FollowPlatform();

            Assert.Equal("mine", bridge.Current!.Id);
            Assert.Equal(Appearance.Dark, bridge.Current.Appearance);
            AssertColoursOf(keptDark);
        });

    [Fact]
    public Task Apply_of_the_kept_id_during_a_bad_streak_keeps_the_last_good_version() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("mine", "#112233");
            using ThemeBridge bridge = CreateBridge(() => Appearance.Light);
            bridge.Apply("mine", "light");
            ResolvedTheme keptDark = Resolve("mine", Appearance.Dark);
            File.WriteAllText(ThemePath("mine"), "{ this is not a theme");
            bridge.Reload();

            bridge.Apply("mine", "dark");

            Assert.Equal("mine", bridge.Current!.Id);
            Assert.Equal(Appearance.Dark, bridge.Current.Appearance);
            AssertColoursOf(keptDark);
            Assert.Equal("dark", bridge.RequestedAppearance);
            Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("instead", StringComparison.Ordinal));
        });

    [Fact]
    public Task Apply_of_a_kept_builtin_override_during_a_bad_streak_keeps_the_users_version() =>
        Headless.Run(() =>
        {
            RemoveYamvResources();
            WriteTheme("paper", "#112233");
            using ThemeBridge bridge = CreateBridge(() => Appearance.Light);
            bridge.Apply("paper", "light");
            ResolvedTheme keptDark = Resolve("paper", Appearance.Dark);
            File.Delete(ThemePath("paper"));
            bridge.Reload();

            bridge.Apply("paper", "dark");

            Assert.Equal(Appearance.Dark, bridge.Current!.Appearance);
            AssertColoursOf(keptDark);
            Assert.Equal(Color.Parse("#ddeeff"), Assert.IsType<SolidColorBrush>(Lookup("YamvAccent")).Color);
        });

    /// <summary>
    /// Applies a user theme <paramref name="id"/>, breaks it on disk with <paramref name="breakIt"/>, reloads, and checks that the
    /// theme in use, its brushes, the css sent and the requested values are all as before.
    /// </summary>
    private void AssertKeepsLastGood(string id, Action breakIt)
    {
        RemoveYamvResources();
        WriteTheme(id, "#123456");
        using ThemeBridge bridge = CreateBridge();
        var surface = new FakeDocumentSurface();
        bridge.Register(surface);
        bridge.Apply(id, "light");
        ResolvedTheme expected = Resolve(id, Appearance.Light);
        ResolvedTheme? current = bridge.Current;
        object? brush = Lookup("YamvAccent");
        Assert.Equal(Color.Parse("#123456"), Assert.IsType<SolidColorBrush>(brush).Color);

        breakIt();
        bridge.Reload();

        Assert.Same(current, bridge.Current);
        Assert.Same(brush, Lookup("YamvAccent"));
        AssertColoursOf(expected);
        Assert.Single(surface.ThemeCss);
        Assert.Equal(id, bridge.RequestedThemeId);
        Assert.Equal("light", bridge.RequestedAppearance);
    }

    /// <summary>Counts the bridge's streak lines about <c>mine</c> at <paramref name="level"/>: the kept-version warning and the recovery line.</summary>
    private int MineEntries(string level) =>
        _log.Entries.Count(entry =>
            entry.Level == level
            && entry.Message
                is "The theme 'mine' is invalid or gone from the themes folder; keeping its last good version."
                    or "The theme 'mine' loads from the themes folder; using it."
        );

    private string ThemePath(string id) => Path.Combine(_themesFolder, id + ThemeCatalog.FileSuffix);

    /// <summary>Writes a valid user theme <paramref name="id"/> whose light accent is <paramref name="lightAccent"/>.</summary>
    private void WriteTheme(string id, string lightAccent)
    {
        Directory.CreateDirectory(_themesFolder);
        File.WriteAllText(
            ThemePath(id),
            "{ \"name\": \"Mine\", \"light\": { \"tokens\": { \"accent\": \""
                + lightAccent
                + "\" } }, \"dark\": { \"tokens\": { \"accent\": \"#ddeeff\" } } }"
        );
    }

    private ThemeBridge CreateBridge() => new(new ThemeCatalog(_themesFolder, _log), Application.Current!, _log);

    /// <summary>A bridge that reads the platform's light or dark setting from <paramref name="platformAppearance"/>.</summary>
    private ThemeBridge CreateBridge(Func<Appearance> platformAppearance) =>
        new(new ThemeCatalog(_themesFolder, _log), Application.Current!, _log, platformAppearance);

    private ResolvedTheme Resolve(string id, Appearance appearance)
    {
        ThemeEntry entry = new ThemeCatalog(_themesFolder, _log)
            .LoadAll()
            .First(candidate => candidate.Id == id && candidate.Result.Theme is not null);
        return ThemeResolver.Resolve(entry.Result.Theme!, appearance);
    }
}
