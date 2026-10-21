using Avalonia;
using Yamv.App.Controls;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;
using Yamv.Core.Theming;

namespace Yamv.App.Services;

/// <summary>Builds the main window from the user's data folder: its log, saved session, themes and document view.</summary>
public static class MainWindowFactory
{
    /// <summary>
    /// Loads the session, applies its theme to <paramref name="application"/>, opens its documents in a persisting window
    /// state, restores the sidebar width and window bounds, and returns the window with what it owns, including the
    /// <see cref="MainWindowSession"/> that saves the session back.
    /// </summary>
    /// <param name="application">The application whose resources the theme is written into.</param>
    public static MainWindowComposition Create(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var log = new FileAppLog(AppPaths.LogFile);
        var store = new SessionStore(AppPaths.SessionFile, log);
        SessionState session = store.Load();
        var bridge = new ThemeBridge(new ThemeCatalog(AppPaths.ThemesDirectory, log), application, log);
        try
        {
            bridge.Apply(session.ThemeId, session.Appearance);
            var view = new DocumentView(log);
            bridge.Register(view);
            var finder = new GitRootFinder(log);
            var state = new DocumentWindowState(persist: true, finder.FindWorkTreeRoot, File.Exists, GroupingOptions.ForCurrentPlatform);
            var viewModel = new DocumentWindowViewModel(state, view, File.ReadAllText, TimeProvider.System, log);
            try
            {
                viewModel.Restore(session);
                var window = new MainWindow(viewModel, view, view, log);
                RestoreLayout(window, session);
                var saver = new MainWindowSession(window, viewModel, bridge, store, TimeProvider.System, log);
                return new MainWindowComposition(window, viewModel, bridge, saver);
            }
            catch
            {
                viewModel.Dispose();
                throw;
            }
        }
        catch
        {
            bridge.Dispose();
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
}

/// <summary>The main window and the objects that live as long as it: its view model, the theme bridge and the session saver.</summary>
/// <param name="window">The window.</param>
/// <param name="viewModel">The window's view model.</param>
/// <param name="bridge">The theme bridge that colours the app and the window's document view.</param>
/// <param name="session">What saves the window's session.</param>
public sealed class MainWindowComposition(MainWindow window, DocumentWindowViewModel viewModel, ThemeBridge bridge, MainWindowSession session)
    : IDisposable
{
    /// <summary>The window.</summary>
    public MainWindow Window { get; } = window;

    /// <summary>The window's view model.</summary>
    public DocumentWindowViewModel ViewModel { get; } = viewModel;

    /// <summary>
    /// Saves what changed in the session since the last save, then stops the view model's timers and the bridge's following of
    /// the platform theme.
    /// </summary>
    public void Dispose()
    {
        session.Dispose();
        ViewModel.Dispose();
        bridge.Dispose();
    }
}
