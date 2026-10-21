using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Launch;
using Yamv.Core.Session;
using Yamv.Core.Theming;
using Yamv.Core.Widths;

namespace Yamv.App.Tests;

/// <summary>Tests that change process-wide environment variables, so they never run beside other tests.</summary>
[CollectionDefinition(nameof(EnvironmentVariables), DisableParallelization = true)]
public sealed class EnvironmentVariables;

[Collection(nameof(EnvironmentVariables))]
public sealed class AppStartupTests
{
    [Fact]
    public Task App_starts_headless_with_fluent_theme() =>
        Headless.Run(() =>
        {
            Application? application = Application.Current;

            Assert.NotNull(application);
            Assert.IsType<App>(application);
            Assert.Contains(application.Styles, style => style is FluentTheme);
        });

    [Fact]
    public Task Start_window_without_harness_variable_is_main_window_over_document_view_model() =>
        Headless.Run(() =>
        {
            string dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataFolder);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            App app = Assert.IsType<App>(Application.Current);
            try
            {
                Window window = app.CreateStartWindow();

                MainWindow main = Assert.IsType<MainWindow>(window);
                DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(main.DataContext);
                Assert.True(viewModel.IsEmpty);
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, recursive: true);
                }
            }
        });

    [Fact]
    public Task Start_window_with_harness_and_theme_settings_variables_is_theme_settings_window() =>
        Headless.Run(() =>
        {
            string dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            string? settingsBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsHarnessVariable);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataFolder);
            Environment.SetEnvironmentVariable(App.HarnessVariable, "1");
            Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, "1");
            App app = Assert.IsType<App>(Application.Current);
            try
            {
                Window window = app.CreateStartWindow();

                ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(window);
                ThemeSettingsViewModel viewModel = Assert.IsType<ThemeSettingsViewModel>(settings.DataContext);
                Assert.Equal("studio", viewModel.Selected?.Id);
                Assert.Equal("system", viewModel.SelectedAppearance.Value);
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, recursive: true);
                }
            }
        });

    [Fact]
    public Task Theme_settings_harness_without_a_data_folder_refuses_to_start() =>
        Headless.Run(() =>
        {
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            string? settingsBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsHarnessVariable);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, null);
            Environment.SetEnvironmentVariable(App.HarnessVariable, "1");
            Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, "1");
            App app = Assert.IsType<App>(Application.Current);
            try
            {
                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => app.CreateStartWindow());

                Assert.Equal("The theme settings harness needs YAMV_DATA_DIR set.", refused.Message);
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
            }
        });

    [Fact]
    public Task Start_window_with_viewer_files_is_a_viewer_window_that_writes_no_session() =>
        Headless.Run(() =>
        {
            string dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
            string missing = Path.Combine(dataFolder, "gone.md");
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataFolder);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            App.ViewerFiles = [missing];
            App app = Assert.IsType<App>(Application.Current);
            try
            {
                Window window = app.CreateStartWindow();

                MainWindow main = Assert.IsType<MainWindow>(window);
                DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(main.DataContext);
                Assert.False(viewModel.IsEmpty);
                Assert.False(viewModel.ShowTree);
                Assert.True(viewModel.SelectedIsMissing);

                viewModel.ToggleSidebar();

                app.ReleaseMainWindow();
                app.ResetLog();

                Assert.False(File.Exists(Path.Combine(dataFolder, "session.json")));
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                App.ViewerFiles = [];
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, recursive: true);
                }
            }
        });

    [Fact]
    public Task Releasing_the_app_disposes_the_bridge_it_created() =>
        Headless.Run(() =>
        {
            string dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataFolder);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            App.ViewerFiles = [];
            App app = Assert.IsType<App>(Application.Current);
            ThemeBridge? bridge = null;
            try
            {
                app.CreateStartWindow();
                bridge = app.Bridge;
                Assert.NotNull(bridge);

                app.ReleaseMainWindow();
                app.ResetLog();

                Assert.Null(app.Bridge);
                Assert.True(IsBridgeDisposed(bridge!));
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                App.ViewerFiles = [];
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, recursive: true);
                }
            }
        });

    [Fact]
    public Task Disposing_a_window_composition_unregisters_its_surface_and_leaves_the_bridge() =>
        Headless.Run(() =>
        {
            string themes = Path.Combine(Path.GetTempPath(), "yamv-themes-" + Guid.NewGuid().ToString("N"));
            var log = new RecordingAppLog();
            var bridge = new ThemeBridge(new ThemeCatalog(themes, log), Application.Current!, log);
            var surface = new FakeDocumentSurface();
            var state = new DocumentWindowState(false, _ => null, _ => true, new GroupingOptions(null, StringComparison.OrdinalIgnoreCase));
            var widths = new WidthStore(Path.Combine(themes, "widths.json"), log);
            var viewModel = new DocumentWindowViewModel(state, surface, new DocumentWindowServices(widths, _ => "", new FakeTimeProvider(), log));
            var window = new MainWindow(viewModel, new Border(), surface, log);
            var composition = new MainWindowComposition(window, viewModel, bridge, surface, session: null);
            try
            {
                bridge.Apply("studio", "light");
                bridge.Register(surface);
                Assert.Single(surface.ThemeCss);

                composition.Dispose();

                bridge.Apply("studio", "dark");
                Assert.Single(surface.ThemeCss);
                Assert.False(IsBridgeDisposed(bridge));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }

                bridge.Dispose();
                if (Directory.Exists(themes))
                {
                    Directory.Delete(themes, recursive: true);
                }
            }
        });

    [Fact]
    public Task MainStart_MarksTheGateReadyOnFirstOpened() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;

            start.Gate.Request();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, start.BroughtForwardCount);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.BroughtForwardCount);

            window.Hide();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.BroughtForwardCount);
        });

    [Fact]
    public Task Activate_UnminimizesTheMainWindow() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;

            start.Gate.Request();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(1, start.BroughtForwardCount);
        });

    [Fact]
    public Task ViewerStart_NeverTouchesTheGate() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(
                viewerFiles: [Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md")],
                stopAnswering: null
            );
            int raised = 0;
            start.Gate.Activate += (_, _) => raised++;
            start.Window.Show();
            Dispatcher.UIThread.RunJobs();

            start.Gate.Request();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, raised);
            Assert.DoesNotContain(start.Log.Entries, entry => entry.Message.StartsWith("Activate:", StringComparison.Ordinal));
        });

    [Fact]
    public Task MainClose_StopsAnsweringBeforeTheSessionIsSaved() =>
        Headless.Run(() =>
        {
            List<string> order = [];
            Window? main = null;
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => StopAnsweringAndHideSidebar(main!, order));
            main = start.Window;
            main.Show();
            Dispatcher.UIThread.RunJobs();

            main.Close();
            Dispatcher.UIThread.RunJobs();
            order.Add("closed");
            start.App.ReleaseMainWindow();

            // The sidebar was hidden inside the stop action: only a session flush that came after it saved that.
            Assert.Equal(["stopped answering", "closed"], order);
            Assert.True(start.LoadSavedSession().SidebarHidden);
        });

    [Fact]
    public Task ReleaseWithoutClosed_StillStopsAnsweringBeforeTheFlush() =>
        Headless.Run(() =>
        {
            List<string> order = [];
            Window? main = null;
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => StopAnsweringAndHideSidebar(main!, order));
            main = start.Window;
            main.Show();
            Dispatcher.UIThread.RunJobs();

            start.App.ReleaseMainWindow();

            Assert.Equal(["stopped answering"], order);
            Assert.True(start.LoadSavedSession().SidebarHidden);
        });

    [Fact]
    public Task Activate_AfterTheMainWindowClosed_LogsNoMainWindow() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            Dispatcher.UIThread.RunJobs();

            start.Gate.Request();
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, start.BroughtForwardCount);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: no main window to bring forward.");
        });

    [Fact]
    public Task MainClose_AfterASecondStart_RunsNeitherStopActionAgain() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("first"));
            Window first = start.Window;
            first.Show();
            Dispatcher.UIThread.RunJobs();

            App.StopAnswering = () => calls.Add("second");
            Window second = start.App.CreateStartWindow();
            try
            {
                second.Show();
                Dispatcher.UIThread.RunJobs();
                first.Close();
                Dispatcher.UIThread.RunJobs();

                start.Gate.Request();
                Dispatcher.UIThread.RunJobs();

                // Releasing the first start for the second one ran the first's action; closing its window afterwards runs
                // nothing, and leaves the second start's window to be brought forward.
                Assert.Equal(["first"], calls);
                Assert.Equal(1, start.BroughtForwardCount);
                Assert.DoesNotContain(start.Log.Entries, entry => entry.Message == "Activate: no main window to bring forward.");
            }
            finally
            {
                second.Close();
            }
        });

    /// <summary>Records the stop and changes what the session stores, so a later flush is the only way that change reaches disk.</summary>
    private static void StopAnsweringAndHideSidebar(Window main, List<string> order)
    {
        order.Add("stopped answering");
        Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();
    }

    private static bool IsBridgeDisposed(ThemeBridge bridge) =>
        (bool)typeof(ThemeBridge).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;

    /// <summary>
    /// A start with an activation gate and a recording process log handed to <see cref="App"/>, over a fresh data folder;
    /// disposing it closes the start window and restores the statics and environment.
    /// </summary>
    private sealed class GateStart : IDisposable
    {
        private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
        private readonly string? _dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        private readonly string? _harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);

        public GateStart(IReadOnlyList<string> viewerFiles, Action? stopAnswering)
        {
            App = Assert.IsType<App>(Application.Current);
            try
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataFolder);
                Environment.SetEnvironmentVariable(App.HarnessVariable, null);
                App.ViewerFiles = viewerFiles;
                App.ProcessLog = Log;
                App.Activation = Gate;
                App.StopAnswering = stopAnswering;
                App.ResetLog();
                Window = App.CreateStartWindow();
            }
            catch
            {
                try
                {
                    App.ReleaseMainWindow();
                }
                finally
                {
                    Restore();
                }

                throw;
            }
        }

        public App App { get; }

        public Window Window { get; }

        public ActivationGate Gate { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public int BroughtForwardCount => Log.Entries.Count(entry => entry.Message == "Activate: main window brought forward.");

        /// <summary>The session the start's data folder holds now.</summary>
        public SessionState LoadSavedSession() => new SessionStore(Path.Combine(_dataFolder, "session.json"), Log).Load();

        public void Dispose()
        {
            try
            {
                if (Window.IsVisible)
                {
                    Window.Close();
                }

                App.ReleaseMainWindow();
            }
            finally
            {
                Restore();
            }
        }

        private void Restore()
        {
            App.ResetLog();
            App.ViewerFiles = [];
            App.ProcessLog = null;
            App.Activation = null;
            App.StopAnswering = null;
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataBefore);
            Environment.SetEnvironmentVariable(App.HarnessVariable, _harnessBefore);
            if (Directory.Exists(_dataFolder))
            {
                Directory.Delete(_dataFolder, recursive: true);
            }
        }
    }
}
