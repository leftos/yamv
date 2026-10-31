using System.ComponentModel;
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
            using var opened = new OpenedMainWindows();
            try
            {
                Window? window = app.CreateStartWindow();

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
            using var opened = new OpenedMainWindows();
            try
            {
                Window? window = app.CreateStartWindow();

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
            using var opened = new OpenedMainWindows();
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
            using var opened = new OpenedMainWindows();
            try
            {
                Window? window = app.CreateStartWindow();

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
            using var opened = new OpenedMainWindows();
            App.ProcessLog = new RecordingAppLog();
            app.ResetLog();
            try
            {
                Window? window = app.CreateStartWindow();

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
            using var opened = new OpenedMainWindows();
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
                new DocumentWindowServices(widths, _ => "", time, log, new ToastCenter(time)) { Elevated = false }
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
    public Task No_job_reaches_the_UI_thread_before_framework_ready() =>
        Headless.Run(() =>
        {
            var latch = new UiReadyLatch();
            App.ReadyLatch = latch;
            try
            {
                List<string> ran = [];
                latch.Post(() => ran.Add("open"));
                latch.Post(() => ran.Add("activate"));
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(ran);

                // What OnFrameworkInitializationCompleted does last: the queued jobs go to the UI thread's queue, in order.
                App.OpenReadyLatch();
                Assert.Empty(ran);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(["open", "activate"], ran);

                latch.Post(() => ran.Add("later"));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(["open", "activate", "later"], ran);
            }
            finally
            {
                App.ReadyLatch = null;
            }
        });

    [Fact]
    public Task Activate_UnminimizesTheMainWindow() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;

            start.App.BringAppForward();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(1, start.BroughtForwardCount);
        });

    [Fact]
    public Task A_main_built_on_demand_reads_the_saved_session() =>
        Headless.Run(() =>
        {
            // A missing document: the session restores it as the selection, and no web view is attached in the headless session.
            string document = MissingFile();
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
            Window viewer = start.Window;
            new SessionStore(start.SessionFile, start.Log).Save(
                new SessionState
                {
                    OpenFiles = [document],
                    SelectedFile = document,
                    SidebarWidth = 333,
                }
            );
            using var opened = new OpenedMainWindows();

            start.App.BringAppForward();

            MainWindow main = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            Assert.Equal([main], opened.Windows);
            Assert.NotSame(viewer, main);
            Assert.True(main.IsVisible);
            Assert.Equal(333, main.SidebarWidth);
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(main.DataContext);
            Assert.False(viewModel.IsEmpty);
            Assert.True(viewModel.SelectedIsMissing);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: main window opened.");
            Assert.Same(viewer, Assert.Single(start.App.ViewerWindows));
        });

    [Fact]
    public Task A_start_whose_window_factory_throws_stops_and_quits() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var syncs = new ThemesSyncRecorder();
            using var watchers = new FailingSettingsWatchers(failures: 1);
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);

            Assert.Null(start.StartWindow);
            Assert.Equal(["stop"], calls);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "The main window could not open.");

            // Nothing the failed build made is kept: the themes sync it made is disposed, before the bridge it served.
            Assert.Null(start.App.Bridge);
            Assert.Null(start.App.ThemesSync);
            Assert.Null(start.App.ThemeSettingsHost);
            Assert.Null(start.App.SettingsWriter);
            Assert.Null(start.App.SettingsWatcher);
            Assert.Equal(1, syncs.Created);
            Assert.Equal([false], syncs.BridgeDisposedAtEachDispose);

            Assert.Equal(0, start.Quits);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.Quits);
            Assert.Equal(["stop"], calls);
        });

    [Fact]
    public Task A_failed_start_under_KeepRunning_lets_a_later_activate_build_the_main_window() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var watchers = new FailingSettingsWatchers(failures: 1);
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.KeepRunning);
            Assert.Null(start.StartWindow);

            start.App.BringAppForward();

            // Had the arbiter still counted the failed main as open, the activate would only have looked for a window to bring forward.
            MainWindow main = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            Assert.True(main.IsVisible);
            Assert.NotNull(start.App.SettingsWatcher);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: main window opened.");
            Assert.Empty(calls);
            Assert.Equal(0, start.Quits);
        });

    [Fact]
    public Task The_quit_job_does_nothing_after_the_app_exited() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);

            start.Window.Close();
            start.App.ReleaseMainWindow();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["stop"], calls);
            Assert.Equal(0, start.Quits);
        });

    [Fact]
    public Task A_second_main_is_never_built_over_a_live_one() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            using var opened = new OpenedMainWindows();

            Assert.Null(start.App.OpenMain());
            start.App.BringAppForward();

            Assert.Empty(opened.Windows);
            Assert.Same(start.Window, start.App.MainDocumentWindow);
            Assert.Equal(1, start.BroughtForwardCount);
        });

    [Fact]
    public Task A_main_that_fails_to_show_is_reported_closed_to_the_arbiter() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
            Window? failed = null;
            using (FailingOpens(window => failed = window))
            {
                start.App.BringAppForward();
            }

            Assert.NotNull(failed);
            Assert.False(failed.IsVisible);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "The main window could not open.");

            // Had the arbiter still counted the failed main as open, this activate would only bring a window forward.
            start.App.BringAppForward();

            MainWindow main = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            Assert.NotSame(failed, main);
            Assert.True(main.IsVisible);
        });

    [Fact]
    public Task An_activate_whose_main_fails_to_build_brings_the_most_recently_active_viewer_forward() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
            Window first = start.Window;
            Window second = ShowViewer(start.App);
            first.Activate();
            Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsActive);
            first.WindowState = WindowState.Minimized;
            second.WindowState = WindowState.Minimized;

            using (FailingOpens(_ => { }))
            {
                start.App.BringAppForward();
            }

            Assert.Equal(WindowState.Normal, first.WindowState);
            Assert.Equal(WindowState.Minimized, second.WindowState);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "The main window could not open.");
            Assert.Single(start.Log.Entries, entry => entry.Message == "Activate: viewer window brought forward.");
            Assert.Null(start.App.MainDocumentWindow);
        });

    [Fact]
    public Task ShowNewViewer_TwiceMakesTwoWindowsAndOneBridge() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
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
    public Task A_launch_naming_a_file_open_in_a_viewer_brings_it_forward_and_opens_no_window() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string file = MissingFile();
            using var start = new GateStart(viewerFiles: [file], stopAnswering: null);
            Window viewer = start.Window;
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(viewer.DataContext);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([file]);
            viewer.WindowState = WindowState.Minimized;

            Assert.True(start.App.OpenViewer([file], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Single(start.App.ViewerWindows);
            Assert.Same(viewer, start.App.ViewerWindows[0]);
            Assert.Equal(WindowState.Normal, viewer.WindowState);
            Assert.True(viewer.IsActive);
            Assert.Equal(file, viewModel.SelectedPath);
            Assert.Contains(start.Log.Entries, entry => entry.Message == $"Open: {file} is already open; brought its window forward.");
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_naming_a_file_in_the_main_window_selects_it_there() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string first = MissingFile();
            string second = MissingFile();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            MainWindow main = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(main.DataContext);
            viewModel.Open([first]);
            viewModel.Open([second]);
            main.WindowState = WindowState.Minimized;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([first]);

            Assert.True(start.App.OpenViewer([first], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(start.App.ViewerWindows);
            Assert.Same(main, start.App.MainDocumentWindow);
            Assert.Equal(WindowState.Normal, main.WindowState);
            Assert.Equal(first, viewModel.SelectedPath);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_naming_an_open_file_picks_the_most_recently_active_window() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string file = MissingFile();
            using var start = new GateStart(viewerFiles: [file], stopAnswering: null);
            Window older = start.Window;
            start.App.ShowNewViewer([file]);
            Window newer = start.App.ViewerWindows[1];
            older.Activate();
            Dispatcher.UIThread.RunJobs();
            newer.Activate();
            Dispatcher.UIThread.RunJobs();
            older.WindowState = WindowState.Minimized;
            newer.WindowState = WindowState.Minimized;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([file]);

            Assert.True(start.App.OpenViewer([file], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, start.App.ViewerWindows.Count);
            Assert.Equal(WindowState.Normal, newer.WindowState);
            Assert.Equal(WindowState.Minimized, older.WindowState);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_with_an_open_file_and_new_files_opens_the_rest_in_one_viewer() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string earlier = MissingFile();
            string open = MissingFile();
            string first = MissingFile();
            string second = MissingFile();
            using var start = new GateStart(viewerFiles: [earlier, open], stopAnswering: null);
            DocumentWindowViewModel holder = Assert.IsType<DocumentWindowViewModel>(start.Window.DataContext);
            int shownBefore = start.Log.Entries.Count(entry => entry.Message == "Window: shown");
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([open, first, second]);

            Assert.True(start.App.OpenViewer([open, first, second], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, start.App.ViewerWindows.Count);
            DocumentWindowViewModel fresh = Assert.IsType<DocumentWindowViewModel>(start.App.ViewerWindows[1].DataContext);
            Assert.True(fresh.HasDocument(first));
            Assert.True(fresh.HasDocument(second));
            Assert.False(fresh.HasDocument(open));
            Assert.Equal(first, fresh.SelectedPath);
            Assert.Equal(open, holder.SelectedPath);
            List<LogEntry> entries = [.. start.Log.Entries];
            int viewerShown = entries.FindLastIndex(entry => entry.Message == "Window: shown");
            int broughtForward = entries.FindIndex(entry => entry.Message == $"Open: {open} is already open; brought its window forward.");
            Assert.Equal(shownBefore + 1, entries.Count(entry => entry.Message == "Window: shown"));
            Assert.True(viewerShown < broughtForward, "the new viewer opened before the holder came forward");
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_path_the_OS_cannot_read_keeps_the_app() =>
        Headless.Run(() =>
        {
            string open = MissingFile();
            string unreadable = "yamv-unreadable-" + '\0' + ".md";
            using var start = new GateStart(viewerFiles: [open], stopAnswering: null);
            Window viewer = start.Window;

            start.App.OpenFiles([unreadable]);

            Assert.Single(start.App.ViewerWindows);
            Assert.True(viewer.IsVisible);
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "warning" && entry.Message == $"Open: cannot compare the path {unreadable}; opening it in a viewer."
            );
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "error" && entry.Message == "Open: the viewer window for 1 file(s) could not open."
            );
        });

    [Fact]
    public Task A_failed_bring_forward_still_opens_the_held_file_and_the_rest() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string earlier = MissingFile();
            string open = MissingFile();
            string added = MissingFile();
            using var start = new GateStart(viewerFiles: [earlier, open], stopAnswering: null);
            DocumentWindowViewModel holder = Assert.IsType<DocumentWindowViewModel>(start.Window.DataContext);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([open, added]);
            static void FailOnSelection(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(DocumentWindowViewModel.SelectedPath))
                {
                    throw new InvalidOperationException("The window could not come forward.");
                }
            }

            holder.PropertyChanged += FailOnSelection;
            try
            {
                Assert.True(start.App.OpenViewer([open, added], request));
            }
            finally
            {
                holder.PropertyChanged -= FailOnSelection;
            }

            Assert.Equal(3, start.App.ViewerWindows.Count);
            DocumentWindowViewModel rest = Assert.IsType<DocumentWindowViewModel>(start.App.ViewerWindows[1].DataContext);
            Assert.Equal(added, rest.SelectedPath);
            DocumentWindowViewModel reopened = Assert.IsType<DocumentWindowViewModel>(start.App.ViewerWindows[2].DataContext);
            Assert.Equal(open, reopened.SelectedPath);
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "error" && entry.Message == $"Open: could not bring the window of {open} forward; opening it in a viewer."
            );
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task Two_files_held_by_different_windows_bring_forward_the_first_only() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string firstFile = MissingFile();
            string secondFile = MissingFile();
            using var start = new GateStart(viewerFiles: [firstFile], stopAnswering: null);
            Window firstWindow = start.Window;
            start.App.ShowNewViewer([secondFile]);
            Window secondWindow = start.App.ViewerWindows[1];
            DocumentWindowViewModel firstModel = Assert.IsType<DocumentWindowViewModel>(firstWindow.DataContext);
            DocumentWindowViewModel secondModel = Assert.IsType<DocumentWindowViewModel>(secondWindow.DataContext);
            secondWindow.Activate();
            Dispatcher.UIThread.RunJobs();
            firstWindow.WindowState = WindowState.Minimized;
            secondWindow.WindowState = WindowState.Minimized;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([firstFile, secondFile]);

            Assert.True(start.App.OpenViewer([firstFile, secondFile], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, start.App.ViewerWindows.Count);
            Assert.Equal(WindowState.Normal, firstWindow.WindowState);
            Assert.Equal(WindowState.Minimized, secondWindow.WindowState);
            Assert.Equal(firstFile, firstModel.SelectedPath);
            Assert.Equal(secondFile, secondModel.SelectedPath);
            Assert.Contains(start.Log.Entries, entry => entry.Message == $"Open: {firstFile} is already open; brought its window forward.");
            Assert.DoesNotContain(start.Log.Entries, entry => entry.Message == $"Open: {secondFile} is already open; brought its window forward.");
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_naming_only_new_files_opens_one_viewer() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string open = MissingFile();
            string added = MissingFile();
            using var start = new GateStart(viewerFiles: [open], stopAnswering: null);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([added]);

            Assert.True(start.App.OpenViewer([added], request));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, start.App.ViewerWindows.Count);
            DocumentWindowViewModel fresh = Assert.IsType<DocumentWindowViewModel>(start.App.ViewerWindows[1].DataContext);
            Assert.Equal(added, fresh.SelectedPath);
            Assert.False(fresh.HasDocument(open));
            Assert.DoesNotContain(
                start.Log.Entries,
                entry =>
                    entry.Message.StartsWith("Open: ", StringComparison.Ordinal) && entry.Message.Contains("already open", StringComparison.Ordinal)
            );
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_of_an_open_file_while_stopping_answers_closing_and_selects_nothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string open = MissingFile();
            string other = MissingFile();
            using var start = new GateStart(viewerFiles: [open], stopAnswering: null);
            Window viewer = start.Window;
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(viewer.DataContext);
            viewModel.Open([other]);
            App.Lifetime!.Exiting();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([open]);

            Assert.False(start.App.OpenViewer([open], request));

            Assert.Equal(other, viewModel.SelectedPath);
            Assert.Single(start.App.ViewerWindows);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Open: the app is closing; answered closing to an open of 1 file(s).");
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_launch_of_an_open_file_answers_opened_before_it_selects() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            string first = MissingFile();
            string second = MissingFile();
            using var start = new GateStart(viewerFiles: [first, second], stopAnswering: null);
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(start.Window.DataContext);
            viewModel.Select(second);
            Dispatcher.UIThread.RunJobs();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([first]);
            int refused = 0;
            void RefuseOnSelection(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(DocumentWindowViewModel.SelectedPath))
                {
                    refused++;
                    request.Complete(InstanceReply.Refused);
                }
            }

            viewModel.PropertyChanged += RefuseOnSelection;
            try
            {
                Assert.True(start.App.OpenViewer([first], request));
            }
            finally
            {
                viewModel.PropertyChanged -= RefuseOnSelection;
            }

            Assert.Equal(1, refused);
            Assert.False(request.TryBegin());
            Assert.Equal(first, viewModel.SelectedPath);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task ShowNewViewer_WhenShowThrows_ClosesTheWindowAndForgetsIt() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
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
            using (FailingOpens(window => failed = window))
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
    public Task A_viewer_that_fails_to_show_as_the_last_window_stops_and_quits() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            Window? failed = null;
            GateStart start;
            using (FailingOpens(window => failed = window))
            {
                start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);
            }

            using (start)
            {
                Assert.Null(start.StartWindow);
                Assert.NotNull(failed);
                Assert.False(failed.IsVisible);
                Assert.Empty(start.App.ViewerWindows);
                Assert.Equal(["stop"], calls);
                Assert.Equal(0, start.Quits);

                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, start.Quits);
            }
        });

    [Fact]
    public Task Each_window_removal_applies_the_policy_once() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);
            Window viewer = ShowViewer(start.App);
            using (FailingOpens(_ => { }))
            {
                start.App.ShowNewViewer([MissingFile()]);
            }

            viewer.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(calls);
            Assert.Equal(0, start.Quits);

            start.Window.Close();
            Assert.Equal(["stop"], calls);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.Quits);

            start.App.ReleaseMainWindow();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["stop"], calls);
            Assert.Equal(1, start.Quits);
        });

    [Fact]
    public Task On_Windows_the_last_window_stops_then_posts_one_quit() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);
            start.Window.Closed += (_, _) => calls.Add($"closed, quits {start.Quits}");

            start.Window.Close();

            // The quit never runs inside the close: it is a later job on the UI thread.
            Assert.Equal(["stop", "closed, quits 0"], calls);
            Assert.Equal(0, start.Quits);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.Quits);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, start.Quits);
            Assert.Single(start.Log.Entries, entry => entry.Message == "Quit: no document window is left; shutting the app down.");
        });

    [Fact]
    public Task On_macOS_the_last_window_keeps_the_app_and_exit_stops_once() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.KeepRunning);

            start.Window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(calls);
            Assert.Equal(0, start.Quits);
            start.App.BringAppForward();
            MainWindow reopened = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            Assert.True(reopened.IsVisible);

            start.App.ReleaseMainWindow();
            start.App.ReleaseMainWindow();

            Assert.Equal(["stop"], calls);
            Assert.Equal(0, start.Quits);
            reopened.Close();
        });

    [Fact]
    public Task The_quit_job_does_nothing_if_a_window_opened_meanwhile() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"), LastWindowBehavior.Quit);

            start.Window.Close();
            ShowViewer(start.App);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["stop"], calls);
            Assert.Equal(0, start.Quits);
        });

    [Fact]
    public Task ViewerWindowClosed_DisposesItsCompositionAndUnregistersItsSurface() =>
        Headless.Run(() =>
        {
            List<string> calls = [];
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => calls.Add("stop"));
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
    public Task An_elevated_app_marks_every_window_and_leaves_the_session_file_alone() =>
        Headless.Run(() =>
        {
            App.Elevated = true;
            try
            {
                using var start = new GateStart(viewerFiles: [], stopAnswering: null);
                string saved = WriteSessionWithSidebarWidth(start.SessionFile, 333);
                MainWindow main = AssertMarked(start.Window);
                Assert.Equal("YAMV (Administrator)", main.Title);
                MainWindow viewer = AssertMarked(ShowViewer(start.App));

                Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();
                main.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(saved, File.ReadAllText(start.SessionFile));

                start.App.BringAppForward();
                MainWindow again = AssertMarked(start.App.MainDocumentWindow);
                Assert.NotSame(main, again);
                Assert.Equal(333, again.SidebarWidth);
                start.App.ShowThemeSettings(again);
                ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(start.App.ThemeSettingsHost?.Window);
                Assert.Equal("Theme settings (Administrator)", settings.Title);
                Assert.True(settings.AdministratorChip.IsVisible);

                Assert.IsType<DocumentWindowViewModel>(again.DataContext).ToggleSidebar();
                settings.Close();
                again.Close();
                viewer.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(saved, File.ReadAllText(start.SessionFile));
            }
            finally
            {
                App.Elevated = false;
            }
        });

    [Fact]
    public Task The_dev_elevated_switch_marks_the_windows_saves_nothing_and_keeps_the_main_lock() =>
        Headless.Run(() =>
        {
            string? before = Environment.GetEnvironmentVariable(MainWindowFactory.ElevatedVariable);
            Environment.SetEnvironmentVariable(MainWindowFactory.ElevatedVariable, "1");
            try
            {
                using var start = new GateStart(viewerFiles: [], stopAnswering: null);
                MainWindow main = AssertMarked(start.Window);
                MainWindow viewer = AssertMarked(ShowViewer(start.App));

                Assert.IsType<DocumentWindowViewModel>(main.DataContext).ToggleSidebar();
                main.Close();
                viewer.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.False(File.Exists(start.SessionFile));
                Assert.False(App.Elevated);
                Assert.Equal("main.lock", Path.GetFileName(Program.MainLockFileFor(App.Elevated)));
            }
            finally
            {
                Environment.SetEnvironmentVariable(MainWindowFactory.ElevatedVariable, before);
            }
        });

    /// <summary>Asserts that <paramref name="window"/> is a document window marked as the elevated instance's, and returns it.</summary>
    private static MainWindow AssertMarked(Window? window)
    {
        MainWindow marked = Assert.IsType<MainWindow>(window);
        Assert.EndsWith(" (Administrator)", marked.Title, StringComparison.Ordinal);
        Assert.True(marked.WindowTitleBar.AdministratorChip.IsVisible);
        return marked;
    }

    /// <summary>Writes a session holding only a sidebar width to <paramref name="sessionFile"/> and returns the file's text.</summary>
    private static string WriteSessionWithSidebarWidth(string sessionFile, double width)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        new SessionStore(sessionFile, new RecordingAppLog()).Save(new SessionState { SidebarWidth = width });
        return File.ReadAllText(sessionFile);
    }

    [Fact]
    public Task OpenViewer_NeverWritesSession() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null);
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
    public Task An_open_while_stopping_answers_closing_and_builds_nothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendAsync([MissingFile()]);
            using var opened = new OpenedMainWindows();

            Assert.False(start.App.OpenViewer([MissingFile()], request));

            Assert.Empty(start.App.ViewerWindows);
            Assert.Empty(opened.Windows);
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_while_stopping_builds_nothing_and_logs() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Close();
            using var opened = new OpenedMainWindows();

            Assert.False(start.App.BringAppForward());
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(opened.Windows);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Empty(start.App.ViewerWindows);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "info" && entry.Message == "Activate ignored: the app is stopping.");
        });

    [Fact]
    public Task An_activate_is_answered_opened_after_the_window_is_forward() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;
            (InstanceRequest first, Task<InstanceRequestOutcome> firstOutcome) = await requests.SendActivateAsync();

            // A refusal while the window comes forward ends the request only if opened was not answered before: then the launch hears it.
            void RefuseOnRaise(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == Window.WindowStateProperty)
                {
                    first.Complete(InstanceReply.Refused);
                }
            }

            window.PropertyChanged += RefuseOnRaise;
            Assert.True(start.App.ActivateRequested(first));
            window.PropertyChanged -= RefuseOnRaise;
            Assert.Equal(
                InstanceRequestOutcome.Refused,
                await firstOutcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );

            window.WindowState = WindowState.Minimized;
            (InstanceRequest second, Task<InstanceRequestOutcome> secondOutcome) = await requests.SendActivateAsync();

            Assert.True(start.App.ActivateRequested(second));

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await secondOutcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_while_stopping_answers_closing_and_raises_nothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Close();
            using var opened = new OpenedMainWindows();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            Assert.False(start.App.ActivateRequested(request));

            Assert.Empty(opened.Windows);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: the app is closing; answered closing.");
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_abandoned_activate_raises_nothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;
            int broughtForward = start.BroughtForwardCount;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();
            await Task.Run(requests.Server.Coordinator.StopAnswering, TestContext.Current.CancellationToken);

            Assert.False(start.App.ActivateRequested(request));

            Assert.Equal(WindowState.Minimized, window.WindowState);
            Assert.Equal(broughtForward, start.BroughtForwardCount);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: abandoned before the app took it; bringing nothing forward.");
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_whose_raise_throws_answers_refused_and_keeps_the_loop() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            static void ThrowOnRaise(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == Window.WindowStateProperty)
                {
                    throw new InvalidOperationException("The window could not come forward.");
                }
            }

            window.PropertyChanged += ThrowOnRaise;
            bool forward = true;
            Dispatcher.UIThread.Post(() => forward = start.App.ActivateRequested(request));
            Dispatcher.UIThread.RunJobs();
            window.PropertyChanged -= ThrowOnRaise;

            // The job returned instead of throwing into the loop, so the loop goes on to the next one.
            Assert.False(forward);
            bool nextRan = false;
            Dispatcher.UIThread.Post(() => nextRan = true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(nextRan);
            LogEntry error = Assert.Single(start.Log.Entries, entry => entry.Level == "error");
            Assert.Equal("Activate: could not bring a window forward.", error.Message);
            Assert.Equal(
                InstanceRequestOutcome.Refused,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_with_no_window_answers_refused() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var watchers = new FailingSettingsWatchers(failures: 1);
            using var start = new GateStart(viewerFiles: [], stopAnswering: null, LastWindowBehavior.KeepRunning);
            Assert.Null(start.StartWindow);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            using (FailingOpens(_ => { }))
            {
                Assert.False(start.App.ActivateRequested(request));
            }

            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: no main window to bring forward.");
            Assert.Equal(
                InstanceRequestOutcome.Refused,
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
    public Task A_viewer_that_cannot_open_theme_settings_shows_the_toast_in_that_viewer() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            MainWindow viewer = Assert.IsType<MainWindow>(ShowViewer(start.App));
            ToastCenter viewerToasts = ToastsOf(viewer);
            ToastCenter mainToasts = ToastsOf(start.Window);

            // A hidden owner cannot own the settings window, so its Show throws.
            viewer.Hide();
            viewer.RequestThemeSettings();

            Assert.Single(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "Theme settings: could not open the window.");
            ToastItem toast = Assert.Single(viewerToasts.Items);
            Assert.Equal((ToastKind.Warning, "Couldn't open the theme settings"), (toast.Kind, toast.Text));
            Assert.Empty(mainToasts.Items);
        });

    [Fact]
    public Task A_closed_viewer_no_longer_opens_theme_settings() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            MainWindow viewer = Assert.IsType<MainWindow>(ShowViewer(start.App));

            viewer.Close();
            viewer.RequestThemeSettings();

            Assert.Null(start.App.ThemeSettingsHost?.Window);
            Assert.DoesNotContain(start.Log.Entries, entry => entry.Message.StartsWith("Theme settings:", StringComparison.Ordinal));
        });

    [Fact]
    public Task A_theme_window_opened_from_a_viewer_stays_when_that_viewer_closes_and_the_main_window_remains() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window main = start.Window;
            MainWindow viewer = Assert.IsType<MainWindow>(ShowViewer(start.App));
            viewer.RequestThemeSettings();
            ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(start.App.ThemeSettingsHost?.Window);
            Assert.Same(viewer, settings.Owner);

            viewer.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewer.IsVisible);
            Assert.True(settings.IsVisible);
            Assert.Same(main, settings.Owner);
            Assert.Same(settings, start.App.ThemeSettingsHost?.Window);
        });

    [Fact]
    public Task The_successor_is_the_most_recently_active_other_window_not_minimised() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window main = start.Window;
            Window a = ShowViewer(start.App);
            Window b = ShowViewer(start.App);
            Window c = ShowViewer(start.App);
            Window minimised = ShowViewer(start.App);
            foreach (Window window in new[] { a, c, b, minimised, main })
            {
                window.Activate();
                Dispatcher.UIThread.RunJobs();
            }

            minimised.WindowState = WindowState.Minimized;
            start.App.ShowThemeSettings(main);
            ThemeSettingsWindow settings = Assert.IsType<ThemeSettingsWindow>(start.App.ThemeSettingsHost?.Window);

            main.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(settings.IsVisible);
            Assert.Same(b, settings.Owner);
        });

    [Fact]
    public Task A_grace_start_shows_the_main_window_only_once_the_grace_ends() =>
        Headless.Run(() =>
        {
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            Assert.Null(start.StartWindow);

            clock.Advance(LaunchArbiter.GracePeriod - TimeSpan.FromMilliseconds(1));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Empty(start.App.ViewerWindows);

            // The timer's callback only posts: the window is built by the UI job, not on the timer's thread.
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Null(start.App.MainDocumentWindow);
            Dispatcher.UIThread.RunJobs();

            Assert.True(start.App.MainDocumentWindow?.IsVisible);
            Assert.Empty(start.App.ViewerWindows);
        });

    [Fact]
    public Task A_Finder_open_in_the_grace_opens_a_viewer_and_no_main_window() =>
        Headless.Run(() =>
        {
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            string file = MissingFile();

            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", file, IsFolder: false)]);
            Window viewer = Assert.Single(start.App.ViewerWindows);
            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(start.App.MainDocumentWindow);
            Assert.Same(viewer, Assert.Single(start.App.ViewerWindows));
            Assert.Equal(file, Assert.IsType<DocumentWindowViewModel>(viewer.DataContext).SelectedPath);
        });

    [Fact]
    public Task A_grace_start_with_launch_files_opens_their_viewer_and_no_main_window() =>
        Headless.Run(() =>
        {
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [MissingFile()], clock);
            Window viewer = start.Window;

            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(start.App.MainDocumentWindow);
            Assert.Same(viewer, Assert.Single(start.App.ViewerWindows));
        });

    [Fact]
    public Task An_activate_in_the_grace_is_answered_opened_once_the_main_window_shows() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            Assert.True(start.App.ActivateRequested(request));
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: held until the launch grace ends.");
            Assert.False(outcome.IsCompleted);

            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.True(start.App.MainDocumentWindow?.IsVisible);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_in_the_grace_after_a_Finder_open_opens_the_main_window_too() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", MissingFile(), IsFolder: false)]);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            Assert.True(start.App.ActivateRequested(request));
            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.True(start.App.MainDocumentWindow?.IsVisible);
            Assert.Single(start.App.ViewerWindows);
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_in_the_grace_is_answered_refused_when_the_main_window_fails() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            using var watchers = new FailingSettingsWatchers(failures: 1);
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();

            Assert.True(start.App.ActivateRequested(request));
            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(start.App.MainDocumentWindow);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "The main window could not open.");
            Assert.Equal(
                InstanceRequestOutcome.Refused,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_held_in_the_grace_whose_launcher_hung_up_is_dropped() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", MissingFile(), IsFolder: false)]);
            Window viewer = Assert.Single(start.App.ViewerWindows);
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();
            Assert.True(start.App.ActivateRequested(request));
            await Task.Run(requests.Server.Coordinator.StopAnswering, TestContext.Current.CancellationToken);

            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            // An abandoned activate is never carried out: it asks for no main window, so the Finder viewer stays the only window.
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Same(viewer, Assert.Single(start.App.ViewerWindows));
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: abandoned while held for the launch grace; dropped.");
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task An_activate_held_when_the_app_exits_is_answered_closing_and_opens_nothing() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            using var opened = new OpenedMainWindows();
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();
            Assert.True(start.App.ActivateRequested(request));

            start.App.ReleaseMainWindow();
            Assert.Equal(
                InstanceRequestOutcome.Closing,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Message == "Activate: the app exits during the launch grace; answered closing to 1 held activate(s)."
            );

            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(opened.Windows);
            Assert.Null(start.App.MainDocumentWindow);
        });

    [Fact]
    public Task An_activate_in_the_grace_brings_the_Finder_viewer_forward_when_the_main_window_fails() =>
        Headless.RunAsync(async () =>
        {
            using var requests = new OpenRequests();
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", MissingFile(), IsFolder: false)]);
            Window viewer = Assert.Single(start.App.ViewerWindows);
            Dispatcher.UIThread.RunJobs();
            viewer.WindowState = WindowState.Minimized;
            (InstanceRequest request, Task<InstanceRequestOutcome> outcome) = await requests.SendActivateAsync();
            Assert.True(start.App.ActivateRequested(request));

            using (FailingOpens(_ => { }))
            {
                clock.Advance(LaunchArbiter.GracePeriod);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Null(start.App.MainDocumentWindow);
            Assert.Equal(WindowState.Normal, viewer.WindowState);
            Assert.Contains(start.Log.Entries, entry => entry.Level == "error" && entry.Message == "The main window could not open.");
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Activate: viewer window brought forward.");
            Assert.Equal(
                InstanceRequestOutcome.Confirmed,
                await outcome.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken)
            );
        });

    [Fact]
    public Task A_Dock_click_while_the_app_is_stopping_opens_nothing() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            start.Window.Close();
            using var opened = new OpenedMainWindows();

            start.App.OnActivated(ActivationKind.Reopen, []);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(opened.Windows);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Dock: the app is closing; not opening a window.");
        });

    [Fact]
    public Task A_Finder_item_that_cannot_be_read_is_skipped_and_the_rest_open() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            string file = MissingFile();

            List<App.ActivationItem> items = start.App.ReadActivationItems([
                ("file", () => throw new IOException("The item is gone.")),
                ("file", () => new App.ActivationItem("a.md", file, IsFolder: false)),
            ]);
            start.App.OnActivated(ActivationKind.File, items);

            Window viewer = Assert.Single(start.App.ViewerWindows);
            Assert.Equal(file, Assert.IsType<DocumentWindowViewModel>(viewer.DataContext).SelectedPath);
            LogEntry warning = Assert.Single(start.Log.Entries, entry => entry.Level == "warning");
            Assert.Equal("Finder open: could not read a file it named; skipped.", warning.Message);
        });

    [Fact]
    public Task Raise_restores_a_window_that_was_maximized_before_it_was_tracked() =>
        Headless.Run(() =>
        {
            var clock = new FakeTimeProvider();
            using var start = new GateStart(viewerFiles: [], clock);
            new SessionStore(start.SessionFile, start.Log).Save(new SessionState { Window = new WindowBounds(10, 10, 800, 600, IsMaximized: true) });
            clock.Advance(LaunchArbiter.GracePeriod);
            Dispatcher.UIThread.RunJobs();
            Window window = Assert.IsType<MainWindow>(start.App.MainDocumentWindow);
            Assert.Equal(WindowState.Maximized, window.WindowState);

            window.WindowState = WindowState.Minimized;
            start.App.BringAppForward();

            Assert.Equal(WindowState.Maximized, window.WindowState);
        });

    [Fact]
    public Task A_Finder_open_of_a_file_already_open_brings_its_window_forward() =>
        Headless.Run(() =>
        {
            string file = MissingFile();
            using var start = new GateStart(viewerFiles: [file], stopAnswering: null);
            Window holder = start.Window;
            Window other = ShowViewer(start.App);
            Dispatcher.UIThread.RunJobs();
            holder.WindowState = WindowState.Minimized;

            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", file, IsFolder: false)]);

            Window[] stillOpen = [holder, other];
            Assert.Equal(stillOpen, start.App.ViewerWindows);
            Assert.Equal(WindowState.Normal, holder.WindowState);
            Assert.True(holder.IsActive);
            Assert.Contains(start.Log.Entries, entry => entry.Message == $"Open: {file} is already open; brought its window forward.");
        });

    [Fact]
    public Task A_Finder_open_of_a_new_file_opens_a_viewer_over_it() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            string file = MissingFile();

            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("a.md", file, IsFolder: false)]);

            Window viewer = Assert.Single(start.App.ViewerWindows);
            Assert.Equal(file, Assert.IsType<DocumentWindowViewModel>(viewer.DataContext).SelectedPath);
        });

    [Fact]
    public Task A_Finder_open_skips_and_logs_folders_and_items_without_a_local_path() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            string file = MissingFile();

            start.App.OnActivated(
                ActivationKind.File,
                [
                    new App.ActivationItem("notes", Path.GetTempPath(), IsFolder: true),
                    new App.ActivationItem("remote.md", LocalPath: null, IsFolder: false),
                    new App.ActivationItem("a.md", file, IsFolder: false),
                ]
            );

            Window viewer = Assert.Single(start.App.ViewerWindows);
            DocumentWindowViewModel viewModel = Assert.IsType<DocumentWindowViewModel>(viewer.DataContext);
            Assert.Equal(file, viewModel.SelectedPath);
            Assert.False(viewModel.HasDocument(Path.GetTempPath()));
            Assert.Contains(start.Log.Entries, entry => entry.Level == "warning" && entry.Message == "Finder open: notes is a folder; skipped.");
            Assert.Contains(
                start.Log.Entries,
                entry => entry.Level == "warning" && entry.Message == "Finder open: remote.md has no local path; skipped."
            );
        });

    [Fact]
    public Task A_Finder_open_of_only_a_folder_opens_nothing() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            using var opened = new OpenedMainWindows();

            start.App.OnActivated(ActivationKind.File, [new App.ActivationItem("notes", Path.GetTempPath(), IsFolder: true)]);

            Assert.Empty(opened.Windows);
            Assert.Empty(start.App.ViewerWindows);
        });

    [Fact]
    public Task A_Dock_click_with_no_window_opens_the_main_window() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null, LastWindowBehavior.KeepRunning);
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(start.App.MainDocumentWindow);

            start.App.OnActivated(ActivationKind.Reopen, []);

            Assert.True(start.App.MainDocumentWindow?.IsVisible);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Dock: main window opened.");
        });

    [Fact]
    public Task A_Dock_click_with_only_a_viewer_open_opens_the_main_window() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [MissingFile()], stopAnswering: null, LastWindowBehavior.KeepRunning);
            Window viewer = start.Window;

            start.App.OnActivated(ActivationKind.Reopen, []);

            Assert.True(start.App.MainDocumentWindow?.IsVisible);
            Assert.Same(viewer, Assert.Single(start.App.ViewerWindows));
        });

    [Fact]
    public Task A_Dock_click_raises_a_minimized_main_window() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null, LastWindowBehavior.KeepRunning);
            using var opened = new OpenedMainWindows();
            Window main = start.Window;
            Dispatcher.UIThread.RunJobs();
            main.WindowState = WindowState.Minimized;

            start.App.OnActivated(ActivationKind.Reopen, []);

            Assert.Equal(WindowState.Normal, main.WindowState);
            Assert.Same(main, start.App.MainDocumentWindow);
            Assert.Empty(opened.Windows);
            Assert.Contains(start.Log.Entries, entry => entry.Message == "Dock: main window brought forward.");
        });

    [Fact]
    public Task A_background_activation_changes_nothing() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null, LastWindowBehavior.KeepRunning);
            start.Window.Close();
            Dispatcher.UIThread.RunJobs();
            using var opened = new OpenedMainWindows();

            start.App.OnActivated(ActivationKind.Background, []);

            Assert.Empty(opened.Windows);
            Assert.Null(start.App.MainDocumentWindow);
        });

    [Fact]
    public Task An_activation_that_throws_is_logged_and_does_not_escape() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null, LastWindowBehavior.KeepRunning);
            Window main = start.Window;
            Dispatcher.UIThread.RunJobs();
            main.WindowState = WindowState.Minimized;

            static void ThrowOnRaise(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == Window.WindowStateProperty)
                {
                    throw new InvalidOperationException("The window could not come forward.");
                }
            }

            main.PropertyChanged += ThrowOnRaise;
            try
            {
                start.App.OnActivated(ActivationKind.Reopen, []);
            }
            finally
            {
                main.PropertyChanged -= ThrowOnRaise;
            }

            LogEntry error = Assert.Single(start.Log.Entries, entry => entry.Level == "error");
            Assert.Equal("Activation (Reopen): could not carry it out.", error.Message);
        });

    [Fact]
    public Task A_quit_with_the_main_window_open_saves_the_session_and_releases() =>
        Headless.Run(() =>
        {
            int stops = 0;
            using var start = new GateStart(viewerFiles: [], stopAnswering: () => stops++, LastWindowBehavior.KeepRunning);
            Assert.IsType<DocumentWindowViewModel>(start.Window.DataContext).ToggleSidebar();
            var request = new ShutdownRequestedEventArgs();
            int askedAgain = 0;

            // Cmd+Q and the Dock's Quit reach the lifetime's ShutdownRequested, then its Exit runs ReleaseMainWindow.
            start.App.OnShutdownRequested(request, () => askedAgain++);
            Assert.False(request.Cancel);
            start.App.ReleaseMainWindow();

            Assert.Equal(0, askedAgain);
            Assert.Equal(1, stops);
            Assert.True(start.LoadSavedSession().SidebarHidden);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Null(start.App.Bridge);
        });

    [Fact]
    public Task Raise_restores_a_maximized_window_maximized() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;

            start.App.BringAppForward();

            Assert.Equal(WindowState.Maximized, window.WindowState);
        });

    [Fact]
    public Task Raise_restores_a_normal_window_normal_after_it_was_once_maximized() =>
        Headless.Run(() =>
        {
            using var start = new GateStart(viewerFiles: [], stopAnswering: null);
            Window window = start.Window;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Normal;
            window.WindowState = WindowState.Minimized;

            start.App.BringAppForward();

            Assert.Equal(WindowState.Normal, window.WindowState);
        });

    /// <summary>Shows a viewer window over a missing file as an open request does once the app took it, and returns it.</summary>
    private static Window ShowViewer(App app)
    {
        app.ShowNewViewer([MissingFile()]);
        return app.ViewerWindows[^1];
    }

    /// <summary>
    /// Makes every <see cref="MainWindow"/> shown until disposed throw from its Opened handler, after telling
    /// <paramref name="failed"/> which window it was, as a native window that fails after it appeared does.
    /// </summary>
    private static IDisposable FailingOpens(Action<Window> failed) =>
        Window.WindowOpenedEvent.AddClassHandler<MainWindow>(
            (window, _) =>
            {
                failed(window);
                throw new InvalidOperationException("The native window failed after it appeared.");
            }
        );

    private static ToastCenter ToastsOf(Window window) =>
        Assert.IsType<ToastCenter>(Assert.IsType<DocumentWindowViewModel>(window.DataContext).Toasts);

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
            // The window is hidden, so the settings window cannot take it as its owner and its Show throws.
            MainWindow main = Assert.IsType<MainWindow>(start.App.CreateStartWindow());
            main.Hide();
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
            using var opened = new OpenedMainWindows();
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
    /// Makes the app's settings watcher seam throw for its next calls, as many as asked, then build real watchers, as a
    /// window build that fails before any window exists; disposing it restores <see cref="App.CreateSettingsWatcher"/>.
    /// </summary>
    private sealed class FailingSettingsWatchers : IDisposable
    {
        private readonly Func<SettingsStore, SettingsWriter, IAppLog, SettingsWatcher> _before = App.CreateSettingsWatcher;
        private int _failuresLeft;

        public FailingSettingsWatchers(int failures)
        {
            _failuresLeft = failures;
            App.CreateSettingsWatcher = (store, writer, log) =>
            {
                if (_failuresLeft > 0)
                {
                    _failuresLeft--;
                    throw new IOException("The settings folder cannot be watched.");
                }

                return _before(store, writer, log);
            };
        }

        public void Dispose() => App.CreateSettingsWatcher = _before;
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
                Main = App.CreateStartWindow() ?? throw new InvalidOperationException("The main start window did not open.");
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

                // The app sets its bridge only once the whole shared set is built, so it is read when the watcher stops.
                var clock = new DisposeRecordingClock(() =>
                    WriterAndBridgeDisposedAtEachDispose.Add((IsWriterDisposed(writer), app.Bridge is not { } bridge || IsBridgeDisposed(bridge)))
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

        private readonly OpenedMainWindows _opened = new();

        public void Dispose()
        {
            try
            {
                App.ReleaseMainWindow();
                _opened.CloseVisible();
            }
            finally
            {
                _opened.Dispose();
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
    /// Records every <see cref="MainWindow"/> that opens until disposed, so a test can count the windows the app built and a
    /// fixture can close what a released start left shown in the shared headless session.
    /// </summary>
    private sealed class OpenedMainWindows : IDisposable
    {
        private readonly List<MainWindow> _windows = [];
        private readonly IDisposable _subscription;

        public OpenedMainWindows() => _subscription = Window.WindowOpenedEvent.AddClassHandler<MainWindow>((window, _) => _windows.Add(window));

        /// <summary>The main and viewer windows that opened, in order.</summary>
        public IReadOnlyList<MainWindow> Windows => _windows;

        /// <summary>Closes every recorded window still shown.</summary>
        public void CloseVisible()
        {
            foreach (MainWindow window in _windows.Where(window => window.IsVisible).ToList())
            {
                window.Close();
            }
        }

        /// <summary>Stops recording, then closes every recorded window still shown.</summary>
        public void Dispose()
        {
            _subscription.Dispose();
            CloseVisible();
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
        public Task<(InstanceRequest Request, Task<InstanceRequestOutcome> Outcome)> SendAsync(IReadOnlyList<string> files) =>
            ReadRequestAsync(Server.SendOpenAsync(files));

        /// <summary>Sends a no-arg launch's <c>activate</c>; returns its request as the server read it, and the launch's outcome.</summary>
        public Task<(InstanceRequest Request, Task<InstanceRequestOutcome> Outcome)> SendActivateAsync() =>
            ReadRequestAsync(Server.SendAsync(InstanceMessages.Activate));

        private async Task<(InstanceRequest Request, Task<InstanceRequestOutcome> Outcome)> ReadRequestAsync(Task<InstanceRequestOutcome> outcome)
        {
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

        private readonly OpenedMainWindows _opened = new();

        public GateStart(IReadOnlyList<string> viewerFiles, Action? stopAnswering)
            : this(viewerFiles, stopAnswering, LastWindowBehavior.Quit) { }

        public GateStart(IReadOnlyList<string> viewerFiles, Action? stopAnswering, LastWindowBehavior lastWindow)
            : this(viewerFiles, stopAnswering, lastWindow, graceClock: null) { }

        /// <summary>A macOS start: it keeps running at the last window and waits the launch grace on <paramref name="graceClock"/>.</summary>
        public GateStart(IReadOnlyList<string> viewerFiles, FakeTimeProvider graceClock)
            : this(viewerFiles, stopAnswering: null, LastWindowBehavior.KeepRunning, graceClock) { }

        private GateStart(IReadOnlyList<string> viewerFiles, Action? stopAnswering, LastWindowBehavior lastWindow, FakeTimeProvider? graceClock)
        {
            App = Assert.IsType<App>(Application.Current);
            try
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataFolder);
                Environment.SetEnvironmentVariable(App.HarnessVariable, null);
                App.LaunchGrace = graceClock is not null;
                App.LaunchClock = graceClock ?? TimeProvider.System;
                App.ViewerFiles = viewerFiles;
                App.ProcessLog = Log;
                App.Lifetime = new AppLifetimePolicy(lastWindow);
                App.QuitForTests = () => Quits++;
                App.StopAnswering = stopAnswering;
                App.ResetLog();
                StartWindow = App.CreateStartWindow();
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

        /// <summary>The window the start showed, or null when it failed to open.</summary>
        public Window? StartWindow { get; }

        /// <summary>The window the start showed.</summary>
        /// <exception cref="InvalidOperationException">The start window failed to open.</exception>
        public Window Window => StartWindow ?? throw new InvalidOperationException("The start window failed to open.");

        /// <summary>How many times the app asked to quit.</summary>
        public int Quits { get; private set; }

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

                if (App.MainDocumentWindow is { IsVisible: true } main)
                {
                    main.Close();
                }

                App.ReleaseMainWindow();
                _opened.CloseVisible();
            }
            finally
            {
                Restore();
            }
        }

        private void Restore()
        {
            _opened.Dispose();
            App.ResetLog();
            App.ViewerFiles = [];
            App.LaunchGrace = false;
            App.LaunchClock = TimeProvider.System;
            App.ProcessLog = null;
            App.Lifetime = null;
            App.QuitForTests = null;
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
