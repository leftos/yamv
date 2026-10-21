using Avalonia.Controls;
using Avalonia.Media;
using Yamv.App.Services;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.Services;

public sealed class ThemeBrushesTests
{
    [Fact]
    public Task FillChrome_writes_every_chrome_token_at_its_colour_keeping_an_eight_digit_alpha() =>
        Headless.Run(() =>
        {
            ResolvedTheme resolved = Resolved(("accent", new ColorValue("#11223380")), ("uiFont", new FontValue("Segoe UI")));
            var resources = new ResourceDictionary();

            ThemeBrushes.FillChrome(resources, resolved);

            foreach (string token in ChromePalette.ColorTokens)
            {
                SolidColorBrush brush = Assert.IsType<SolidColorBrush>(resources[ThemeBridge.ResourceKey(token)]);
                Assert.Equal(Color.FromUInt32(((ColorValue)resolved.Tokens[token]).ToArgb()), brush.Color);
            }

            Assert.Equal(Color.FromArgb(0x80, 0x11, 0x22, 0x33), ((SolidColorBrush)resources["YamvAccent"]!).Color);
            FontFamily font = Assert.IsType<FontFamily>(resources[ThemeBridge.UiFontKey]);
            Assert.Equal("Segoe UI", font.FamilyNames.PrimaryFamilyName);
            Assert.Equal(2, font.FamilyNames.Count);
        });

    [Fact]
    public Task Brush_reads_a_preview_token_and_refuses_a_token_that_is_not_a_colour() =>
        Headless.Run(() =>
        {
            ResolvedTheme resolved = Resolved(("quote", new ColorValue("#3fb950")));

            Assert.Equal(Color.FromRgb(0x3f, 0xb9, 0x50), ThemeBrushes.Brush(resolved, "quote").Color);
            Assert.Throws<InvalidOperationException>(() => ThemeBrushes.Brush(resolved, "bodyFont"));
        });

    [Fact]
    public Task Fonts_fall_back_to_Inter_and_to_the_platform_monospace_fonts_when_blank() =>
        Headless.Run(() =>
        {
            ResolvedTheme resolved = Resolved(("bodyFont", new FontValue(" ")), ("monoFont", new FontValue("Fira Code")));

            Assert.Equal("Inter", ThemeBrushes.SansFont(resolved, "bodyFont").FamilyNames.PrimaryFamilyName);
            FontFamily mono = ThemeBrushes.MonoFont(resolved, "monoFont");
            Assert.Equal("Fira Code", mono.FamilyNames.PrimaryFamilyName);
            Assert.Contains("Consolas", mono.FamilyNames);
        });

    private static ResolvedTheme Resolved(params (string Name, ThemeValue Value)[] overrides)
    {
        var tokens = ThemeTokens.DefaultsFor(Appearance.Light).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach ((string name, ThemeValue value) in overrides)
        {
            tokens[name] = value;
        }

        return new ResolvedTheme("test", "Test", Appearance.Light, tokens, "");
    }
}
