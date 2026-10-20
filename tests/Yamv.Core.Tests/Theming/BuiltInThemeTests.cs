using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class BuiltInThemeTests
{
    public static TheoryData<string> Ids => [.. ThemeCatalog.BuiltInIds];

    [Theory]
    [MemberData(nameof(Ids))]
    public void BuiltIn_ParsesCleanlyAndResolvesInBothAppearances(string id)
    {
        ThemeLoadResult result = ThemeParser.Parse(ThemeCatalog.ReadBuiltInText(id), id, ThemeSource.BuiltIn, null);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        ThemeDefinition theme = result.Theme!;
        Assert.NotNull(theme.Light);
        Assert.NotNull(theme.Dark);
        foreach (Appearance appearance in Enum.GetValues<Appearance>())
        {
            ResolvedTheme resolved = ThemeResolver.Resolve(theme, appearance);
            Assert.Equal(appearance, resolved.Appearance);
            Assert.Equal(ThemeTokens.All.Count, resolved.Tokens.Count);
        }
    }

    [Fact]
    public void Paper_And_Ink_SetTheirMockValues()
    {
        ThemeDefinition paper = ThemeParser.Parse(ThemeCatalog.ReadBuiltInText("paper"), "paper", ThemeSource.BuiltIn, null).Theme!;
        ThemeDefinition ink = ThemeParser.Parse(ThemeCatalog.ReadBuiltInText("ink"), "ink", ThemeSource.BuiltIn, null).Theme!;

        ResolvedTheme paperLight = ThemeResolver.Resolve(paper, Appearance.Light);
        Assert.Equal(new FontValue("Source Serif 4"), paperLight.Tokens["bodyFont"]);
        Assert.Equal(new NumberValue(17.5), paperLight.Tokens["bodySize"]);
        Assert.Equal(new BoolValue(false), paperLight.Tokens["h2Rule"]);
        Assert.Equal(new ColorValue("#a4542a"), paperLight.Tokens["accent"]);

        ResolvedTheme inkDark = ThemeResolver.Resolve(ink, Appearance.Dark);
        Assert.Equal(new FontValue("JetBrains Mono"), inkDark.Tokens["headingFont"]);
        Assert.Equal(new NumberValue(1.68), inkDark.Tokens["lineHeight"]);
        Assert.Contains("h1::before { content: \"# \"; color: var(--yamv-accent); }", inkDark.Css, StringComparison.Ordinal);
        Assert.Contains("text-transform: uppercase", inkDark.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedResources_AreExactlyTheBuiltInIds()
    {
        string[] themeResources =
        [
            .. typeof(ThemeCatalog)
                .Assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(ThemeCatalog.FileSuffix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(ThemeCatalog.BuiltInIds.Select(ThemeCatalog.ResourceName).Order(StringComparer.Ordinal), themeResources);
    }
}
