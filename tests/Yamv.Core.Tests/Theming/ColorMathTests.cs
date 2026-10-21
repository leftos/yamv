using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ColorMathTests
{
    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(60, 255, 255, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(180, 0, 255, 255)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(300, 255, 0, 255)]
    public void ToRgb_GivesThePrimariesAndSecondaries_AndToHsvGivesTheirHueBack(double hue, byte r, byte g, byte b)
    {
        Assert.Equal((r, g, b), ColorMath.ToRgb(new Hsv(hue, 1, 1)));
        Assert.Equal(new Hsv(hue, 1, 1), ColorMath.ToHsv(r, g, b, greyHue: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void ToHsv_OfAGrey_KeepsTheHueTheCallerPasses_AndRoundTrips(byte level)
    {
        var hsv = ColorMath.ToHsv(level, level, level, greyHue: 217.5);

        Assert.Equal(217.5, hsv.H);
        Assert.Equal(0, hsv.S);
        Assert.Equal((level, level, level), ColorMath.ToRgb(hsv));
    }

    [Fact]
    public void ToRgb_WithNoSaturation_IsGreyWhateverTheHue()
    {
        Assert.Equal(((byte)128, (byte)128, (byte)128), ColorMath.ToRgb(new Hsv(0, 0, 128 / 255.0)));
        Assert.Equal(((byte)128, (byte)128, (byte)128), ColorMath.ToRgb(new Hsv(250, 0, 128 / 255.0)));
    }

    [Fact]
    public void ToRgb_TakesHue360AsHue0()
    {
        Assert.Equal(ColorMath.ToRgb(new Hsv(0, 0.7, 0.9)), ColorMath.ToRgb(new Hsv(360, 0.7, 0.9)));
        Assert.Equal(ColorMath.ToRgb(new Hsv(10, 0.7, 0.9)), ColorMath.ToRgb(new Hsv(370, 0.7, 0.9)));
    }

    [Fact]
    public void ToRgb_ThenToHsv_IsExactInBytes_OverAGridOfColours()
    {
        byte[] levels = [.. Enumerable.Range(0, 52).Select(step => (byte)(step * 5)), 1, 127, 128, 254];
        foreach (byte r in levels)
        {
            foreach (byte g in levels)
            {
                foreach (byte b in levels)
                {
                    (byte R, byte G, byte B) back = ColorMath.ToRgb(ColorMath.ToHsv(r, g, b, greyHue: 0));
                    Assert.True(back == (r, g, b), $"({r}, {g}, {b}) came back as {back}.");
                }
            }
        }
    }

    [Fact]
    public void FromHsv_OfFullSaturationAndValue_IsThePureHue() =>
        Assert.Equal(new ColorValue("#00ff00"), ColorMath.FromHsv(new Hsv(120, 1, 1), byte.MaxValue));

    [Fact]
    public void FromRgba_WritesSixLowerCaseDigitsWhenOpaque_AndEightOtherwise()
    {
        Assert.Equal("#abcdef", ColorMath.FromRgba(0xAB, 0xCD, 0xEF, 255).Hex);
        Assert.Equal("#abcdef80", ColorMath.FromRgba(0xAB, 0xCD, 0xEF, 0x80).Hex);
        Assert.Equal("#abcdef00", ColorMath.FromRgba(0xAB, 0xCD, 0xEF, 0).Hex);
    }

    [Theory]
    [InlineData("#3fb950", 255)]
    [InlineData("#3fb95080", 0x80)]
    [InlineData("#3fb95000", 0)]
    public void ToHsv_OfAColorValue_ThenFromHsv_GivesTheSameColour(string hex, byte alpha)
    {
        var color = new ColorValue(hex);

        var hsv = ColorMath.ToHsv(color, greyHue: 0);

        Assert.Equal(alpha, color.A);
        Assert.Equal(color, ColorMath.FromHsv(hsv, color.A));
    }

    [Fact]
    public void ToRgb_ClampsSaturationAndValue_AndWrapsANegativeHue()
    {
        Assert.Equal(ColorMath.ToRgb(new Hsv(0, 1, 1)), ColorMath.ToRgb(new Hsv(0, 2, 3)));
        Assert.Equal(((byte)0, (byte)0, (byte)0), ColorMath.ToRgb(new Hsv(0, -1, -1)));
        Assert.Equal(ColorMath.ToRgb(new Hsv(300, 1, 1)), ColorMath.ToRgb(new Hsv(-60, 1, 1)));
    }
}
