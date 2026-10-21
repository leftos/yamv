using Microsoft.Extensions.Time.Testing;
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
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _sent = [];
    private readonly List<TimeSpan> _budgets = [];
    private TimeSpan _delayed;
    private string _message = "";
    private long _started;
    private bool _requestTakesItsBudget;
    private Func<string, TimeSpan, Task<InstanceRequestOutcome>> _request = (_, _) =>
        throw new InvalidOperationException("An activate launch sends no request.");

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
            Request: (_, _) => throw new InvalidOperationException("An activate launch sends no request."),
            BringForward: _ => false,
            RetryLock: () => Held,
            Delay: _ => Task.CompletedTask,
            Time: _time
        );

        await new SecondLaunch(actions, _log).RunAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(1500), SecondLaunch.SignalTimeout);
        Assert.Equal(SecondLaunch.SignalTimeout, used);
    }

    [Fact]
    public async Task Open_Confirmed_ReturnsOpenedAndNeverBringsForward()
    {
        SecondLaunchResult result = await Open(pids: [42], replies: [InstanceRequestOutcome.Confirmed]);

        Assert.Equal(SecondLaunchResult.Opened, result);
        Assert.Equal(["read", "allow 42", "request 3000"], _calls);
        Assert.Equal(_message, Assert.Single(_sent));
        Assert.Equal(TimeSpan.FromSeconds(3), SecondLaunch.OpenDeadline);
        Assert.Equal(TimeSpan.FromSeconds(8), SecondLaunch.OpenOverallCap);
    }

    [Fact]
    public async Task Open_Refused_FailsWithReasonAndNeverBringsForward()
    {
        SecondLaunchResult result = await Open(pids: [42], replies: [InstanceRequestOutcome.Refused]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.Refused), result);
        Assert.DoesNotContain(_calls, call => call.StartsWith("bring", StringComparison.Ordinal));
        Assert.Equal("warning", Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public async Task Open_NoReply_FailsWithoutLockWaitOrBringForward()
    {
        SecondLaunchResult result = await Open(pids: [42, 42], replies: [InstanceRequestOutcome.NoReply], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.Equal(["read", "allow 42", "request 3000"], _calls);
    }

    [Fact]
    public async Task Open_NoServer_PidKnown_FailsWithoutBringForward()
    {
        SecondLaunchResult result = await Open(pids: [42, 42], replies: [InstanceRequestOutcome.NoServer], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.Equal(["read", "allow 42", "request 3000", "read"], _calls);
    }

    [Fact]
    public async Task Open_NoServerNoPid_WaitsForLockThenBecameMain()
    {
        SecondLaunchResult result = await Open(pids: [null, null, null], replies: [InstanceRequestOutcome.NoServer], locks: [Held, Acquired]);

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Equal(["read", "request 3000", "read", "retry lock", "read", "delay 100", "retry lock"], _calls);
    }

    [Fact]
    public async Task Open_ClosingThenLockTaken_BecameMain()
    {
        SecondLaunchResult result = await Open(pids: [42], replies: [InstanceRequestOutcome.Closing], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Equal(["read", "allow 42", "request 3000", "retry lock"], _calls);
    }

    [Fact]
    public async Task Open_ClosingThenNewPid_SendsAgainAndConfirms()
    {
        SecondLaunchResult result = await Open(
            pids: [42, null, 43],
            replies: [InstanceRequestOutcome.Closing, InstanceRequestOutcome.Confirmed],
            locks: [Held, Held]
        );

        Assert.Equal(SecondLaunchResult.Opened, result);
        Assert.Equal(
            ["read", "allow 42", "request 3000", "retry lock", "read", "delay 100", "retry lock", "read", "allow 43", "request 3000"],
            _calls
        );
        Assert.Equal([_message, _message], _sent);
    }

    [Fact]
    public async Task Open_WaitsLongerThanOverallCap_Fails()
    {
        // Each request takes its whole budget and is told closing, and each lock wait meets a new app at once.
        _requestTakesItsBudget = true;
        SecondLaunchResult result = await Open(
            pids: [40, 41, 42, 43, 44],
            replies: [InstanceRequestOutcome.Closing, InstanceRequestOutcome.Closing, InstanceRequestOutcome.Closing],
            locks: []
        );

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        // 3 s, 3 s, then what is left of the 8 s cap; nothing is sent once it has run out.
        double[] expectedBudgets = [3000, 3000, 2000];
        Assert.Equal(expectedBudgets, _budgets.Select(budget => budget.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromSeconds(8), _time.GetElapsedTime(_started));
    }

    [Fact]
    public async Task Open_PayloadOverCap_FailsBeforeConnecting()
    {
        string root = OperatingSystem.IsWindows() ? "C:/" : "/";
        string[] files = [root + new string('a', 70_000)];

        SecondLaunchResult result = await Open(pids: [42], replies: [InstanceRequestOutcome.Confirmed], files: files);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.Unsendable), result);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public async Task Open_OpenDeadlineNotPositiveOrFinite_Throws(double milliseconds)
    {
        TimeSpan deadline = double.IsNaN(milliseconds) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], deadline, SecondLaunch.OpenOverallCap)
        );
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public async Task Open_OverallCapNotPositiveOrFinite_Throws(double milliseconds)
    {
        TimeSpan cap = double.IsNaN(milliseconds) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], SecondLaunch.OpenDeadline, cap)
        );
        Assert.Empty(_calls);
    }

    /// <summary>A launch whose app is never reached: every pid read is unknown and every request finds no server.</summary>
    private SecondLaunch NewOpenLaunch()
    {
        _request = (_, _) =>
        {
            _calls.Add("request");
            return Task.FromResult(InstanceRequestOutcome.NoServer);
        };
        return new SecondLaunch(Actions(new int?[64], signal: false, bringForward: false, locks: null), _log);
    }

    /// <summary>
    /// Runs an open launch for <paramref name="files"/> (one temp file by default) over recorded actions; each request answers
    /// the next of <paramref name="replies"/>, and the lock retries answer <paramref name="locks"/> in turn, then held.
    /// </summary>
    private Task<SecondLaunchResult> Open(int?[] pids, InstanceRequestOutcome[] replies, MainLockAttempt[]? locks = null, string[]? files = null)
    {
        string[] paths = files ?? [Path.Combine(Path.GetTempPath(), "notes.md")];
        Assert.True(files is not null || OpenMessage.TryEncode(paths, out _message, out _));
        var answers = new Queue<InstanceRequestOutcome>(replies);
        _request = (message, budget) =>
        {
            _calls.Add($"request {budget.TotalMilliseconds:0}");
            _sent.Add(message);
            _budgets.Add(budget);
            if (_requestTakesItsBudget)
            {
                _time.Advance(budget);
            }

            return Task.FromResult(answers.Dequeue());
        };
        _started = _time.GetTimestamp();
        return new SecondLaunch(Actions(pids, signal: false, bringForward: false, locks), _log).OpenAsync(
            paths,
            SecondLaunch.OpenDeadline,
            SecondLaunch.OpenOverallCap
        );
    }

    /// <summary>Runs a launch over recorded actions; the lock retries answer <paramref name="locks"/> in turn, then held.</summary>
    private Task<SecondLaunchOutcome> Run(int?[] pids, bool signal, bool bringForward = false, MainLockAttempt[]? locks = null) =>
        new SecondLaunch(Actions(pids, signal, bringForward, locks), _log).RunAsync();

    private SecondLaunchActions Actions(int?[] pids, bool signal, bool bringForward, MainLockAttempt[]? locks)
    {
        var reads = new Queue<int?>(pids);
        var lockAnswers = new Queue<MainLockAttempt>(locks ?? []);
        return new SecondLaunchActions(
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
            Request: (message, budget) => _request(message, budget),
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
                _time.Advance(delay);
                return Task.CompletedTask;
            },
            Time: _time
        );
    }
}
