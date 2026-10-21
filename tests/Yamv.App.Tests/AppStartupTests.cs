using System.Reflection;
using System.Text;
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
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
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
