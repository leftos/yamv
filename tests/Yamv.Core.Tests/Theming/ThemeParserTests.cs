using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeParserTests
{
    private static ThemeLoadResult Parse(string json) => ThemeParser.Parse(json, "test", ThemeSource.User, filePath: null);

    [Fact]
    public void Parse_CommentsAndTrailingCommas_AreAccepted()
    {
        ThemeLoadResult result = Parse(
            """
            {
              // a comment
              "$schema": "x", "formatVersion": 1,
              "name": "T", /* another */
              "light": { "tokens": { "accent": "#123456", }, },
            }
            """
        );

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("T", result.Theme!.Name);
        Assert.Equal(new ColorValue("#123456"), result.Theme.Light!.Tokens["accent"]);
        Assert.Null(result.Theme.Dark);
    }

    [Fact]
    public void Parse_InvalidJson_IsAnError()
    {
        ThemeLoadResult result = Parse("{ \"name\": ");

        Assert.Null(result.Theme);
        Assert.StartsWith("The file is not valid theme JSON: ", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_JsonNull_IsAnError()
    {
        ThemeLoadResult result = Parse("null");

        Assert.Null(result.Theme);
        Assert.Equal("The file is not valid theme JSON: it holds null instead of a theme object.", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("""{ "light": {} }""")]
    [InlineData("""{ "name": "  ", "light": {} }""")]
    public void Parse_MissingName_IsAnError(string json)
    {
        ThemeLoadResult result = Parse(json);

        Assert.Null(result.Theme);
        Assert.Equal("The theme has no 'name'.", Assert.Single(result.Errors));
    }

    [Fact]
    public void Parse_NoVariant_IsAnError()
    {
        ThemeLoadResult result = Parse("""{ "name": "T", "tokens": { "accent": "#fff" } }""");

        Assert.Null(result.Theme);
        Assert.Equal("The theme has neither a 'light' nor a 'dark' variant.", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("\"#12\"", "'#12'")]
    [InlineData("\"#1234\"", "'#1234'")]
    [InlineData("\"#ggg\"", "'#ggg'")]
    [InlineData("\"123456\"", "'123456'")]
    [InlineData("12", "12")]
    public void Parse_BadColour_IsAnErrorNamingTokenAndValue(string json, string shown)
    {
        ThemeLoadResult result = Parse($$"""{ "name": "T", "light": { "tokens": { "accent": {{json}} } } }""");

        Assert.Null(result.Theme);
        Assert.Equal($"Token 'accent' in light: {shown} is not a colour (#rgb, #rrggbb or #rrggbbaa).", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("bodySize", "40", "Token 'bodySize' in shared: 40 is out of range (10–32).")]
    [InlineData("bodySize", "9.5", "Token 'bodySize' in shared: 9.5 is out of range (10–32).")]
    [InlineData("lineHeight", "2.6", "Token 'lineHeight' in shared: 2.6 is out of range (1.0–2.5).")]
    [InlineData("h1Size", "0.5", "Token 'h1Size' in shared: 0.5 is out of range (1.0–4.0).")]
    [InlineData("headingWeight", "950", "Token 'headingWeight' in shared: 950 is out of range (100–900).")]
    public void Parse_NumberOutOfRange_IsAnError(string token, string value, string expected)
    {
        ThemeLoadResult result = Parse($$"""{ "name": "T", "tokens": { "{{token}}": {{value}} }, "dark": {} }""");

        Assert.Null(result.Theme);
        Assert.Equal(expected, Assert.Single(result.Errors));
    }

    [Fact]
    public void Parse_NumberAtRangeBounds_IsAccepted()
    {
        ThemeLoadResult result = Parse("""{ "name": "T", "tokens": { "bodySize": 10, "h1Size": 4.0 }, "dark": {} }""");

        Assert.Empty(result.Errors);
        Assert.Equal(new NumberValue(10), result.Theme!.SharedTokens["bodySize"]);
        Assert.Equal(new NumberValue(4), result.Theme.SharedTokens["h1Size"]);
    }

    [Theory]
    [InlineData("\"\\t\"", "'\\u0009'")]
    [InlineData("\"   \"", "'   '")]
    [InlineData("\"Inter\\nBold\"", "'Inter\\u000ABold'")]
    [InlineData("\"Inter\\u007f\"", "'Inter\\u007F'")]
    public void Parse_FontNameWhitespaceOnlyOrWithAControlCharacter_IsAnError(string value, string shown)
    {
        ThemeLoadResult result = Parse($$"""{ "name": "T", "dark": { "tokens": { "bodyFont": {{value}} } } }""");

        Assert.Null(result.Theme);
        Assert.Equal($"Token 'bodyFont' in dark: {shown} is not a font family name.", Assert.Single(result.Errors));
    }

    [Fact]
    public void Parse_UnknownField_IsAWarningAndSchemaIsNot()
    {
        ThemeLoadResult result = Parse("""{ "$schema": "x", "name": "T", "Light": {}, "dark": { "token": {}, "tokens": {} } }""");

        Assert.Empty(result.Errors);
        Assert.Equal(["Unknown field 'Light' is ignored.", "Unknown field 'token' in dark is ignored."], result.Warnings);
        Assert.Null(result.Theme!.Light);
    }

    [Fact]
    public void Parse_FormatVersionOtherThanOne_IsAWarning()
    {
        ThemeLoadResult result = Parse("""{ "formatVersion": 2, "name": "T", "dark": {} }""");

        Assert.NotNull(result.Theme);
        Assert.Equal("The theme has formatVersion 2; this YAMV reads version 1 and may miss parts of it.", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("\"2\"")]
    public void Parse_NonIntegerFormatVersion_Warns(string version)
    {
        ThemeLoadResult result = Parse($$"""{ "formatVersion": {{version}}, "name": "T", "dark": {} }""");

        Assert.NotNull(result.Theme);
        Assert.Equal($"The theme has formatVersion {version}; this YAMV reads version 1 and may miss parts of it.", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("bodySize", "\"17px\"", "Token 'bodySize' in dark: '17px' is not a number.")]
    [InlineData("h2Rule", "\"yes\"", "Token 'h2Rule' in dark: 'yes' is not true or false.")]
    [InlineData("h2Rule", "1", "Token 'h2Rule' in dark: 1 is not true or false.")]
    [InlineData("bodyFont", "12", "Token 'bodyFont' in dark: 12 is not a font family name.")]
    [InlineData("bodyFont", "\"\"", "Token 'bodyFont' in dark: '' is not a font family name.")]
    [InlineData("accent", "null", "Token 'accent' in dark: null is not a colour (#rgb, #rrggbb or #rrggbbaa).")]
    public void Parse_WrongJsonType_IsAnError(string token, string value, string expected)
    {
        ThemeLoadResult result = Parse($$"""{ "name": "T", "dark": { "tokens": { "{{token}}": {{value}} } } }""");

        Assert.Null(result.Theme);
        Assert.Equal(expected, Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("bodyFont")]
    [InlineData("accent")]
    public void Parse_UnpairedSurrogateInATokenString_IsAnErrorNotACrash(string token)
    {
        ThemeLoadResult result = Parse($$$"""{"name":"T","light":{"tokens":{"{{{token}}}":"\ud800"} } }""");

        Assert.Null(result.Theme);
        Assert.Equal($"Token '{token}' in light: \"\\ud800\" is not valid text.", Assert.Single(result.Errors));
    }

    [Fact]
    public void Parse_UnknownToken_IsAWarningNamingWhereAndTheTokenIsIgnored()
    {
        ThemeLoadResult result = Parse(
            """
            {
              "name": "T",
              "tokens": { "glow": "#fff" },
              "light": { "tokens": { "Accent": "#fff" } },
              "dark": { "tokens": { "sparkle": 1 } }
            }
            """
        );

        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                "Unknown token 'glow' in shared is ignored.",
                "Unknown token 'Accent' in light is ignored.",
                "Unknown token 'sparkle' in dark is ignored.",
            ],
            result.Warnings
        );
        Assert.Empty(result.Theme!.SharedTokens);
        Assert.Empty(result.Theme.Light!.Tokens);
        Assert.Empty(result.Theme.Dark!.Tokens);
    }

    [Theory]
    [InlineData("#ABC", "#aabbcc")]
    [InlineData("#A1B2C3", "#a1b2c3")]
    [InlineData("#A1B2C3D4", "#a1b2c3d4")]
    public void Parse_Colour_IsNormalised(string written, string expected)
    {
        ThemeLoadResult result = Parse($$"""{ "name": "T", "light": { "tokens": { "accent": "{{written}}" } } }""");

        Assert.Equal(new ColorValue(expected), result.Theme!.Light!.Tokens["accent"]);
        Assert.Equal(expected, ((ColorValue)result.Theme.Light.Tokens["accent"]).Hex);
    }

    [Fact]
    public void ColorValue_Invalid_Throws() => Assert.Throws<ArgumentException>(() => new ColorValue("#12"));

    [Theory]
    [InlineData("#bb800926", 0xbb, 0x80, 0x09, 0x26, 0x26bb8009u)]
    [InlineData("#0969DA", 0x09, 0x69, 0xda, 0xff, 0xff0969dau)]
    [InlineData("#abc", 0xaa, 0xbb, 0xcc, 0xff, 0xffaabbccu)]
    public void ColorValue_Channels_ReadHexInCssOrderWithAlphaLast(string hex, int r, int g, int b, int a, uint argb)
    {
        var color = new ColorValue(hex);

        Assert.Equal((r, g, b, a), (color.R, color.G, color.B, color.A));
        Assert.Equal(argb, color.ToArgb());
    }

    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("LPT¹")]
    [InlineData("a\tb")]
    [InlineData("../x")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("..")]
    public void IsValid_RejectsDeviceNamesControlCharactersAndPaths(string id) => Assert.False(ThemeIds.IsValid(id));

    [Theory]
    [InlineData("console")]
    [InlineData("con-copy")]
    [InlineData("com10")]
    [InlineData("My Theme")]
    public void IsValid_AcceptsPlainNames(string id) => Assert.True(ThemeIds.IsValid(id));

    [Fact]
    public void Parse_CarriesIdSourcePathAndMetadata()
    {
        ThemeLoadResult result = ThemeParser.Parse(
            """{ "name": "N", "author": "A", "description": "D", "css": "p {}", "light": { "css": "q {}" } }""",
            "my id",
            ThemeSource.BuiltIn,
            "C:/x/my id.yamv-theme.json"
        );

        ThemeDefinition theme = result.Theme!;
        Assert.Equal(("my id", "N", "A", "D"), (theme.Id, theme.Name, theme.Author, theme.Description));
        Assert.Equal(ThemeSource.BuiltIn, theme.Source);
        Assert.Equal("C:/x/my id.yamv-theme.json", theme.FilePath);
        Assert.Equal("p {}", theme.SharedCss);
        Assert.Equal("q {}", theme.Light!.Css);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsEveryToken()
    {
        ThemeDefinition original = ThemeParser
            .Parse(
                """
                {
                  "name": "Round \"trip\"", "author": "Me", "description": "</style> ü",
                  "tokens": { "bodyFont": "Source Serif 4", "bodySize": 17.5, "h1Size": 2.1, "h2Rule": false },
                  "css": "h1 { color: red; }",
                  "light": { "tokens": { "accent": "#ABC", "warningBackground": "#bb800926" }, "css": "a {}" },
                  "dark": { "tokens": { "headingWeight": 650, "lineHeight": 1.68 } }
                }
                """,
                "rt",
                ThemeSource.User,
                null
            )
            .Theme!;

        string json = ThemeSerializer.Serialize(original);
        ThemeLoadResult reparsed = ThemeParser.Parse(json, "rt", ThemeSource.User, null);

        Assert.Empty(reparsed.Errors);
        Assert.Empty(reparsed.Warnings);
        ThemeDefinition copy = reparsed.Theme!;
        Assert.Equal((original.Name, original.Author, original.Description), (copy.Name, copy.Author, copy.Description));
        Assert.Equal(original.SharedTokens.OrderBy(pair => pair.Key), copy.SharedTokens.OrderBy(pair => pair.Key));
        Assert.Equal(original.Light!.Tokens.OrderBy(pair => pair.Key), copy.Light!.Tokens.OrderBy(pair => pair.Key));
        Assert.Equal(original.Dark!.Tokens.OrderBy(pair => pair.Key), copy.Dark!.Tokens.OrderBy(pair => pair.Key));
        Assert.Equal((original.SharedCss, original.Light.Css, original.Dark.Css), (copy.SharedCss, copy.Light.Css, copy.Dark.Css));
        Assert.Contains("\"$schema\": \"" + ThemeSerializer.SchemaUrl + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"formatVersion\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', json);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My Theme.yamv-theme.json", "My Theme")]
    [InlineData("C:/themes/paper.yamv-theme.json", "paper")]
    [InlineData("shared.json", "shared")]
    public void FromFileName_IsTheFileNameWithoutTheSuffix(string fileName, string expected) =>
        Assert.Equal(expected, ThemeIds.FromFileName(fileName));

    [Fact]
    public void Unique_AppendsTheFirstFreeNumber()
    {
        var taken = new HashSet<string>(["a", "A-2"], ThemeIds.Comparer);

        Assert.Equal("b", ThemeIds.Unique("b", taken));
        Assert.Equal("a-3", ThemeIds.Unique("a", taken));
    }
}
