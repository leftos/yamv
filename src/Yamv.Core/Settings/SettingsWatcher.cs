using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;

namespace Yamv.Core.Settings;

/// <summary>
/// Watches <c>settings.json</c> and raises <see cref="Changed"/> with the settings the file holds after each change, made by
/// hand, by another YAMV process or by this one's own <see cref="SettingsWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Changed"/> is raised on a thread-pool thread. A subscriber posts its work to its UI thread and never waits on it
/// there, since <see cref="Dispose"/> waits for a <see cref="Changed"/> already running. A subscriber that throws, or a read of
/// the file that throws, is logged.
/// </para>
/// <para>
/// Watching starts in the background after the constructor returns, so a change made between the owner's own
/// <see cref="SettingsStore.Load"/> before creating this watcher and the first watch start is not reported.
/// </para>
/// <para>
/// A change seen while the writer is saving a pick is not read at once: the watcher waits, without holding a thread, for the
/// writer to go idle, then reads the file as it then stands and reports it once, however many changes came in meanwhile. A file
/// that is missing, locked or does not parse reports the settings read before (<see cref="SettingsStore.Load"/>). Every change is
/// reported, even one that leaves the values as they were: telling a change apart from what is already applied is the
/// subscriber's business.
/// </para>
/// </remarks>
public sealed class SettingsWatcher : IDisposable
{
    private readonly Lock gate = new();
    private readonly Lock runGate = new();
    private readonly SettingsStore store;
    private readonly SettingsWriter writer;
    private readonly IAppLog log;
    private readonly DocumentFileWatcher watcher;

    // Guarded by gate.
    private Task waiting = Task.CompletedTask;
    private bool waitingForWriter;
    private bool disposed;

    /// <summary>Starts watching <paramref name="store"/>'s file, whose folder need not exist yet.</summary>
    /// <param name="store">The store whose file is watched and read.</param>
    /// <param name="writer">The writer whose saves a change waits for.</param>
    /// <param name="timeProvider">The watcher's clock; the app passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="log">Where a failing subscriber and a folder that cannot be watched are reported.</param>
    public SettingsWatcher(SettingsStore store, SettingsWriter writer, TimeProvider timeProvider, IAppLog log)
        : this(store, writer, log, new DocumentFileWatcher(timeProvider, log)) { }

    /// <summary>Starts watching <paramref name="store"/>'s file through <paramref name="watcher"/>, which this watcher owns.</summary>
    internal SettingsWatcher(SettingsStore store, SettingsWriter writer, IAppLog log, DocumentFileWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(watcher);
        this.store = store;
        this.writer = writer;
        this.log = log;
        this.watcher = watcher;
        watcher.Changed += OnFileChanged;
        watcher.SetPaths([store.FilePath]);
    }

    /// <summary>Raised on a thread-pool thread with the settings the file holds after a change.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>The wait for the writer a change is held on; complete when none is.</summary>
    internal Task Waiting
    {
        get
        {
            lock (gate)
            {
                return waiting;
            }
        }
    }

    /// <summary>Stops watching and waits for a <see cref="Changed"/> already running; nothing is raised after it returns.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        watcher.Changed -= OnFileChanged;
        watcher.Dispose();
        lock (runGate)
        {
            // Wait for a Changed already running; nothing is raised after this.
        }
    }

    private void OnFileChanged(object? sender, DocumentsChangedEventArgs e) => ReportWhenIdle();

    /// <summary>Reports the file now when the writer is idle, else once it is, holding one wait however many changes come.</summary>
    private void ReportWhenIdle()
    {
        Task idle = writer.WhenIdle();
        if (idle.IsCompleted)
        {
            Report();
            return;
        }

        lock (gate)
        {
            if (disposed || waitingForWriter)
            {
                return;
            }

            waitingForWriter = true;
            waiting = idle.ContinueWith(_ => OnWriterIdle(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void OnWriterIdle()
    {
        lock (gate)
        {
            waitingForWriter = false;
        }

        ReportWhenIdle();
    }

    private void Report()
    {
        lock (runGate)
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
                AppSettings settings = store.Load();
                Changed?.Invoke(this, settings);
            }
            catch (Exception exception)
            {
                log.LogError("Cannot read or report the settings file after a change.", exception);
            }
        }
    }
}
