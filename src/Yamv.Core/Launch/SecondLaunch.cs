using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Launch;

/// <summary>
/// Runs a launch that found the main-instance lock taken. A no-arg launch (<see cref="RunAsync"/>) lets the app take the
/// foreground, asks it to come forward and waits for its answer; when it does not answer, the launch brings its window forward
/// directly and waits for the lock. A launch with files (<see cref="OpenAsync"/>) asks the app to open them and waits for its
/// confirmation. Only the lock makes either launch the app.
/// </summary>
/// <param name="actions">The platform actions the launch runs through.</param>
/// <param name="log">The process's log.</param>
public sealed class SecondLaunch(SecondLaunchActions actions, IAppLog log)
{
    /// <summary>How long one <c>activate</c> request waits when no pid is known, so no app may be running to answer it.</summary>
    public static readonly TimeSpan NoPidDeadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a launch that got no answer keeps retrying the lock, so an app that is still saving its session hands over to it
    /// once the lock is free.
    /// </summary>
    public static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The pause between lock retries within <see cref="LockWaitTimeout"/>.</summary>
    public static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long one request waits, from its first connect attempt, for the app to answer it. It stays below the server's 10 s reply
    /// bound: past that bound a request the app has begun but not answered is closed with no reply, which the launcher reads as
    /// closing and may send the request again.
    /// </summary>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(3);

    /// <summary>The bound on a whole launch: every request, lock wait and resend included.</summary>
    public static readonly TimeSpan OverallCap = TimeSpan.FromSeconds(8);

    /// <summary>How long a launch waits for the bring-forward of an app that did not answer: a hung app may hold that call up.</summary>
    public static readonly TimeSpan BringForwardBound = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the running app to come forward and waits for its answer, allowing it the foreground first whenever its pid is known.
    /// Success is the app's <c>opened</c> alone: a <c>refused</c> fails at once and a <c>closing</c> waits for the lock or a new
    /// app, which gets the request instead. An app that does not answer, or cannot be reached, has its window brought forward from
    /// here (the pid read afresh, within <see cref="BringForwardBound"/>); the launch then still fails unless the lock comes free or
    /// a new app appears, and a pipe it may not use fails at once. The lock wait lasts up to <see cref="LockWaitTimeout"/>.
    /// </summary>
    /// <param name="requestDeadline">
    /// The budget of one request when a pid is known; the app passes <see cref="RequestDeadline"/>. Keep it below the server's 10 s
    /// reply bound.
    /// </param>
    /// <param name="noPidDeadline">The budget of one request with no pid; the app passes <see cref="NoPidDeadline"/>.</param>
    /// <param name="overallCap">The bound on the whole launch; the app passes <see cref="OverallCap"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Any of the times is not positive and finite, or is over <see cref="int.MaxValue"/> ms, which a timer or a cancellation delay
    /// refuses.
    /// </exception>
    /// <returns>Activated (exit 0), BecameMain (start as the app), or Failed with its reason (exit 1).</returns>
    public async Task<SecondLaunchResult> RunAsync(TimeSpan requestDeadline, TimeSpan noPidDeadline, TimeSpan overallCap)
    {
        RequirePositiveAndFinite(requestDeadline, nameof(requestDeadline), "An activate launch's request deadline");
        RequirePositiveAndFinite(noPidDeadline, nameof(noPidDeadline), "An activate launch's no-pid deadline");
        RequirePositiveAndFinite(overallCap, nameof(overallCap), "An activate launch's overall cap");

        long start = actions.Time.GetTimestamp();
        int? pid = actions.ReadPid();
        while (true)
        {
            if (pid is { } known)
            {
                actions.AllowForeground(known);
            }

            TimeSpan left = overallCap - actions.Time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return GaveUp(overallCap);
            }

            TimeSpan budget = Min(pid is null ? noPidDeadline : requestDeadline, left);
            InstanceRequestOutcome outcome = await actions.Request(InstanceMessages.Activate, budget).ConfigureAwait(false);
            (SecondLaunchResult? ended, int? newPid) = await AfterActivateAsync(outcome, pid, start, overallCap).ConfigureAwait(false);
            if (ended is not null)
            {
                return ended;
            }

            pid = newPid;
        }
    }

    /// <summary>What an activate's outcome leads to: the launch's result, or the pid of a new app to send the request to.</summary>
    private async Task<(SecondLaunchResult? Ended, int? NewPid)> AfterActivateAsync(
        InstanceRequestOutcome outcome,
        int? pid,
        long start,
        TimeSpan overallCap
    )
    {
        string shown = pid is { } value ? $"pid {value}" : "pid unknown";
        switch (outcome)
        {
            case InstanceRequestOutcome.Confirmed:
                log.LogInfo($"Second launch: asked the app ({shown}) to come forward.");
                return (SecondLaunchResult.Activated, null);
            case InstanceRequestOutcome.Refused:
                log.LogWarning($"Second launch: the app ({shown}) refused to come forward.");
                return (SecondLaunchResult.Failed(SecondLaunchFailure.Refused), null);
            case InstanceRequestOutcome.Closing:
                return await WaitForLockOrNewAppAsync(pid, start, overallCap, closing: true).ConfigureAwait(false);
            default:
                return await AfterNoAnswerAsync(outcome, pid, start, overallCap).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings forward the window of the app the pid file names now, then fails on a pipe this process may not use, or waits for the
    /// lock or for an app that is not the one <paramref name="pid"/> named, which gets the request instead. A fresh read re-checks
    /// the process's start time, so a pid reused while the request waited is never brought forward.
    /// </summary>
    private async Task<(SecondLaunchResult? Ended, int? NewPid)> AfterNoAnswerAsync(
        InstanceRequestOutcome outcome,
        int? pid,
        long start,
        TimeSpan overallCap
    )
    {
        int? fresh = actions.ReadPid();
        if (fresh is { } running)
        {
            await BringForwardBoundedAsync(running, start, overallCap).ConfigureAwait(false);
        }

        return outcome == InstanceRequestOutcome.Denied
            ? (SecondLaunchResult.Failed(SecondLaunchFailure.Denied), null)
            : await WaitForLockOrNewAppAsync(pid, start, overallCap, closing: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the bring-forward on the pool and waits for it within <see cref="BringForwardBound"/> and what is left of the cap, so a
    /// hung app cannot hold the launch; with no time left it skips the raise.
    /// </summary>
    private async Task BringForwardBoundedAsync(int pid, long start, TimeSpan overallCap)
    {
        TimeSpan left = overallCap - actions.Time.GetElapsedTime(start);
        if (left <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            bool raised = await Task.Run(() => actions.BringForward(pid)).WaitAsync(Min(BringForwardBound, left), actions.Time).ConfigureAwait(false);
            string result = raised ? "did not answer; brought its window forward" : "did not answer and its window could not be brought forward";
            log.LogWarning($"Second launch: the app (pid {pid}) {result}.");
        }
        catch (TimeoutException)
        {
            log.LogWarning($"Second launch: bringing the app (pid {pid}) forward did not finish; going on.");
        }
    }

    /// <summary>
    /// Asks the running app to open <paramref name="files"/> in a viewer window and waits for its confirmation; never brings a
    /// window forward. Each request gets <paramref name="openDeadline"/>, cut to what is left of <paramref name="overallCap"/>,
    /// which bounds the whole launch. An app that answers <c>closing</c> (or has no server and no pid) is waited out the way
    /// <see cref="RunAsync"/> waits for the lock, for up to <see cref="LockWaitTimeout"/>; when a new app's pid appears during that
    /// wait, the request goes to it instead. A payload too large for one message fails before anything is sent.
    /// </summary>
    /// <param name="files">The full paths to open.</param>
    /// <param name="openDeadline">
    /// The budget of one request; the app passes <see cref="RequestDeadline"/>. Keep it below the server's 10 s reply bound, or a
    /// request the app has begun may close with no reply and the files be sent twice.
    /// </param>
    /// <param name="overallCap">The bound on the whole launch; the app passes <see cref="OverallCap"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null; nothing is read, sent or waited for.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either time is not positive and finite, or is over <see cref="int.MaxValue"/> ms, which a timer or a cancellation delay
    /// refuses.
    /// </exception>
    /// <returns>Opened (exit 0), BecameMain (start as the app with the files), or Failed with its reason (show the error, exit 1).</returns>
    public async Task<SecondLaunchResult> OpenAsync(IReadOnlyList<string> files, TimeSpan openDeadline, TimeSpan overallCap)
    {
        ArgumentNullException.ThrowIfNull(files);
        RequirePositiveAndFinite(openDeadline, nameof(openDeadline), "An open launch's deadline");
        RequirePositiveAndFinite(overallCap, nameof(overallCap), "An open launch's overall cap");
        if (!OpenMessage.TryEncode(files, out string message, out OpenMessageProblem problem))
        {
            log.LogWarning($"Second launch: the {files.Count} file path(s) cannot be sent in an open message: {Describe(problem)}.");
            return SecondLaunchResult.Failed(SecondLaunchFailure.Unsendable);
        }

        long start = actions.Time.GetTimestamp();
        int? pid = actions.ReadPid();
        while (true)
        {
            if (pid is { } known)
            {
                actions.AllowForeground(known);
            }

            TimeSpan left = overallCap - actions.Time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return GaveUp(overallCap);
            }

            InstanceRequestOutcome outcome = await actions.Request(message, Min(openDeadline, left)).ConfigureAwait(false);
            SecondLaunchResult? settled = Settle(outcome, pid);
            if (settled is not null)
            {
                return settled;
            }

            (SecondLaunchResult? ended, int? newPid) = await WaitForLockOrNewAppAsync(
                    pid,
                    start,
                    overallCap,
                    outcome == InstanceRequestOutcome.Closing
                )
                .ConfigureAwait(false);
            if (ended is not null)
            {
                return ended;
            }

            pid = newPid;
        }
    }

    /// <summary>
    /// The result an outcome settles, or null when the app is closing (or gone without a pid) and the launch waits for the next.
    /// </summary>
    private SecondLaunchResult? Settle(InstanceRequestOutcome outcome, int? pid)
    {
        string shown = pid is { } value ? $"pid {value}" : "pid unknown";
        switch (outcome)
        {
            case InstanceRequestOutcome.Confirmed:
                log.LogInfo($"Second launch: the app ({shown}) took the files to open.");
                return SecondLaunchResult.Opened;
            case InstanceRequestOutcome.Refused:
                log.LogWarning($"Second launch: the app ({shown}) refused to open the files.");
                return SecondLaunchResult.Failed(SecondLaunchFailure.Refused);
            case InstanceRequestOutcome.NoReply:
                log.LogWarning($"Second launch: the app ({shown}) did not confirm the open in time.");
                return SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding);
            case InstanceRequestOutcome.Denied:
                log.LogWarning($"Second launch: the app ({shown}) cannot be reached from this process.");
                return SecondLaunchResult.Failed(SecondLaunchFailure.Denied);
            case InstanceRequestOutcome.NoServer when actions.ReadPid() is { } running:
                log.LogWarning($"Second launch: the app (pid {running}) is running but does not answer on its pipe.");
                return SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding);
            default:
                return null;
        }
    }

    /// <summary>
    /// Retries the lock every <see cref="LockRetryInterval"/> for up to <see cref="LockWaitTimeout"/>, within the overall cap,
    /// reading the pid between tries: the result once the lock is taken or the wait is over, or the pid of a new app (one that
    /// is not <paramref name="closingPid"/>) to send the request to. <paramref name="closing"/> says which wait this is: the app
    /// answered <c>closing</c>, or nothing answered at all.
    /// </summary>
    private async Task<(SecondLaunchResult? Ended, int? NewPid)> WaitForLockOrNewAppAsync(
        int? closingPid,
        long start,
        TimeSpan overallCap,
        bool closing
    )
    {
        LogWaitingForTheNextApp(closing);
        for (TimeSpan waited = TimeSpan.Zero; ; waited += LockRetryInterval)
        {
            MainLockAttempt attempt = actions.RetryLock();
            if (attempt == MainLockAttempt.Acquired)
            {
                log.LogInfo("Second launch: the app has exited; starting as the app.");
                return (SecondLaunchResult.BecameMain, null);
            }

            if (attempt == MainLockAttempt.CannotOpen)
            {
                log.LogWarning("Second launch: the lock file cannot be opened; giving up.");
                return (SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), null);
            }

            if (actions.ReadPid() is { } fresh && fresh != closingPid)
            {
                log.LogInfo($"Second launch: a new app (pid {fresh}) holds the lock; sending the request to it.");
                return (null, fresh);
            }

            if (waited >= LockWaitTimeout || actions.Time.GetElapsedTime(start) >= overallCap)
            {
                return (GaveUp(overallCap), null);
            }

            await actions.Delay(LockRetryInterval).ConfigureAwait(false);
        }
    }

    /// <summary>The wait's opening line: an app that is closing exits, while one that never answered may let a new app take the lock.</summary>
    private void LogWaitingForTheNextApp(bool closing)
    {
        string state = closing
            ? $"the app is closing; waiting up to {LockWaitTimeout.TotalSeconds:0} s for it to exit."
            : $"no app is answering; waiting up to {LockWaitTimeout.TotalSeconds:0} s for one to take the lock.";
        log.LogInfo($"Second launch: {state}");
    }

    private SecondLaunchResult GaveUp(TimeSpan overallCap)
    {
        log.LogWarning($"Second launch: no app answered within {overallCap.TotalSeconds:0} s or the lock wait; giving up.");
        return SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Throws unless <paramref name="value"/> is positive and at most <see cref="int.MaxValue"/> milliseconds, the longest delay a
    /// timer or a cancellation accepts; <see cref="Timeout.InfiniteTimeSpan"/> is negative, so it throws too.
    /// </summary>
    private static void RequirePositiveAndFinite(TimeSpan value, string name, string description)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{description} is positive, finite and at most {int.MaxValue} ms.");
        }
    }

    private static string Describe(OpenMessageProblem problem) =>
        problem switch
        {
            OpenMessageProblem.TooLarge => "too large for one message",
            OpenMessageProblem.NotFullPath => "a path is not a full path",
            OpenMessageProblem.NotWellFormed => "a path is not well-formed text",
            OpenMessageProblem.Empty => "no path was given",
            _ => problem.ToString(),
        };
}

/// <summary>The platform actions a <see cref="SecondLaunch"/> runs through.</summary>
/// <param name="ReadPid">Reads the running main instance's pid, or null when it is unknown.</param>
/// <param name="AllowForeground">Lets the process with this pid take the foreground.</param>
/// <param name="Request">
/// Sends this message to the running app and waits for its reply within the budget; the app passes
/// <see cref="InstanceCoordinator.RequestAsync(string, string, TimeSpan, IAppLog, CancellationToken)"/>.
/// </param>
/// <param name="BringForward">Restores and brings forward the top-level window of the process with this pid; true on success.</param>
/// <param name="RetryLock">
/// Tries once more to take the main-instance lock without logging a lock another process holds; reports what it found.
/// </param>
/// <param name="Delay">Waits the given time between lock retries; the app passes <see cref="Task.Delay(TimeSpan)"/>.</param>
/// <param name="Time">The clock a launch's overall cap is measured on; the app passes <see cref="TimeProvider.System"/>.</param>
public sealed record SecondLaunchActions(
    Func<int?> ReadPid,
    Action<int> AllowForeground,
    Func<string, TimeSpan, Task<InstanceRequestOutcome>> Request,
    Func<int, bool> BringForward,
    Func<MainLockAttempt> RetryLock,
    Func<TimeSpan, Task> Delay,
    TimeProvider Time
);

/// <summary>How a <see cref="SecondLaunch"/> ended, and why when it failed.</summary>
/// <param name="Outcome">Activated, Opened, BecameMain or Failed.</param>
/// <param name="Failure">Why it failed; null unless <paramref name="Outcome"/> is <see cref="SecondLaunchOutcome.Failed"/>.</param>
public sealed record SecondLaunchResult(SecondLaunchOutcome Outcome, SecondLaunchFailure? Failure)
{
    /// <summary>The app brought a window forward; exit 0.</summary>
    public static SecondLaunchResult Activated { get; } = new(SecondLaunchOutcome.Activated, null);

    /// <summary>The app took the files and is opening them; exit 0.</summary>
    public static SecondLaunchResult Opened { get; } = new(SecondLaunchOutcome.Opened, null);

    /// <summary>The app had exited and this process now holds the lock; start as the app, with the launch's files if any.</summary>
    public static SecondLaunchResult BecameMain { get; } = new(SecondLaunchOutcome.BecameMain, null);

    /// <summary>The launch failed, for <paramref name="failure"/>; exit 1.</summary>
    public static SecondLaunchResult Failed(SecondLaunchFailure failure) => new(SecondLaunchOutcome.Failed, failure);
}

/// <summary>Why a launch failed. Each shows the same error to the user; the reason is for the log and the caller.</summary>
public enum SecondLaunchFailure
{
    /// <summary>
    /// The paths cannot be sent: too large for one message, not full paths, not well-formed text, or none; nothing was sent.
    /// </summary>
    Unsendable,

    /// <summary>The app answered that it will not carry the request out.</summary>
    Refused,

    /// <summary>The app did not confirm in time, is running without answering, or no app answered before the wait ran out.</summary>
    NotResponding,

    /// <summary>The app's pipe belongs to another user or refused the connection for good.</summary>
    Denied,
}

/// <summary>How a <see cref="SecondLaunch"/> ended.</summary>
public enum SecondLaunchOutcome
{
    /// <summary>The app brought a window forward; exit 0.</summary>
    Activated,

    /// <summary>The app took the files of an open launch and is opening them; exit 0.</summary>
    Opened,

    /// <summary>The main instance had exited and this process now holds the lock; go on as the main instance.</summary>
    BecameMain,

    /// <summary>The launch did not get what it asked for; exit 1.</summary>
    Failed,
}
