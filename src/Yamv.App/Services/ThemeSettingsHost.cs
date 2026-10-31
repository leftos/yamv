using Avalonia.Controls;
using Yamv.App.Platform;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;

namespace Yamv.App.Services;

/// <summary>What the theme settings host reaches the platform through: the trash per window, and the pickers and the editor.</summary>
internal sealed class ThemeSettingsHostSeams
{
    /// <summary>The trash of a settings window, given a getter of that window, which is built after its trash.</summary>
    public required Func<Func<Window>, IFileTrash> Trash { get; init; }

    /// <summary>The Import and Export pickers, and the editor Edit as text opens a file in.</summary>
    public required IFileActions FileActions { get; init; }

    /// <summary>Given the settings window's owner that is closing, the document window it moves to, or null when none remains.</summary>
    public required Func<Window, Window?> OwnerSuccessor { get; init; }
}

/// <summary>What <see cref="ThemeSettingsHost.BuildWindow"/> builds a theme settings window from.</summary>
internal sealed class ThemeSettingsWindowParts
{
    /// <summary>The themes the window lists and edits.</summary>
    public required ThemeCatalog Catalog { get; init; }

    /// <summary>Makes a theme the app's theme: its id and the appearance.</summary>
    public required Action<string, string> Apply { get; init; }

    /// <summary>The id of the theme in use when the window opens.</summary>
    public required string ThemeId { get; init; }

    /// <summary>The appearance in use when the window opens: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public required string Appearance { get; init; }

    /// <summary>Where the window and its view model report failures.</summary>
    public required IAppLog Log { get; init; }

    /// <summary>The trash, given a getter of the window it serves, which is built after its trash.</summary>
    public required Func<Func<Window>, IFileTrash> Trash { get; init; }

    /// <summary>Opens a theme file in the user's editor.</summary>
    public required Action<string> OpenAsText { get; init; }

    /// <summary>Asks for a theme file to import, over the window given; null when the pick was cancelled.</summary>
    public required Func<Window, Task<string?>> PickImport { get; init; }

    /// <summary>The Export picker.</summary>
    public required IFileActions FileActions { get; init; }
}

/// <summary>
/// The app's one theme settings window: <see cref="Show"/> builds it over the bridge's requested theme and Appearance, shown over
/// the window that asked and centred over it, or activates the open one; when its owner closes while another document window
/// stays open, it moves to that window (<see cref="ThemeSettingsHostSeams.OwnerSuccessor"/>). While it is open, a reload
/// of the themes folder and a change of the requested theme the window did not make (an edit of the settings file) reach its
/// view model. The owner's close and the app's shutdown wait on its unsaved-edits confirm: the window's own close check holds an
/// owner's close, and <see cref="ConfirmShutdownAsync"/> is the shutdown's. Every member is called on the UI thread.
/// </summary>
internal sealed class ThemeSettingsHost : IDisposable
{
    private readonly ThemeBridge _bridge;
    private readonly ThemeCatalog _catalog;
    private readonly IAppLog _log;
    private readonly ThemeSettingsHostSeams _seams;
    private ThemeSettingsWindow? _window;
    private ThemeSettingsViewModel? _viewModel;
    private bool _disposed;

    /// <summary>Creates a host that builds its windows over <paramref name="catalog"/> and applies through <paramref name="bridge"/>.</summary>
    /// <param name="bridge">The app's bridge: the theme in use, and where the window applies.</param>
    /// <param name="catalog">The themes the window lists and edits.</param>
    /// <param name="log">Where the window reports failures.</param>
    /// <param name="seams">The trash per window, the pickers and the editor.</param>
    public ThemeSettingsHost(ThemeBridge bridge, ThemeCatalog catalog, IAppLog log, ThemeSettingsHostSeams seams)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(seams);
        _bridge = bridge;
        _catalog = catalog;
        _log = log;
        _seams = seams;
        bridge.ThemesReloaded += OnThemesReloaded;
        bridge.RequestChanged += OnRequestChanged;
    }

    /// <summary>The open settings window, or null.</summary>
    internal ThemeSettingsWindow? Window => _window;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// The app's host over the data folder's themes, with the platform's trash owned by the settings window, and the system's
    /// pickers and default editor.
    /// </summary>
    /// <param name="bridge">The app's bridge: the theme in use, and where the window applies.</param>
    /// <param name="log">Where the window reports failures.</param>
    /// <param name="ownerSuccessor">Given the settings window's owner that is closing, the document window it moves to, or null.</param>
    public static ThemeSettingsHost Create(ThemeBridge bridge, IAppLog log, Func<Window, Window?> ownerSuccessor)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(ownerSuccessor);
        var seams = new ThemeSettingsHostSeams
        {
            Trash = windowOf => new PlatformTrash(() => windowOf().TryGetPlatformHandle()?.Handle ?? 0),
            FileActions = new AvaloniaFileActions(log, null, AvaloniaFileActions.StartProcess, FileActionSeams.Default),
            OwnerSuccessor = ownerSuccessor,
        };
        return new ThemeSettingsHost(bridge, new ThemeCatalog(AppPaths.ThemesDirectory, log), log, seams);
    }

    /// <summary>
    /// Builds a theme settings window and its view model from <paramref name="parts"/>, its confirms, Import notice and pickers
    /// shown by the window itself.
    /// </summary>
    public static (ThemeSettingsWindow Window, ThemeSettingsViewModel ViewModel) BuildWindow(ThemeSettingsWindowParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ThemeSettingsWindow? window = null;
        Window WindowOf() => window ?? throw new InvalidOperationException("The theme settings window is not built yet.");
        var seams = new ThemeSettingsSeams
        {
            Apply = parts.Apply,
            Trash = parts.Trash(WindowOf),
            OpenAsText = parts.OpenAsText,
            ConfirmUnsaved = name => window!.ConfirmUnsavedAsync(name),
            ConfirmDelete = name => window!.ConfirmDeleteAsync(name),
            PickImport = () => parts.PickImport(WindowOf()),
            PickExport = suggested => parts.FileActions.PickThemeDestinationAsync(WindowOf(), suggested),
            ConfirmImport = inspection => window!.ConfirmImportAsync(inspection),
        };
        var viewModel = new ThemeSettingsViewModel(parts.Catalog, seams, parts.ThemeId, parts.Appearance, parts.Log);
        window = new ThemeSettingsWindow(viewModel, parts.Log);
        return (window, viewModel);
    }

    /// <summary>
    /// Shows the settings window over <paramref name="owner"/>, centred over it; while one is open, restores it if minimised and
    /// activates it instead. When its owner closes while another document window stays open, it moves to that window.
    /// </summary>
    /// <param name="owner">The window that asked.</param>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    public void Show(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        IFileActions fileActions = _seams.FileActions;
        (ThemeSettingsWindow window, ThemeSettingsViewModel viewModel) = BuildWindow(
            new ThemeSettingsWindowParts
            {
                Catalog = _catalog,
                Apply = _bridge.Apply,
                ThemeId = _bridge.RequestedThemeId,
                Appearance = _bridge.RequestedAppearance,
                Log = _log,
                Trash = _seams.Trash,
                OpenAsText = fileActions.OpenWithDefaultApp,
                PickImport = fileActions.PickThemeAsync,
                FileActions = fileActions,
            }
        );
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.OwnerSuccessor = _seams.OwnerSuccessor;
        window.Closed += OnWindowClosed;
        try
        {
            window.Show(owner);
        }
        catch
        {
            window.Closed -= OnWindowClosed;
            throw;
        }

        _window = window;
        _viewModel = viewModel;
    }

    /// <summary>
    /// The check the app's shutdown waits on: true at once without an open window, else the window's close check, which cancels
    /// an open Import notice or confirm and asks about unsaved edits; false on Cancel or a failed save.
    /// </summary>
    public Task<bool> ConfirmShutdownAsync() => _window?.ConfirmCloseAsync() ?? Task.FromResult(true);

    /// <summary>
    /// Stops following the bridge and closes an open window without its unsaved-edits confirm, which the shutdown check has
    /// asked already; a second call does nothing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge.ThemesReloaded -= OnThemesReloaded;
        _bridge.RequestChanged -= OnRequestChanged;
        ThemeSettingsWindow? window = _window;
        Forget();
        window?.Closed -= OnWindowClosed;
        window?.ForceClose();
    }

    private void OnThemesReloaded(object? sender, EventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        viewModel.OnThemesChangedOnDisk();
        viewModel.SyncInUse(_bridge.RequestedThemeId, _bridge.RequestedAppearance);
    }

    private void OnRequestChanged(object? sender, EventArgs e) => _viewModel?.SyncInUse(_bridge.RequestedThemeId, _bridge.RequestedAppearance);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= OnWindowClosed;
        }

        Forget();
    }

    private void Forget()
    {
        _window = null;
        _viewModel = null;
    }
}
