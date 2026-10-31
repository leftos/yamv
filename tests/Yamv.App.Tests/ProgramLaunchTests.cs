using Yamv.App.Tests.Fakes;
using Yamv.Core.Instance;
using Yamv.Core.Launch;

namespace Yamv.App.Tests;

/// <summary>What a launch that did not get the main-instance lock does, with the second launch's two ways faked.</summary>
public sealed class ProgramLaunchTests : IDisposable
{
    private readonly RecordingAppLog _log = new();
    private readonly List<string> _calls = [];
    private readonly List<string> _errors = [];
    private readonly string _gateRoot = Path.Combine(Path.GetTempPath(), "yamv-launch-error-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "main.lock")]
    [InlineData(true, "main.admin.lock")]
    public void TheLaunchTakesTheElevatedLockOnlyWhenElevated(bool elevated, string lockName) =>
        Assert.Equal(lockName, Path.GetFileName(Program.MainLockFileFor(elevated)));

    [Fact]
    public void A_second_launch_reads_the_pid_of_the_instance_holding_the_lock_it_was_given()
    {
        string elevatedLock = Path.Combine(_gateRoot, "main.admin.lock");
        using var running = new InstanceCoordinator(elevatedLock, _log);
        Assert.True(running.TryAcquireMainLock());
        using var launch = new InstanceCoordinator(elevatedLock, _log);

        Assert.Equal(Environment.ProcessId, Program.SecondLaunchActionsFor(launch, elevatedLock, _log).ReadPid());
        Assert.Null(Program.SecondLaunchActionsFor(launch, Path.Combine(_gateRoot, "main.lock"), _log).ReadPid());
    }

    [Fact]
    public async Task A_second_launchs_request_dials_the_instance_holding_the_lock_it_was_given()
    {
        string elevatedLock = Path.Combine(_gateRoot, "main.admin.lock");
        // Longer than the hang guard, so the request is never abandoned by the clock, however busy the machine.
        using var running = new InstanceCoordinator(elevatedLock, _log) { ReplyBound = TimeSpan.FromMinutes(5) };
        Assert.True(running.TryAcquireMainLock());
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        running.StartServer(request =>
        {
            Assert.True(request.TryBegin());
            request.Complete(InstanceReply.Opened);
            received.TrySetResult(request.Message);
        });
        using var launch = new InstanceCoordinator(elevatedLock, _log);

        InstanceRequestOutcome outcome = await Program
            .SecondLaunchActionsFor(launch, elevatedLock, _log)
            .Request(InstanceMessages.Activate, TestInstanceServer.HangGuard);

        Assert.Equal(InstanceRequestOutcome.Confirmed, outcome);
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SecondLaunchOutcome.Activated, 0)]
    [InlineData(SecondLaunchOutcome.BecameMain, null)]
    [InlineData(SecondLaunchOutcome.Failed, 1)]
    public void NoFiles_AsksTheAppToComeForward(SecondLaunchOutcome outcome, int? exitCode)
    {
        SecondLaunchFailure? failure = outcome == SecondLaunchOutcome.Failed ? SecondLaunchFailure.NotResponding : null;

        int? result = Run([], new SecondLaunchResult(outcome, failure), SecondLaunchResult.Opened);

        Assert.Equal(exitCode, result);
        Assert.Equal(["activate"], _calls);
        Assert.Empty(_errors);
    }

    [Fact]
    public void NoFiles_NothingBroughtForward_LogTheReasonAndExit1()
    {
        int? result = Run([], SecondLaunchResult.Failed(SecondLaunchFailure.Refused), SecondLaunchResult.Opened);

        Assert.Equal(1, result);
        Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: the app did not bring a window forward (Refused); exiting 1.");
    }

    [Fact]
    public void Files_OpenedByTheApp_Exit0()
    {
        Assert.Equal(0, Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.Opened));
        Assert.Equal(["open"], _calls);
    }

    [Fact]
    public void Files_WhenTheAppHasExited_RunAsTheApp()
    {
        Assert.Null(Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.BecameMain));
        Assert.Equal(["open"], _calls);
    }

    [Fact]
    public void Files_NotOpened_LogTheReasonAndExit1()
    {
        int? result = Run(
            [@"C:\docs\a.md", @"C:\docs\b.md"],
            SecondLaunchResult.Activated,
            SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding)
        );

        Assert.Equal(1, result);
        Assert.Equal(["open"], _calls);
        Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: the 2 file(s) were not opened (NotResponding); exiting 1.");
    }

    [Fact]
    public void Files_NotOpened_ShowsTheNotRespondingBoxOnce()
    {
        int? result = Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding));

        Assert.Equal(1, result);
        Assert.Equal(["YAMV is not responding; the file was not opened."], _errors);
    }

    [Fact]
    public void Files_NotOpened_CountsTheFilesInTheBox()
    {
        int? result = Run(
            [@"C:\docs\a.md", @"C:\docs\b.md", @"C:\docs\c.md"],
            SecondLaunchResult.Activated,
            SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding)
        );

        Assert.Equal(1, result);
        Assert.Equal(["YAMV is not responding; 3 files were not opened."], _errors);
    }

    [Theory]
    [InlineData(SecondLaunchFailure.Unsendable)]
    [InlineData(SecondLaunchFailure.Refused)]
    [InlineData(SecondLaunchFailure.NotResponding)]
    [InlineData(SecondLaunchFailure.Denied)]
    public void Files_NotOpened_ShowsTheBoxForEveryFailure(SecondLaunchFailure failure)
    {
        int? result = Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.Failed(failure));

        Assert.Equal(1, result);
        Assert.Equal(["YAMV is not responding; the file was not opened."], _errors);
    }

    [Fact]
    public void Files_OpenedByTheApp_ShowsNothing()
    {
        Assert.Equal(0, Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.Opened));

        Assert.Empty(_errors);
    }

    [Fact]
    public void Files_WhenTheAppHasExited_ShowsNothing()
    {
        Assert.Null(Run([@"C:\docs\a.md"], SecondLaunchResult.Activated, SecondLaunchResult.BecameMain));

        Assert.Empty(_errors);
    }

    [Fact]
    public void NoFiles_NotBroughtForward_ShowsNothing()
    {
        Assert.Equal(1, Run([], SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), SecondLaunchResult.Opened));

        Assert.Empty(_errors);
    }

    [Fact]
    public void Files_NotOpened_WhileAnotherLaunchHoldsTheBox_ShowsNothingAndLogs()
    {
        string gate = NewGatePath();
        using IDisposable held = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(gate)).Holder;

        int? result = RunWith(
            [@"C:\docs\a.md"],
            SecondLaunchResult.Activated,
            SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding),
            _errors.Add,
            gate
        );

        Assert.Equal(1, result);
        Assert.Empty(_errors);
        Assert.Contains(
            _log.Entries,
            entry => entry.Message == "Second launch: another launch is already showing the not-responding box; not showing another."
        );
    }

    [Fact]
    public void Files_NotOpened_WhenTheBoxLockCannotBeOpened_LogsAndShowsTheBoxAnyway()
    {
        string gate = NewGatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(gate)!);
        File.WriteAllText(gate, "read only");
        File.SetAttributes(gate, FileAttributes.ReadOnly);

        int? result;
        try
        {
            result = RunWith(
                [@"C:\docs\a.md"],
                SecondLaunchResult.Activated,
                SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding),
                _errors.Add,
                gate
            );
        }
        finally
        {
            File.SetAttributes(gate, FileAttributes.Normal);
        }

        Assert.Equal(1, result);
        Assert.Equal(["YAMV is not responding; the file was not opened."], _errors);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.Equal($"Second launch: could not open the not-responding box's lock at {gate}; showing the box without it.", error.Message);
        Assert.IsType<UnauthorizedAccessException>(error.Exception);
    }

    [Fact]
    public void Files_NotOpened_WhenShowingTheBoxFails_IsLoggedAndReleasesIt()
    {
        string gate = NewGatePath();
        var thrown = new InvalidOperationException("The box failed.");

        int? result = RunWith(
            [@"C:\docs\a.md"],
            SecondLaunchResult.Activated,
            SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding),
            _ => throw thrown,
            gate
        );

        Assert.Equal(1, result);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.Equal("Second launch: showing the not-responding box failed.", error.Message);
        Assert.Same(thrown, error.Exception);
        using IDisposable later = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(gate)).Holder;
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
            },
            _log
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
            Program.RunAsApp(() => throw new InvalidOperationException("The UI loop failed."), () => order.Add("stopped"), _log)
        );

        Assert.Equal(["stopped"], order);
    }

    [Fact]
    public void A_throw_from_the_stop_keeps_the_loop_exception()
    {
        var stopFailure = new UnauthorizedAccessException("The stop failed.");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            Program.RunAsApp(() => throw new InvalidOperationException("The UI loop failed."), () => throw stopFailure, _log)
        );

        Assert.Equal("The UI loop failed.", thrown.Message);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.Equal("Instance server: finishing the stop failed after the UI loop failed; the loop's exception is kept.", error.Message);
        Assert.Same(stopFailure, error.Exception);
    }

    [Fact]
    public void A_throw_from_the_stop_after_a_clean_loop_is_logged_and_rethrown()
    {
        var stopFailure = new UnauthorizedAccessException("The stop failed.");

        UnauthorizedAccessException thrown = Assert.Throws<UnauthorizedAccessException>(() =>
            Program.RunAsApp(() => 0, () => throw stopFailure, _log)
        );

        Assert.Same(stopFailure, thrown);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.Equal("Instance server: finishing the stop failed.", error.Message);
    }

    private int? Run(IReadOnlyList<string> files, SecondLaunchResult activated, SecondLaunchResult opened) =>
        RunWith(files, activated, opened, _errors.Add, NewGatePath());

    private int? RunWith(
        IReadOnlyList<string> files,
        SecondLaunchResult activated,
        SecondLaunchResult opened,
        Action<string> showError,
        string gatePath
    ) =>
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
            _log,
            showError,
            gatePath
        );

    private string NewGatePath() => Path.Combine(_gateRoot, Guid.NewGuid().ToString("N"), "launch-error.lock");

    public void Dispose()
    {
        if (Directory.Exists(_gateRoot))
        {
            Directory.Delete(_gateRoot, recursive: true);
        }
    }
}
