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
    /// state and returns the window with what it owns. Nothing is saved back: the session saver is not wired here.
    /// </summary>
    /// <param name="application">The application whose resources the theme is written into.</param>
    public static MainWindowComposition Create(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var log = new FileAppLog(AppPaths.LogFile);
        SessionState session = new SessionStore(AppPaths.SessionFile, log).Load();
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
                return new MainWindowComposition(new MainWindow(viewModel, view, log), viewModel, bridge);
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
}

/// <summary>The main window and the objects that live as long as it: its view model and the theme bridge.</summary>
/// <param name="window">The window.</param>
/// <param name="viewModel">The window's view model.</param>
/// <param name="bridge">The theme bridge that colours the app and the window's document view.</param>
public sealed class MainWindowComposition(MainWindow window, DocumentWindowViewModel viewModel, ThemeBridge bridge) : IDisposable
{
    /// <summary>The window.</summary>
    public MainWindow Window { get; } = window;

    /// <summary>The window's view model.</summary>
    public DocumentWindowViewModel ViewModel { get; } = viewModel;

    /// <summary>Stops the view model's timers and the bridge's following of the platform theme.</summary>
    public void Dispose()
    {
        ViewModel.Dispose();
        bridge.Dispose();
    }
}
