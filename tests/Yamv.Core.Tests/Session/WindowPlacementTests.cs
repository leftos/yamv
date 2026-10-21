using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, Scaling: 1);
    private static readonly ScreenArea Right = new(1920, 0, 1280, 680, Scaling: 1);

    [Fact]
    public void Fit_InsideOneArea_ReturnsItUnchanged()
    {
        var saved = new WindowBounds(100, 100, 900, 700, IsMaximized: false);

        Assert.Equal(saved, WindowPlacement.Fit(saved, [Primary]));
    }

    [Fact]
    public void Fit_PartlyOffTheRightEdge_MovesItIn()
    {
        var saved = new WindowBounds(1500, 200, 900, 700, IsMaximized: false);

        Assert.Equal(new WindowBounds(1020, 200, 900, 700, IsMaximized: false), WindowPlacement.Fit(saved, [Primary]));
    }

    [Fact]
    public void Fit_LargerThanTheArea_ShrinksItToTheArea()
    {
        var saved = new WindowBounds(-50, -20, 2500, 1400, IsMaximized: false);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040, IsMaximized: false), WindowPlacement.Fit(saved, [Primary]));
    }

    [Fact]
    public void Fit_OverlappingTwoAreas_GoesToTheLargerOverlap()
    {
        var saved = new WindowBounds(1700, 100, 900, 500, IsMaximized: false);

        Assert.Equal(new WindowBounds(1920, 100, 900, 500, IsMaximized: false), WindowPlacement.Fit(saved, [Primary, Right]));
    }

    [Fact]
    public void Fit_OnNoArea_GoesToTheFirstArea()
    {
        var saved = new WindowBounds(5000, 3000, 900, 700, IsMaximized: false);

        Assert.Equal(new WindowBounds(1020, 340, 900, 700, IsMaximized: false), WindowPlacement.Fit(saved, [Primary, Right]));
    }

    [Fact]
    public void Fit_WithNoAreas_ReturnsItUnchanged()
    {
        var saved = new WindowBounds(5000, 3000, 900, 700, IsMaximized: true);

        Assert.Equal(saved, WindowPlacement.Fit(saved, []));
    }

    [Fact]
    public void Fit_KeepsIsMaximized()
    {
        var saved = new WindowBounds(1500, 200, 900, 700, IsMaximized: true);

        Assert.True(WindowPlacement.Fit(saved, [Primary]).IsMaximized);
    }

    [Fact]
    public void Fit_OnAScaledLaptopBesideAnUnscaledMonitor_StaysOnTheLaptop()
    {
        var monitor = new ScreenArea(0, 0, 2560, 1440, Scaling: 1);
        var laptop = new ScreenArea(2560, 0, 2560, 1600, Scaling: 1.5);
        var saved = new WindowBounds(1746.67, 100, 700, 600, IsMaximized: false, Scaling: 1.5);

        WindowBounds fitted = WindowPlacement.Fit(saved, [monitor, laptop]);

        Assert.Equal(1.5, fitted.Scaling);
        Assert.Equal(2620, fitted.X * fitted.Scaling, precision: 0);
        Assert.Equal(150, fitted.Y * fitted.Scaling, precision: 6);
        Assert.Equal(700, fitted.Width, precision: 6);
        Assert.Equal(600, fitted.Height, precision: 6);
    }

    [Fact]
    public void Fit_OnAScaledLaptop_ClampsInTheLaptopsPixels()
    {
        var monitor = new ScreenArea(0, 0, 2560, 1440, Scaling: 1);
        var laptop = new ScreenArea(2560, 0, 2560, 1600, Scaling: 1.5);
        var saved = new WindowBounds(1746.67, 100, 700, 1050, IsMaximized: false, Scaling: 1.5);

        WindowBounds fitted = WindowPlacement.Fit(saved, [monitor, laptop]);

        Assert.Equal(25, fitted.Y * fitted.Scaling, precision: 6);
        Assert.Equal(1050, fitted.Height, precision: 6);
        Assert.Equal(2620, fitted.X * fitted.Scaling, precision: 0);
    }

    [Fact]
    public void Fit_ResultIsInTheTargetScreensDip()
    {
        var monitor = new ScreenArea(0, 0, 1920, 1080, Scaling: 1);
        var laptop = new ScreenArea(1920, 0, 2880, 1800, Scaling: 2);
        var saved = new WindowBounds(100, 100, 800, 600, IsMaximized: true, Scaling: 1);

        Assert.Equal(new WindowBounds(100, 100, 800, 600, IsMaximized: true, Scaling: 1), WindowPlacement.Fit(saved, [laptop, monitor]));

        var onLaptop = new WindowBounds(1100, 50, 400, 300, IsMaximized: false, Scaling: 2);
        Assert.Equal(new WindowBounds(1100, 50, 400, 300, IsMaximized: false, Scaling: 2), WindowPlacement.Fit(onLaptop, [monitor, laptop]));
    }

    [Fact]
    public void Fit_EqualOverlap_GoesToTheFirstArea()
    {
        var left = new ScreenArea(0, 0, 1000, 1000, Scaling: 1);
        var right = new ScreenArea(1000, 0, 1000, 1000, Scaling: 1);
        var saved = new WindowBounds(900, 100, 200, 200, IsMaximized: false);

        Assert.Equal(new WindowBounds(800, 100, 200, 200, IsMaximized: false), WindowPlacement.Fit(saved, [left, right]));
    }
}
