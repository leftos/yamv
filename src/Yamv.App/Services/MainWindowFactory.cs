using Avalonia;
using Avalonia.Controls;
using Yamv.App.Controls;
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
    /// Loads the session, applies its theme to <paramref name="application"/>, opens its documents in a persisting window
    /// state, restores the sidebar width and window bounds, and returns the window with what it owns, including the
    /// <see cref="MainWindowSession"/> that saves the session back.
    /// </summary>
    /// <param name="application">The application whose resources the theme is written into.</param>
    /// <param name="bridge">The app's theme bridge, which the window registers its document view with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="log">The process's log.</param>
    public static MainWindowComposition Create(Application application, ThemeBridge bridge, WidthStore widths, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(log);
        var store = new SessionStore(AppPaths.SessionFile, log);
        SessionState session = store.Load();
        bridge.Apply(session.ThemeId, session.Appearance);
        var view = new DocumentView(log);
        bridge.Register(view);
        DocumentWindowViewModel? viewModel = null;
        try
        {
            var finder = new GitRootFinder(log);
            var state = new DocumentWindowState(persist: true, finder.FindWorkTreeRoot, File.Exists, GroupingOptions.ForCurrentPlatform);
            viewModel = new DocumentWindowViewModel(state, view, new DocumentWindowServices(widths, File.ReadAllText, TimeProvider.System, log));
            viewModel.Restore(session);
            var window = new MainWindow(viewModel, view, view, log);
            RestoreLayout(window, session);
            var saver = new MainWindowSession(window, viewModel, bridge, store, TimeProvider.System, log);
            return new MainWindowComposition(window, viewModel, bridge, view, saver);
        }
        catch
        {
            viewModel?.Dispose();
            bridge.Unregister(view);
            throw;
        }
    }

    /// <summary>
    /// Builds a viewer window over <paramref name="files"/>: a window that saves no session, reads the session only for its
    /// theme, restores no layout, and opens the files as they are, a missing path included.
    /// </summary>
    /// <param name="application">The application whose resources the theme is written into.</param>
    /// <param name="bridge">The app's theme bridge, which the window registers its document view with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="files">The full paths the window shows.</param>
    /// <param name="log">The process's log.</param>
    public static MainWindowComposition CreateViewer(
        Application application,
        ThemeBridge bridge,
        WidthStore widths,
        IReadOnlyList<string> files,
        IAppLog log
    )
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(log);
        var view = new DocumentView(log);
        return CreateViewer(application, bridge, widths, files, log, view, view);
    }

    /// <summary>
    /// Builds a viewer window that draws its documents on <paramref name="surface"/>: a test supplies a fake surface, whose
    /// renders complete headless, in place of a web view.
    /// </summary>
    /// <param name="application">The application whose resources the theme is written into.</param>
    /// <param name="bridge">The app's theme bridge, which the window registers its document surface with.</param>
    /// <param name="widths">The app's one width store, which gives each document its content width.</param>
    /// <param name="files">The full paths the window shows.</param>
    /// <param name="log">The process's log.</param>
    /// <param name="documentView">The control that draws the selected document.</param>
    /// <param name="surface">The view model's surface.</param>
    internal static MainWindowComposition CreateViewer(
        Application application,
        ThemeBridge bridge,
        WidthStore widths,
        IReadOnlyList<string> files,
        IAppLog log,
        Control documentView,
        IDocumentSurface surface
    )
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(documentView);
        ArgumentNullException.ThrowIfNull(surface);
        var store = new SessionStore(AppPaths.SessionFile, log);
        SessionState session = store.LoadReadOnly();
        bridge.Apply(session.ThemeId, session.Appearance);
        bridge.Register(surface);
        DocumentWindowViewModel? viewModel = null;
        try
        {
            var finder = new GitRootFinder(log);
            var state = new DocumentWindowState(persist: false, finder.FindWorkTreeRoot, File.Exists, GroupingOptions.ForCurrentPlatform);
            viewModel = new DocumentWindowViewModel(state, surface, new DocumentWindowServices(widths, File.ReadAllText, TimeProvider.System, log));
            viewModel.Open(ExistingFirst(files));
            var window = new MainWindow(viewModel, documentView, surface, log);
            return new MainWindowComposition(window, viewModel, bridge, surface, session: null);
        }
        catch
        {
            viewModel?.Dispose();
            bridge.Unregister(surface);
            throw;
        }
    }

    /// <summary>
    /// Applies the saved layout to <paramref name="window"/> before it shows: the sidebar width, and the window bounds fitted to
    /// the screens' working areas in physical pixels, the primary screen first. With no saved bounds, or no screens known, the
    /// window keeps Avalonia's default placement.
    /// </summary>
    internal static void RestoreLayout(MainWindow window, SessionState session)
    {
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
            window.RestoreBounds(WindowPlacement.Fit(saved, areas));
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

    /// <summary>
    /// Saves what changed in the session since the last save on a main window, then stops the view model's timers and
    /// unregisters the document surface; the bridge itself stays with the app.
    /// </summary>
    public void Dispose()
    {
        session?.Dispose();
        ViewModel.Dispose();
        bridge.Unregister(surface);
    }
}
