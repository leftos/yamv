using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, Scaling: 1);
    private static readonly ScreenArea Right = new(1920, 0, 1280, 680, Scaling: 1);

    // YAMV's main window on Windows at 96 dpi: the client starts 8 px right of Position's outer corner, level with it, and a
    // 1 px visible outline runs along the client's left, right and bottom sides.
    private static readonly WindowFrame WindowsAt96 = new(ClientLeft: 8, ClientTop: 0, Outline: 1);

    [Fact]
    public void Fit_InsideOneArea_ReturnsItUnchanged()
    {
        var saved = new WindowBounds(100, 100, 900, 700, IsMaximized: false);

        Assert.Equal(saved, WindowPlacement.Fit(saved, [Primary], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_PartlyOffTheRightEdge_MovesItIn()
    {
        var saved = new WindowBounds(1500, 200, 900, 700, IsMaximized: false);

        Assert.Equal(new WindowBounds(1020, 200, 900, 700, IsMaximized: false), WindowPlacement.Fit(saved, [Primary], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_LargerThanTheArea_ShrinksItToTheArea()
    {
        var saved = new WindowBounds(-50, -20, 2500, 1400, IsMaximized: false);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040, IsMaximized: false), WindowPlacement.Fit(saved, [Primary], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_OverlappingTwoAreas_GoesToTheLargerOverlap()
    {
        var saved = new WindowBounds(1700, 100, 900, 500, IsMaximized: false);

        Assert.Equal(new WindowBounds(1920, 100, 900, 500, IsMaximized: false), WindowPlacement.Fit(saved, [Primary, Right], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_OnNoArea_GoesToTheFirstArea()
    {
        var saved = new WindowBounds(5000, 3000, 900, 700, IsMaximized: false);

        Assert.Equal(new WindowBounds(1020, 340, 900, 700, IsMaximized: false), WindowPlacement.Fit(saved, [Primary, Right], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_WithNoAreas_ReturnsItUnchanged()
    {
        var saved = new WindowBounds(5000, 3000, 900, 700, IsMaximized: true);

        Assert.Equal(saved, WindowPlacement.Fit(saved, [], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_KeepsIsMaximized()
    {
        var saved = new WindowBounds(1500, 200, 900, 700, IsMaximized: true);

        Assert.True(WindowPlacement.Fit(saved, [Primary], _ => WindowFrame.None).IsMaximized);
    }

    [Fact]
    public void Fit_OnAScaledLaptopBesideAnUnscaledMonitor_StaysOnTheLaptop()
    {
        var monitor = new ScreenArea(0, 0, 2560, 1440, Scaling: 1);
        var laptop = new ScreenArea(2560, 0, 2560, 1600, Scaling: 1.5);
        var saved = new WindowBounds(1746.67, 100, 700, 600, IsMaximized: false, Scaling: 1.5);

        WindowBounds fitted = WindowPlacement.Fit(saved, [monitor, laptop], _ => WindowFrame.None);

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

        WindowBounds fitted = WindowPlacement.Fit(saved, [monitor, laptop], _ => WindowFrame.None);

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

        Assert.Equal(
            new WindowBounds(100, 100, 800, 600, IsMaximized: true, Scaling: 1),
            WindowPlacement.Fit(saved, [laptop, monitor], _ => WindowFrame.None)
        );

        var onLaptop = new WindowBounds(1100, 50, 400, 300, IsMaximized: false, Scaling: 2);
        Assert.Equal(
            new WindowBounds(1100, 50, 400, 300, IsMaximized: false, Scaling: 2),
            WindowPlacement.Fit(onLaptop, [monitor, laptop], _ => WindowFrame.None)
        );
    }

    [Fact]
    public void Fit_EqualOverlap_GoesToTheFirstArea()
    {
        var left = new ScreenArea(0, 0, 1000, 1000, Scaling: 1);
        var right = new ScreenArea(1000, 0, 1000, 1000, Scaling: 1);
        var saved = new WindowBounds(900, 100, 200, 200, IsMaximized: false);

        Assert.Equal(new WindowBounds(800, 100, 200, 200, IsMaximized: false), WindowPlacement.Fit(saved, [left, right], _ => WindowFrame.None));
    }

    [Fact]
    public void Fit_PartlyOffTheBottomRight_PutsTheOutlineAgainstTheEdges()
    {
        var area = new ScreenArea(0, 0, 5120, 1392, Scaling: 1);
        var saved = new WindowBounds(5000, 1300, 1100, 760, IsMaximized: false);

        WindowBounds fitted = WindowPlacement.Fit(saved, [area], _ => WindowsAt96);

        Assert.Equal(new WindowBounds(4011, 631, 1100, 760, IsMaximized: false), fitted);
    }

    [Fact]
    public void Fit_PartlyOffTheTopLeft_PutsTheOutlineAgainstTheEdges()
    {
        var area = new ScreenArea(0, 0, 5120, 1392, Scaling: 1);
        var saved = new WindowBounds(-200, -200, 1100, 760, IsMaximized: false);

        WindowBounds fitted = WindowPlacement.Fit(saved, [area], _ => WindowsAt96);

        Assert.Equal(new WindowBounds(-7, 0, 1100, 760, IsMaximized: false), fitted);
    }

    [Fact]
    public void Fit_LargerThanTheArea_ShrinksTheClientToLeaveRoomForTheOutline()
    {
        var area = new ScreenArea(0, 0, 5120, 1392, Scaling: 1);
        var saved = new WindowBounds(0, 0, 6000, 2000, IsMaximized: false);

        WindowBounds fitted = WindowPlacement.Fit(saved, [area], _ => WindowsAt96);

        Assert.Equal(new WindowBounds(-7, 0, 5118, 1391, IsMaximized: false), fitted);
    }

    [Fact]
    public void Fit_OnAScaledScreen_UsesThatScreensFrame()
    {
        var monitor = new ScreenArea(0, 0, 2560, 1440, Scaling: 1);
        var laptop = new ScreenArea(2560, 0, 2560, 1600, Scaling: 1.5);
        var saved = new WindowBounds(3400, 100, 700, 600, IsMaximized: false, Scaling: 1.5);

        WindowBounds fitted = WindowPlacement.Fit(saved, [monitor, laptop], scaling => scaling == 1.5 ? new WindowFrame(11, 0, 1) : WindowsAt96);

        Assert.Equal(5120 - (1050 + 2) + 1 - 11, fitted.X * fitted.Scaling, precision: 6);
    }

    [Fact]
    public void Fit_SavedAtAnotherScaling_ReadsItWithThatScalingsFrame()
    {
        var area = new ScreenArea(0, 0, 3840, 2160, Scaling: 1.5);
        var saved = new WindowBounds(-7, 0, 1000, 700, IsMaximized: false, Scaling: 1);

        WindowBounds fitted = WindowPlacement.Fit(saved, [area], scaling => scaling == 1.5 ? new WindowFrame(11, 0, 1) : new WindowFrame(8, 0, 1));

        Assert.Equal(-10, fitted.X * fitted.Scaling, precision: 6);
    }

    [Fact]
    public void Fit_WithNoFrame_ClampsTheOuterRectangle()
    {
        var saved = new WindowBounds(-200, 200, 900, 700, IsMaximized: false);

        WindowBounds fitted = WindowPlacement.Fit(saved, [Primary], _ => WindowFrame.None);

        Assert.Equal(new WindowBounds(0, 200, 900, 700, IsMaximized: false), fitted);
    }
}
