using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Yamv.App.Services;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Launch;
using Yamv.Core.Settings;
using Yamv.Core.Theming;
using Yamv.Core.Widths;

namespace Yamv.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "An Avalonia Application is never disposed; ReleaseMainWindow disposes what the app owns when it exits."
)]
public partial class App : Application
{
    /// <summary>The environment variable that, set to <c>1</c>, starts the dev harness instead of the main window.</summary>
    public const string HarnessVariable = "YAMV_DEV_HARNESS";

    /// <summary>
    /// The environment variable that, set to <c>1</c> beside <see cref="HarnessVariable"/>, starts the theme settings window
    /// instead of the dev harness.
    /// </summary>
    public const string ThemeSettingsHarnessVariable = "YAMV_DEV_THEME_SETTINGS";

    /// <summary>
    /// The environment variable that, beside <see cref="ThemeSettingsHarnessVariable"/>, names a theme file whose Import notice the
    /// harness opens once the window has shown; empty or white space counts as unset.
    /// </summary>
    public const string ImportFileVariable = "YAMV_DEV_IMPORT_FILE";

    /// <summary>
    /// The environment variable that, set to <c>1</c>, has the main or viewer window open its content-width popover once it has
    /// shown its first document, so a hidden-desktop capture shows the popover; <c>clearall</c> then also asks the Clear all
    /// question, and <c>folder</c> selects the document's own folder segment.
    /// </summary>
    public const string WidthPopoverVariable = "YAMV_DEV_WIDTH_POPOVER";

    private MainWindowComposition? _mainWindow;
    private ThemeBridge? _bridge;
    private ThemeBridge? _harnessBridge;
    private SettingsWriter? _settingsWriter;
    private SettingsWatcher? _settingsWatcher;
    private IDisposable? _themesSync;
    private ThemeSettingsHost? _themeSettingsHost;

    /// <summary>The shutdown check a held shutdown waits on, or null; a request while it runs starts no second one.</summary>
    private Task? _shutdownCheck;
    private IAppLog? _log;
    private ActivationGate? _connectedGate;

    /// <summary>Unhooks the main window's <c>Closed</c> handler and runs its start's stop action if it has not run yet.</summary>
    private Action? _releaseMainWindowHooks;

    /// <summary>
    /// The current start's own closed flag, set only by that start's main window, so an activate posted after it closed finds no
    /// window; null for a viewer or harness start.
    /// </summary>
    private StrongBox<bool>? _mainWindowClosed;

    /// <summary>The files a viewer launch opens: empty for a main launch. Set before Avalonia starts.</summary>
    internal static IReadOnlyList<string> ViewerFiles { get; set; } = [];

    /// <summary>The log <c>Program</c> made for the process, or null when it made none. Set before Avalonia starts.</summary>
    internal static IAppLog? ProcessLog { get; set; }

    /// <summary>
    /// The gate the instance server's <c>activate</c> messages pass through, for a main launch; null for viewer and harness
    /// launches. Set before Avalonia starts.
    /// </summary>
    internal static ActivationGate? Activation { get; set; }

    /// <summary>
    /// Stops the main instance answering second launches while it keeps the lock, for a main launch; null for viewer and harness
    /// launches. Run once when the main window has closed, before the app's exit saves the session. Set before Avalonia starts.
    /// </summary>
    internal static Action? StopAnswering { get; set; }

    /// <summary>
    /// Makes the one watcher of the themes folder that has the bridge reload after a change, for a main or viewer start; a test
    /// wraps it to see when the app creates and disposes it.
    /// </summary>
    internal static Func<ThemeBridge, IAppLog, IDisposable> CreateThemesSync { get; set; } =
        (bridge, log) => new ThemeFolderSync(bridge, AppPaths.ThemesDirectory, TimeProvider.System, log);

    /// <summary>
    /// Makes the watcher of <c>settings.json</c> over the app's settings store and writer, for a main or viewer start; a test
    /// wraps it to see when the app creates and disposes it.
    /// </summary>
    internal static Func<SettingsStore, SettingsWriter, IAppLog, SettingsWatcher> CreateSettingsWatcher { get; set; } =
        (store, writer, log) => new SettingsWatcher(store, writer, TimeProvider.System, log);

    /// <summary>What <see cref="CreateSettingsWatcher"/> made for the current start; null for a harness start and after it is released.</summary>
    internal SettingsWatcher? SettingsWatcher => _settingsWatcher;

    /// <summary>What <see cref="CreateThemesSync"/> made for the current start; null for a harness start and after it is released.</summary>
    internal IDisposable? ThemesSync => _themesSync;

    /// <summary>
    /// The one theme settings window's host, for a main or viewer start; null for a harness start and after it is released.
    /// </summary>
    internal ThemeSettingsHost? ThemeSettingsHost => _themeSettingsHost;

    /// <summary>The one theme bridge the app applies through; null before the first window is built and after it is released.</summary>
    internal ThemeBridge? Bridge => _bridge;

    /// <summary>
    /// What saves the bridge's theme picks to <c>settings.json</c>; null before the first main or viewer window is built and
    /// after it is released.
    /// </summary>
    internal SettingsWriter? SettingsWriter => _settingsWriter;

    /// <summary>The process's one log: <see cref="ProcessLog"/> when set, else one made when the first window is built.</summary>
    private IAppLog Log => _log ??= ProcessLog ?? new FileAppLog(AppPaths.LogFile);

    /// <summary>
    /// Forgets the process's log, so the next window makes one where the data folder is then. A test that changes
    /// <c>YAMV_DATA_DIR</c> between windows calls this; production keeps one log for the process.
    /// </summary>
    internal void ResetLog() => _log = null;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateStartWindow();
            desktop.ShutdownRequested += (_, e) => OnShutdownRequested(e, () => desktop.TryShutdown());
            desktop.Exit += (_, _) => ReleaseMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Shows the theme settings window owned by <paramref name="owner"/>, or activates the open one, which keeps its first owner.
    /// </summary>
    /// <param name="owner">The window that asked.</param>
    /// <exception cref="InvalidOperationException">The start is a harness start or is released, so it has no host.</exception>
    internal void ShowThemeSettings(Window owner) =>
        (
            _themeSettingsHost ?? throw new InvalidOperationException("This start has no theme settings host: it is a harness start or is released.")
        ).Show(owner);

    /// <summary>
    /// Holds a shutdown while the theme settings window's close check runs: an open Import notice or confirm is cancelled, and
    /// unsaved edits ask Save, Discard or Cancel. Cancel, or a failed save, keeps the app running; otherwise the shutdown is asked
    /// again once the check has passed, and the window then closes without asking. A request another handler cancelled is left
    /// alone, a cancel is never cleared, and a request while the check runs is held without a second check.
    /// </summary>
    /// <param name="e">The shutdown request.</param>
    /// <param name="tryShutdown">Asks for the shutdown again: the lifetime's <c>TryShutdown</c>.</param>
    internal void OnShutdownRequested(ShutdownRequestedEventArgs e, Action tryShutdown)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(tryShutdown);
        if (e.Cancel)
        {
            return;
        }

        if (_shutdownCheck is not null)
        {
            e.Cancel = true;
            return;
        }

        if (_themeSettingsHost is not { } host)
        {
            return;
        }

        Task<bool> check = host.ConfirmShutdownAsync();
        if (check.IsCompletedSuccessfully)
        {
            if (!check.Result)
            {
                e.Cancel = true;
            }

            return;
        }

        e.Cancel = true;
        Task pending = ShutdownAfterCheckAsync(check, tryShutdown);
        if (!pending.IsCompleted)
        {
            _shutdownCheck = pending;
        }
    }

    /// <summary>
    /// Waits for the pending shutdown check, forgets it, then asks for the shutdown again when the check passed. A check that
    /// throws keeps the app running and is logged, so unsaved edits are never dropped on an error.
    /// </summary>
    private async Task ShutdownAfterCheckAsync(Task<bool> check, Action tryShutdown)
    {
        bool passed = false;
        try
        {
            passed = await check;
        }
        // A failed check must not end the app with unsaved edits, nor go unseen; the app keeps running.
        catch (Exception exception)
        {
            Log.LogError("Theme settings: the shutdown check failed; the app keeps running.", exception);
        }
        finally
        {
            _shutdownCheck = null;
        }

        if (!passed)
        {
            return;
        }

        try
        {
            tryShutdown();
        }
        // Nothing awaits this task, so a failure here would be lost; it is logged and the app keeps running.
        catch (Exception exception)
        {
            Log.LogError("Theme settings: asking for the shutdown again failed; the app keeps running.", exception);
        }
    }

    /// <summary>
    /// The window a start shows: with <c>YAMV_DEV_HARNESS</c> at <c>1</c>, the theme settings window when
    /// <c>YAMV_DEV_THEME_SETTINGS</c> is <c>1</c> too, else the dev harness; otherwise the main window, or a viewer window
    /// over <see cref="ViewerFiles"/> when the launch named files.
    /// </summary>
    internal Window CreateStartWindow()
    {
        ReleaseMainWindow();
        if (Environment.GetEnvironmentVariable(HarnessVariable) == "1")
        {
            return Environment.GetEnvironmentVariable(ThemeSettingsHarnessVariable) == "1" ? CreateThemeSettingsHarness() : new HarnessWindow();
        }

        IAppLog log = Log;
        ThemeBridge bridge = MainWindowFactory.CreateBridge(this, log);
        _bridge = bridge;
        _themesSync = CreateThemesSync(bridge, log);
        _themeSettingsHost = ThemeSettingsHost.Create(bridge, log);
        var settings = new SettingsStore(AppPaths.SettingsFile, log);
        AppSettings saved = settings.Load();
        bridge.Apply(saved.ThemeId, saved.Appearance);
        _settingsWriter = new SettingsWriter(settings, log);
        _settingsWatcher = CreateSettingsWatcher(settings, _settingsWriter, log);
        _settingsWatcher.Changed += OnSettingsFileChanged;
        bridge.RequestChanged += OnThemeRequestChanged;
        var widths = new WidthStore(AppPaths.WidthsFile, log);
        _mainWindow =
            ViewerFiles.Count == 0 ? MainWindowFactory.Create(bridge, widths, log) : MainWindowFactory.CreateViewer(bridge, widths, ViewerFiles, log);
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
            && Environment.GetEnvironmentVariable(WidthPopoverVariable) is { Length: > 0 } popoverStart
            && _mainWindow.Window is MainWindow documentWindow
        )
        {
            documentWindow.WidthPopoverWhenShown = popoverStart switch
            {
                "1" => DevWidthPopover.Open,
                "clearall" => DevWidthPopover.AskClearAll,
                "folder" => DevWidthPopover.SelectDocumentFolder,
                _ => throw new InvalidOperationException($"{WidthPopoverVariable} is '{popoverStart}'; use 1, clearall or folder."),
            };
        }

        if (ViewerFiles.Count == 0 && Activation is { } gate)
        {
            ConnectGate(gate, _mainWindow.Window);
        }

        if (ViewerFiles.Count == 0)
        {
            HookMainWindowClosed(_mainWindow.Window, StopAnswering);
        }

        return _mainWindow.Window;
    }

    /// <summary>
    /// When the main window has closed, marks this start's window closed for <see cref="BringMainWindowForward"/> and runs this
    /// start's <paramref name="stopAnswering"/>: <c>Closed</c>, unlike <c>Closing</c>, cannot be cancelled.
    /// <see cref="ReleaseMainWindow"/> unhooks this and runs the action too when it has not run yet, before it saves the session,
    /// so the order holds whichever of the window's <c>Closed</c> and the app's exit comes first. The action runs at most once.
    /// </summary>
    private void HookMainWindowClosed(Window window, Action? stopAnswering)
    {
        var closed = new StrongBox<bool>();
        Action? pending = stopAnswering;
        void RunStopAnsweringOnce()
        {
            Action? action = pending;
            pending = null;
            action?.Invoke();
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            closed.Value = true;
            RunStopAnsweringOnce();
        }

        window.Closed += OnClosed;
        _mainWindowClosed = closed;
        _releaseMainWindowHooks = () =>
        {
            window.Closed -= OnClosed;
            RunStopAnsweringOnce();
        };
    }

    /// <summary>Brings the main window forward on each activate the gate raises, and marks the gate ready on its first show.</summary>
    private void ConnectGate(ActivationGate gate, Window window)
    {
        _connectedGate = gate;
        gate.Activate += OnActivateRequested;
        void MarkReadyOnce(object? sender, EventArgs e)
        {
            window.Opened -= MarkReadyOnce;
            gate.MarkReady();
        }

        window.Opened += MarkReadyOnce;
    }

    /// <summary>Raised on whichever thread asked; the work moves to the UI thread and never blocks the asker.</summary>
    private void OnActivateRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(BringMainWindowForward);

    private void BringMainWindowForward()
    {
        Window? window = _mainWindow?.Window;
        if (window is null || _mainWindowClosed is { Value: true })
        {
            Log.LogInfo("Activate: no main window to bring forward.");
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        Log.LogInfo("Activate: main window brought forward.");
    }

    /// <summary>
    /// Disposes the theme settings host, closing its window without the unsaved-edits confirm, then what the start window owns,
    /// then the themes folder watcher, then the settings file watcher, then the settings writer, waiting up to 2 s for the theme
    /// picks still being saved, then the bridge the app owns, once the app exits. A second call does nothing more.
    /// </summary>
    internal void ReleaseMainWindow()
    {
        _themeSettingsHost?.Dispose();
        _themeSettingsHost = null;
        _connectedGate?.Activate -= OnActivateRequested;
        _connectedGate = null;
        Action? releaseHooks = _releaseMainWindowHooks;
        _releaseMainWindowHooks = null;
        releaseHooks?.Invoke();

        _mainWindow?.Dispose();
        _mainWindow = null;
        _mainWindowClosed = null;
        _themesSync?.Dispose();
        _themesSync = null;
        ReleaseSettingsWatcher();
        _bridge?.RequestChanged -= OnThemeRequestChanged;
        _settingsWriter?.Dispose();
        _settingsWriter = null;
        _bridge?.Dispose();
        _bridge = null;
        _harnessBridge?.Dispose();
        _harnessBridge = null;
    }

    /// <summary>Stops watching <c>settings.json</c>, waiting for a report already running; a failure to stop is logged.</summary>
    private void ReleaseSettingsWatcher()
    {
        SettingsWatcher? watcher = _settingsWatcher;
        _settingsWatcher = null;
        if (watcher is null)
        {
            return;
        }

        watcher.Changed -= OnSettingsFileChanged;
        try
        {
            watcher.Dispose();
        }
        // A watcher that fails to stop must not stop the app's exit from disposing the rest.
        catch (Exception exception)
        {
            Log.LogError("Cannot stop watching the settings file.", exception);
        }
    }

    /// <summary>
    /// Runs on a thread-pool thread with what <c>settings.json</c> holds after a change; the work moves to the UI thread and
    /// never blocks the watcher, whose dispose waits for this.
    /// </summary>
    internal void OnSettingsFileChanged(object? sender, AppSettings settings) => Dispatcher.UIThread.Post(() => ApplySettingsFile(sender, settings));

    /// <summary>
    /// Applies the settings file's theme and Appearance when they differ from what the bridge was asked for, unless the start
    /// that watched the file is released or a pick is still being saved: that save changes the file again, and its report
    /// carries the final state. A change applied from the file is never saved back, so a stale report cannot overwrite a newer
    /// edit.
    /// </summary>
    private void ApplySettingsFile(object? watcher, AppSettings settings)
    {
        if (!ReferenceEquals(watcher, _settingsWatcher) || _bridge is not { } bridge || _settingsWriter is not { } writer)
        {
            return;
        }

        if (!writer.WhenIdle().IsCompleted)
        {
            return;
        }

        if (
            string.Equals(bridge.RequestedThemeId, settings.ThemeId, StringComparison.Ordinal)
            && string.Equals(bridge.RequestedAppearance, settings.Appearance, StringComparison.Ordinal)
        )
        {
            return;
        }

        bridge.RequestChanged -= OnThemeRequestChanged;
        try
        {
            bridge.Apply(settings.ThemeId, settings.Appearance);
        }
        finally
        {
            bridge.RequestChanged += OnThemeRequestChanged;
        }
    }

    /// <summary>Saves the theme the bridge was just asked for; the writer does the file IO off this thread.</summary>
    private void OnThemeRequestChanged(object? sender, EventArgs e)
    {
        if (_bridge is { } bridge)
        {
            _settingsWriter?.Request(bridge.RequestedThemeId, bridge.RequestedAppearance);
        }
    }

    /// <summary>
    /// The theme settings window over the data folder's themes, with Studio applied to follow the system, a trash that deletes
    /// outright inside a dev data folder, and Edit as text written to the log. With <see cref="ImportFileVariable"/> set its
    /// Import pick answers with that file once, and the notice opens as soon as the window has shown. It refuses to start without
    /// <c>YAMV_DATA_DIR</c>, before it touches any folder, so it never writes into the owner's data folder.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>YAMV_DATA_DIR</c> is not set.</exception>
    private ThemeSettingsWindow CreateThemeSettingsHarness()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
        {
            throw new InvalidOperationException($"The theme settings harness needs {AppPaths.DataDirectoryVariable} set.");
        }

        var log = new FileAppLog(AppPaths.LogFile);
        var catalog = new ThemeCatalog(AppPaths.ThemesDirectory, log);
        var bridge = new ThemeBridge(catalog, this, log);
        _harnessBridge = bridge;
        bridge.Apply(ThemeCatalog.DefaultThemeId, "system");
        var fileActions = new AvaloniaFileActions(log, AvaloniaFileActions.StartProcess, FileActionSeams.Default);
        string? importFile = Environment.GetEnvironmentVariable(ImportFileVariable) is { } given && !string.IsNullOrWhiteSpace(given) ? given : null;
        string? pendingImport = null;
        (ThemeSettingsWindow window, ThemeSettingsViewModel viewModel) = ThemeSettingsHost.BuildWindow(
            new ThemeSettingsWindowParts
            {
                Catalog = catalog,
                Apply = bridge.Apply,
                ThemeId = ThemeCatalog.DefaultThemeId,
                Appearance = "system",
                Log = log,
                Trash = _ => new HarnessTrash(),
                OpenAsText = path => log.LogInfo($"Harness: edit as text {path}"),
                PickImport = owner =>
                {
                    if (pendingImport is not { } path)
                    {
                        return fileActions.PickThemeAsync(owner);
                    }

                    pendingImport = null;
                    return Task.FromResult<string?>(path);
                },
                FileActions = fileActions,
            }
        );
        window.Opened += (_, _) =>
        {
            log.LogInfo("Window: shown");
            if (importFile is not { } path)
            {
                return;
            }

            importFile = null;
            pendingImport = path;
            log.LogInfo($"Harness: import {path}");
            viewModel.ImportCommand.Execute(null);
            pendingImport = null;
        };
        return window;
    }

    /// <summary>
    /// The dev harness's <see cref="IFileTrash"/>: it deletes the file outright, since a hidden harness run must not reach the
    /// owner's Recycle Bin or Trash, and it refuses unless <c>YAMV_DATA_DIR</c> is set, so it only ever deletes from a dev data
    /// folder and never from the owner's themes.
    /// </summary>
    private sealed class HarnessTrash : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
            {
                throw new UnauthorizedAccessException(
                    $"The dev harness deletes theme files only when {AppPaths.DataDirectoryVariable} points at a dev data folder; {path} is kept."
                );
            }

            File.Delete(path);
        }
    }
}
