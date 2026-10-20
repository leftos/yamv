using System.Text.Json;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeCssTests
{
    private static string Build(string tokens, string css = "", Appearance appearance = Appearance.Light)
    {
        string variant = appearance == Appearance.Dark ? "dark" : "light";
        string json = $$"""{ "name": "T", "tokens": { {{tokens}} }, "css": {{JsonSerializer.Serialize(css)}}, "{{variant}}": {} }""";
        ThemeDefinition theme = ThemeParser.Parse(json, "t", ThemeSource.User, null).Theme!;
        return ThemeCss.Build(ThemeResolver.Resolve(theme, appearance));
    }

    [Fact]
    public void Build_StartsWithRootBlockOfEveryToken()
    {
        string css = Build("");

        Assert.StartsWith(":root {\n  --yamv-page-background: #ffffff;\n  --yamv-text: #1f2328;\n", css, StringComparison.Ordinal);
        foreach (TokenInfo token in ThemeTokens.All)
        {
            Assert.Contains("\n  " + token.CssVariable + ": ", css, StringComparison.Ordinal);
        }

        Assert.Contains("  --yamv-h2-rule: 1px solid var(--yamv-line);\n  color-scheme: light;\n}\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Numbers_GetTheirUnit()
    {
        string css = Build("\"bodySize\": 17.5, \"lineHeight\": 1.7, \"h1Size\": 2.5, \"headingWeight\": 650");

        Assert.Contains("  --yamv-body-size: 17.5px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --yamv-line-height: 1.7;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --yamv-h1-size: 2.5em;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --yamv-heading-weight: 650;\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FontWithControlCharacters_EscapesThemAsHex()
    {
        var theme = new ResolvedTheme(
            "t",
            "T",
            Appearance.Light,
            new Dictionary<string, ThemeValue>(ThemeTokens.DefaultsFor(Appearance.Light)) { ["bodyFont"] = new FontValue("A\nB\u007fC\u0001") },
            ""
        );

        Assert.Contains("  --yamv-body-font: \"A\\0a B\\7f C\\01 \", \"Inter V\", sans-serif;\n", ThemeCss.Build(theme), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "1px solid var(--yamv-line)")]
    [InlineData("false", "none")]
    public void Build_H2Rule_BothWays(string value, string expected) =>
        Assert.Contains($"  --yamv-h2-rule: {expected};\n", Build($"\"h2Rule\": {value}"), StringComparison.Ordinal);

    [Fact]
    public void Build_Fonts_AreQuotedEscapedAndFallBackToTheBundledFamily()
    {
        string css = Build("\"bodyFont\": \"My \\\"Odd\\\" \\\\Font\", \"monoFont\": \"Fira Code\"");

        Assert.Contains("  --yamv-body-font: \"My \\\"Odd\\\" \\\\Font\", \"Inter V\", sans-serif;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --yamv-mono-font: \"Fira Code\", \"JetBrains Mono V\", monospace;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --yamv-ui-font: \"Inter\", \"Inter V\", sans-serif;\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DarkAppearance_SetsDarkColorScheme() =>
        Assert.Contains("  color-scheme: dark;\n}\n", Build("", appearance: Appearance.Dark), StringComparison.Ordinal);

    [Fact]
    public void Build_AppendsTheThemeCssAfterTheRootBlock() =>
        Assert.EndsWith("}\nh1 { color: red; }", Build("", "h1 { color: red; }"), StringComparison.Ordinal);

    [Fact]
    public void Build_ClosingTagInCss_IsNeutralised()
    {
        string css = Build("\"bodyFont\": \"A</style>\"", "p {} </style><script>alert(1)</script>");

        Assert.DoesNotContain("</", css, StringComparison.Ordinal);
        Assert.EndsWith("p {} <\\/style><script>alert(1)<\\/script>", css, StringComparison.Ordinal);
    }
}
