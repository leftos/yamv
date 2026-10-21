using Avalonia.Controls;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Diagnostics;
using Yamv.Core.Session;

namespace Yamv.App.Services;

/// <summary>
/// Saves the main window's session: 500 ms after a change to its documents, selection, collapsed groups, sidebar, bounds or
/// sidebar width (a splitter drag or any other route), and at once when the window closes and when this is disposed. A scroll
/// change only marks the session dirty, so it is written with the next save. Every member is called on the UI thread.
/// </summary>
public sealed class MainWindowSession : IDisposable
{
    private readonly MainWindow _window;
    private readonly DocumentWindowViewModel _viewModel;
    private readonly SessionSaver _saver;
    private bool _disposed;

    /// <summary>Starts following <paramref name="window"/> and <paramref name="viewModel"/>, saving through <paramref name="store"/>.</summary>
    /// <param name="window">The main window, whose bounds and sidebar width are saved.</param>
    /// <param name="viewModel">The window's view model, which builds the session and reports its changes.</param>
    /// <param name="store">Where the session is written.</param>
    /// <param name="time">The clock of the save delay; the app passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="log">Where a failed save is reported.</param>
    public MainWindowSession(MainWindow window, DocumentWindowViewModel viewModel, SessionStore store, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        _window = window;
        _viewModel = viewModel;
        _saver = new SessionSaver(store, time, log);
        viewModel.SessionDirty += OnSessionDirty;
        window.SavedBoundsChanged += OnLayoutChanged;
        window.SidebarWidthChanged += OnLayoutChanged;
        window.Closing += OnWindowClosing;
    }

    /// <summary>Stops following the window, saves what changed since the last save, and stops the saver.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.SessionDirty -= OnSessionDirty;
        _window.SavedBoundsChanged -= OnLayoutChanged;
        _window.SidebarWidthChanged -= OnLayoutChanged;
        _window.Closing -= OnWindowClosing;
        _saver.Flush(Snapshot());
        _saver.Dispose();
    }

    private SessionState Snapshot() => _viewModel.BuildSession(_window.SavedBounds, _window.SidebarWidth);

    private void OnSessionDirty(object? sender, SessionDirtyEventArgs e)
    {
        if (e.ScrollOnly)
        {
            _saver.MarkDirty();
        }
        else
        {
            _saver.Schedule(Snapshot());
        }
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => _saver.Schedule(Snapshot());

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e) => _saver.Flush(Snapshot());
}
