using Yamv.Core.Instance;
using Yamv.Core.Launch;
using Yamv.Core.Tests.Session;

namespace Yamv.Core.Tests.Launch;

public sealed class SecondLaunchTests
{
    private const MainLockAttempt Acquired = MainLockAttempt.Acquired;
    private const MainLockAttempt Held = MainLockAttempt.HeldByAnother;

    private readonly List<string> _calls = [];
    private readonly RecordingAppLog _log = new();
    private TimeSpan _delayed;

    [Fact]
    public async Task SignalSucceeds_AllowsForegroundBeforeSignalling()
    {
        SecondLaunchOutcome outcome = await Run(pids: [42], signal: true);

        Assert.Equal(SecondLaunchOutcome.Activated, outcome);
        Assert.Equal(["read", "allow 42", "signal"], _calls);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("info", entry.Level);
        Assert.Equal("Second launch: asked the main instance (pid 42) to come forward.", entry.Message);
    }

    [Fact]
    public async Task SignalSucceeds_PidUnknownAtFirst_ReReadsAndAllowsForeground()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, 42], signal: true);

        Assert.Equal(SecondLaunchOutcome.Activated, outcome);
        Assert.Equal(["read", "signal", "read", "allow 42"], _calls);
        Assert.Equal("Second launch: asked the main instance (pid 42) to come forward.", Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task SignalSucceeds_PidStillUnknown_LogsPidUnknown()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, null], signal: true);

        Assert.Equal(SecondLaunchOutcome.Activated, outcome);
        Assert.Equal(["read", "signal", "read"], _calls);
        Assert.Equal("Second launch: asked the main instance (pid unknown) to come forward.", Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task SignalFails_PidKnown_BringsForwardAndFails()
    {
        SecondLaunchOutcome outcome = await Run(pids: [42, 42], signal: false, bringForward: true);

        Assert.Equal(SecondLaunchOutcome.Failed, outcome);
        Assert.Equal(["read", "allow 42", "signal", "read", "bring 42"], _calls);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("warning", entry.Level);
        Assert.Equal("Second launch: the main instance (pid 42) did not answer; brought its window forward.", entry.Message);
    }

    [Fact]
    public async Task SignalFails_BringForwardFails_Fails()
    {
        SecondLaunchOutcome outcome = await Run(pids: [42, 42], signal: false, bringForward: false);

        Assert.Equal(SecondLaunchOutcome.Failed, outcome);
        Assert.Equal(["read", "allow 42", "signal", "read", "bring 42"], _calls);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("warning", entry.Level);
        Assert.Equal("Second launch: the main instance (pid 42) did not answer and its window could not be brought forward.", entry.Message);
    }

    [Fact]
    public async Task SignalFails_PidUnknown_LockRetrySucceeds_BecomesMain()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, null], signal: false, locks: [Acquired]);

        Assert.Equal(SecondLaunchOutcome.BecameMain, outcome);
        Assert.Equal(["read", "signal", "read", "retry lock"], _calls);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("info", entry.Level);
        Assert.Equal("Second launch: the main instance has exited; starting as the main instance.", entry.Message);
    }

    [Fact]
    public async Task SignalFails_PidGone_LockFreedLater_BecomesMain()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, null], signal: false, locks: [Held, Held, Acquired]);

        Assert.Equal(SecondLaunchOutcome.BecameMain, outcome);
        Assert.Equal(["read", "signal", "read", "retry lock", "delay 100", "retry lock", "delay 100", "retry lock"], _calls);
        Assert.Equal(
            [
                ("info", "Second launch: waiting up to 5 s for the main instance to exit."),
                ("info", "Second launch: the main instance has exited; starting as the main instance."),
            ],
            _log.Entries.Select(entry => (entry.Level, entry.Message))
        );
    }

    [Fact]
    public async Task SignalFails_PidGone_LockCannotOpen_FailsAfterOneAttempt()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, null], signal: false, locks: [MainLockAttempt.CannotOpen, Acquired]);

        Assert.Equal(SecondLaunchOutcome.Failed, outcome);
        Assert.Equal(["read", "signal", "read", "retry lock"], _calls);
        Assert.Equal(TimeSpan.Zero, _delayed);
        Assert.Equal("Second launch: the main instance did not answer and its pid is unknown; giving up.", Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task SignalFails_PidGone_LockNeverFree_FailsAfterTheWait()
    {
        SecondLaunchOutcome outcome = await Run(pids: [null, null], signal: false, locks: []);

        Assert.Equal(SecondLaunchOutcome.Failed, outcome);
        Assert.Equal(["read", "signal", "read", "retry lock"], _calls[..4]);
        Assert.Equal(51, _calls.Count(call => call == "retry lock"));
        Assert.Equal(50, _calls.Count(call => call == "delay 100"));
        Assert.Equal("retry lock", _calls[^1]);
        Assert.Equal(SecondLaunch.LockWaitTimeout, _delayed);
        Assert.Equal(TimeSpan.FromSeconds(5), SecondLaunch.LockWaitTimeout);
        Assert.Equal(
            [
                ("info", "Second launch: waiting up to 5 s for the main instance to exit."),
                ("warning", "Second launch: the main instance did not answer and its pid is unknown; giving up."),
            ],
            _log.Entries.Select(entry => (entry.Level, entry.Message))
        );
    }

    [Fact]
    public async Task SignalFails_PidKnown_NeverRetriesLock()
    {
        await Run(pids: [null, 42], signal: false, bringForward: false, locks: [Acquired]);

        Assert.DoesNotContain("retry lock", _calls);
        Assert.Contains("bring 42", _calls);
    }

    [Fact]
    public async Task SignalFails_RereadsPid_BringsForwardTheFreshPid()
    {
        SecondLaunchOutcome outcome = await Run(pids: [42, 43], signal: false, bringForward: true);

        Assert.Equal(SecondLaunchOutcome.Failed, outcome);
        Assert.Equal(["read", "allow 42", "signal", "read", "bring 43"], _calls);
        Assert.Equal("Second launch: the main instance (pid 43) did not answer; brought its window forward.", Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task SignalFails_PidGoneOnReread_RetriesLock()
    {
        SecondLaunchOutcome outcome = await Run(pids: [42, null], signal: false, bringForward: true, locks: [Acquired]);

        Assert.Equal(SecondLaunchOutcome.BecameMain, outcome);
        Assert.Equal(["read", "allow 42", "signal", "read", "retry lock"], _calls);
    }

    [Fact]
    public async Task Signal_UsesTheNamedTimeout()
    {
        TimeSpan? used = null;
        var actions = new SecondLaunchActions(
            ReadPid: () => 42,
            AllowForeground: _ => { },
            Signal: timeout =>
            {
                used = timeout;
                return Task.FromResult(true);
            },
            BringForward: _ => false,
            RetryLock: () => Held,
            Delay: _ => Task.CompletedTask
        );

        await new SecondLaunch(actions, _log).RunAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(1500), SecondLaunch.SignalTimeout);
        Assert.Equal(SecondLaunch.SignalTimeout, used);
    }

    /// <summary>Runs a launch over recorded actions; the lock retries answer <paramref name="locks"/> in turn, then held.</summary>
    private Task<SecondLaunchOutcome> Run(int?[] pids, bool signal, bool bringForward = false, MainLockAttempt[]? locks = null)
    {
        var reads = new Queue<int?>(pids);
        var lockAnswers = new Queue<MainLockAttempt>(locks ?? []);
        var actions = new SecondLaunchActions(
            ReadPid: () =>
            {
                _calls.Add("read");
                return reads.Dequeue();
            },
            AllowForeground: pid => _calls.Add($"allow {pid}"),
            Signal: _ =>
            {
                _calls.Add("signal");
                return Task.FromResult(signal);
            },
            BringForward: pid =>
            {
                _calls.Add($"bring {pid}");
                return bringForward;
            },
            RetryLock: () =>
            {
                _calls.Add("retry lock");
                return lockAnswers.TryDequeue(out MainLockAttempt answer) ? answer : Held;
            },
            Delay: delay =>
            {
                _calls.Add($"delay {delay.TotalMilliseconds:0}");
                _delayed += delay;
                return Task.CompletedTask;
            }
        );
        return new SecondLaunch(actions, _log).RunAsync();
    }
}
