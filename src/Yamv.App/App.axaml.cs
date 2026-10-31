using System.Diagnostics.CodeAnalysis;
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
using Yamv.Core.Instance;
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
    /// The environment variable that, set to <c>1</c>, has the start window (main or viewer) open its content-width popover once it has
    /// shown its first document, so a hidden-desktop capture shows the popover; <c>clearall</c> then also asks the Clear all
    /// question, and <c>folder</c> selects the document's own folder segment.
    /// </summary>
    public const string WidthPopoverVariable = "YAMV_DEV_WIDTH_POPOVER";

    /// <summary>
    /// The environment variable that has the start window (main or viewer), once it has shown its first document, open its ⋯ menu
    /// (<c>menu</c>) or ask for the theme settings window as the menu's item does (<c>open</c>), so a hidden-desktop capture
    /// shows it.
    /// </summary>
    public const string ThemeSettingsEntryVariable = "YAMV_DEV_THEME_SETTINGS_ENTRY";

    private readonly List<DocumentWindow> _viewers = [];
    private DocumentWindow? _main;
    private ThemeBridge? _bridge;
    private ThemeBridge? _harnessBridge;
    private SettingsWriter? _settingsWriter;
    private SettingsWatcher? _settingsWatcher;
    private IDisposable? _themesSync;
    private ThemeSettingsHost? _themeSettingsHost;
    private WidthStore? _widths;

    /// <summary>The shutdown check a held shutdown waits on, or null; a request while it runs starts no second one.</summary>
    private Task? _shutdownCheck;
    private IAppLog? _log;

    /// <summary>The current start's stop action, until it has run; it runs at most once per start.</summary>
    private Action? _pendingStop;

    /// <summary>
    /// The current start's last-window rule: once it says the app is stopping, an open request is answered <c>closing</c> and an
    /// activate builds nothing. A start replaces it.
    /// </summary>
    private AppLifetimePolicy _policy = new(LastWindowBehavior.Quit);

    /// <summary>The current start's launch state machine: it decides the start window and whether an activate builds the main window.</summary>
    private LaunchArbiter _arbiter = new();

    /// <summary>The desktop lifetime the quit shuts down; null in a headless test.</summary>
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    /// <summary>Set once the app's exit began, so a quit job still queued does nothing.</summary>
    private bool _exited;

    /// <summary>The last stamp given to a document window's activation; a higher stamp is a more recent activation.</summary>
    private long _activationStamp;

    /// <summary>The files the first window opens: empty for a main launch. Set before Avalonia starts.</summary>
    internal static IReadOnlyList<string> ViewerFiles { get; set; } = [];

    /// <summary>The log <c>Program</c> made for the process, or null when it made none. Set before Avalonia starts.</summary>
    internal static IAppLog? ProcessLog { get; set; }

    /// <summary>
    /// The latch every instance-server request reaches the UI thread through; the app opens it once its framework is initialized
    /// and its start window shown. Null for harness launches. Set before Avalonia starts.
    /// </summary>
    internal static UiReadyLatch? ReadyLatch { get; set; }

    /// <summary>
    /// The start's last-window rule, from the platform: quit at the last document window on Windows, keep running on macOS. Null
    /// for harness launches, where a start uses a fresh quit rule. Set before Avalonia starts.
    /// </summary>
    internal static AppLifetimePolicy? Lifetime { get; set; }

    /// <summary>When set, what the quit job calls instead of shutting the desktop lifetime down; a test records the quit with it.</summary>
    internal static Action? QuitForTests { get; set; }

    /// <summary>
    /// Starts stopping the app answering launches while it keeps the lock, and returns at once: it must never wait on the
    /// instance server. Run once when the last document window has closed, before the main session's last save, or by the
    /// app's exit when no window closed first; null for harness launches. Set before Avalonia starts.
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

    /// <summary>The open viewer windows, in the order they opened.</summary>
    internal IReadOnlyList<Window> ViewerWindows => [.. _viewers.Select(viewer => (Window)viewer.Composition.Window)];

    /// <summary>The main window, or null while none is open.</summary>
    internal Window? MainDocumentWindow => _main?.Composition.Window;

    /// <summary>The open document windows, main and viewers; read on the UI thread only.</summary>
    private int DocumentWindowCount => (_main is null ? 0 : 1) + _viewers.Count;

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
            desktop.ShutdownRequested += (_, e) => OnShutdownRequested(e, () => desktop.TryShutdown());
            desktop.Exit += (_, _) => ReleaseMainWindow();
            if (IsHarnessStart)
            {
                desktop.MainWindow = CreateStartWindow();
            }
            else
            {
                _desktop = desktop;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                CreateStartWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
        OpenReadyLatch();
    }

    /// <summary>Opens the ready latch, if this launch has one: its queued jobs, and every later one, are posted to the UI thread.</summary>
    internal static void OpenReadyLatch() => ReadyLatch?.Open(job => Dispatcher.UIThread.Post(job));

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
    /// over <see cref="ViewerFiles"/> when the launch named files, as the launch arbiter's start entry decides. A harness window is
    /// returned unshown, for the lifetime to show as its main window; a main or viewer start window is shown here, through the
    /// same guarded path as any later window, and a start window that fails to show is removed like any other.
    /// </summary>
    /// <returns>The start window, or null when the main or viewer start window failed to open.</returns>
    internal Window? CreateStartWindow()
    {
        if (IsHarnessStart)
        {
            return Environment.GetEnvironmentVariable(ThemeSettingsHarnessVariable) == "1" ? CreateThemeSettingsHarness() : new HarnessWindow();
        }

        _pendingStop = StopAnswering;
        _policy = Lifetime ?? new AppLifetimePolicy(LastWindowBehavior.Quit);
        _arbiter = new LaunchArbiter();
        _exited = false;
        return _arbiter.Start(ViewerFiles) switch
        {
            OpenViewerWindow viewer => ShowViewer(viewer.Files, ApplyDevStartHooks),
            OpenMainWindow => ShowMain(ApplyDevStartHooks),
            _ => null,
        };
    }

    private static bool IsHarnessStart => Environment.GetEnvironmentVariable(HarnessVariable) == "1";

    /// <summary>
    /// Opens a viewer window over <paramref name="files"/> for a launch's <paramref name="request"/>, on the UI thread. A stopping
    /// app answers <c>closing</c>; a request the server already abandoned opens nothing; otherwise the request is answered
    /// <c>opened</c> as soon as it is taken, before the window shows, and a window that then fails to open is the app's to report.
    /// </summary>
    /// <returns>True when the app took the request, whether or not its window then opened.</returns>
    internal bool OpenViewer(IReadOnlyList<string> files, InstanceRequest request)
    {
        if (!_policy.MayOpenWindows)
        {
            request.Complete(InstanceReply.Closing);
            Log.LogInfo($"Open: the app is closing; answered closing to an open of {files.Count} file(s).");
            return false;
        }

        if (!request.TryBegin())
        {
            Log.LogInfo($"Open: an open of {files.Count} file(s) was abandoned before the app took it; opening nothing.");
            return false;
        }

        request.Complete(InstanceReply.Opened);
        ShowNewViewer(files);
        return true;
    }

    /// <summary>
    /// Builds, shows and activates a viewer window over <paramref name="files"/>. A window that fails to show is logged, removed
    /// and closed, so no window is left that the app does not follow.
    /// </summary>
    internal void ShowNewViewer(IReadOnlyList<string> files) => ShowViewer(files, beforeShow: null);

    /// <summary>
    /// Builds, shows and activates the main window from the saved session, unless one is open or the app is stopping. A main
    /// window that fails to show is logged, removed and closed, and the arbiter hears it closed.
    /// </summary>
    /// <returns>The main window it opened, or null when it opened none.</returns>
    internal Window? OpenMain() => ShowMain(beforeShow: null);

    /// <summary>
    /// Brings the app forward for an activate, on the UI thread: builds the main window when the arbiter asks for it, else brings
    /// the main window forward, or with no main window the most recently active viewer. A stopping app builds nothing.
    /// </summary>
    internal void ActivateRequested()
    {
        if (!_policy.MayOpenWindows)
        {
            Log.LogInfo("Activate ignored: the app is stopping.");
            return;
        }

        if (_arbiter.ActivateRequested() is OpenMainWindow && ShowMain(beforeShow: null) is not null)
        {
            Log.LogInfo("Activate: main window opened.");
            return;
        }

        BringWindowForward();
    }

    /// <summary>
    /// Builds, shows and activates a viewer window, running <paramref name="beforeShow"/> on it first. A failure is logged, the
    /// window removed through the one removal path and closed.
    /// </summary>
    private MainWindow? ShowViewer(IReadOnlyList<string> files, Action<MainWindow>? beforeShow)
    {
        DocumentWindow? tracked = null;
        try
        {
            (ThemeBridge bridge, WidthStore widths) = Shared();
            tracked = Track(MainWindowFactory.CreateViewer(bridge, widths, files, Log));
            _viewers.Add(tracked);
            return ShowBuilt(tracked.Composition.Window, beforeShow);
        }
        // The launcher was already told opened, so the app itself reports a window that could not open.
        catch (Exception exception)
        {
            Log.LogError($"Open: the viewer window for {files.Count} file(s) could not open.", exception);
            RemoveFailed(tracked);
            return null;
        }
    }

    /// <summary>
    /// Builds, shows and activates the main window, running <paramref name="beforeShow"/> on it first, unless one is open or the
    /// app is stopping. A failure is logged, the window removed through the one removal path (which tells the arbiter) and closed.
    /// </summary>
    private MainWindow? ShowMain(Action<MainWindow>? beforeShow)
    {
        if (_main is not null || !_policy.MayOpenWindows)
        {
            return null;
        }

        DocumentWindow? tracked = null;
        try
        {
            (ThemeBridge bridge, WidthStore widths) = Shared();
            tracked = Track(MainWindowFactory.Create(bridge, widths, Log));
            _main = tracked;
            if (!_arbiter.MainWindowOpen)
            {
                _arbiter.MainWindowOpened();
            }

            return ShowBuilt(tracked.Composition.Window, beforeShow);
        }
        // An activate or a start that fails must not end the UI loop; the failure is logged and the window removed.
        catch (Exception exception)
        {
            Log.LogError("The main window could not open.", exception);
            if (tracked is null)
            {
                _arbiter.MainWindowClosed();
            }

            RemoveFailed(tracked);
            return null;
        }
    }

    private static MainWindow ShowBuilt(MainWindow window, Action<MainWindow>? beforeShow)
    {
        beforeShow?.Invoke(window);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>
    /// Removes a document window that failed to open and closes it; when it failed before it was built, applies the last-window
    /// rule to what is left.
    /// </summary>
    private void RemoveFailed(DocumentWindow? tracked)
    {
        if (tracked is null)
        {
            ApplyLastWindowRule(gone: null);
            return;
        }

        RemoveDocumentWindow(tracked);
        CloseFailedWindow(tracked.Composition.Window);
    }

    /// <summary>Closes a document window that failed to show, so no window the app no longer follows keeps the process alive.</summary>
    private void CloseFailedWindow(Window window)
    {
        try
        {
            window.Close();
        }
        // The window is already removed; a close that fails too is logged, never rethrown into the UI job.
        catch (Exception exception)
        {
            Log.LogError("A document window that failed to show could not be closed either.", exception);
        }
    }

    /// <summary>Runs the start's dev hooks on the start window before it shows.</summary>
    private static void ApplyDevStartHooks(MainWindow window)
    {
        ApplyDevWidthPopover(window);
        ApplyDevThemeSettingsEntry(window);
    }

    /// <summary>
    /// The bridge, the settings writer and the width store every document window shares, made with the first window: the
    /// bridge applies <c>settings.json</c> once, and the writer saves each theme pick back. Every part is built before any field
    /// is set: when a step throws, what this call built is disposed, newest first, and the exception rethrown, so a later window
    /// builds the whole set again and nothing half-built is left behind.
    /// </summary>
    private (ThemeBridge Bridge, WidthStore Widths) Shared()
    {
        if (_bridge is { } existingBridge && _widths is { } existingWidths)
        {
            return (existingBridge, existingWidths);
        }

        IAppLog log = Log;
        Stack<IDisposable> built = new();
        try
        {
            ThemeBridge bridge = MainWindowFactory.CreateBridge(this, log);
            built.Push(bridge);
            IDisposable themesSync = CreateThemesSync(bridge, log);
            built.Push(themesSync);
            var host = ThemeSettingsHost.Create(bridge, log);
            built.Push(host);
            var settings = new SettingsStore(AppPaths.SettingsFile, log);
            AppSettings saved = settings.Load();
            bridge.Apply(saved.ThemeId, saved.Appearance);
            var writer = new SettingsWriter(settings, log);
            built.Push(writer);
            SettingsWatcher watcher = CreateSettingsWatcher(settings, writer, log);
            built.Push(watcher);
            var widths = new WidthStore(AppPaths.WidthsFile, log);
            watcher.Changed += OnSettingsFileChanged;
            bridge.RequestChanged += OnThemeRequestChanged;
            _bridge = bridge;
            _themesSync = themesSync;
            _themeSettingsHost = host;
            _settingsWriter = writer;
            _settingsWatcher = watcher;
            _widths = widths;
            return (bridge, widths);
        }
        catch
        {
            DisposeBuilt(built, log);
            throw;
        }
    }

    /// <summary>Disposes what a failed <see cref="Shared"/> built, newest first; a dispose that throws is logged and the rest still run.</summary>
    private static void DisposeBuilt(Stack<IDisposable> built, IAppLog log)
    {
        while (built.TryPop(out IDisposable? part))
        {
            try
            {
                part.Dispose();
            }
            // The build's own exception is the one the caller sees; a failed cleanup is logged so it is not lost.
            catch (Exception exception)
            {
                log.LogError($"Could not dispose a {part.GetType().Name} after building the shared window parts failed.", exception);
            }
        }
    }

    /// <summary>
    /// Follows a document window until it closes: each activation stamps it as the most recently active, its theme settings
    /// requests open the theme settings window, and its close removes it.
    /// </summary>
    private DocumentWindow Track(MainWindowComposition composition)
    {
        var tracked = new DocumentWindow(composition);
        MainWindow window = composition.Window;
        void OnActivated(object? sender, EventArgs e) => tracked.ActivatedAt = ++_activationStamp;

        void OnClosed(object? sender, EventArgs e) => RemoveDocumentWindow(tracked);

        window.Activated += OnActivated;
        window.Closed += OnClosed;
        window.ThemeSettingsRequested += OnThemeSettingsRequested;
        tracked.Unhook = () =>
        {
            window.Activated -= OnActivated;
            window.Closed -= OnClosed;
            window.ThemeSettingsRequested -= OnThemeSettingsRequested;
        };
        return tracked;
    }

    /// <summary>
    /// The one removal path of a document window, closed or failed to show: unhooks it, forgets it (a main window is reported
    /// closed to the arbiter), then applies the last-window rule once. A window already removed is left alone.
    /// </summary>
    private void RemoveDocumentWindow(DocumentWindow tracked)
    {
        tracked.Unhook();
        if (ReferenceEquals(_main, tracked))
        {
            _main = null;
            _arbiter.MainWindowClosed();
        }
        else if (!_viewers.Remove(tracked))
        {
            return;
        }

        ApplyLastWindowRule(tracked.Composition);
    }

    /// <summary>
    /// Applies the last-window rule to the document windows left: when it says stop and quit, the app stops answering first, then
    /// <paramref name="gone"/>'s composition is disposed (saving the main window's session), then one quit job is posted; never a
    /// shutdown inside the close.
    /// </summary>
    private void ApplyLastWindowRule(MainWindowComposition? gone)
    {
        LastWindowStep step = _policy.DocumentWindowGone(DocumentWindowCount);
        if (step == LastWindowStep.StopAndQuit)
        {
            RunStopOnce();
        }

        gone?.Dispose();
        if (step == LastWindowStep.StopAndQuit)
        {
            AppLifetimePolicy policy = _policy;
            Dispatcher.UIThread.Post(() => QuitIfStillEmpty(policy));
        }
    }

    /// <summary>
    /// The quit job: shuts the app down unless the start that posted it was replaced, a document window opened meanwhile or the
    /// app is already exiting.
    /// </summary>
    private void QuitIfStillEmpty(AppLifetimePolicy postedBy)
    {
        if (!ReferenceEquals(postedBy, _policy) || DocumentWindowCount > 0 || _exited)
        {
            return;
        }

        Log.LogInfo("Quit: no document window is left; shutting the app down.");
        if (QuitForTests is { } quit)
        {
            quit();
        }
        else
        {
            _desktop?.Shutdown();
        }
    }

    /// <summary>
    /// Tells <paramref name="window"/> what to do with its content-width popover once shown, from <see cref="WidthPopoverVariable"/>,
    /// read only when <c>YAMV_DATA_DIR</c> points at a dev data folder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The variable is set to a value other than <c>1</c>, <c>clearall</c> or <c>folder</c>.</exception>
    private static void ApplyDevWidthPopover(MainWindow window)
    {
        if (
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
            || Environment.GetEnvironmentVariable(WidthPopoverVariable) is not { Length: > 0 } popoverStart
        )
        {
            return;
        }

        window.WidthPopoverWhenShown = popoverStart switch
        {
            "1" => DevWidthPopover.Open,
            "clearall" => DevWidthPopover.AskClearAll,
            "folder" => DevWidthPopover.SelectDocumentFolder,
            _ => throw new InvalidOperationException($"{WidthPopoverVariable} is '{popoverStart}'; use 1, clearall or folder."),
        };
    }

    /// <summary>
    /// Tells <paramref name="window"/> what to do with its ⋯ menu once shown, from <see cref="ThemeSettingsEntryVariable"/>, read
    /// only when <c>YAMV_DATA_DIR</c> points at a dev data folder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The variable is set to a value other than <c>menu</c> or <c>open</c>.</exception>
    private static void ApplyDevThemeSettingsEntry(MainWindow window)
    {
        if (
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
            || Environment.GetEnvironmentVariable(ThemeSettingsEntryVariable) is not { Length: > 0 } entry
        )
        {
            return;
        }

        window.ThemeSettingsEntryWhenShown = entry switch
        {
            "menu" => DevThemeSettingsEntry.Menu,
            "open" => DevThemeSettingsEntry.Open,
            _ => throw new InvalidOperationException($"{ThemeSettingsEntryVariable} is '{entry}'; use menu or open."),
        };
    }

    /// <summary>
    /// Shows the theme settings window owned by the document window that asked for it. A failure to open it is logged and shown
    /// as a toast in the window that asked, since nothing else would catch it on the UI thread.
    /// </summary>
    private void OnThemeSettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            ShowThemeSettings((Window)sender!);
        }
        // A user-started action that fails must not end the app; it is logged and shown instead.
        catch (Exception exception)
        {
            Log.LogError("Theme settings: could not open the window.", exception);
            TrackedComposition(sender)?.ViewModel.Toasts.Show(ToastKind.Warning, "Couldn't open the theme settings");
        }
    }

    /// <summary>The composition of the tracked document window <paramref name="window"/>, or null when the app does not follow it.</summary>
    private MainWindowComposition? TrackedComposition(object? window)
    {
        if (_main is { } main && ReferenceEquals(main.Composition.Window, window))
        {
            return main.Composition;
        }

        return _viewers.Find(viewer => ReferenceEquals(viewer.Composition.Window, window))?.Composition;
    }

    /// <summary>Runs this start's stop action, once.</summary>
    private void RunStopOnce()
    {
        Action? stop = _pendingStop;
        _pendingStop = null;
        stop?.Invoke();
    }

    /// <summary>Brings the main window forward, or with no main window open the most recently active viewer window.</summary>
    private void BringWindowForward()
    {
        if (_main is { } main)
        {
            Raise(main.Composition.Window);
            Log.LogInfo("Activate: main window brought forward.");
        }
        else if (_viewers.Count > 0)
        {
            Raise(_viewers.MaxBy(viewer => viewer.ActivatedAt)!.Composition.Window);
            Log.LogInfo("Activate: viewer window brought forward.");
        }
        else
        {
            Log.LogInfo("Activate: no main window to bring forward.");
        }
    }

    private static void Raise(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Releases what the app owns when it exits: disposes the theme settings host, closing its window without the unsaved-edits
    /// confirm, runs the stop action if the app had not begun stopping (the last-window rule's stop came first), disposes every
    /// document window's composition (saving the main session), then the themes folder watcher, then the settings file watcher,
    /// then the settings writer, waiting up to 2 s for the theme picks still being saved, then the bridge. A quit job still queued
    /// does nothing once this ran. Safe to call more than once.
    /// </summary>
    internal void ReleaseMainWindow()
    {
        _exited = true;
        _themeSettingsHost?.Dispose();
        _themeSettingsHost = null;
        if (_policy.Exiting())
        {
            RunStopOnce();
        }

        if (_main is { } main)
        {
            _main = null;
            main.Unhook();
            main.Composition.Dispose();
        }

        foreach (DocumentWindow viewer in _viewers)
        {
            viewer.Unhook();
            viewer.Composition.Dispose();
        }

        _viewers.Clear();
        _themesSync?.Dispose();
        _themesSync = null;
        ReleaseSettingsWatcher();
        _bridge?.RequestChanged -= OnThemeRequestChanged;
        _settingsWriter?.Dispose();
        _settingsWriter = null;
        _bridge?.Dispose();
        _bridge = null;
        _widths = null;
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
        var fileActions = new AvaloniaFileActions(log, null, AvaloniaFileActions.StartProcess, FileActionSeams.Default);
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

    /// <summary>A main or viewer window the app follows, with what unhooks the app's handlers from it.</summary>
    private sealed class DocumentWindow(MainWindowComposition composition)
    {
        public MainWindowComposition Composition { get; } = composition;

        public Action Unhook { get; set; } = () => { };

        /// <summary>The app's activation stamp at this window's last activation; 0 until it is first activated.</summary>
        public long ActivatedAt { get; set; }
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
