using System.Reflection;
using System.Threading.Channels;
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
using Yamv.Core.Instance;
using Yamv.Core.Launch;
using Yamv.Core.Session;
using Yamv.Core.Settings;
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
                Assert.Null(app.SettingsWriter);
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
                Assert.Null(app.SettingsWriter);
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
    public Task Start_applies_the_settings_file_to_the_bridge_and_writes_nothing_back() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.WriteSettings("""{ "themeId": "ink", "appearance": "dark" }""");
            byte[] before = File.ReadAllBytes(start.SettingsFile);

            start.App.CreateStartWindow();

            Assert.Equal("ink", start.App.Bridge!.RequestedThemeId);
            Assert.Equal("dark", start.App.Bridge.RequestedAppearance);
            Assert.True(start.App.SettingsWriter!.WhenIdle().IsCompleted);
            Assert.Equal(before, File.ReadAllBytes(start.SettingsFile));
        });

    [Fact]
    public Task Apply_on_the_apps_bridge_is_saved_to_the_settings_file() =>
        Headless.RunAsync(async () =>
        {
            using var start = new SettingsStart();
            start.App.CreateStartWindow();

            start.App.Bridge!.Apply("paper", "light");
            await start.App.SettingsWriter!.WhenIdle().WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            Assert.Equal(new AppSettings("paper", "light"), new SettingsStore(start.SettingsFile, new RecordingAppLog()).Load());
        });

    [Fact]
    public Task Start_without_a_settings_file_uses_the_defaults_and_creates_none() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();

            start.App.CreateStartWindow();

            Assert.Equal("studio", start.App.Bridge!.RequestedThemeId);
            Assert.Equal("system", start.App.Bridge.RequestedAppearance);
            Assert.True(start.App.SettingsWriter!.WhenIdle().IsCompleted);
            Assert.False(File.Exists(start.SettingsFile));
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
    public Task Activate_WithOnlyViewersOpen_BringsTheLatestViewerForward() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
            Window first = start.Window;
            first.Show();
            Dispatcher.UIThread.RunJobs();
            Window second = ShowViewer(start.App);

            second.WindowState = WindowState.Minimized;
            start.Gate.Request();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Normal, second.WindowState);

            first.Activate();
            Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsActive);
            first.WindowState = WindowState.Minimized;
            start.Gate.Request();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Normal, first.WindowState);
            Assert.Equal(2, start.Log.Entries.Count(entry => entry.Message == "Activate: viewer window brought forward."));
            Assert.Equal(0, start.BroughtForwardCount);
        });

    [Fact]
    public Task OpenViewer_TwiceMakesTwoWindowsAndOneBridge() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            ThemeBridge bridge = start.App.Bridge!;
            int surfaces = SurfaceCount(bridge);

            Window first = ShowViewer(start.App);
            Window second = ShowViewer(start.App);

            Assert.NotSame(first, second);
            Assert.Equal([first, second], start.App.ViewerWindows);
            Assert.True(first.IsVisible);
            Assert.True(second.IsVisible);
            Assert.Same(bridge, start.App.Bridge);
            Assert.Equal(surfaces + 2, SurfaceCount(bridge));
        });

    [Fact]
    public Task OpenViewer_BeginsTheRequestThenCompletesOpenedBeforeTheWindowShows() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([MissingFile()]);
            int shown = 0;

            // A refusal while the window opens ends the request only if opened was not answered before: then the launch hears it.
            using IDisposable onOpened = Window.WindowOpenedEvent.AddClassHandler<MainWindow>(
                (window, _) =>
                {
                    shown++;
                    request.Complete(InstanceReply.Refused);
                }
            );
            Assert.True(start.App.OpenViewer([MissingFile()], request));

            Assert.Equal(1, shown);
            Assert.False(request.TryBegin());
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task OpenViewer_OnAnAbandonedRequest_OpensNothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([MissingFile()]);
            await Task.Run(requests.Server.Coordinator.StopAnswering, TestContext.Current.CancellationToken);

            Assert.False(start.App.OpenViewer([MissingFile()], request));

            Assert.Empty(start.App.ViewerWindows);
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Message == "Open: an open of 1 file(s) was abandoned before the app took it; opening nothing."
            );
        });

    [Fact]
    public Task ShowNewViewer_WhenShowThrows_ClosesTheWindowAndForgetsIt() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            ThemeBridge bridge = start.App.Bridge!;
            int surfaces = SurfaceCount(bridge);
            Window? failed = null;
            int failedClosed = 0;
            using IDisposable onClosed = Window.WindowClosedEvent.AddClassHandler<MainWindow>(
                (window, _) =>
                {
                    if (ReferenceEquals(window, failed))
                    {
                        failedClosed++;
                    }
                }
            );
            using (
                Window.WindowOpenedEvent.AddClassHandler<MainWindow>(
                    (window, _) =>
                    {
                        failed = window;
                        throw new InvalidOperationException("The native window failed after it appeared.");
                    }
                )
            )
            {
                start.App.ShowNewViewer([MissingFile()]);
            }

            Assert.NotNull(failed);
            Assert.Equal(1, failedClosed);
            Assert.False(failed.IsVisible);
            Assert.Empty(start.App.ViewerWindows);
            Assert.Equal(surfaces, SurfaceCount(bridge));
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "error" && entry.Message == "Open: the viewer window for 1 file(s) could not open."
            );
            Assert.True(start.Window.IsVisible);
        });

    [Fact]
    public Task ViewerWindowClosed_DisposesItsCompositionAndUnregistersItsSurface() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"));
            start.Window.Show();
            Window viewer = ShowViewer(start.App);
            ThemeBridge bridge = start.App.Bridge!;
            int surfaces = SurfaceCount(bridge);

            viewer.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(start.App.ViewerWindows);
            Assert.Equal(surfaces - 1, SurfaceCount(bridge));
            Assert.Empty(calls);
            Assert.True(start.Window.IsVisible);
        });

    [Fact]
    public Task OpenViewer_NeverWritesSession() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
            start.Window.Show();
            Window viewer = ShowViewer(start.App);
            Assert.IsType<DocumentWindowViewModel>(viewer.DataContext).ToggleSidebar();

            viewer.Close();
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            start.App.ReleaseMainWindow();

            Assert.False(File.Exists(start.SessionFile));
        });

    [Fact]
    public Task ClosingTheMainWindowWithAViewerOpen_KeepsAnsweringAndTheViewer() =>
        Headless.RunAsync(async () =>
        {
            List<string> calls = [];
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"));
            Window main = start.Window;
            main.Show();
            Window viewer = ShowViewer(start.App);
            Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();

            main.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(calls);
            Assert.True(viewer.IsVisible);
            Assert.True(start.LoadSavedSession().SidebarHidden);
            string file = MissingFile();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([file]);
            Assert.True(start.App.OpenViewer([file], request));
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
            Assert.Equal(2, start.App.ViewerWindows.Count);
            Assert.Same(viewer, start.App.ViewerWindows[0]);
        });

    [Fact]
    public Task ClosingTheLastWindow_StopsAnsweringBeforeTheMainSessionFlush() =>
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

            // The sidebar was hidden inside the stop action: only a session flush that came after it saved that.
            Assert.Equal(["stopped answering"], order);
            Assert.True(start.LoadSavedSession().SidebarHidden);
            start.App.ReleaseMainWindow();
            Assert.Equal(["stopped answering"], order);
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
            ShowViewer(start.App);
            Dispatcher.UIThread.RunJobs();

            start.App.ReleaseMainWindow();

            Assert.Equal(["stopped answering"], order);
            Assert.True(start.LoadSavedSession().SidebarHidden);
            Assert.Empty(start.App.ViewerWindows);
        });

    [Fact]
    public Task ClosingTheLastViewerAfterMain_StopsAnsweringOnce() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"));
            start.Window.Show();
            Window viewer = ShowViewer(start.App);

            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(calls);

            viewer.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["stop"], calls);

            start.App.ReleaseMainWindow();
            Assert.Equal(["stop"], calls);
        });

    [Fact]
    public Task OpenViewer_AfterTheLastWindowClosed_AnswersClosing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([MissingFile()]);

            Assert.False(start.App.OpenViewer([MissingFile()], request));

            Assert.Empty(start.App.ViewerWindows);
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task Teardown_WhenThePidDeleteFails_StillSavesAndReleases() =>
        Headless.RunAsync(async () =>
        {
            using var server = new TestInstanceServer(request => request.Complete(InstanceReply.Refused));
            int begun = 0;
            using var start = new GateStart(
                viewerFiles: [],
                stopAnswering: () =>
                {
                    begun++;
                    Program.StopAnsweringInBackground(server.Coordinator, server.Log);
                }
            );
            File.Delete(server.PidFile);
            Directory.CreateDirectory(server.PidFile);
            Window main = start.Window;
            main.Show();
            Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();

            main.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, begun);
            Assert.True(start.LoadSavedSession().SidebarHidden);

            // What Program does once the UI loop is over: finish the stop, then release the lock.
            await Task.Run(server.Coordinator.StopAnswering, TestContext.Current.CancellationToken)
                .WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken);
            Assert.Equal(1, PidDeleteWarnings(server));
            await Task.Run(server.Coordinator.Dispose, TestContext.Current.CancellationToken)
                .WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken);

            // The stop logged the failed delete before the lock was released; the release did not try again.
            Assert.Equal(1, PidDeleteWarnings(server));
            using var next = new InstanceCoordinator(server.LockPath, new RecordingAppLog());
            Assert.True(next.TryAcquireMainLock());
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

    /// <summary>Shows a viewer window over a missing file as an open request does once the app took it, and returns it.</summary>
    private static Window ShowViewer(App app)
    {
        app.ShowNewViewer([MissingFile()]);
        return app.ViewerWindows[^1];
    }

    private static int PidDeleteWarnings(TestInstanceServer server) =>
        server.Log.Entries.Count(entry =>
            entry.Level == "warning" && entry.Message.StartsWith("Could not delete the main-instance pid file", StringComparison.Ordinal)
        );

    private static string MissingFile() => Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md");

    private static int SurfaceCount(ThemeBridge bridge)
    {
        object surfaces = typeof(ThemeBridge).GetField("_surfaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        return (int)surfaces.GetType().GetProperty("Count")!.GetValue(surfaces)!;
    }

    /// <summary>Records the stop and changes what the session stores, so a later flush is the only way that change reaches disk.</summary>
    private static void StopAnsweringAndHideSidebar(Window main, List<string> order)
    {
        order.Add("stopped answering");
        Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();
    }

    private static bool IsBridgeDisposed(ThemeBridge bridge) =>
        (bool)typeof(ThemeBridge).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;

    /// <summary>
    /// A main start over a fresh data folder whose <c>settings.json</c> a test may seed before it calls
    /// <see cref="App.CreateStartWindow"/>; disposing it releases the start and restores the statics and environment.
    /// </summary>
    private sealed class SettingsStart : IDisposable
    {
        private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "yamv-app-start-" + Guid.NewGuid().ToString("N"));
        private readonly string? _dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        private readonly string? _harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);

        public SettingsStart()
        {
            App = Assert.IsType<App>(Application.Current);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataFolder);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            App.ViewerFiles = [];
            App.ProcessLog = Log;
            App.ResetLog();
        }

        public App App { get; }

        public RecordingAppLog Log { get; } = new();

        public string SettingsFile => Path.Combine(_dataFolder, "settings.json");

        /// <summary>Writes the data folder's <c>settings.json</c>.</summary>
        /// <param name="json">The settings JSON.</param>
        public void WriteSettings(string json)
        {
            Directory.CreateDirectory(_dataFolder);
            File.WriteAllText(SettingsFile, json);
        }

        public void Dispose()
        {
            try
            {
                App.ReleaseMainWindow();
            }
            finally
            {
                App.ResetLog();
                App.ViewerFiles = [];
                App.ProcessLog = null;
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, _harnessBefore);
                if (Directory.Exists(_dataFolder))
                {
                    Directory.Delete(_dataFolder, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// A real instance server whose handler only keeps each request, so the test hands it to the app as the UI job would.
    /// </summary>
    private sealed class OpenRequests : IDisposable
    {
        private readonly Channel<InstanceRequest> _requests = Channel.CreateUnbounded<InstanceRequest>();

        public OpenRequests() => Server = new TestInstanceServer(request => _requests.Writer.TryWrite(request));

        public TestInstanceServer Server { get; }

        /// <summary>Sends an open launch for <paramref name="files"/>; returns its request as the server read it, and the launch's outcome.</summary>
        public async Task<(InstanceRequest Request, Task<InstanceRequestOutcome> Outcome)> SendAsync(IReadOnlyList<string> files)
        {
            Task<InstanceRequestOutcome> outcome = Server.SendOpenAsync(files);
            InstanceRequest request = await _requests
                .Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken);
            return (request, outcome);
        }

        public void Dispose() => Server.Dispose();
    }

    /// <summary>
    /// A start with an activation gate and a recording process log handed to <see cref="App"/>, over a fresh data folder;
    /// disposing it closes every document window and restores the statics and environment.
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
        public SessionState LoadSavedSession() => new SessionStore(SessionFile, Log).Load();

        /// <summary>The start data folder's <c>session.json</c>.</summary>
        public string SessionFile => Path.Combine(_dataFolder, "session.json");

        public void Dispose()
        {
            try
            {
                foreach (Window viewer in App.ViewerWindows.Where(viewer => viewer.IsVisible))
                {
                    viewer.Close();
                }

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
