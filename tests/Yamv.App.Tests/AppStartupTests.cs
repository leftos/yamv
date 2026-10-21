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
}
