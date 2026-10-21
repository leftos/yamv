using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yamv.App.Services;
using Yamv.App.Views;

namespace Yamv.App;

public partial class App : Application
{
    /// <summary>The environment variable that, set to <c>1</c>, starts the dev harness instead of the main window.</summary>
    public const string HarnessVariable = "YAMV_DEV_HARNESS";

    private MainWindowComposition? _mainWindow;

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

    /// <summary>The window a start shows: the dev harness when <c>YAMV_DEV_HARNESS</c> is <c>1</c>, else the main window.</summary>
    internal Window CreateStartWindow()
    {
        if (Environment.GetEnvironmentVariable(HarnessVariable) == "1")
        {
            return new HarnessWindow();
        }

        ReleaseMainWindow();
        _mainWindow = MainWindowFactory.Create(this);
        return _mainWindow.Window;
    }

    /// <summary>Disposes what the main window owns, once the app exits.</summary>
    internal void ReleaseMainWindow()
    {
        _mainWindow?.Dispose();
        _mainWindow = null;
    }
}
