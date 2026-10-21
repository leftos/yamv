using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;

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
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                Environment.SetEnvironmentVariable(App.HarnessVariable, harnessBefore);
                Environment.SetEnvironmentVariable(App.ThemeSettingsHarnessVariable, settingsBefore);
            }
        });
}
