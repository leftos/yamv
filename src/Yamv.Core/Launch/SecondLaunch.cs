using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Launch;

/// <summary>
/// Runs a no-arg launch that found the main-instance lock taken: it lets the main instance take the foreground, asks it to
/// come forward, and when it does not answer brings its window forward directly or, when the main instance has gone, takes
/// the lock itself.
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
}

/// <summary>The platform actions a <see cref="SecondLaunch"/> runs through.</summary>
/// <param name="ReadPid">Reads the running main instance's pid, or null when it is unknown.</param>
/// <param name="AllowForeground">Lets the process with this pid take the foreground.</param>
/// <param name="Signal">Sends <c>activate</c> to the main instance within the timeout; true once it was delivered.</param>
/// <param name="BringForward">Restores and brings forward the top-level window of the process with this pid; true on success.</param>
/// <param name="RetryLock">
/// Tries once more to take the main-instance lock without logging a lock another process holds; reports what it found.
/// </param>
/// <param name="Delay">Waits the given time between lock retries; the app passes <see cref="Task.Delay(TimeSpan)"/>.</param>
public sealed record SecondLaunchActions(
    Func<int?> ReadPid,
    Action<int> AllowForeground,
    Func<TimeSpan, Task<bool>> Signal,
    Func<int, bool> BringForward,
    Func<MainLockAttempt> RetryLock,
    Func<TimeSpan, Task> Delay
);

/// <summary>How a <see cref="SecondLaunch"/> ended.</summary>
public enum SecondLaunchOutcome
{
    /// <summary>The main instance got the <c>activate</c> message; exit 0.</summary>
    Activated,

    /// <summary>The main instance had exited and this process now holds the lock; go on as the main instance.</summary>
    BecameMain,

    /// <summary>The main instance did not answer; exit 1.</summary>
    Failed,
}
