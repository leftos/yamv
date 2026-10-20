using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeResolverTests
{
    private static ThemeDefinition Theme(string json) => ThemeParser.Parse(json, "t", ThemeSource.User, null).Theme!;

    [Fact]
    public void Resolve_LayersDefaultsThenSharedThenVariant()
    {
        ThemeDefinition theme = Theme(
            """
            {
              "name": "T",
              "tokens": { "accent": "#111111", "text": "#222222" },
              "light": { "tokens": { "accent": "#333333" } }
            }
            """
        );

        ResolvedTheme resolved = ThemeResolver.Resolve(theme, Appearance.Light);

        Assert.Equal(new ColorValue("#333333"), resolved.Tokens["accent"]);
        Assert.Equal(new ColorValue("#222222"), resolved.Tokens["text"]);
        Assert.Equal(ThemeTokens.DefaultsFor(Appearance.Light)["pageBackground"], resolved.Tokens["pageBackground"]);
        Assert.Equal(ThemeTokens.All.Select(token => token.Name).Order(), resolved.Tokens.Keys.Order());
        Assert.Equal(("t", "T", Appearance.Light), (resolved.Id, resolved.Name, resolved.Appearance));
    }

    [Fact]
    public void Resolve_UsesTheDefaultsOfThePickedAppearance()
    {
        ThemeDefinition theme = Theme("""{ "name": "T", "light": {}, "dark": {} }""");

        Assert.Equal(new ColorValue("#ffffff"), ThemeResolver.Resolve(theme, Appearance.Light).Tokens["pageBackground"]);
        Assert.Equal(new ColorValue("#0f1318"), ThemeResolver.Resolve(theme, Appearance.Dark).Tokens["pageBackground"]);
    }

    [Fact]
    public void Resolve_LightOnlyTheme_GivesLightWhenDarkIsRequested()
    {
        ThemeDefinition theme = Theme("""{ "name": "T", "light": { "tokens": { "accent": "#333333" } } }""");

        ResolvedTheme resolved = ThemeResolver.Resolve(theme, Appearance.Dark);

        Assert.Equal(Appearance.Light, resolved.Appearance);
        Assert.Equal(new ColorValue("#333333"), resolved.Tokens["accent"]);
        Assert.Equal(new ColorValue("#ffffff"), resolved.Tokens["pageBackground"]);
    }

    [Fact]
    public void Resolve_DarkOnlyTheme_GivesDarkWhenLightIsRequested()
    {
        ThemeDefinition theme = Theme("""{ "name": "T", "dark": { "tokens": { "accent": "#333333" } } }""");

        ResolvedTheme resolved = ThemeResolver.Resolve(theme, Appearance.Light);

        Assert.Equal(Appearance.Dark, resolved.Appearance);
        Assert.Equal(new ColorValue("#333333"), resolved.Tokens["accent"]);
        Assert.Equal(new ColorValue("#0f1318"), resolved.Tokens["pageBackground"]);
    }

    [Theory]
    [InlineData("\"css\": \"  a {}\\n \", \"light\": { \"css\": \" b {} \" }", "a {}\nb {}")]
    [InlineData("\"css\": \"a {}\", \"light\": {}", "a {}")]
    [InlineData("\"light\": { \"css\": \"b {}\" }", "b {}")]
    [InlineData("\"light\": {}", "")]
    public void Resolve_Css_IsSharedThenVariantTrimmed(string body, string expected)
    {
        ThemeDefinition theme = Theme("{ \"name\": \"T\", " + body + " }");

        Assert.Equal(expected, ThemeResolver.Resolve(theme, Appearance.Light).Css);
    }
}
