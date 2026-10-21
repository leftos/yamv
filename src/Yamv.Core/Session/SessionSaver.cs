using Yamv.Core.Diagnostics;

namespace Yamv.Core.Session;

/// <summary>
/// Saves the session 500 ms after the last change, or at once on <see cref="Flush"/>. Every save runs through one
/// <see cref="Debouncer"/>, so two saves never run at once. A save that fails is logged and retried by the next save.
/// </summary>
/// <remarks>
/// The states given are values the caller built on its own thread; the saver never calls back into the window. A scheduled
/// save runs on the timer's thread, a <see cref="Flush"/> on the calling thread.
/// </remarks>
public sealed class SessionSaver : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly Lock gate = new();
    private readonly SessionStore store;
    private readonly IAppLog log;
    private readonly Debouncer debouncer;
    private SessionState? latest;
    private long latestVersion;
    private long changeVersion;
    private long savedVersion;
    private bool disposed;

    /// <summary>Creates a saver that writes through <paramref name="store"/> on <paramref name="time"/>'s clock.</summary>
    /// <param name="store">Where the session is written.</param>
    /// <param name="time">The clock and timer source; the app passes <see cref="TimeProvider.System"/>, a test a fake.</param>
    /// <param name="log">Where a failed save is reported.</param>
    public SessionSaver(SessionStore store, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        this.store = store;
        this.log = log;
        debouncer = new Debouncer(Delay, time, SaveLatest, OnSaveFailed);
    }

    /// <summary>Keeps <paramref name="state"/> as the one to save and starts the 500 ms delay again.</summary>
    public void Schedule(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            latest = state;
            latestVersion = ++changeVersion;
        }

        debouncer.Schedule();
    }

    /// <summary>Records that something the next save must include changed (a scroll), without starting the delay.</summary>
    public void MarkDirty()
    {
        lock (gate)
        {
            changeVersion++;
        }
    }

    /// <summary>
    /// Saves <paramref name="state"/> now, on this thread, when a save is pending or something changed since the last save,
    /// and leaves nothing pending; otherwise does nothing.
    /// </summary>
    public void Flush(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            if (disposed || changeVersion == savedVersion)
            {
                return;
            }

            latest = state;
            latestVersion = changeVersion;
        }

        debouncer.Schedule();
        debouncer.Flush();
    }

    /// <summary>Cancels a pending save and waits for one already running to finish.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }

        debouncer.Dispose();
    }

    private void SaveLatest()
    {
        SessionState state;
        long version;
        lock (gate)
        {
            if (latest is null)
            {
                return;
            }

            state = latest;
            version = latestVersion;
        }

        store.Save(state);
        lock (gate)
        {
            savedVersion = Math.Max(savedVersion, version);
        }
    }

    private void OnSaveFailed(Exception exception) => log.LogError($"Session: could not save {store.FilePath}.", exception);
}
