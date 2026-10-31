using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class AppLifetimePolicyTests
{
    [Fact]
    public void Quit_stops_and_quits_once_at_zero_windows()
    {
        AppLifetimePolicy policy = new(LastWindowBehavior.Quit);

        Assert.Equal(LastWindowStep.StopAndQuit, policy.DocumentWindowGone(0));
        Assert.Equal(LastWindowStep.None, policy.DocumentWindowGone(0));
    }

    [Fact]
    public void Quit_does_nothing_while_windows_remain()
    {
        AppLifetimePolicy policy = new(LastWindowBehavior.Quit);

        Assert.Equal(LastWindowStep.None, policy.DocumentWindowGone(2));
        Assert.Equal(LastWindowStep.None, policy.DocumentWindowGone(1));
        Assert.True(policy.MayOpenWindows);
        Assert.Equal(LastWindowStep.StopAndQuit, policy.DocumentWindowGone(0));
    }

    [Fact]
    public void KeepRunning_never_stops_at_zero_windows()
    {
        AppLifetimePolicy policy = new(LastWindowBehavior.KeepRunning);

        Assert.Equal(LastWindowStep.None, policy.DocumentWindowGone(0));
        Assert.Equal(LastWindowStep.None, policy.DocumentWindowGone(0));
        Assert.True(policy.MayOpenWindows);
    }

    [Fact]
    public void KeepRunning_stops_once_at_exit()
    {
        AppLifetimePolicy policy = new(LastWindowBehavior.KeepRunning);
        policy.DocumentWindowGone(0);

        Assert.True(policy.Exiting());
        Assert.False(policy.Exiting());
        Assert.False(policy.MayOpenWindows);
    }

    [Fact]
    public void Exiting_after_a_quit_stop_does_not_stop_again()
    {
        AppLifetimePolicy policy = new(LastWindowBehavior.Quit);
        Assert.Equal(LastWindowStep.StopAndQuit, policy.DocumentWindowGone(0));

        Assert.False(policy.Exiting());
    }

    [Theory]
    [InlineData(LastWindowBehavior.Quit)]
    [InlineData(LastWindowBehavior.KeepRunning)]
    public void No_window_may_open_once_stopping(LastWindowBehavior behavior)
    {
        AppLifetimePolicy byLastWindow = new(behavior);
        AppLifetimePolicy byExit = new(behavior);
        Assert.True(byLastWindow.MayOpenWindows);

        byLastWindow.DocumentWindowGone(0);
        byExit.Exiting();

        Assert.Equal(behavior == LastWindowBehavior.KeepRunning, byLastWindow.MayOpenWindows);
        Assert.False(byExit.MayOpenWindows);
    }
}
