using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeTokenViewTests
{
    private static readonly ThemeDefinition Layered = ThemeParser
        .Parse(
            """
            {
              "name": "T",
              "tokens": { "accent": "#111111", "text": "#222222" },
              "light": { "tokens": { "accent": "#333333", "line": "#444444" } },
              "dark": { "tokens": { "heading": "#555555" } }
            }
            """,
            "t",
            ThemeSource.User,
            null
        )
        .Theme!;

    private static ThemeTokenRow Row(IReadOnlyList<ThemeTokenRow> rows, string name) => rows.Single(row => row.Token.Name == name);

    [Fact]
    public void Build_GivesOneRowPerTokenInCatalogueOrder()
    {
        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(Layered, ThemeTab.Light, Appearance.Light);

        Assert.Equal(ThemeTokens.All, rows.Select(row => row.Token));
        Assert.Equal("Selected row", Row(rows, "selectionBackground").Token.Label);
    }

    [Fact]
    public void Build_VariantTab_VariantOverSharedOverDefault()
    {
        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(Layered, ThemeTab.Light, Appearance.Dark);

        Assert.Equal(new ThemeTokenRow(ThemeTokens.Find("accent")!, new ColorValue("#333333"), TokenLayer.Variant, true), Row(rows, "accent"));
        Assert.Equal(new ThemeTokenRow(ThemeTokens.Find("line")!, new ColorValue("#444444"), TokenLayer.Variant, true), Row(rows, "line"));
        Assert.Equal(new ThemeTokenRow(ThemeTokens.Find("text")!, new ColorValue("#222222"), TokenLayer.Shared, false), Row(rows, "text"));
        ThemeTokenRow page = Row(rows, "pageBackground");
        Assert.Equal((new ColorValue("#ffffff"), TokenLayer.Default, false), (page.Value, page.Layer, page.SetHere));
    }

    [Fact]
    public void Build_DarkTab_UsesTheDarkDefaults()
    {
        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(Layered, ThemeTab.Dark, Appearance.Light);

        Assert.Equal(
            (new ColorValue("#555555"), TokenLayer.Variant, true),
            (Row(rows, "heading").Value, Row(rows, "heading").Layer, Row(rows, "heading").SetHere)
        );
        Assert.Equal((new ColorValue("#111111"), TokenLayer.Shared), (Row(rows, "accent").Value, Row(rows, "accent").Layer));
        Assert.Equal(new ColorValue("#0f1318"), Row(rows, "pageBackground").Value);
    }

    [Theory]
    [InlineData(Appearance.Light, "#ffffff")]
    [InlineData(Appearance.Dark, "#0f1318")]
    public void Build_SharedTab_IsSharedOverTheShownAppearancesDefaults(Appearance shown, string page)
    {
        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(Layered, ThemeTab.Shared, shown);

        ThemeTokenRow accent = Row(rows, "accent");
        Assert.Equal((new ColorValue("#111111"), TokenLayer.Shared, true), (accent.Value, accent.Layer, accent.SetHere));
        ThemeTokenRow background = Row(rows, "pageBackground");
        Assert.Equal((new ColorValue(page), TokenLayer.Default, false), (background.Value, background.Layer, background.SetHere));
        Assert.DoesNotContain(rows, row => row.Layer == TokenLayer.Variant);
    }

    [Fact]
    public void Build_TabForAMissingHalf_IsAllInherited()
    {
        ThemeDefinition lightOnly = Layered with { Dark = null };

        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(lightOnly, ThemeTab.Dark, Appearance.Light);

        Assert.All(rows, row => Assert.False(row.SetHere, row.Token.Name));
        Assert.Equal((new ColorValue("#111111"), TokenLayer.Shared), (Row(rows, "accent").Value, Row(rows, "accent").Layer));
        Assert.Equal(new ColorValue("#0f1318"), Row(rows, "pageBackground").Value);
    }

    [Theory]
    [InlineData(ThemeTab.Light, Appearance.Light)]
    [InlineData(ThemeTab.Dark, Appearance.Dark)]
    public void Build_VariantTab_MatchesTheResolver(ThemeTab tab, Appearance appearance)
    {
        IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(Layered, tab, appearance);

        IReadOnlyDictionary<string, ThemeValue> resolved = ThemeResolver.Resolve(Layered, appearance).Tokens;
        Assert.All(rows, row => Assert.Equal(resolved[row.Token.Name], row.Value));
    }
}
