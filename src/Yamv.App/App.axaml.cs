using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yamv.App.Services;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;

namespace Yamv.App;

public partial class App : Application
{
    /// <summary>The environment variable that, set to <c>1</c>, starts the dev harness instead of the main window.</summary>
    public const string HarnessVariable = "YAMV_DEV_HARNESS";

    /// <summary>
    /// The environment variable that, set to <c>1</c> beside <see cref="HarnessVariable"/>, starts the theme settings window
    /// instead of the dev harness.
    /// </summary>
    public const string ThemeSettingsHarnessVariable = "YAMV_DEV_THEME_SETTINGS";

    private MainWindowComposition? _mainWindow;
    private ThemeBridge? _bridge;
    private ThemeBridge? _harnessBridge;
    private IAppLog? _log;

    /// <summary>The files a viewer launch opens: empty for a main launch. Set before Avalonia starts.</summary>
    internal static IReadOnlyList<string> ViewerFiles { get; set; } = [];

    /// <summary>The one theme bridge the app applies through; null before the first window is built and after it is released.</summary>
    internal ThemeBridge? Bridge => _bridge;

    /// <summary>The process's one log, made when the first window is built.</summary>
    private IAppLog Log => _log ??= new FileAppLog(AppPaths.LogFile);

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
        _mainWindow =
            ViewerFiles.Count == 0 ? MainWindowFactory.Create(this, bridge, log) : MainWindowFactory.CreateViewer(this, bridge, ViewerFiles, log);
        return _mainWindow.Window;
    }

    /// <summary>Disposes what the start window owns, and the bridge the app owns, once the app exits.</summary>
    internal void ReleaseMainWindow()
    {
        _mainWindow?.Dispose();
        _mainWindow = null;
        _bridge?.Dispose();
        _bridge = null;
        _harnessBridge?.Dispose();
        _harnessBridge = null;
    }

    /// <summary>
    /// The theme settings window over the data folder's themes, with Studio applied to follow the system, a trash that deletes
    /// outright inside a dev data folder, and Edit as text written to the log. It refuses to start without
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
        var fileActions = new AvaloniaFileActions(log, AvaloniaFileActions.StartProcess);
        var seams = new ThemeSettingsSeams
        {
            Apply = bridge.Apply,
            Trash = new HarnessTrash(),
            OpenAsText = path => log.LogInfo($"Harness: edit as text {path}"),
            ConfirmUnsaved = name => window!.ConfirmUnsavedAsync(name),
            ConfirmDelete = name => window!.ConfirmDeleteAsync(name),
            PickImport = () => fileActions.PickThemeAsync(window!),
            PickExport = suggested => fileActions.PickThemeDestinationAsync(window!, suggested),
            ConfirmImport = _ => Task.FromResult(true),
        };
        window = new ThemeSettingsWindow(new ThemeSettingsViewModel(catalog, seams, ThemeCatalog.DefaultThemeId, "system", log), log);
        window.Opened += (_, _) => log.LogInfo("Window: shown");
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
