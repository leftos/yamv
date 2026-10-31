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
    private Func<int, bool>? _bringForward;
    private Func<string, TimeSpan, Task<InstanceRequestOutcome>> _request = (_, _) =>
        throw new InvalidOperationException("The test staged no reply.");

    [Fact]
    public async Task Activate_Confirmed_ExitsZeroAndAllowsForegroundFirst()
    {
        SecondLaunchResult result = await Activate(pids: [42], replies: [InstanceRequestOutcome.Confirmed]);

        Assert.Equal(SecondLaunchResult.Activated, result);
        Assert.Equal(["read", "allow 42", "request 3000"], _calls);
        Assert.Equal(InstanceMessages.Activate, Assert.Single(_sent));
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("info", entry.Level);
        Assert.Equal("Second launch: asked the app (pid 42) to come forward.", entry.Message);
    }

    [Fact]
    public async Task Activate_NoPidAtFirst_UsesTheShortDeadline()
    {
        SecondLaunchResult result = await Activate(pids: [null], replies: [InstanceRequestOutcome.Confirmed]);

        Assert.Equal(SecondLaunchResult.Activated, result);
        Assert.Equal(["read", "request 1500"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), SecondLaunch.NoPidDeadline);
        Assert.Equal("Second launch: asked the app (pid unknown) to come forward.", Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task Activate_LivePid_UsesTheRequestDeadline()
    {
        await Activate(pids: [42], replies: [InstanceRequestOutcome.Confirmed]);

        Assert.Equal(SecondLaunch.RequestDeadline, Assert.Single(_budgets));
    }

    [Fact]
    public async Task Activate_Refused_FailsWithNoRaiseAndNoLockWait()
    {
        SecondLaunchResult result = await Activate(pids: [42], replies: [InstanceRequestOutcome.Refused], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.Refused), result);
        Assert.Equal(["read", "allow 42", "request 3000"], _calls);
        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal("warning", entry.Level);
        Assert.Equal("Second launch: the app (pid 42) refused to come forward.", entry.Message);
    }

    [Fact]
    public async Task Activate_Closing_WaitsForTheLock_AndBecomesMain()
    {
        SecondLaunchResult result = await Activate(pids: [42, null], replies: [InstanceRequestOutcome.Closing], locks: [Held, Acquired]);

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Equal(["read", "allow 42", "request 3000", "retry lock", "read", "delay 100", "retry lock"], _calls);
        Assert.Equal(
            [
                ("info", "Second launch: the app is closing; waiting up to 5 s for it to exit."),
                ("info", "Second launch: the app has exited; starting as the app."),
            ],
            _log.Entries.Select(entry => (entry.Level, entry.Message))
        );
    }

    [Fact]
    public async Task Activate_Closing_NeverRaises()
    {
        SecondLaunchResult result = await Activate(pids: [42], replies: [InstanceRequestOutcome.Closing], bringForward: true);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.DoesNotContain(_calls, call => call.StartsWith("bring", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activate_Closing_ThenNewApp_ResendsToIt()
    {
        SecondLaunchResult result = await Activate(
            pids: [42, null, 43],
            replies: [InstanceRequestOutcome.Closing, InstanceRequestOutcome.Confirmed],
            locks: [Held, Held]
        );

        Assert.Equal(SecondLaunchResult.Activated, result);
        Assert.Equal(
            ["read", "allow 42", "request 3000", "retry lock", "read", "delay 100", "retry lock", "read", "allow 43", "request 3000"],
            _calls
        );
        Assert.Equal([InstanceMessages.Activate, InstanceMessages.Activate], _sent);
        Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: a new app (pid 43) holds the lock; sending the request to it.");
    }

    [Fact]
    public async Task Activate_NoReply_LivePid_RaisesWithAFreshPidThenWaitsThenFails()
    {
        // The frozen app: the raise works, but only an answer or the lock ends a launch well, and the app holds the lock.
        _requestTakesItsBudget = true;
        SecondLaunchResult result = await Activate(pids: [42], replies: [InstanceRequestOutcome.NoReply], bringForward: true);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.Equal(["read", "allow 42", "request 3000", "read", "bring 42", "retry lock", "read", "delay 100"], _calls[..8]);
        Assert.Single(_sent);
        Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: the app (pid 42) did not answer; brought its window forward.");
        Assert.Equal("Second launch: no app answered within 8 s or the lock wait; giving up.", _log.Entries[^1].Message);
        Assert.Equal(SecondLaunch.OverallCap, _time.GetElapsedTime(_started));
    }

    [Fact]
    public async Task Activate_NoReply_FreshPidIsANewApp_ResendsToIt()
    {
        SecondLaunchResult result = await Activate(
            pids: [42, 43],
            replies: [InstanceRequestOutcome.NoReply, InstanceRequestOutcome.Confirmed],
            bringForward: true
        );

        Assert.Equal(SecondLaunchResult.Activated, result);
        Assert.Equal(["read", "allow 42", "request 3000", "read", "bring 43", "retry lock", "read", "allow 43", "request 3000"], _calls);
        Assert.Equal([InstanceMessages.Activate, InstanceMessages.Activate], _sent);
    }

    [Fact]
    public async Task Activate_NoReply_LockFrees_BecomesMain()
    {
        SecondLaunchResult result = await Activate(
            pids: [42],
            replies: [InstanceRequestOutcome.NoReply],
            bringForward: true,
            locks: [Held, Acquired]
        );

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Contains("bring 42", _calls);
    }

    [Fact]
    public async Task Activate_NoReply_NoPid_WaitsForTheLock_AndBecomesMain()
    {
        SecondLaunchResult result = await Activate(pids: [null], replies: [InstanceRequestOutcome.NoReply], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Equal(["read", "request 1500", "read", "retry lock"], _calls);
    }

    [Fact]
    public async Task Activate_NoServer_NoPid_WaitsForTheLock_AndBecomesMain()
    {
        SecondLaunchResult result = await Activate(pids: [null], replies: [InstanceRequestOutcome.NoServer], locks: [Held, Acquired]);

        Assert.Equal(SecondLaunchResult.BecameMain, result);
        Assert.Equal(["read", "request 1500", "read", "retry lock", "read", "delay 100", "retry lock"], _calls);
        Assert.Equal("Second launch: no app is answering; waiting up to 5 s for one to take the lock.", _log.Entries[0].Message);
    }

    [Fact]
    public async Task Activate_Denied_RaisesAndFails_WithNoLockWait()
    {
        SecondLaunchResult result = await Activate(pids: [42], replies: [InstanceRequestOutcome.Denied], locks: [Acquired]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.Denied), result);
        Assert.Equal(["read", "allow 42", "request 3000", "read", "bring 42"], _calls);
        Assert.Equal(
            "Second launch: the app (pid 42) did not answer and its window could not be brought forward.",
            Assert.Single(_log.Entries).Message
        );
    }

    [Fact]
    public async Task Activate_HungBringForward_IsBounded_AndTheLaunchStillEnds()
    {
        using var release = new ManualResetEventSlim();
        _bringForward = _ => release.Wait(TimeSpan.FromMinutes(5));
        try
        {
            // The fakes answer at once, so the launch is already waiting on the raise's bound when this returns.
            Task<SecondLaunchResult> run = Activate(pids: [42], replies: [InstanceRequestOutcome.NoReply], locks: [Acquired]);
            _time.Advance(SecondLaunch.BringForwardBound);

            SecondLaunchResult result = await run.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

            Assert.Equal(SecondLaunchResult.BecameMain, result);
            Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: bringing the app (pid 42) forward did not finish; going on.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Activate_HungBringForward_IsCutToWhatIsLeftOfTheCap()
    {
        using var release = new ManualResetEventSlim();
        _bringForward = _ => release.Wait(TimeSpan.FromMinutes(5));
        _requestTakesItsBudget = true;
        try
        {
            // The fakes answer at once, so the launch is already waiting on the raise's bound when this returns.
            Task<SecondLaunchResult> run = Activate(
                pids: [42],
                replies: [InstanceRequestOutcome.NoReply],
                overallCap: SecondLaunch.RequestDeadline + TimeSpan.FromMilliseconds(500)
            );
            _time.Advance(TimeSpan.FromMilliseconds(500));

            SecondLaunchResult result = await run.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

            Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
            Assert.Contains(_log.Entries, entry => entry.Message == "Second launch: bringing the app (pid 42) forward did not finish; going on.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Activate_NoTimeLeft_SkipsTheRaise()
    {
        _requestTakesItsBudget = true;
        SecondLaunchResult result = await Activate(
            pids: [42],
            replies: [InstanceRequestOutcome.NoReply],
            bringForward: true,
            overallCap: SecondLaunch.RequestDeadline
        );

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.DoesNotContain(_calls, call => call.StartsWith("bring", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("forward", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activate_NeverPassesTheOverallCap()
    {
        // Each request takes its whole budget and is told closing, and each lock wait meets a new app at once.
        _requestTakesItsBudget = true;
        SecondLaunchResult result = await Activate(
            pids: [40, 41, 42, 43, 44],
            replies: [InstanceRequestOutcome.Closing, InstanceRequestOutcome.Closing, InstanceRequestOutcome.Closing]
        );

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        // 3 s, 3 s, then what is left of the 8 s cap; nothing is sent once it has run out.
        double[] expectedBudgets = [3000, 3000, 2000];
        Assert.Equal(expectedBudgets, _budgets.Select(budget => budget.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromSeconds(8), _time.GetElapsedTime(_started));
    }

    [Fact]
    public async Task Activate_LockCannotOpen_FailsAtOnce()
    {
        SecondLaunchResult result = await Activate(
            pids: [null],
            replies: [InstanceRequestOutcome.NoServer],
            locks: [MainLockAttempt.CannotOpen, Acquired]
        );

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), result);
        Assert.Equal(["read", "request 1500", "read", "retry lock"], _calls);
        Assert.Equal(TimeSpan.Zero, _delayed);
        Assert.Equal("Second launch: the lock file cannot be opened; giving up.", _log.Entries[^1].Message);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public async Task Activate_DeadlineNotPositiveOrFinite_Throws(double milliseconds)
    {
        TimeSpan deadline = double.IsNaN(milliseconds) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().RunAsync(deadline, SecondLaunch.NoPidDeadline, SecondLaunch.OverallCap)
        );
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().RunAsync(SecondLaunch.RequestDeadline, deadline, SecondLaunch.OverallCap)
        );
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public async Task Activate_OverallCapNotPositiveOrFinite_Throws(double milliseconds)
    {
        TimeSpan cap = double.IsNaN(milliseconds) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().RunAsync(SecondLaunch.RequestDeadline, SecondLaunch.NoPidDeadline, cap)
        );
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Open_Confirmed_ReturnsOpenedAndNeverBringsForward()
    {
        SecondLaunchResult result = await Open(pids: [42], replies: [InstanceRequestOutcome.Confirmed]);

        Assert.Equal(SecondLaunchResult.Opened, result);
        Assert.Equal(["read", "allow 42", "request 3000"], _calls);
        Assert.Equal(_message, Assert.Single(_sent));
        Assert.Equal(TimeSpan.FromSeconds(3), SecondLaunch.RequestDeadline);
        Assert.Equal(TimeSpan.FromSeconds(8), SecondLaunch.OverallCap);
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
    public async Task Open_Closing_LogsThatTheAppIsClosing()
    {
        await Open(pids: [42], replies: [InstanceRequestOutcome.Closing], locks: [Acquired]);

        LogEntry entry = _log.Entries[0];
        Assert.Equal("info", entry.Level);
        Assert.Equal("Second launch: the app is closing; waiting up to 5 s for it to exit.", entry.Message);
    }

    [Fact]
    public async Task Open_NoServerNoPid_LogsThatNoAppIsAnswering()
    {
        await Open(pids: [null, null, null], replies: [InstanceRequestOutcome.NoServer], locks: [Held, Acquired]);

        LogEntry entry = _log.Entries[0];
        Assert.Equal("info", entry.Level);
        Assert.Equal("Second launch: no app is answering; waiting up to 5 s for one to take the lock.", entry.Message);
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

    [Fact]
    public async Task Open_RelativePath_ReturnsUnsendableWithoutConnecting()
    {
        SecondLaunchResult result = await Open(pids: [], replies: [], files: ["notes/readme.md"]);

        Assert.Equal(SecondLaunchResult.Failed(SecondLaunchFailure.Unsendable), result);
        Assert.Empty(_calls);
        Assert.Equal(
            "Second launch: the 1 file path(s) cannot be sent in an open message: a path is not a full path.",
            Assert.Single(_log.Entries).Message
        );
    }

    [Fact]
    public async Task OpenAsync_WithADeadlineOverIntMaxMilliseconds_ThrowsBeforeAnyAction()
    {
        ArgumentOutOfRangeException ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], AboveIntMax(), SecondLaunch.OverallCap)
        );

        Assert.Equal("openDeadline", ex.ParamName);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task OpenAsync_WithAnOverallCapOverIntMaxMilliseconds_ThrowsBeforeAnyAction()
    {
        ArgumentOutOfRangeException ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], SecondLaunch.RequestDeadline, AboveIntMax())
        );

        Assert.Equal("overallCap", ex.ParamName);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task OpenAsync_WithNullFiles_ThrowsNamingFiles()
    {
        ArgumentNullException ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NewOpenLaunch().OpenAsync(null!, SecondLaunch.RequestDeadline, SecondLaunch.OverallCap)
        );

        Assert.Equal("files", ex.ParamName);
        Assert.Empty(_calls);
    }

    /// <summary>One millisecond past the longest time a timer or a cancellation delay accepts.</summary>
    private static TimeSpan AboveIntMax() => TimeSpan.FromMilliseconds((double)int.MaxValue + 1);

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public async Task Open_OpenDeadlineNotPositiveOrFinite_Throws(double milliseconds)
    {
        TimeSpan deadline = double.IsNaN(milliseconds) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], deadline, SecondLaunch.OverallCap)
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
            NewOpenLaunch().OpenAsync([Path.Combine(Path.GetTempPath(), "notes.md")], SecondLaunch.RequestDeadline, cap)
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
        return new SecondLaunch(Actions(new int?[64], bringForward: false, locks: null), _log);
    }

    /// <summary>
    /// Runs an open launch for <paramref name="files"/> (one temp file by default) over recorded actions; each request answers
    /// the next of <paramref name="replies"/>, and the lock retries answer <paramref name="locks"/> in turn, then held.
    /// </summary>
    private Task<SecondLaunchResult> Open(int?[] pids, InstanceRequestOutcome[] replies, MainLockAttempt[]? locks = null, string[]? files = null)
    {
        string[] paths = files ?? [Path.Combine(Path.GetTempPath(), "notes.md")];
        Assert.True(files is not null || OpenMessage.TryEncode(paths, out _message, out _));
        StageReplies(replies);
        return new SecondLaunch(Actions(pids, bringForward: false, locks), _log).OpenAsync(
            paths,
            SecondLaunch.RequestDeadline,
            SecondLaunch.OverallCap
        );
    }

    /// <summary>
    /// Runs a no-arg launch over recorded actions; each request answers the next of <paramref name="replies"/>, the lock retries
    /// answer <paramref name="locks"/> in turn, then held, and the pid reads answer <paramref name="pids"/>, then the last again.
    /// </summary>
    private Task<SecondLaunchResult> Activate(
        int?[] pids,
        InstanceRequestOutcome[] replies,
        bool bringForward = false,
        MainLockAttempt[]? locks = null,
        TimeSpan? overallCap = null
    )
    {
        StageReplies(replies);
        return new SecondLaunch(Actions(pids, bringForward, locks), _log).RunAsync(
            SecondLaunch.RequestDeadline,
            SecondLaunch.NoPidDeadline,
            overallCap ?? SecondLaunch.OverallCap
        );
    }

    private void StageReplies(InstanceRequestOutcome[] replies)
    {
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
    }

    private SecondLaunchActions Actions(int?[] pids, bool bringForward, MainLockAttempt[]? locks)
    {
        var reads = new Queue<int?>(pids);
        var lockAnswers = new Queue<MainLockAttempt>(locks ?? []);
        return new SecondLaunchActions(
            ReadPid: () =>
            {
                _calls.Add("read");
                return reads.Count > 1 ? reads.Dequeue() : reads.Peek();
            },
            AllowForeground: pid => _calls.Add($"allow {pid}"),
            Request: (message, budget) => _request(message, budget),
            BringForward: pid =>
            {
                if (_bringForward is { } hold)
                {
                    return hold(pid);
                }

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
