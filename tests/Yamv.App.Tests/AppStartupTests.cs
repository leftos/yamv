using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Theming;

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
            var viewModel = new DocumentWindowViewModel(state, surface, _ => "", new FakeTimeProvider(), log);
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

    private static bool IsBridgeDisposed(ThemeBridge bridge) =>
        (bool)typeof(ThemeBridge).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
}
