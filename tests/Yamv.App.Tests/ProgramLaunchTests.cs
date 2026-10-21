using Yamv.App.Tests.Fakes;
using Yamv.Core.Instance;
using Yamv.Core.Launch;

namespace Yamv.App.Tests;

/// <summary>What a launch that did not get the main-instance lock does, with the second launch's two ways faked.</summary>
public sealed class ProgramLaunchTests
{
    private readonly RecordingAppLog _log = new();
    private readonly List<string> _calls = [];

    [Theory]
    [InlineData(SecondLaunchOutcome.Activated, 0)]
    [InlineData(SecondLaunchOutcome.BecameMain, null)]
    [InlineData(SecondLaunchOutcome.Failed, 1)]
    public void NoFiles_AsksTheAppToComeForward(SecondLaunchOutcome outcome, int? exitCode)
    {
        int? result = Run([], outcome, SecondLaunchResult.Opened);

        Assert.Equal(exitCode, result);
        Assert.Equal(["activate"], _calls);
    }

    [Fact]
    public void Files_OpenedByTheApp_Exit0()
    {
        Assert.Equal(0, Run([@"C:\docs\a.md"], SecondLaunchOutcome.Activated, SecondLaunchResult.Opened));
        Assert.Equal(["open"], _calls);
    }

    [Fact]
    public void Files_WhenTheAppHasExited_RunAsTheApp()
    {
        Assert.Null(Run([@"C:\docs\a.md"], SecondLaunchOutcome.Activated, SecondLaunchResult.BecameMain));
        Assert.Equal(["open"], _calls);
    }

    [Fact]
    public void Files_NotOpened_LogTheReasonAndExit1()
    {
        int? result = Run(
            [@"C:\docs\a.md", @"C:\docs\b.md"],
            SecondLaunchOutcome.Activated,
            SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding)
        );

        Assert.Equal(1, result);
        Assert.Equal(["open"], _calls);
        Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: the 2 file(s) were not opened (NotResponding); exiting 1.");
    }

    [Fact]
    public void RunAsApp_FinishesStoppingAfterTheLoopAndBeforeItReturns()
    {
        using var server = new TestInstanceServer(request => request.Complete(InstanceReply.Refused));
        List<string> order = [];

        int exitCode = Program.RunAsApp(
            () =>
            {
                order.Add(File.Exists(server.PidFile) ? "loop ran, pid live" : "loop ran, pid gone");
                return 3;
            },
            () =>
            {
                server.Coordinator.StopAnswering();
                order.Add("stopped");
            }
        );

        // The pid is gone before RunAsApp returns, so before the caller's dispose releases the lock.
        Assert.Equal(3, exitCode);
        Assert.Equal(["loop ran, pid live", "stopped"], order);
        Assert.False(File.Exists(server.PidFile));
    }

    [Fact]
    public void RunAsApp_WhenTheLoopThrows_StillFinishesStopping()
    {
        List<string> order = [];

        Assert.Throws<InvalidOperationException>(() =>
            Program.RunAsApp(() => throw new InvalidOperationException("The UI loop failed."), () => order.Add("stopped"))
        );

        Assert.Equal(["stopped"], order);
    }

    private int? Run(IReadOnlyList<string> files, SecondLaunchOutcome activated, SecondLaunchResult opened) =>
        Program.RunSecondLaunch(
            files,
            () =>
            {
                _calls.Add("activate");
                return activated;
            },
            () =>
            {
                _calls.Add("open");
                return opened;
            },
            _log
        );
}
