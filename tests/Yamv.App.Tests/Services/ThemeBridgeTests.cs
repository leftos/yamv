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

    private ThemeBridge CreateBridge() => new(new ThemeCatalog(_themesFolder, _log), Application.Current!, _log);

    private ResolvedTheme Resolve(string id, Appearance appearance)
    {
        ThemeEntry entry = new ThemeCatalog(_themesFolder, _log)
            .LoadAll()
            .First(candidate => candidate.Id == id && candidate.Result.Theme is not null);
        return ThemeResolver.Resolve(entry.Result.Theme!, appearance);
    }
}
