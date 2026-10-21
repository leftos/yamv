using Yamv.Core.Tests.Session;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ChromePaletteTests
{
    private static readonly string[] ColorTokens =
    [
        "chromeBackground",
        "sidebarBackground",
        "pageBackground",
        "text",
        "textSecondary",
        "textMuted",
        "heading",
        "accent",
        "accentSoft",
        "line",
        "selectionBackground",
        "selectionText",
        "hoverBackground",
        "warning",
        "warningBackground",
        "warningLine",
    ];

    public static TheoryData<string, Appearance> BuiltInThemes()
    {
        var data = new TheoryData<string, Appearance>();
        foreach (string id in ThemeCatalog.BuiltInIds)
        {
            data.Add(id, Appearance.Light);
            data.Add(id, Appearance.Dark);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInThemes))]
    public void From_BuiltInTheme_HasEveryChromeColourAndAFont(string id, Appearance appearance)
    {
        var palette = ChromePalette.From(ThemeResolver.Resolve(BuiltIn(id), appearance));

        Assert.Equal(ColorTokens.Order(StringComparer.Ordinal), palette.Colors.Keys.Order(StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(palette.UiFont));
    }

    [Fact]
    public void From_EightDigitColour_PutsAlphaFirst()
    {
        ResolvedTheme resolved = ThemeResolver.Resolve(BuiltIn("studio"), Appearance.Dark);
        Assert.Equal("#bb800926", ((ColorValue)resolved.Tokens["warningBackground"]).Hex);

        var palette = ChromePalette.From(resolved);

        Assert.Equal(0x26BB8009u, palette.Colors["warningBackground"]);
    }

    [Fact]
    public void From_ThemeMissingAccent_ThrowsNamingTheToken()
    {
        var tokens = new Dictionary<string, ThemeValue>(ThemeTokens.DefaultsFor(Appearance.Light), StringComparer.Ordinal);
        tokens.Remove("accent");
        var resolved = new ResolvedTheme("broken", "Broken", Appearance.Light, tokens, "");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ChromePalette.From(resolved));

        Assert.Contains("accent", error.Message, StringComparison.Ordinal);
    }

    private static ThemeDefinition BuiltIn(string id)
    {
        var catalog = new ThemeCatalog(Path.Combine(Path.GetTempPath(), "yamv-no-themes-" + Guid.NewGuid().ToString("N")), new RecordingAppLog());
        ThemeEntry entry = catalog.LoadAll().Single(candidate => candidate.Id == id);
        return entry.Result.Theme ?? throw new InvalidOperationException($"The built-in theme {id} did not load.");
    }
}
