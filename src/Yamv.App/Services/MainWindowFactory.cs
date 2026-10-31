using Avalonia;
using Avalonia.Controls;
using Yamv.App.Controls;
using Yamv.App.Platform;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;
using Yamv.Core.Theming;
using Yamv.Core.Widths;

namespace Yamv.App.Services;

/// <summary>Builds a window from the user's data folder: its log, saved session, themes and document view.</summary>
public static class MainWindowFactory
{
    /// <summary>
    /// Set to 1 in a run with its own data folder, it makes the main window show three sample toasts once it has shown, for a
    /// hidden-desktop capture.
    /// </summary>
    internal const string ToastsVariable = "YAMV_DEV_TOASTS";

    /// <summary>True when <see cref="ToastsVariable"/> is 1 and the run has its own data folder, so the owner's app never shows them.</summary>
    private static bool DevToastsAsked() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
        && Environment.GetEnvironmentVariable(ToastsVariable) == "1";

    /// <summary>
    /// Set to 1 in a run with its own data folder, it makes the main window open the confirm card once it has shown, for a
    /// hidden-desktop capture.
    /// </summary>
    internal const string ConfirmCardVariable = "YAMV_DEV_CONFIRM_CARD";

    /// <summary>True when <see cref="ConfirmCardVariable"/> is 1 and the run has its own data folder, so the owner's app never shows it.</summary>
    private static bool DevConfirmCardAsked() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
        && Environment.GetEnvironmentVariable(ConfirmCardVariable) == "1";

    /// <summary>
    /// Set to ok or no in a run with its own data folder, it makes the main window show that form of the drop overlay once it has
    /// shown, for a hidden-desktop capture.
    /// </summary>
    internal const string DropOverlayVariable = "YAMV_DEV_DROP_OVERLAY";

    /// <summary>The form <see cref="DropOverlayVariable"/> asks for, in a run with its own data folder, so the owner's app never shows it.</summary>
    private static DevDropOverlay DevDropOverlayAsked()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
        {
            return DevDropOverlay.None;
        }

        return Environment.GetEnvironmentVariable(DropOverlayVariable) switch
        {
            "ok" => DevDropOverlay.Open,
            "no" => DevDropOverlay.Refused,
            _ => DevDropOverlay.None,
        };
    }

    /// <summary>
    /// Set to 1 in a run with its own data folder, it makes every window show the Administrator mark as an elevated instance's do,
    /// for a hidden-desktop capture; the run keeps the normal lock.
    /// </summary>
    internal const string ElevatedVariable = "YAMV_DEV_ELEVATED";

    /// <summary>True when <see cref="ElevatedVariable"/> is 1 and the run has its own data folder, so the owner's app never shows it.</summary>
    internal static bool DevElevatedAsked() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable))
        && Environment.GetEnvironmentVariable(ElevatedVariable) == "1";

    /// <summary>Creates the one theme bridge the app applies through: the catalog over the data folder's themes.</summary>
    /// <param name="application">The application whose resources and theme variant the bridge writes.</param>
    /// <param name="log">The process's log.</param>
    public static ThemeBridge CreateBridge(Application application, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(log);
        return new ThemeBridge(new ThemeCatalog(AppPaths.ThemesDirectory, log), application, log);
    }

    /// <summary>
    /// Loads the session, opens its documents in a persisting window state, restores the sidebar width and window bounds, and
    /// returns the window with what it owns, including the <see cref="MainWindowSession"/> that saves the session back. The
    /// theme is the app's: the window leaves the bridge's theme as it was. An elevated instance's main window restores the session
    /// the same way but saves nothing, like a viewer window.
    /// </summary>
    /// <param name="bridge">The app's theme bridge, which the window registers its document view with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="log">The process's log.</param>
    /// <param name="elevated">Whether the window is the elevated (Administrator) instance's: it shows the mark and saves nothing.</param>
    public static MainWindowComposition Create(ThemeBridge bridge, WidthStore widths, IAppLog log, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(log);
        var changes = new DocumentChanges(log);
        try
        {
            return Create(bridge, widths, log, changes, elevated);
        }
        catch
        {
            changes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the main window as <see cref="Create(ThemeBridge, WidthStore, IAppLog, bool)"/> does, following its documents'
    /// changes through <paramref name="changes"/>: a test supplies a fake in place of a file watcher. The window's view model owns it.
    /// </summary>
    /// <param name="bridge">The app's theme bridge, which the window registers its document view with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="log">The process's log.</param>
    /// <param name="changes">What reports the window's documents' changes on disk.</param>
    /// <param name="elevated">Whether the window is the elevated (Administrator) instance's: it shows the mark and saves nothing.</param>
    internal static MainWindowComposition Create(ThemeBridge bridge, WidthStore widths, IAppLog log, IDocumentChanges changes, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(changes);
        var store = new SessionStore(AppPaths.SessionFile, log);
        SessionState session = store.Load();
        var view = new DocumentView(log, elevated);
        bridge.Register(view);
        DocumentWindowViewModel? viewModel = null;
        try
        {
            var finder = new GitRootFinder(log);
            var state = new DocumentWindowState(persist: true, finder.FindWorkTreeRoot, File.Exists, GroupingOptions.ForCurrentPlatform);
            var services = new DocumentWindowServices(
                widths,
                DocumentText.Read,
                TimeProvider.System,
                log,
                new ToastCenter(TimeProvider.System),
                changes
            )
            {
                Elevated = elevated,
            };
            viewModel = new DocumentWindowViewModel(state, view, services);
            viewModel.Restore(session);
            var window = new MainWindow(viewModel, view, view, log)
            {
                ToastsWhenShown = DevToastsAsked(),
                DropOverlayWhenShown = DevDropOverlayAsked(),
                ConfirmCardWhenShown = DevConfirmCardAsked(),
            };
            RestoreLayout(window, session, log);
            MainWindowSession? saver = elevated ? null : new MainWindowSession(window, viewModel, store, TimeProvider.System, log);
            return new MainWindowComposition(window, viewModel, bridge, view, saver);
        }
        catch
        {
            viewModel?.Dispose();
            changes.Dispose();
            bridge.Unregister(view);
            throw;
        }
    }

    /// <summary>
    /// Builds a viewer window over <paramref name="files"/>: a window that neither reads nor saves a session, leaves the
    /// bridge's theme as it was, restores no layout, and opens the files as they are, a missing path included.
    /// </summary>
    /// <param name="bridge">The app's theme bridge, which the window registers its document view with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="files">The full paths the window shows.</param>
    /// <param name="log">The process's log.</param>
    /// <param name="elevated">Whether the window is the elevated (Administrator) instance's, so it shows the mark.</param>
    public static MainWindowComposition CreateViewer(ThemeBridge bridge, WidthStore widths, IReadOnlyList<string> files, IAppLog log, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(log);
        var view = new DocumentView(log, elevated);
        var changes = new DocumentChanges(log);
        try
        {
            return CreateViewer(bridge, widths, files, log, new ViewerParts(view, view, changes, elevated));
        }
        catch
        {
            changes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds a viewer window that draws its documents on the surface of <paramref name="parts"/> and follows their changes
    /// through its changes seam: a test supplies a fake surface, whose renders complete headless, in place of a web view, and
    /// a fake in place of a file watcher. The window's view model owns the changes seam.
    /// </summary>
    /// <param name="bridge">The app's theme bridge, which the window registers its document surface with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="files">The full paths the window shows.</param>
    /// <param name="log">The process's log.</param>
    /// <param name="parts">The control that draws the document, the view model's surface and what reports the changes.</param>
    internal static MainWindowComposition CreateViewer(
        ThemeBridge bridge,
        WidthStore widths,
        IReadOnlyList<string> files,
        IAppLog log,
        ViewerParts parts
    )
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(parts);
        (Control documentView, IDocumentSurface surface, IDocumentChanges changes, bool elevated) = parts;
        ArgumentNullException.ThrowIfNull(documentView);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(changes);
        bridge.Register(surface);
        DocumentWindowViewModel? viewModel = null;
        try
        {
            var finder = new GitRootFinder(log);
            var state = new DocumentWindowState(persist: false, finder.FindWorkTreeRoot, File.Exists, GroupingOptions.ForCurrentPlatform);
            var services = new DocumentWindowServices(
                widths,
                DocumentText.Read,
                TimeProvider.System,
                log,
                new ToastCenter(TimeProvider.System),
                changes
            )
            {
                Elevated = elevated,
            };
            viewModel = new DocumentWindowViewModel(state, surface, services);
            viewModel.Open(ExistingFirst(files));
            var window = new MainWindow(viewModel, documentView, surface, log);
            return new MainWindowComposition(window, viewModel, bridge, surface, session: null);
        }
        catch
        {
            viewModel?.Dispose();
            changes.Dispose();
            bridge.Unregister(surface);
            throw;
        }
    }

    /// <summary>
    /// Applies the saved layout to <paramref name="window"/> before it shows: the sidebar width, and the window bounds fitted to
    /// the screens' working areas in physical pixels, the primary screen first. With no saved bounds, or no screens known, the
    /// window keeps Avalonia's default placement.
    /// </summary>
    /// <param name="window">The window to place.</param>
    /// <param name="session">The saved session.</param>
    /// <param name="log">Where a failed frame measurement is reported.</param>
    internal static void RestoreLayout(MainWindow window, SessionState session, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        window.SidebarWidth = session.SidebarWidth;
        if (session.Window is not { } saved || window.Screens is not { } screens)
        {
            return;
        }

        List<ScreenArea> areas =
        [
            .. screens
                .All.OrderByDescending(screen => screen.IsPrimary)
                .Select(screen => new ScreenArea(
                    screen.WorkingArea.X,
                    screen.WorkingArea.Y,
                    screen.WorkingArea.Width,
                    screen.WorkingArea.Height,
                    screen.Scaling
                )),
        ];
        if (areas.Count > 0)
        {
            bool warned = false;
            window.RestoreBounds(WindowPlacement.Fit(saved, areas, FrameAt));

            // On Windows the client starts just right of and level with the outer rectangle's corner, with a 1 px visible
            // outline along its left, right and bottom; on macOS under the full-size content view it starts at the corner.
            WindowFrame FrameAt(double scaling)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return WindowFrame.None;
                }

                if (WindowsFrame.At(scaling, out int error) is { } frame)
                {
                    return frame;
                }

                if (!warned)
                {
                    log.LogWarning($"Window placement: AdjustWindowRectExForDpi failed (error {error}); using an 8 px border.");
                    warned = true;
                }

                return new WindowFrame(ClientLeft: Math.Round(8 * scaling), ClientTop: 0, Outline: 1);
            }
        }
    }

    /// <summary>
    /// <paramref name="files"/> with the first one that exists moved to the front, so the window selects it; the others keep
    /// their order. A window selects the first path it is given, and the tree orders its rows itself.
    /// </summary>
    private static List<string> ExistingFirst(IReadOnlyList<string> files)
    {
        List<string> ordered = [.. files];
        int existing = ordered.FindIndex(File.Exists);
        if (existing > 0)
        {
            string path = ordered[existing];
            ordered.RemoveAt(existing);
            ordered.Insert(0, path);
        }

        return ordered;
    }
}

/// <summary>The parts a viewer window is built over, which a test replaces with fakes.</summary>
/// <param name="DocumentView">The control that draws the selected document.</param>
/// <param name="Surface">The view model's surface.</param>
/// <param name="Changes">What reports the window's documents' changes on disk; the window's view model owns it.</param>
/// <param name="Elevated">Whether the window is the elevated (Administrator) instance's, so it shows the mark.</param>
internal sealed record ViewerParts(Control DocumentView, IDocumentSurface Surface, IDocumentChanges Changes, bool Elevated);

/// <summary>
/// The window and the objects that live as long as it: its view model, the theme bridge it registered with, and the session
/// saver of a main window.
/// </summary>
/// <param name="window">The window.</param>
/// <param name="viewModel">The window's view model.</param>
/// <param name="bridge">The app's theme bridge, which the window's document view is registered with.</param>
/// <param name="surface">The document surface to unregister from the bridge when the window closes.</param>
/// <param name="session">What saves the window's session; null for a viewer window, which saves nothing.</param>
public sealed class MainWindowComposition(
    MainWindow window,
    DocumentWindowViewModel viewModel,
    ThemeBridge bridge,
    IDocumentSurface surface,
    MainWindowSession? session
) : IDisposable
{
    /// <summary>The window.</summary>
    public MainWindow Window { get; } = window;

    /// <summary>The window's view model.</summary>
    public DocumentWindowViewModel ViewModel { get; } = viewModel;

    /// <summary>What saves the window's session; null for a window that saves nothing (a viewer, an elevated instance's main window).</summary>
    internal MainWindowSession? Session { get; } = session;

    /// <summary>
    /// Saves what changed in the session since the last save on a main window, then stops the view model's timers and
    /// unregisters the document surface; the bridge itself stays with the app.
    /// </summary>
    public void Dispose()
    {
        Session?.Dispose();
        ViewModel.Dispose();
        bridge.Unregister(surface);
    }
}
