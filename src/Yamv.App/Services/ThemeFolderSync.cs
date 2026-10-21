using Avalonia.Threading;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;

namespace Yamv.App.Services;

/// <summary>
/// Watches the user themes folder and has the <see cref="ThemeBridge"/> reload the themes on the UI thread after each change, so
/// every window shows the theme in use as it is on disk.
/// </summary>
/// <remarks>
/// The watcher reports a change on a thread-pool thread; the sync only posts the reload to the UI thread and never waits on it,
/// since <see cref="Dispose"/> runs on the UI thread and waits for a report already running. A reload posted before
/// <see cref="Dispose"/> that runs after it does nothing, and a reload that fails is logged.
/// </remarks>
public sealed class ThemeFolderSync : IDisposable
{
    private readonly Action _reload;
    private readonly IAppLog _log;
    private readonly IDisposable _watcher;
    private int _disposed;

    /// <summary>Starts watching <paramref name="folder"/> for <paramref name="bridge"/>.</summary>
    /// <param name="bridge">The bridge whose <see cref="ThemeBridge.Reload"/> runs after each change.</param>
    /// <param name="folder">The user themes folder, which need not exist yet.</param>
    /// <param name="timeProvider">The watcher's clock; the app passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="log">Where a failed reload and a failure to stop watching are reported.</param>
    public ThemeFolderSync(ThemeBridge bridge, string folder, TimeProvider timeProvider, IAppLog log)
        : this(ReloadOf(bridge), log, onChanged => Watch(folder, timeProvider, log, onChanged)) => Folder = folder;

    /// <summary>Starts the change signal <paramref name="watch"/> returns, which calls the action it is given on any thread.</summary>
    /// <param name="reload">What runs on the UI thread after each change.</param>
    /// <param name="log">Where a failed reload and a failure to stop watching are reported.</param>
    /// <param name="watch">Starts the change signal and returns what stops it.</param>
    internal ThemeFolderSync(Action reload, IAppLog log, Func<Action, IDisposable> watch)
    {
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(watch);
        _reload = reload;
        _log = log;
        _watcher = watch(OnChanged);
    }

    /// <summary>The folder the watcher watches; null when the sync was given its change signal instead.</summary>
    internal string? Folder { get; }

    /// <summary>Stops watching; a reload already posted does nothing. A second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _watcher.Dispose();
        }
        // A watcher that fails to stop must not stop the app's exit from disposing the rest.
        catch (Exception exception)
        {
            _log.LogError("Cannot stop watching the themes folder.", exception);
        }
    }

    private static Action ReloadOf(ThemeBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return bridge.Reload;
    }

    private static ThemeFolderWatcher Watch(string folder, TimeProvider timeProvider, IAppLog log, Action onChanged)
    {
        var watcher = new ThemeFolderWatcher(folder, timeProvider, log);
        watcher.Changed += (_, _) => onChanged();
        return watcher;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(ReloadOnUiThread);

    private void ReloadOnUiThread()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _reload();
        }
        // Posted work that throws would take the UI thread down; a bad theme folder must never do that.
        catch (Exception exception)
        {
            _log.LogError("Cannot reload the themes after a change in the themes folder.", exception);
        }
    }
}
