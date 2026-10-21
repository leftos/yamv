using Yamv.Core.Widths;

namespace Yamv.Core.Tests.Widths;

public sealed class ContentWidthTests
{
    private const double WidePane = 1280;

    [Theory]
    [InlineData(763, 800)]
    [InlineData(800, 840)]
    [InlineData(480, 520)]
    [InlineData(1200, 1240)]
    public void StepUp_FromPx_GoesToTheNextGridValue(double from, double expected) =>
        Assert.Equal(expected, ContentWidth.FromPx(from).StepUp(WidePane).Px);

    [Theory]
    [InlineData(763, 760)]
    [InlineData(800, 760)]
    [InlineData(520, 480)]
    [InlineData(480, 480)]
    [InlineData(500, 480)]
    public void StepDown_FromPx_GoesToThePreviousGridValueAndStopsAt480(double from, double expected) =>
        Assert.Equal(expected, ContentWidth.FromPx(from).StepDown(WidePane).Px);

    [Fact]
    public void StepUp_PastThePaneWidth_IsFull() => Assert.True(ContentWidth.FromPx(800).StepUp(820).IsFull);

    [Fact]
    public void StepUp_ToExactlyThePaneWidth_StaysPx() => Assert.Equal(840, ContentWidth.FromPx(800).StepUp(840).Px);

    [Fact]
    public void StepUp_FromFull_StaysFull() => Assert.True(ContentWidth.Full.StepUp(WidePane).IsFull);

    [Theory]
    [InlineData(1280, 1240)]
    [InlineData(841, 840)]
    [InlineData(840, 800)]
    [InlineData(500, 480)]
    [InlineData(480, 480)]
    [InlineData(300, 480)]
    public void StepDown_FromFull_IsTheLargestGridValueBelowThePane(double pane, double expected) =>
        Assert.Equal(expected, ContentWidth.Full.StepDown(pane).Px);

    [Fact]
    public void StepDown_FromPxWiderThanThePane_IsTheLargestGridValueBelowThePane() => Assert.Equal(880, ContentWidth.FromPx(1200).StepDown(900).Px);

    [Fact]
    public void FromPx_BelowMinimum_IsRaisedTo480() => Assert.Equal(ContentWidth.MinimumPx, ContentWidth.FromPx(300).Px);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromPx_NotAPositiveNumber_Throws(double px) => Assert.Throws<ArgumentOutOfRangeException>(() => ContentWidth.FromPx(px));

    [Fact]
    public void FromSlider_AtZero_Is480() => Assert.Equal(480, ContentWidth.FromSlider(0, WidePane).Px);

    [Fact]
    public void FromSlider_Below95Percent_IsPx()
    {
        var width = ContentWidth.FromSlider(0.949, WidePane);

        Assert.False(width.IsFull);
        Assert.Equal(Math.Round(480 + (0.949 * 800)), width.Px);
    }

    [Theory]
    [InlineData(0.95)]
    [InlineData(1)]
    public void FromSlider_InTheLast5Percent_IsFull(double fraction) => Assert.True(ContentWidth.FromSlider(fraction, WidePane).IsFull);

    [Theory]
    [InlineData(480)]
    [InlineData(760)]
    [InlineData(1000)]
    public void ToSliderThenFromSlider_RoundTripsPx(double px)
    {
        var width = ContentWidth.FromPx(px);

        Assert.Equal(width, ContentWidth.FromSlider(width.ToSlider(WidePane), WidePane));
    }

    [Fact]
    public void ToSlider_Full_IsTheEndOfTheTravel()
    {
        Assert.Equal(1, ContentWidth.Full.ToSlider(WidePane));
        Assert.True(ContentWidth.FromSlider(ContentWidth.Full.ToSlider(WidePane), WidePane).IsFull);
    }

    [Fact]
    public void ToSlider_PxWiderThanThePane_IsClampedToTheTravel() => Assert.Equal(1, ContentWidth.FromPx(2000).ToSlider(WidePane));
}
