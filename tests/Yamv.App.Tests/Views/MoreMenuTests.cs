using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Theming;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.Views;

/// <summary>The title bar's ⋯ menu in a viewer window, built over a temp data folder as the app builds one.</summary>
[Collection(nameof(EnvironmentVariables))]
public sealed class MoreMenuTests
{
    [Fact]
    public Task A_viewer_window_has_the_same_more_button() =>
        Headless.RunAsync(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "yamv-more-menu-" + Guid.NewGuid().ToString("N"));
            string? dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, Path.Combine(root, "data"));
            var log = new RecordingAppLog();
            var bridge = new ThemeBridge(new ThemeCatalog(AppPaths.ThemesDirectory, log), Application.Current!, log);
            MainWindowComposition? viewer = null;
            try
            {
                string document = Path.Combine(root, "notes.md");
                File.WriteAllText(document, "# Notes");
                viewer = MainWindowFactory.CreateViewer(
                    bridge,
                    new WidthStore(Path.Combine(root, "data", "widths.json"), log),
                    [document],
                    log,
                    new ViewerParts(new Border(), new FakeDocumentSurface(), new FakeDocumentChanges())
                );
                MainWindow window = viewer.Window;
                window.Show();
                Settle(window);
                TitleBar bar = window.WindowTitleBar;

                int widthIndex = bar.TitleBarTools.Children.IndexOf(bar.ContentWidthButton);
                Assert.Same(bar.MoreButton, bar.TitleBarTools.Children[widthIndex + 1]);
                // A pointer over the title bar's tools does not reach them in the headless window, so the test raises the click.
                bar.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);

                Assert.True(window.MorePopup.IsOpen);
                window.Close();
            }
            finally
            {
                viewer?.Dispose();
                bridge.Dispose();
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dataBefore);
                if (viewer is not null)
                {
                    await viewer.ViewModel.WhenRendersSettled();
                }

                Directory.Delete(root, recursive: true);
            }
        });

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
