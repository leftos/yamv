using System.Text.RegularExpressions;
using Yamv.Core.Diagnostics;
using Yamv.Core.Session;

namespace Yamv.Core.Theming;

/// <summary>
/// Watches the user themes folder and raises <see cref="Changed"/> once its entries have been quiet for 300 ms after a change,
/// so the subscriber can re-run <see cref="ThemeCatalog.LoadAll"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Changed"/> is raised on a thread-pool thread; a subscriber posts its work to its UI thread and never waits on it
/// there, since <see cref="Dispose"/> waits for a <see cref="Changed"/> already running, and a check that starts after it
/// returns probes and logs nothing. A subscriber that throws is logged.
/// </para>
/// <para>
/// Every 2 s the watcher checks that the folder exists and is still the folder it watches: a missing folder is watched once it
/// exists, and a folder deleted, recreated or replaced is watched again. The folder appearing, disappearing or being replaced
/// raises <see cref="Changed"/>, as does every time watching starts, the first included. A watcher error raises
/// <see cref="Changed"/> and the folder is watched again on the next check. Events that name only the catalog's temporary files
/// raise nothing.
/// </para>
/// </remarks>
public sealed partial class ThemeFolderWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly Lock gate = new();
    private readonly string folder;
    private readonly IAppLog log;
    private readonly Func<string, IFolderEvents> arm;
    private readonly IFolderProbe probe;
    private readonly Debouncer debouncer;

    // Guarded by gate.
    private readonly ITimer? tickTimer;
    private IFolderEvents? current;
    private string? armedIdentity;
    private bool reArmRequested;
    private bool folderPresent;
    private bool disposed;
    private bool tickedBeforeTimerSet;
    private bool watcherFailing;

    // Touched only by the tick, and ticks never overlap.
    private bool folderMissingBefore;
    private bool missingLogged;
    private bool armFailing;
    private bool tickFailing;

    /// <summary>Starts watching <paramref name="folder"/>, which need not exist yet.</summary>
    /// <param name="folder">The user themes folder.</param>
    /// <param name="timeProvider">The clock for the debounce and the folder checks; the app passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="log">Where failures to watch the folder are reported.</param>
    public ThemeFolderWatcher(string folder, TimeProvider timeProvider, IAppLog log)
        : this(folder, timeProvider, log, path => new FileSystemFolderEvents(path), new FileSystemFolderProbe()) { }

    internal ThemeFolderWatcher(string folder, TimeProvider timeProvider, IAppLog log, Func<string, IFolderEvents> arm, IFolderProbe probe)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(probe);
        this.folder = folder;
        this.log = log;
        this.arm = arm;
        this.probe = probe;
        debouncer = new Debouncer(DebounceDelay, timeProvider, RaiseChanged, OnSubscriberError);

        // Last: a timer due at once may tick inside CreateTimer, before the field is set.
        ITimer timer = timeProvider.CreateTimer(OnTick, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        lock (gate)
        {
            tickTimer = timer;
            if (tickedBeforeTimerSet && !disposed)
            {
                timer.Change(TickInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Raised on a thread-pool thread once the folder has changed and been quiet for 300 ms.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Stops watching and waits for a <see cref="Changed"/> already running; nothing is raised after it returns, and a check
    /// that starts after it returns probes nothing and logs nothing.
    /// </summary>
    public void Dispose()
    {
        IFolderEvents? stale;
        ITimer? timer;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stale = current;
            current = null;
            timer = tickTimer;
        }

        // Outside the lock: the debouncer's Dispose waits for a running Changed, which must not wait on this lock.
        timer?.Dispose();
        stale?.Dispose();
        debouncer.Dispose();
    }

    /// <summary>Whether <paramref name="name"/> is one of <see cref="ThemeCatalog"/>'s temporary files, a write in progress.</summary>
    private static bool IsTemporary(string? name) => name is not null && TemporaryName().IsMatch(name);

    private static bool NamesOnlyTemporaries(FolderEvent folderEvent) =>
        folderEvent.Kind switch
        {
            FolderEventKind.Error => false,
            FolderEventKind.Renamed => IsTemporary(folderEvent.Name) && IsTemporary(folderEvent.OldName),
            _ => IsTemporary(folderEvent.Name),
        };

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void OnSubscriberError(Exception exception) => log.LogError($"A subscriber to the themes folder watcher of {folder} failed.", exception);

    private void OnRaised(IFolderEvents sender, FolderEvent folderEvent)
    {
        try
        {
            if (NamesOnlyTemporaries(folderEvent))
            {
                return;
            }

            bool firstError = false;
            lock (gate)
            {
                if (disposed || sender != current)
                {
                    return;
                }

                if (folderEvent.Kind == FolderEventKind.Error)
                {
                    reArmRequested = true;
                    firstError = !watcherFailing;
                    watcherFailing = true;
                }

                debouncer.Schedule();
            }

            if (firstError)
            {
                log.LogWarning($"The themes folder watcher of {folder} failed; re-arming.", folderEvent.Error);
            }
        }
        catch (Exception exception)
        {
            log.LogError($"A handler of a change in the themes folder {folder} failed.", exception);
        }
    }

    private void OnTick(object? state)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
        }

        try
        {
            Tick();
            if (tickFailing)
            {
                tickFailing = false;
                log.LogInfo($"Checking the themes folder {folder} works again.");
            }
        }
        catch (Exception exception)
        {
            if (!tickFailing)
            {
                tickFailing = true;
                log.LogError($"Cannot check the themes folder {folder}; retrying every 2 s.", exception);
            }
        }
        finally
        {
            ScheduleNextTick();
        }
    }

    private void ScheduleNextTick()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (tickTimer is null)
            {
                tickedBeforeTimerSet = true;
                return;
            }

            tickTimer.Change(TickInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        FolderState state = probe.Probe(folder);
        if (!state.Exists)
        {
            OnFolderMissing();
            return;
        }

        bool returning = folderMissingBefore;
        folderMissingBefore = false;
        IFolderEvents? stale;
        bool replacing;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            missingLogged = false;
            folderPresent = true;
            if (current is not null && armedIdentity == state.Identity && !reArmRequested)
            {
                return;
            }

            stale = current;
            replacing = stale is not null && armedIdentity != state.Identity;
            current = null;
            armedIdentity = null;
            reArmRequested = false;
        }

        stale?.Dispose();
        Arm(state.Identity, replacing || returning);
    }

    private void OnFolderMissing()
    {
        IFolderEvents? stale;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            stale = current;
            current = null;
            armedIdentity = null;
            reArmRequested = false;
            if (folderPresent)
            {
                folderPresent = false;
                debouncer.Schedule();
            }
        }

        stale?.Dispose();
        folderMissingBefore = true;
        if (!missingLogged)
        {
            missingLogged = true;
            log.LogInfo($"The themes folder {folder} does not exist; it is watched once it does.");
        }
    }

    private static bool IsArmFailure(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException;

    /// <summary>Starts watching the folder, which now has <paramref name="identity"/>; on failure, logs and leaves it to the next tick.</summary>
    /// <remarks>
    /// The source becomes <see cref="current"/> before it starts, so an Error it raises while starting takes the normal Error path.
    /// </remarks>
    /// <param name="identity">The folder's identity from this tick's probe.</param>
    /// <param name="changed">Whether the folder returned or replaced the one watched until now, a change even if watching fails.</param>
    private void Arm(string? identity, bool changed)
    {
        IFolderEvents next;
        try
        {
            next = arm(folder);
        }
        catch (Exception exception) when (IsArmFailure(exception))
        {
            OnArmFailed(exception, changed);
            return;
        }

        next.Raised += folderEvent => OnRaised(next, folderEvent);
        bool kept;
        lock (gate)
        {
            kept = !disposed;
            if (kept)
            {
                current = next;
                armedIdentity = identity;
            }
        }

        if (!kept)
        {
            next.Dispose();
            return;
        }

        Start(next, changed);
    }

    private void Start(IFolderEvents next, bool changed)
    {
        try
        {
            next.Start();
        }
        catch (ObjectDisposedException) when (IsGone(next))
        {
            return;
        }
        catch (Exception exception) when (IsArmFailure(exception))
        {
            lock (gate)
            {
                if (current == next)
                {
                    current = null;
                    armedIdentity = null;
                }
            }

            next.Dispose();
            OnArmFailed(exception, changed);
            return;
        }

        bool recovered;
        lock (gate)
        {
            if (disposed || current != next)
            {
                return;
            }

            debouncer.Schedule();
            recovered = armFailing || (watcherFailing && !reArmRequested);
            watcherFailing = watcherFailing && reArmRequested;
        }

        armFailing = false;
        if (recovered)
        {
            log.LogInfo($"Watching the themes folder {folder} again.");
        }
    }

    /// <summary>Whether the watcher is disposed, or has already moved on from <paramref name="source"/>.</summary>
    private bool IsGone(IFolderEvents source)
    {
        lock (gate)
        {
            return disposed || !ReferenceEquals(current, source);
        }
    }

    private void OnArmFailed(Exception exception, bool changed)
    {
        if (!armFailing)
        {
            armFailing = true;
            log.LogWarning($"Cannot watch the themes folder {folder}; retrying every 2 s.", exception);
        }

        if (changed)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    debouncer.Schedule();
                }
            }
        }
    }

    // A copy of ThemeCatalog's pattern for the temporary files its writes go through.
    [GeneratedRegex(@"^\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryName();
}
