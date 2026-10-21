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

    private readonly List<DocumentWindow> _viewers = [];
    private DocumentWindow? _main;
    private ThemeBridge? _bridge;
    private ThemeBridge? _harnessBridge;
    private SettingsWriter? _settingsWriter;
    private WidthStore? _widths;
    private IAppLog? _log;
    private ActivationGate? _connectedGate;

    /// <summary>The current start's stop action, until it has run; it runs at most once per start.</summary>
    private Action? _pendingStop;

    /// <summary>
    /// Set once the last document window has closed or the app is exiting: from then on an open request is answered
    /// <c>closing</c>, never carried out. A start clears it.
    /// </summary>
    private bool _stopping;

    /// <summary>The files the first window opens: empty for a main launch. Set before Avalonia starts.</summary>
    internal static IReadOnlyList<string> ViewerFiles { get; set; } = [];

    /// <summary>The log <c>Program</c> made for the process, or null when it made none. Set before Avalonia starts.</summary>
    internal static IAppLog? ProcessLog { get; set; }

    /// <summary>
    /// The gate the instance server's <c>activate</c> messages pass through; null for harness launches. Set before Avalonia starts.
    /// </summary>
    internal static ActivationGate? Activation { get; set; }

    /// <summary>
    /// Starts stopping the app answering launches while it keeps the lock, and returns at once: it must never wait on the
    /// instance server. Run once when the last document window has closed, before the main session's last save, or by the
    /// app's exit when no window closed first; null for harness launches. Set before Avalonia starts.
    /// </summary>
    internal static Action? StopAnswering { get; set; }

    /// <summary>The one theme bridge the app applies through; null before the first window is built and after it is released.</summary>
    internal ThemeBridge? Bridge => _bridge;

    /// <summary>
    /// What saves the bridge's theme picks to <c>settings.json</c>; null before the first main or viewer window is built and
    /// after it is released.
    /// </summary>
    internal SettingsWriter? SettingsWriter => _settingsWriter;

    /// <summary>The open viewer windows, the most recently active last.</summary>
    internal IReadOnlyList<Window> ViewerWindows => [.. _viewers.Select(viewer => (Window)viewer.Composition.Window)];

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
            desktop.Exit += (_, _) => ReleaseMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The window a start shows: with <c>YAMV_DEV_HARNESS</c> at <c>1</c>, the theme settings window when
    /// <c>YAMV_DEV_THEME_SETTINGS</c> is <c>1</c> too, else the dev harness; otherwise the main window, or a viewer window
    /// over <see cref="ViewerFiles"/> when the launch named files. The gate is marked ready when that window first shows.
    /// </summary>
    internal Window CreateStartWindow()
    {
        if (Environment.GetEnvironmentVariable(HarnessVariable) == "1")
        {
            return Environment.GetEnvironmentVariable(ThemeSettingsHarnessVariable) == "1" ? CreateThemeSettingsHarness() : new HarnessWindow();
        }

        _stopping = false;
        _pendingStop = StopAnswering;
        Window window = ViewerFiles.Count == 0 ? AddMain().Window : AddViewer(ViewerFiles).Window;
        if (Activation is { } gate)
        {
            ConnectGate(gate, window);
        }

        return window;
    }

    /// <summary>
    /// Opens a viewer window over <paramref name="files"/> for a launch's <paramref name="request"/>, on the UI thread. A stopping
    /// app answers <c>closing</c>; a request the server already abandoned opens nothing; otherwise the request is answered
    /// <c>opened</c> as soon as it is taken, before the window shows, and a window that then fails to open is the app's to report.
    /// </summary>
    /// <returns>True when the app took the request, whether or not its window then opened.</returns>
    internal bool OpenViewer(IReadOnlyList<string> files, InstanceRequest request)
    {
        if (_stopping)
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
    /// Builds, shows and activates a viewer window over <paramref name="files"/>. A window that fails to show is logged, forgotten
    /// and closed, so no window is left that the app does not follow.
    /// </summary>
    internal void ShowNewViewer(IReadOnlyList<string> files)
    {
        MainWindowComposition? composition = null;
        try
        {
            composition = AddViewer(files);
            composition.Window.Show();
            composition.Window.Activate();
        }
        // The launcher was already told opened, so the app itself reports a window that could not open.
        catch (Exception exception)
        {
            Log.LogError($"Open: the viewer window for {files.Count} file(s) could not open.", exception);
            if (composition is not null)
            {
                Forget(composition);
                CloseFailedWindow(composition.Window);
            }
        }
    }

    /// <summary>Closes a viewer window that failed to show, so no window the app no longer follows keeps the process alive.</summary>
    private void CloseFailedWindow(Window window)
    {
        try
        {
            window.Close();
        }
        // The window is already forgotten; a close that fails too is logged, never rethrown into the UI job.
        catch (Exception exception)
        {
            Log.LogError("Open: a viewer window that failed to show could not be closed either.", exception);
        }
    }

    private MainWindowComposition AddMain()
    {
        (ThemeBridge bridge, WidthStore widths) = Shared();
        MainWindowComposition composition = MainWindowFactory.Create(bridge, widths, Log);
        _main = Track(composition);
        return composition;
    }

    private MainWindowComposition AddViewer(IReadOnlyList<string> files)
    {
        (ThemeBridge bridge, WidthStore widths) = Shared();
        MainWindowComposition composition = MainWindowFactory.CreateViewer(bridge, widths, files, Log);
        _viewers.Add(Track(composition));
        return composition;
    }

    /// <summary>
    /// The bridge, the settings writer and the width store every document window shares, made with the first window: the
    /// bridge applies <c>settings.json</c> once, and the writer saves each theme pick back.
    /// </summary>
    private (ThemeBridge Bridge, WidthStore Widths) Shared()
    {
        if (_bridge is { } bridge && _widths is { } widths)
        {
            return (bridge, widths);
        }

        IAppLog log = Log;
        bridge = MainWindowFactory.CreateBridge(this, log);
        _bridge = bridge;
        var settings = new SettingsStore(AppPaths.SettingsFile, log);
        AppSettings saved = settings.Load();
        bridge.Apply(saved.ThemeId, saved.Appearance);
        _settingsWriter = new SettingsWriter(settings, log);
        bridge.RequestChanged += OnThemeRequestChanged;
        widths = new WidthStore(AppPaths.WidthsFile, log);
        _widths = widths;
        return (bridge, widths);
    }

    /// <summary>Follows a document window until it closes: its activations order the viewers, and its close forgets it.</summary>
    private DocumentWindow Track(MainWindowComposition composition)
    {
        var tracked = new DocumentWindow(composition);
        MainWindow window = composition.Window;
        void OnActivated(object? sender, EventArgs e)
        {
            if (_viewers.Remove(tracked))
            {
                _viewers.Add(tracked);
            }
        }

        void OnClosed(object? sender, EventArgs e) => OnDocumentWindowClosed(tracked);

        window.Activated += OnActivated;
        window.Closed += OnClosed;
        tracked.Unhook = () =>
        {
            window.Activated -= OnActivated;
            window.Closed -= OnClosed;
        };
        return tracked;
    }

    /// <summary>
    /// Forgets the closed window. When it was the last document window, the app stops answering first; then the window's
    /// composition is disposed, which saves the main window's session.
    /// </summary>
    private void OnDocumentWindowClosed(DocumentWindow tracked)
    {
        tracked.Unhook();
        if (ReferenceEquals(_main, tracked))
        {
            _main = null;
        }

        _viewers.Remove(tracked);
        if (_main is null && _viewers.Count == 0)
        {
            RunStopOnce();
        }

        tracked.Composition.Dispose();
    }

    /// <summary>Forgets a viewer whose window failed to show, and disposes what it owns.</summary>
    private void Forget(MainWindowComposition composition)
    {
        DocumentWindow? tracked = _viewers.Find(viewer => ReferenceEquals(viewer.Composition, composition));
        if (tracked is not null)
        {
            _viewers.Remove(tracked);
            tracked.Unhook();
        }

        composition.Dispose();
    }

    /// <summary>Marks the app stopping and runs this start's stop action, once.</summary>
    private void RunStopOnce()
    {
        _stopping = true;
        Action? stop = _pendingStop;
        _pendingStop = null;
        stop?.Invoke();
    }

    /// <summary>Brings a window forward on each activate the gate raises, and marks the gate ready on the start window's first show.</summary>
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
    private void OnActivateRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(BringWindowForward);

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
            Raise(_viewers[^1].Composition.Window);
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
    /// Releases what the app owns when it exits: runs the stop action if no last window closed first, disposes every document
    /// window's composition (saving the main session), then the settings writer, waiting up to 2 s for the theme picks still
    /// being saved, then the bridge. Safe to call more than once.
    /// </summary>
    internal void ReleaseMainWindow()
    {
        RunStopOnce();
        _connectedGate?.Activate -= OnActivateRequested;
        _connectedGate = null;
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
        _bridge?.RequestChanged -= OnThemeRequestChanged;
        _settingsWriter?.Dispose();
        _settingsWriter = null;
        _bridge?.Dispose();
        _bridge = null;
        _widths = null;
        _harnessBridge?.Dispose();
        _harnessBridge = null;
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
        ThemeSettingsWindow? window = null;
        var fileActions = new AvaloniaFileActions(log, AvaloniaFileActions.StartProcess, FileActionSeams.Default);
        string? importFile = Environment.GetEnvironmentVariable(ImportFileVariable) is { } given && !string.IsNullOrWhiteSpace(given) ? given : null;
        string? pendingImport = null;
        var seams = new ThemeSettingsSeams
        {
            Apply = bridge.Apply,
            Trash = new HarnessTrash(),
            OpenAsText = path => log.LogInfo($"Harness: edit as text {path}"),
            ConfirmUnsaved = name => window!.ConfirmUnsavedAsync(name),
            ConfirmDelete = name => window!.ConfirmDeleteAsync(name),
            PickImport = () =>
            {
                if (pendingImport is not { } path)
                {
                    return fileActions.PickThemeAsync(window!);
                }

                pendingImport = null;
                return Task.FromResult<string?>(path);
            },
            PickExport = suggested => fileActions.PickThemeDestinationAsync(window!, suggested),
            ConfirmImport = inspection => window!.ConfirmImportAsync(inspection),
        };
        var viewModel = new ThemeSettingsViewModel(catalog, seams, ThemeCatalog.DefaultThemeId, "system", log);
        window = new ThemeSettingsWindow(viewModel, log);
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
