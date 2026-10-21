using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.App.Views.ThemeSettings;
using Yamv.Core;
using Yamv.Core.Diagnostics;
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
    private const string PaperLight = """{ "themeId": "paper", "appearance": "light" }""";
    private const string InkDark = """{ "themeId": "ink", "appearance": "dark" }""";
    private const string MintDark = """{ "themeId": "mint", "appearance": "dark" }""";

    /// <summary>A hang guard: a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

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
    public Task A_start_without_a_data_folder_ignores_the_width_popover_variable() =>
        Headless.Run(() =>
        {
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            string? popoverBefore = Environment.GetEnvironmentVariable(App.WidthPopoverVariable);
            // A viewer start reads no session and saves none, so the missing data folder never falls back to the owner's own.
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, null);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            Environment.SetEnvironmentVariable(App.WidthPopoverVariable, "bogus");
            App.ViewerFiles = [Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md")];
            App app = Assert.IsType<App>(Application.Current);
            App.ProcessLog = new RecordingAppLog();
            app.ResetLog();
            try
            {
                Window window = app.CreateStartWindow();

                MainWindow main = Assert.IsType<MainWindow>(window);
                Assert.Equal(DevWidthPopover.None, main.WidthPopoverWhenShown);
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                App.ViewerFiles = [];
                App.ProcessLog = null;
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                Environment.SetEnvironmentVariable(App.WidthPopoverVariable, popoverBefore);
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
            var time = new FakeTimeProvider();
            var viewModel = new DocumentWindowViewModel(
                state,
                surface,
                new DocumentWindowServices(widths, _ => "", time, log, new ToastCenter(time))
            );
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

    [Fact]
    public Task Main_start_creates_one_themes_watcher_and_release_disposes_it_before_the_bridge() =>
        Headless.Run(() =>
        {
            using var syncs = new ThemesSyncRecorder();
            using var start = new SettingsStart();

            start.App.CreateStartWindow();

            Assert.Equal(1, syncs.Created);
            Assert.NotNull(start.App.ThemesSync);

            start.App.ReleaseMainWindow();

            Assert.Equal([false], syncs.BridgeDisposedAtEachDispose);
            Assert.Null(start.App.ThemesSync);
        });

    [Fact]
    public Task Main_start_watches_the_themes_folder_under_the_data_dir() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();

            start.App.CreateStartWindow();

            ThemeFolderSync sync = Assert.IsType<ThemeFolderSync>(start.App.ThemesSync);
            Assert.Equal(AppPaths.ThemesDirectory, sync.Folder);
            Assert.StartsWith(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)!, sync.Folder, StringComparison.Ordinal);
        });

    [Fact]
    public Task Viewer_start_creates_a_themes_watcher_too() =>
        Headless.Run(() =>
        {
            using var syncs = new ThemesSyncRecorder();
            using var start = new SettingsStart();
            App.ViewerFiles = [Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md")];

            start.App.CreateStartWindow();

            Assert.Equal(1, syncs.Created);
            Assert.NotNull(start.App.ThemesSync);
        });

    [Fact]
    public Task Harness_starts_create_no_themes_watcher() =>
        Headless.Run(() =>
        {
            using var syncs = new ThemesSyncRecorder();
            using var start = new SettingsStart();
            string? settingsBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsHarnessVariable);
            Environment.SetEnvironmentVariable(App.HarnessVariable, "1");
            try
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, "1");
                Assert.IsType<ThemeSettingsWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.ThemesSync);

                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, null);
                Assert.IsType<HarnessWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.ThemesSync);

                Assert.Equal(0, syncs.Created);
            }
            finally
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
            }
        });

    [Fact]
    public Task Releasing_twice_after_a_watcher_start_is_safe() =>
        Headless.Run(() =>
        {
            using var syncs = new ThemesSyncRecorder();
            using var start = new SettingsStart();
            start.App.CreateStartWindow();

            start.App.ReleaseMainWindow();
            start.App.ReleaseMainWindow();

            Assert.Single(syncs.BridgeDisposedAtEachDispose);
            Assert.Null(start.App.ThemesSync);
            Assert.Null(start.App.Bridge);
        });

    [Fact]
    public Task Main_start_creates_the_theme_settings_host_and_release_disposes_it_before_the_themes_sync() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            Func<ThemeBridge, IAppLog, IDisposable> before = App.CreateThemesSync;
            ThemeSettingsHost? host = null;
            List<bool?> hostDisposedAtSyncDispose = [];
            App.CreateThemesSync = (bridge, log) => new DisposeProbe(before(bridge, log), () => hostDisposedAtSyncDispose.Add(host?.IsDisposed));
            try
            {
                start.App.CreateStartWindow();
                host = start.App.ThemeSettingsHost;
                Assert.NotNull(host);
                Assert.False(host.IsDisposed);

                start.App.ReleaseMainWindow();

                Assert.Equal([true], hostDisposedAtSyncDispose);
                Assert.Null(start.App.ThemeSettingsHost);
            }
            finally
            {
                App.CreateThemesSync = before;
            }
        });

    [Fact]
    public Task Releasing_twice_with_a_settings_window_open_is_safe() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: false);
            Assert.True(start.Settings.IsVisible);

            start.App.ReleaseMainWindow();
            start.App.ReleaseMainWindow();

            Assert.False(start.Settings.IsVisible);
            Assert.Null(start.App.ThemeSettingsHost);
        });

    [Fact]
    public Task Release_closes_a_window_with_unsaved_edits_without_waiting() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            Assert.True(start.ViewModel.IsDirty);
            bool closed = false;
            start.Settings.Closed += (_, _) => closed = true;

            start.App.ReleaseMainWindow();

            Assert.True(closed);
            Assert.False(start.Settings.IsVisible);
        });

    [Fact]
    public Task Viewer_start_creates_a_theme_settings_host_too() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            App.ViewerFiles = [Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md")];

            start.App.CreateStartWindow();

            Assert.NotNull(start.App.ThemeSettingsHost);
        });

    [Fact]
    public Task The_main_windows_theme_settings_request_opens_the_host_window() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            MainWindow main = Assert.IsType<MainWindow>(start.App.CreateStartWindow());
            main.Show();
            try
            {
                main.RequestThemeSettings();

                ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(start.App.ThemeSettingsHost?.Window);
                Assert.True(settings.IsVisible);
                Assert.Same(main, settings.Owner);
            }
            finally
            {
                start.App.ReleaseMainWindow();
                main.Close();
            }
        });

    [Fact]
    public Task A_viewer_opened_by_an_open_request_opens_the_theme_settings_window() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Show();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([MissingFile()]);
            Assert.True(start.App.OpenViewer([MissingFile()], request));
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
            MainWindow viewer = Assert.IsType<MainWindow>(Assert.Single(start.App.ViewerWindows));
            try
            {
                viewer.RequestThemeSettings();

                ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(start.App.ThemeSettingsHost?.Window);
                Assert.True(settings.IsVisible);
                Assert.Same(viewer, settings.Owner);
            }
            finally
            {
                start.App.ReleaseMainWindow();
                viewer.Close();
            }
        });

    [Fact]
    public Task A_request_that_cannot_open_the_window_is_logged_and_shown_as_a_toast() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            // The window is never shown, so the settings window cannot take it as its owner and its Show throws.
            MainWindow main = Assert.IsType<MainWindow>(start.App.CreateStartWindow());
            ToastCenter toasts = Assert.IsType<ToastCenter>(Assert.IsType<DocumentWindowViewModel>(main.DataContext).Toasts);

            main.RequestThemeSettings();

            Assert.Single(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "Theme settings: could not open the window.");
            ToastItem toast = Assert.Single(toasts.Items);
            Assert.Equal((ToastKind.Warning, "Couldn't open the theme settings"), (toast.Kind, toast.Text));
        });

    [Fact]
    public Task A_start_without_a_data_folder_ignores_the_theme_settings_entry_variable() =>
        Headless.Run(() =>
        {
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            string? harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);
            string? entryBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsEntryVariable);
            // A viewer start reads no session and saves none, so the missing data folder never falls back to the owner's own.
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, null);
            Environment.SetEnvironmentVariable(App.HarnessVariable, null);
            Environment.SetEnvironmentVariable(App.ThemeSettingsEntryVariable, "bogus");
            App.ViewerFiles = [Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md")];
            App app = Assert.IsType<App>(Application.Current);
            App.ProcessLog = new RecordingAppLog();
            app.ResetLog();
            try
            {
                MainWindow main = Assert.IsType<MainWindow>(app.CreateStartWindow());

                Assert.Equal(DevThemeSettingsEntry.None, main.ThemeSettingsEntryWhenShown);
            }
            finally
            {
                app.ReleaseMainWindow();
                app.ResetLog();
                App.ViewerFiles = [];
                App.ProcessLog = null;
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                Environment.SetEnvironmentVariable(App.ThemeSettingsEntryVariable, entryBefore);
            }
        });

    [Fact]
    public Task A_released_start_no_longer_answers_the_request() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            MainWindow main = Assert.IsType<MainWindow>(start.App.CreateStartWindow());
            main.Show();
            try
            {
                start.App.ReleaseMainWindow();

                main.RequestThemeSettings();

                Assert.Empty(main.OwnedWindows);
                Assert.Null(start.App.ThemeSettingsHost);
            }
            finally
            {
                main.Close();
            }
        });

    [Fact]
    public Task A_shutdown_request_without_unsaved_edits_goes_ahead() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: false);
            var request = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(request, start.TryShutdown);

            Assert.False(request.Cancel);
            Assert.Equal(0, start.ShutdownsTried);
        });

    [Fact]
    public Task A_shutdown_request_with_unsaved_edits_and_Cancel_stays_cancelled() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            var request = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(request, start.TryShutdown);
            Assert.True(request.Cancel);
            Assert.True(start.ConfirmOpen);
            start.Click("CancelButton");

            Assert.Equal(0, start.ShutdownsTried);
            Assert.True(start.Settings.IsVisible);
            Assert.True(start.ViewModel.IsDirty);
        });

    [Fact]
    public Task A_shutdown_request_with_unsaved_edits_and_Discard_asks_for_the_shutdown_once_more() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            var request = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(request, start.TryShutdown);
            Assert.True(request.Cancel);
            Assert.Equal(0, start.ShutdownsTried);
            start.Click("SecondaryButton");

            Assert.Equal(1, start.ShutdownsTried);
            Assert.False(start.ViewModel.IsDirty);
        });

    [Fact]
    public Task A_failure_asking_for_the_shutdown_again_is_logged_and_the_app_keeps_running() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            var request = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(request, () => throw new InvalidOperationException("The lifetime refused."));
            start.Click("SecondaryButton");

            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "error" && entry.Exception is InvalidOperationException { Message: "The lifetime refused." }
            );
            Assert.True(start.Settings.IsVisible);
        });

    [Fact]
    public Task A_shutdown_check_that_throws_is_logged_and_the_app_keeps_running() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            bool thrown = false;
            start.ViewModel.PropertyChanged += (_, e) =>
            {
                if (!thrown && e.PropertyName == nameof(ThemeSettingsViewModel.IsDirty) && !start.ViewModel.IsDirty)
                {
                    thrown = true;
                    throw new InvalidOperationException("The revert failed.");
                }
            };
            var request = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(request, start.TryShutdown);
            start.Click("SecondaryButton");

            Assert.Contains(
                start.Log.Entries,
                entry =>
                    entry.Level == "error"
                    && entry.Message == "Theme settings: the shutdown check failed; the app keeps running."
                    && entry.Exception is InvalidOperationException { Message: "The revert failed." }
            );
            Assert.Equal(0, start.ShutdownsTried);
            Assert.True(request.Cancel);
            Assert.True(start.Settings.IsVisible);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_shutdown_request_another_handler_cancelled_stays_cancelled_and_asks_nothing(bool dirty) =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty);
            var request = new ShutdownRequestedEventArgs { Cancel = true };

            start.App.OnShutdownRequested(request, start.TryShutdown);

            Assert.True(request.Cancel);
            Assert.False(start.ConfirmOpen);
            Assert.Equal(0, start.ShutdownsTried);
        });

    [Fact]
    public Task Repeated_shutdown_requests_while_the_check_runs_start_no_second_check_and_Save_shuts_down_once() =>
        Headless.Run(() =>
        {
            using var start = new SettingsWindowStart(dirty: true);
            var first = new ShutdownRequestedEventArgs();
            var second = new ShutdownRequestedEventArgs();

            start.App.OnShutdownRequested(first, start.TryShutdown);
            start.App.OnShutdownRequested(second, start.TryShutdown);
            Assert.True(first.Cancel);
            Assert.True(second.Cancel);
            start.Click("PrimaryButton");

            Assert.Equal(1, start.ShutdownsTried);
            Assert.False(start.ViewModel.IsDirty);
        });

    [Fact]
    public Task Harness_starts_create_no_host() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            string? settingsBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsHarnessVariable);
            Environment.SetEnvironmentVariable(App.HarnessVariable, "1");
            try
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, "1");
                Assert.IsType<ThemeSettingsWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.ThemeSettingsHost);

                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, null);
                Assert.IsType<HarnessWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.ThemeSettingsHost);
            }
            finally
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
            }
        });

    [Fact]
    public Task Settings_file_edited_while_running_is_applied_to_the_bridge() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.WriteSettings(PaperLight);
            start.App.CreateStartWindow();
            Assert.Equal("paper", start.App.Bridge!.RequestedThemeId);

            EditUntilApplied(start, InkDark, "ink", () => Dispatcher.UIThread.RunJobs());

            Assert.Equal("ink", start.App.Bridge.RequestedThemeId);
            Assert.Equal("dark", start.App.Bridge.RequestedAppearance);
        });

    [Fact]
    public Task Applied_file_change_writes_nothing_back() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.WriteSettings(PaperLight);
            start.App.CreateStartWindow();
            SettingsWriter writer = start.App.SettingsWriter!;
            Lock storeGate = StoreGate(writer);
            bool saveAsked = false;

            EditUntilApplied(
                start,
                InkDark,
                "ink",
                () =>
                {
                    // Holding the store keeps a save the posted work asks for from finishing before the writer is looked at.
                    using (storeGate.EnterScope())
                    {
                        Dispatcher.UIThread.RunJobs();
                        saveAsked |= !writer.WhenIdle().IsCompleted;
                    }
                }
            );
            Assert.True(writer.WhenIdle().Wait(HangGuard, TestContext.Current.CancellationToken), "The writer never went idle.");

            Assert.False(saveAsked, "Applying the settings file's change asked the writer to save it back.");
            Assert.Equal(Encoding.UTF8.GetBytes(InkDark), File.ReadAllBytes(start.SettingsFile));
        });

    [Fact]
    public Task A_stale_settings_report_is_applied_without_writing_over_a_newer_file() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.WriteSettings(PaperLight);
            start.App.CreateStartWindow();
            start.WriteSettings(MintDark);

            start.App.OnSettingsFileChanged(start.App.SettingsWatcher, new AppSettings("ink", "dark"));
            Dispatcher.UIThread.RunJobs();
            // Loose on purpose: the real watcher may apply the Mint edit in the same RunJobs, so the theme is ink or mint.
            Assert.NotEqual("paper", start.App.Bridge!.RequestedThemeId);
            Assert.True(start.App.SettingsWriter!.WhenIdle().Wait(HangGuard, TestContext.Current.CancellationToken), "The writer never went idle.");

            Assert.Equal(Encoding.UTF8.GetBytes(MintDark), File.ReadAllBytes(start.SettingsFile));
        });

    [Fact]
    public Task A_settings_change_posted_by_an_earlier_start_does_nothing_to_the_next() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.App.CreateStartWindow();
            start.App.OnSettingsFileChanged(start.App.SettingsWatcher, new AppSettings("ink", "dark"));
            start.App.ReleaseMainWindow();

            start.App.CreateStartWindow();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("studio", start.App.Bridge!.RequestedThemeId);
            Assert.Equal("system", start.App.Bridge.RequestedAppearance);
        });

    [Fact]
    public Task Release_disposes_the_settings_watcher_before_the_writer_and_the_bridge() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            using var watchers = new SettingsWatcherRecorder(start.App);
            start.App.CreateStartWindow();
            Assert.Equal(1, watchers.Created);
            Assert.NotNull(start.App.SettingsWatcher);

            start.App.ReleaseMainWindow();

            Assert.Equal([(false, false)], watchers.WriterAndBridgeDisposedAtEachDispose);
            Assert.Null(start.App.SettingsWatcher);
        });

    [Fact]
    public Task A_settings_change_posted_after_release_does_nothing() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            start.App.CreateStartWindow();
            ThemeBridge bridge = start.App.Bridge!;
            start.App.OnSettingsFileChanged(start.App.SettingsWatcher, new AppSettings("ink", "dark"));

            start.App.ReleaseMainWindow();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("studio", bridge.RequestedThemeId);
            Assert.Equal("system", bridge.RequestedAppearance);
            Assert.False(File.Exists(start.SettingsFile));
        });

    [Fact]
    public Task Harness_starts_create_no_settings_watcher() =>
        Headless.Run(() =>
        {
            using var start = new SettingsStart();
            using var watchers = new SettingsWatcherRecorder(start.App);
            string? settingsBefore = Environment.GetEnvironmentVariable(App.ThemeSettingsHarnessVariable);
            Environment.SetEnvironmentVariable(App.HarnessVariable, "1");
            try
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, "1");
                Assert.IsType<ThemeSettingsWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.SettingsWatcher);

                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, null);
                Assert.IsType<HarnessWindow>(start.App.CreateStartWindow());
                Assert.Null(start.App.SettingsWatcher);

                Assert.Equal(0, watchers.Created);
            }
            finally
            {
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
            }
        });

    /// <summary>
    /// Writes <paramref name="json"/> to the settings file until the bridge asks for <paramref name="themeId"/>, running the UI
    /// thread's posted work meanwhile. The watcher arms its folder on a thread-pool thread, so an edit made before that is not
    /// seen; the file is written again each second until one is. <paramref name="runJobs"/> runs the posted work.
    /// </summary>
    private static void EditUntilApplied(SettingsStart start, string json, string themeId, Action runJobs)
    {
        DateTime deadline = DateTime.UtcNow + HangGuard;
        DateTime nextWrite = DateTime.MinValue;
        while (start.App.Bridge!.RequestedThemeId != themeId)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The settings file edit to '{themeId}' was never applied.");
            if (DateTime.UtcNow >= nextWrite)
            {
                start.WriteSettings(json);
                nextWrite = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            }

            runJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>The lock <paramref name="writer"/>'s store holds through each read and save of the file.</summary>
    private static Lock StoreGate(SettingsWriter writer)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        object store = typeof(SettingsWriter).GetField("store", Private)!.GetValue(writer)!;
        return (Lock)typeof(SettingsStore).GetField("gate", Private)!.GetValue(store)!;
    }

    private static bool IsWriterDisposed(SettingsWriter writer) =>
        (bool)typeof(SettingsWriter).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;

    /// <summary>Records the stop and changes what the session stores, so a later flush is the only way that change reaches disk.</summary>
    private static void StopAnsweringAndHideSidebar(Window main, List<string> order)
    {
        order.Add("stopped answering");
        Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();
    }

    private static bool IsBridgeDisposed(ThemeBridge bridge) =>
        (bool)typeof(ThemeBridge).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;

    /// <summary>
    /// Wraps each themes sync the app creates while it lives, counting them and recording, at each one's dispose, whether the
    /// bridge it serves was already disposed; disposing it restores <see cref="App.CreateThemesSync"/>.
    /// </summary>
    private sealed class ThemesSyncRecorder : IDisposable
    {
        private readonly Func<ThemeBridge, IAppLog, IDisposable> _before = App.CreateThemesSync;

        public ThemesSyncRecorder() =>
            App.CreateThemesSync = (bridge, log) =>
            {
                Created++;
                return new Recorded(_before(bridge, log), bridge, BridgeDisposedAtEachDispose);
            };

        public int Created { get; private set; }

        public List<bool> BridgeDisposedAtEachDispose { get; } = [];

        public void Dispose() => App.CreateThemesSync = _before;

        private sealed class Recorded(IDisposable inner, ThemeBridge bridge, List<bool> disposals) : IDisposable
        {
            public void Dispose()
            {
                disposals.Add(IsBridgeDisposed(bridge));
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// A main start with the user theme Mine in use and the theme settings window open over the shown main window, with unsaved
    /// edits when asked; it counts the shutdowns the app asks for again. Disposing it releases the start, then closes the main
    /// window, so a failed assert leaves no window in the shared headless session.
    /// </summary>
    private sealed class SettingsWindowStart : IDisposable
    {
        private readonly SettingsStart _start = new();

        public SettingsWindowStart(bool dirty)
        {
            try
            {
                _start.WriteSettings("""{ "themeId": "mine", "appearance": "light" }""");
                Directory.CreateDirectory(AppPaths.ThemesDirectory);
                File.WriteAllText(
                    Path.Combine(AppPaths.ThemesDirectory, "mine" + ThemeCatalog.FileSuffix),
                    """{ "name": "Mine", "light": { "tokens": { "accent": "#112233" } } }"""
                );
                Main = App.CreateStartWindow();
                Main.Show();
                App.ShowThemeSettings(Main);
                Settings = App.ThemeSettingsHost!.Window!;
                ViewModel = Assert.IsType<ThemeSettingsViewModel>(Settings.DataContext);
                Assert.Equal("mine", ViewModel.Selected?.Id);
                if (dirty)
                {
                    ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public App App => _start.App;

        /// <summary>The process log the start writes to.</summary>
        public RecordingAppLog Log => _start.Log;

        public Window? Main { get; }

        public ThemeSettingsWindow Settings { get; } = null!;

        public ThemeSettingsViewModel ViewModel { get; } = null!;

        public int ShutdownsTried { get; private set; }

        /// <summary>Whether the settings window's confirm is open.</summary>
        public bool ConfirmOpen => Settings.GetLogicalDescendants().OfType<ConfirmDialog>().Single(dialog => dialog.Name == "Confirm").IsOpen;

        public void TryShutdown() => ShutdownsTried++;

        /// <summary>Clicks the settings window's button named <paramref name="name"/> and runs the work it posted.</summary>
        public void Click(string name)
        {
            Settings
                .GetLogicalDescendants()
                .OfType<Button>()
                .Single(button => button.Name == name)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            try
            {
                App.ReleaseMainWindow();
            }
            finally
            {
                try
                {
                    Main?.Close();
                }
                finally
                {
                    _start.Dispose();
                }
            }
        }
    }

    /// <summary>Disposes <paramref name="inner"/> after running <paramref name="onDispose"/>.</summary>
    private sealed class DisposeProbe(IDisposable inner, Action onDispose) : IDisposable
    {
        public void Dispose()
        {
            onDispose();
            inner.Dispose();
        }
    }

    /// <summary>
    /// Has the app make each settings watcher with a clock whose timers record, when the first of them is disposed, whether the
    /// app's settings writer and bridge were already disposed; disposing it restores <see cref="App.CreateSettingsWatcher"/>.
    /// </summary>
    private sealed class SettingsWatcherRecorder : IDisposable
    {
        private readonly Func<SettingsStore, SettingsWriter, IAppLog, SettingsWatcher> _before = App.CreateSettingsWatcher;

        public SettingsWatcherRecorder(App app) =>
            App.CreateSettingsWatcher = (store, writer, log) =>
            {
                Created++;
                ThemeBridge bridge = app.Bridge!;
                var clock = new DisposeRecordingClock(() =>
                    WriterAndBridgeDisposedAtEachDispose.Add((IsWriterDisposed(writer), IsBridgeDisposed(bridge)))
                );
                return new SettingsWatcher(store, writer, clock, log);
            };

        public int Created { get; private set; }

        public List<(bool Writer, bool Bridge)> WriterAndBridgeDisposedAtEachDispose { get; } = [];

        public void Dispose() => App.CreateSettingsWatcher = _before;
    }

    /// <summary>The system clock, whose timers run <paramref name="onFirstDispose"/> when the first of them is disposed.</summary>
    private sealed class DisposeRecordingClock(Action onFirstDispose) : TimeProvider
    {
        private int _disposed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new RecordingTimer(System.CreateTimer(callback, state, dueTime, period), this);

        private void OnDispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onFirstDispose();
            }
        }

        private sealed class RecordingTimer(ITimer inner, DisposeRecordingClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                clock.OnDispose();
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                clock.OnDispose();
                return inner.DisposeAsync();
            }
        }
    }

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
