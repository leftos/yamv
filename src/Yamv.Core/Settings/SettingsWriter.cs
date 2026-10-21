using Yamv.Core.Diagnostics;

namespace Yamv.Core.Settings;

/// <summary>
/// Saves the theme picks to a <see cref="SettingsStore"/> off the caller's thread. Safe to call from any thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Request"/> records the latest theme id and Appearance and returns at once, with no file IO. One background
/// write at a time saves the latest pair through <see cref="SettingsStore.SetThemeId"/> then
/// <see cref="SettingsStore.SetAppearance"/>; requests that arrive while a write runs coalesce into one more write of the
/// latest pair, so a burst of picks saves the last one.
/// </para>
/// <para>
/// A write that throws is logged as a warning and the next request writes again. <see cref="Dispose"/> stops taking requests
/// and waits, for at most 2 s, until the pairs requested before it are written.
/// </para>
/// </remarks>
public sealed class SettingsWriter : IDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly Lock gate = new();
    private readonly SettingsStore store;
    private readonly IAppLog log;
    private readonly TaskScheduler scheduler;
    private readonly TimeSpan disposeWait;
    private AppSettings? pending;
    private TaskCompletionSource? running;
    private bool disposed;

    /// <summary>Creates a writer that saves to <paramref name="store"/> on the thread pool.</summary>
    /// <param name="store">The store the picks are saved to.</param>
    /// <param name="log">Where a failed write is reported.</param>
    public SettingsWriter(SettingsStore store, IAppLog log)
        : this(store, log, TaskScheduler.Default, DisposeWait) { }

    /// <summary>Creates a writer whose writes run on <paramref name="scheduler"/>, so tests decide when and where they run.</summary>
    /// <param name="store">The store the picks are saved to.</param>
    /// <param name="log">Where a failed write is reported.</param>
    /// <param name="scheduler">Runs each write.</param>
    /// <param name="disposeWait">How long <see cref="Dispose"/> waits for the writes in progress.</param>
    internal SettingsWriter(SettingsStore store, IAppLog log, TaskScheduler scheduler, TimeSpan disposeWait)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scheduler);
        this.store = store;
        this.log = log;
        this.scheduler = scheduler;
        this.disposeWait = disposeWait;
    }

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>
    /// Saves <paramref name="themeId"/> and <paramref name="appearance"/> in the background, replacing a pair requested before
    /// that is not written yet; ignored after <see cref="Dispose"/>. Never blocks and never touches the file.
    /// </summary>
    /// <param name="themeId">The theme id to save.</param>
    /// <param name="appearance"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
    public void Request(string themeId, string appearance)
    {
        ArgumentNullException.ThrowIfNull(themeId);
        ArgumentNullException.ThrowIfNull(appearance);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            pending = new AppSettings(themeId, appearance);
            if (running is not null)
            {
                return;
            }

            running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScheduleWrite();
        }
    }

    /// <summary>A task that completes once every pair requested so far is written or failed; already complete when nothing is pending.</summary>
    public Task WhenIdle()
    {
        lock (gate)
        {
            return running?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>Stops taking requests and waits, for at most 2 s, until the pairs requested before are written.</summary>
    public void Dispose()
    {
        Task idle;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            idle = running?.Task ?? Task.CompletedTask;
        }

        if (!idle.Wait(disposeWait))
        {
            log.LogWarning($"Stopped waiting for the theme to be saved to {store.FilePath} after {disposeWait.TotalSeconds:0.###} s.");
        }
    }

    private void ScheduleWrite() => _ = Task.Factory.StartNew(WriteLatest, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);

    /// <summary>Writes the latest pair, then schedules one more write when a request came in meanwhile, else marks the writer idle.</summary>
    private void WriteLatest()
    {
        AppSettings settings;
        lock (gate)
        {
            settings = pending!;
            pending = null;
        }

        try
        {
            Write(settings);
        }
        finally
        {
            ScheduleNextOrFinish();
        }
    }

    /// <summary>Schedules one more write when a request came in during the last one, else marks the writer idle.</summary>
    private void ScheduleNextOrFinish()
    {
        TaskCompletionSource? finished = null;
        lock (gate)
        {
            if (pending is null)
            {
                finished = running;
                running = null;
            }
            else
            {
                ScheduleWrite();
            }
        }

        finished?.SetResult();
    }

    private void Write(AppSettings settings)
    {
        try
        {
            store.SetThemeId(settings.ThemeId);
            store.SetAppearance(settings.Appearance);
        }
        catch (Exception exception)
        {
            log.LogWarning($"Cannot save the theme '{settings.ThemeId}' with the appearance '{settings.Appearance}' to {store.FilePath}.", exception);
        }
    }
}
