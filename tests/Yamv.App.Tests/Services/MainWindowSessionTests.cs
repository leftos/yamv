using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.Services;

public sealed class MainWindowSessionTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    [Fact]
    public Task Selection_change_writes_the_session_500_ms_later() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();
            fixture.Time.Advance(Delay - TimeSpan.FromMilliseconds(1));
            Assert.False(File.Exists(fixture.SessionFile));

            fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(fixture.DocB, fixture.Saved().SelectedFile);
        });

    [Fact]
    public Task Scroll_change_alone_writes_nothing_until_the_window_closes() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Surface.RaiseScroll(0.5);
            fixture.Time.Advance(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(fixture.SessionFile));

            fixture.Window.Close();
            Assert.Equal(0.5, fixture.Saved().ScrollPositions[fixture.DocA]);
        });

    [Fact]
    public Task Scroll_after_a_scheduled_change_is_written_when_the_window_closes() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();

            fixture.Surface.RaiseScroll(0.5);
            fixture.Time.Advance(Delay);
            fixture.Window.Close();

            Assert.Equal(0.5, fixture.Saved().ScrollPositions[fixture.DocB]);
        });

    [Fact]
    public Task Sidebar_width_change_alone_schedules_a_save() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Window.SidebarWidth = 360;
            await fixture.SettleAsync();
            fixture.Time.Advance(Delay);

            Assert.Equal(360, fixture.Saved().SidebarWidth);
        });

    [Fact]
    public Task Dispose_writes_a_change_made_after_the_window_closed() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Window.Close();
            File.Delete(fixture.SessionFile);

            fixture.ViewModel.SelectNext();
            fixture.DisposeSession();

            Assert.Equal(fixture.DocB, fixture.Saved().SelectedFile);
        });

    [Fact]
    public Task Closing_the_window_with_a_save_pending_writes_at_once() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();

            fixture.Window.Close();

            Assert.Equal(fixture.DocB, fixture.Saved().SelectedFile);
        });

    [Fact]
    public Task Saved_sidebar_width_follows_the_window_after_a_scheduled_save() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Window.SidebarWidth = 360;

            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();
            fixture.Time.Advance(Delay);

            Assert.Equal(360, fixture.Saved().SidebarWidth);
        });

    [Fact]
    public Task Saved_theme_is_the_one_the_bridge_was_asked_for() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("nope", "system");
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();
            fixture.Time.Advance(Delay);

            Assert.Equal("nope", fixture.Saved().ThemeId);
        });

    [Fact]
    public Task Locate_writes_the_new_path_to_the_session() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            File.Delete(fixture.DocA);
            fixture.ViewModel.Reload();
            await fixture.SettleAsync();
            string moved = Path.Combine(Path.GetDirectoryName(fixture.DocA)!, "moved.md");
            File.WriteAllText(moved, "# Moved");
            fixture.Files.ReplacementToPick = moved;

            fixture.Window.MissingPane.LocateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.SettleAsync();
            fixture.Time.Advance(Delay);

            SessionState saved = fixture.Saved();
            Assert.Equal([moved, fixture.DocB], saved.OpenFiles);
            Assert.Equal(moved, saved.SelectedFile);
        });

    /// <summary>
    /// A shown main window over two temp documents, saving through a <see cref="MainWindowSession"/> to a temp session file on
    /// a fake clock. <see cref="OpenAsync"/> lets the saves that opening and showing schedule run, then deletes the file.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-session-" + Guid.NewGuid().ToString("N"));
        private readonly MainWindowSession _session;

        public Fixture()
        {
            string docs = Path.Combine(_root, "docs");
            Directory.CreateDirectory(docs);
            DocA = Path.Combine(docs, "a.md");
            DocB = Path.Combine(docs, "b.md");
            File.WriteAllText(DocA, "# A");
            File.WriteAllText(DocB, "# B");
            SessionFile = Path.Combine(_root, "data", "session.json");
            var state = new DocumentWindowState(persist: true, _ => null, File.Exists, new GroupingOptions(null, StringComparison.OrdinalIgnoreCase));
            ViewModel = new DocumentWindowViewModel(state, Surface, File.ReadAllText, new FakeTimeProvider(), Log);
            Window = new MainWindow(ViewModel, new Border(), Surface, Log) { FileActions = Files };
            Bridge = new ThemeBridge(new ThemeCatalog(Path.Combine(_root, "themes"), Log), Application.Current!, Log);
            _session = new MainWindowSession(Window, ViewModel, Bridge, new SessionStore(SessionFile, Log), Time, Log);
        }

        public string DocA { get; }

        public string DocB { get; }

        public string SessionFile { get; }

        public FakeTimeProvider Time { get; } = new();

        public FakeFileActions Files { get; } = new();

        public FakeDocumentSurface Surface { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public DocumentWindowViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public ThemeBridge Bridge { get; }

        public async Task OpenAsync(params string[] paths)
        {
            ViewModel.Open(paths);
            Window.Show();
            await SettleAsync();
            Time.Advance(Delay);
            Assert.True(File.Exists(SessionFile), "opening the documents scheduled no save");
            File.Delete(SessionFile);
        }

        public async Task SettleAsync()
        {
            Settle();
            await ViewModel.WhenRendersSettled();
            Settle();
        }

        public SessionState Saved() => new SessionStore(SessionFile, Log).Load();

        public void DisposeSession() => _session.Dispose();

        public void Dispose()
        {
            _session.Dispose();
            if (Window.IsVisible)
            {
                Window.Close();
            }

            ViewModel.Dispose();
            Bridge.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
