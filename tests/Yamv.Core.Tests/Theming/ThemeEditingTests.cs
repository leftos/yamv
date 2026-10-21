using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeEditingTests
{
    private static readonly ThemeDefinition Theme = ThemeParser
        .Parse("""{ "name": "T", "tokens": { "text": "#222222" }, "light": { "tokens": { "accent": "#333333" } } }""", "t", ThemeSource.User, null)
        .Theme!;

    [Fact]
    public void SetToken_Shared_AddsTheValueAndLeavesTheOriginal()
    {
        ThemeDefinition edited = ThemeEditing.SetToken(Theme, ThemeTab.Shared, "accent", new ColorValue("#abcdef"));

        Assert.Equal(new ColorValue("#abcdef"), edited.SharedTokens["accent"]);
        Assert.Equal(new ColorValue("#222222"), edited.SharedTokens["text"]);
        Assert.False(Theme.SharedTokens.ContainsKey("accent"));
    }

    [Fact]
    public void SetToken_Variant_ReplacesTheValueInThatHalf()
    {
        ThemeDefinition edited = ThemeEditing.SetToken(Theme, ThemeTab.Light, "accent", new ColorValue("#abcdef"));

        Assert.Equal(new ColorValue("#abcdef"), edited.Light!.Tokens["accent"]);
        Assert.Equal(new ColorValue("#333333"), Theme.Light!.Tokens["accent"]);
    }

    public static TheoryData<string, ThemeValue, string> InvalidValues =>
        new()
        {
            { "accent", new NumberValue(3), "Token 'accent' in light: 3 is not a colour (#rgb, #rrggbb or #rrggbbaa)." },
            { "bodySize", new NumberValue(99), "Token 'bodySize' in light: 99 is out of range (10–32)." },
            { "lineHeight", new NumberValue(0.5), "Token 'lineHeight' in light: 0.5 is out of range (1.0–2.5)." },
            { "bodyFont", new FontValue(" "), "Token 'bodyFont' in light: ' ' is not a font family name." },
            { "h2Rule", new ColorValue("#fff"), "Token 'h2Rule' in light: '#ffffff' is not true or false." },
            { "bodySize", new NumberValue(double.NaN), "Token 'bodySize' in light: NaN is not a finite number." },
        };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void SetToken_InvalidValue_ThrowsWithTheParsersWording(string token, ThemeValue value, string error)
    {
        ThemeException exception = Assert.Throws<ThemeException>(() => ThemeEditing.SetToken(Theme, ThemeTab.Light, token, value));

        Assert.Equal([error], exception.Errors);
        Assert.Equal(error, exception.Message);
    }

    [Fact]
    public void SetToken_UnknownTokenOrMissingHalf_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThemeEditing.SetToken(Theme, ThemeTab.Shared, "nope", new ColorValue("#fff")));
        Assert.Throws<InvalidOperationException>(() => ThemeEditing.SetToken(Theme, ThemeTab.Dark, "accent", new ColorValue("#fff")));
    }

    [Fact]
    public void ResetToken_RemovesItFromThatLayerOnly()
    {
        ThemeDefinition shared = ThemeEditing.ResetToken(Theme, ThemeTab.Shared, "text");
        ThemeDefinition light = ThemeEditing.ResetToken(Theme, ThemeTab.Light, "accent");

        Assert.Empty(shared.SharedTokens);
        Assert.Equal(Theme.Light, shared.Light);
        Assert.Empty(light.Light!.Tokens);
        Assert.Equal(new ColorValue("#222222"), light.SharedTokens["text"]);
        Assert.Equal(["text"], ThemeEditing.ResetToken(Theme, ThemeTab.Shared, "accent").SharedTokens.Keys);
    }

    [Fact]
    public void ResetToken_NotHeld_ReturnsTheSameInstance()
    {
        Assert.Same(Theme, ThemeEditing.ResetToken(Theme, ThemeTab.Shared, "accent"));
        Assert.Same(Theme, ThemeEditing.ResetToken(Theme, ThemeTab.Light, "text"));
    }

    [Fact]
    public void ResetToken_UnknownTokenOrMissingHalf_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThemeEditing.ResetToken(Theme, ThemeTab.Shared, "nope"));
        Assert.Throws<InvalidOperationException>(() => ThemeEditing.ResetToken(Theme, ThemeTab.Dark, "accent"));
    }

    [Fact]
    public void ContentEquals_ComparesValuesNotDictionaries()
    {
        ThemeDefinition edited = ThemeEditing.SetToken(Theme, ThemeTab.Light, "accent", new ColorValue("#abcdef"));
        ThemeDefinition back = ThemeEditing.SetToken(edited, ThemeTab.Light, "accent", new ColorValue("#333333"));

        Assert.NotEqual(Theme, back);
        Assert.True(Theme.ContentEquals(back));
        Assert.True(Theme.ContentEquals(back with { Id = "other", FilePath = "x" }));
        Assert.False(Theme.ContentEquals(edited));
        Assert.False(Theme.ContentEquals(Theme with { Author = "Ann" }));
        Assert.False(Theme.ContentEquals(ThemeEditing.AddVariant(Theme, Appearance.Dark)));
    }

    [Fact]
    public void AddVariant_AddsAnEmptyHalf_AndRefusesOneThatExists()
    {
        ThemeDefinition edited = ThemeEditing.AddVariant(Theme, Appearance.Dark);

        Assert.Empty(edited.Dark!.Tokens);
        Assert.Equal("", edited.Dark.Css);
        Assert.All(ThemeTokenView.Build(edited, ThemeTab.Dark, Appearance.Dark), row => Assert.False(row.SetHere));
        Assert.Throws<InvalidOperationException>(() => ThemeEditing.AddVariant(Theme, Appearance.Light));
    }

    [Fact]
    public void Rename_ChangesTheNameOnly_AndRefusesABlankOne()
    {
        ThemeDefinition renamed = ThemeEditing.Rename(Theme, "New name");

        Assert.Equal(Theme with { Name = "New name" }, renamed);
        Assert.Equal(["The theme has no 'name'."], Assert.Throws<ThemeException>(() => ThemeEditing.Rename(Theme, " ")).Errors);
    }
}
