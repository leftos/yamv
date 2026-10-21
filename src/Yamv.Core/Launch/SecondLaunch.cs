using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Launch;

/// <summary>
/// Runs a launch that found the main-instance lock taken. A no-arg launch (<see cref="RunAsync"/>) lets the main instance take
/// the foreground, asks it to come forward, and when it does not answer brings its window forward directly or, when the main
/// instance has gone, takes the lock itself. A launch with files (<see cref="OpenAsync"/>) asks the app to open them and waits
/// for its confirmation.
/// </summary>
/// <param name="actions">The platform actions the launch runs through.</param>
/// <param name="log">The process's log.</param>
public sealed class SecondLaunch(SecondLaunchActions actions, IAppLog log)
{
    /// <summary>How long the launch waits for the main instance to read its <c>activate</c> message.</summary>
    public static readonly TimeSpan SignalTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a launch that found no pid after a failed signal keeps retrying the lock, so a main instance that is still saving
    /// its session hands over to it once the lock is free.
    /// </summary>
    public static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The pause between lock retries within <see cref="LockWaitTimeout"/>.</summary>
    public static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long one open request waits, from its first connect attempt, for the app to confirm it. It stays below the server's
    /// 10 s reply bound: past that bound a request the app has begun but not answered is closed with no reply, which the
    /// launcher reads as closing and may send the files again.
    /// </summary>
    public static readonly TimeSpan OpenDeadline = TimeSpan.FromSeconds(3);

    /// <summary>The bound on a whole open launch: every request, lock wait and resend included.</summary>
    public static readonly TimeSpan OpenOverallCap = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Reads the main instance's pid and, when known, allows it the foreground before signalling <c>activate</c>. A signal
    /// that fails reads the pid again and falls back to bringing that process's window forward, or, with no pid, to retrying the
    /// lock for up to <see cref="LockWaitTimeout"/>.
    /// </summary>
    /// <returns>What the caller does next: exit 0, start as the main instance, or exit 1.</returns>
    public async Task<SecondLaunchOutcome> RunAsync()
    {
        int? pid = actions.ReadPid();
        if (pid is { } known)
        {
            actions.AllowForeground(known);
        }

        bool delivered = await actions.Signal(SignalTimeout).ConfigureAwait(false);
        if (delivered)
        {
            return Activated(pid);
        }

        // A fresh read re-checks the process's start time, so a pid reused while the signal waited is never brought forward.
        return actions.ReadPid() is { } fresh ? BringForward(fresh) : await WaitForLockAsync().ConfigureAwait(false);
    }

    private SecondLaunchOutcome Activated(int? pid)
    {
        if (pid is null)
        {
            pid = actions.ReadPid();
            if (pid is { } known)
            {
                actions.AllowForeground(known);
            }
        }

        string shown = pid is { } value ? $"pid {value}" : "pid unknown";
        log.LogInfo($"Second launch: asked the main instance ({shown}) to come forward.");
        return SecondLaunchOutcome.Activated;
    }

    private SecondLaunchOutcome BringForward(int pid)
    {
        if (actions.BringForward(pid))
        {
            log.LogWarning($"Second launch: the main instance (pid {pid}) did not answer; brought its window forward.");
        }
        else
        {
            log.LogWarning($"Second launch: the main instance (pid {pid}) did not answer and its window could not be brought forward.");
        }

        return SecondLaunchOutcome.Failed;
    }

    /// <summary>
    /// Retries the lock every <see cref="LockRetryInterval"/> until it is taken or <see cref="LockWaitTimeout"/> has passed: a
    /// main instance that stopped answering keeps the lock until it has saved its session. A lock file that cannot be opened
    /// ends the wait at once, since waiting cannot fix it. Logs one line when the wait begins, and the outcome.
    /// </summary>
    private async Task<SecondLaunchOutcome> WaitForLockAsync()
    {
        for (TimeSpan waited = TimeSpan.Zero; ; waited += LockRetryInterval)
        {
            MainLockAttempt attempt = actions.RetryLock();
            if (attempt == MainLockAttempt.Acquired)
            {
                log.LogInfo("Second launch: the main instance has exited; starting as the main instance.");
                return SecondLaunchOutcome.BecameMain;
            }

            if (attempt == MainLockAttempt.CannotOpen || waited >= LockWaitTimeout)
            {
                break;
            }

            if (waited == TimeSpan.Zero)
            {
                log.LogInfo($"Second launch: waiting up to {LockWaitTimeout.TotalSeconds:0} s for the main instance to exit.");
            }

            await actions.Delay(LockRetryInterval).ConfigureAwait(false);
        }

        log.LogWarning("Second launch: the main instance did not answer and its pid is unknown; giving up.");
        return SecondLaunchOutcome.Failed;
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
    /// The budget of one request; the app passes <see cref="OpenDeadline"/>. Keep it below the server's 10 s reply bound, or a
    /// request the app has begun may close with no reply and the files be sent twice.
    /// </param>
    /// <param name="overallCap">The bound on the whole launch; the app passes <see cref="OpenOverallCap"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either time is not positive and finite.</exception>
    /// <returns>Opened (exit 0), BecameMain (start as the app with the files), or Failed with its reason (show the error, exit 1).</returns>
    public async Task<SecondLaunchResult> OpenAsync(IReadOnlyList<string> files, TimeSpan openDeadline, TimeSpan overallCap)
    {
        RequirePositiveAndFinite(openDeadline, nameof(openDeadline));
        RequirePositiveAndFinite(overallCap, nameof(overallCap));
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

            (SecondLaunchResult? ended, int? newPid) = await WaitForLockOrNewAppAsync(pid, start, overallCap).ConfigureAwait(false);
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
    /// is not <paramref name="closingPid"/>) to send the request to.
    /// </summary>
    private async Task<(SecondLaunchResult? Ended, int? NewPid)> WaitForLockOrNewAppAsync(int? closingPid, long start, TimeSpan overallCap)
    {
        log.LogInfo($"Second launch: the app is closing; waiting up to {LockWaitTimeout.TotalSeconds:0} s for it to exit.");
        for (TimeSpan waited = TimeSpan.Zero; ; waited += LockRetryInterval)
        {
            MainLockAttempt attempt = actions.RetryLock();
            if (attempt == MainLockAttempt.Acquired)
            {
                log.LogInfo("Second launch: the app has exited; starting as the app with the files.");
                return (SecondLaunchResult.BecameMain, null);
            }

            if (attempt == MainLockAttempt.CannotOpen)
            {
                log.LogWarning("Second launch: the lock file cannot be opened; the files were not opened.");
                return (SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding), null);
            }

            if (actions.ReadPid() is { } fresh && fresh != closingPid)
            {
                log.LogInfo($"Second launch: a new app (pid {fresh}) holds the lock; sending the files to it.");
                return (null, fresh);
            }

            if (waited >= LockWaitTimeout || actions.Time.GetElapsedTime(start) >= overallCap)
            {
                return (GaveUp(overallCap), null);
            }

            await actions.Delay(LockRetryInterval).ConfigureAwait(false);
        }
    }

    private SecondLaunchResult GaveUp(TimeSpan overallCap)
    {
        log.LogWarning($"Second launch: no app took the files within {overallCap.TotalSeconds:0} s or the lock wait; giving up.");
        return SecondLaunchResult.Failed(SecondLaunchFailure.NotResponding);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Throws unless <paramref name="value"/> is positive; <see cref="Timeout.InfiniteTimeSpan"/> is negative, so it throws too.</summary>
    private static void RequirePositiveAndFinite(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "An open launch's deadline and overall cap are positive and finite.");
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
/// <param name="Signal">Sends <c>activate</c> to the main instance within the timeout; true once it was delivered.</param>
/// <param name="Request">
/// Sends this message to the running app and waits for its reply within the budget; the app passes
/// <see cref="InstanceCoordinator.RequestAsync(string, string, TimeSpan, IAppLog, CancellationToken)"/>.
/// </param>
/// <param name="BringForward">Restores and brings forward the top-level window of the process with this pid; true on success.</param>
/// <param name="RetryLock">
/// Tries once more to take the main-instance lock without logging a lock another process holds; reports what it found.
/// </param>
/// <param name="Delay">Waits the given time between lock retries; the app passes <see cref="Task.Delay(TimeSpan)"/>.</param>
/// <param name="Time">The clock the open launch's overall cap is measured on; the app passes <see cref="TimeProvider.System"/>.</param>
public sealed record SecondLaunchActions(
    Func<int?> ReadPid,
    Action<int> AllowForeground,
    Func<TimeSpan, Task<bool>> Signal,
    Func<string, TimeSpan, Task<InstanceRequestOutcome>> Request,
    Func<int, bool> BringForward,
    Func<MainLockAttempt> RetryLock,
    Func<TimeSpan, Task> Delay,
    TimeProvider Time
);

/// <summary>How an open launch (<see cref="SecondLaunch.OpenAsync"/>) ended, and why when it failed.</summary>
/// <param name="Outcome">Opened, BecameMain or Failed.</param>
/// <param name="Failure">Why it failed; null unless <paramref name="Outcome"/> is <see cref="SecondLaunchOutcome.Failed"/>.</param>
public sealed record SecondLaunchResult(SecondLaunchOutcome Outcome, SecondLaunchFailure? Failure)
{
    /// <summary>The app took the files and is opening them; exit 0.</summary>
    public static SecondLaunchResult Opened { get; } = new(SecondLaunchOutcome.Opened, null);

    /// <summary>The app had exited and this process now holds the lock; start as the app with the files.</summary>
    public static SecondLaunchResult BecameMain { get; } = new(SecondLaunchOutcome.BecameMain, null);

    /// <summary>The files were not opened, for <paramref name="failure"/>; show the error and exit 1.</summary>
    public static SecondLaunchResult Failed(SecondLaunchFailure failure) => new(SecondLaunchOutcome.Failed, failure);
}

/// <summary>Why an open launch failed. Each shows the same error to the user; the reason is for the log and the caller.</summary>
public enum SecondLaunchFailure
{
    /// <summary>
    /// The paths cannot be sent: too large for one message, not full paths, not well-formed text, or none; nothing was sent.
    /// </summary>
    Unsendable,

    /// <summary>The app answered that it will not open them.</summary>
    Refused,

    /// <summary>The app did not confirm in time, is running without answering, or no app took the files before the wait ran out.</summary>
    NotResponding,

    /// <summary>The app's pipe belongs to another user or refused the connection for good.</summary>
    Denied,
}

/// <summary>How a <see cref="SecondLaunch"/> ended.</summary>
public enum SecondLaunchOutcome
{
    /// <summary>The main instance got the <c>activate</c> message; exit 0.</summary>
    Activated,

    /// <summary>The app took the files of an open launch and is opening them; exit 0.</summary>
    Opened,

    /// <summary>The main instance had exited and this process now holds the lock; go on as the main instance.</summary>
    BecameMain,

    /// <summary>The main instance did not answer; exit 1.</summary>
    Failed,
}
