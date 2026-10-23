using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.Services;

public sealed class ThemeSettingsHostTests
{
    [Fact]
    public Task Closing_an_owner_offers_its_owned_window_a_cancellable_close_first_and_a_cancel_keeps_both_open() =>
        Headless.Run(() =>
        {
            var owner = new Window();
            var owned = new Window();
            List<WindowCloseReason> ownedReasons = [];
            bool ownerClosingRaised = false;
            bool cancel = true;
            owned.Closing += (_, e) =>
            {
                ownedReasons.Add(e.CloseReason);
                e.Cancel = cancel;
            };
            owner.Closing += (_, _) => ownerClosingRaised = true;
            owner.Show();
            owned.Show(owner);

            owner.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([WindowCloseReason.OwnerWindowClosing], ownedReasons);
            Assert.False(ownerClosingRaised);
            Assert.True(owner.IsVisible);
            Assert.True(owned.IsVisible);

            cancel = false;
            owner.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(ownerClosingRaised);
            Assert.False(owner.IsVisible);
            Assert.False(owned.IsVisible);
        });

    [Fact]
    public Task Opening_shows_one_window_owned_by_the_requesting_window() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window owner = fixture.NewOwner();

            fixture.Host.Show(owner);
            fixture.Settle();

            ThemeSettingsWindow window = Assert.IsType<ThemeSettingsWindow>(fixture.Host.Window);
            Assert.Same(owner, window.Owner);
            Assert.True(window.IsVisible);
            Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.Equal(1, fixture.Builds);
        });

    [Fact]
    public Task A_second_open_activates_the_open_window_and_builds_none() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow first = fixture.Host.Window!;
            first.WindowState = WindowState.Minimized;
            fixture.Settle();

            fixture.Host.Show(owner);
            fixture.Settle();

            Assert.Same(first, fixture.Host.Window);
            Assert.Equal(WindowState.Normal, first.WindowState);
            Assert.True(first.IsVisible);
            Assert.Equal(1, fixture.Builds);
        });

    [Fact]
    public Task A_second_open_from_another_window_keeps_the_first_owner() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window first = fixture.NewOwner();
            Window second = fixture.NewOwner();
            fixture.Host.Show(first);

            fixture.Host.Show(second);
            fixture.Settle();

            Assert.Same(first, fixture.Host.Window!.Owner);
            Assert.Equal(1, fixture.Builds);
        });

    [Fact]
    public Task After_the_window_closes_the_next_open_builds_a_new_one() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow first = fixture.Host.Window!;

            first.Close();
            fixture.Settle();
            Assert.Null(fixture.Host.Window);

            fixture.Host.Show(owner);
            fixture.Settle();

            Assert.NotSame(first, fixture.Host.Window);
            Assert.True(fixture.Host.Window!.IsVisible);
            Assert.Equal(2, fixture.Builds);
        });

    [Fact]
    public Task The_window_starts_with_the_bridges_requested_theme_and_appearance() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "dark");
            Window owner = fixture.NewOwner();

            fixture.Host.Show(owner);
            fixture.Settle();

            ThemeSettingsViewModel viewModel = fixture.ViewModel;
            Assert.Equal("mine", viewModel.InUseId);
            Assert.Equal("mine", viewModel.Selected?.Id);
            Assert.Equal("dark", viewModel.SelectedAppearance.Value);
        });

    [Fact]
    public Task Flipping_Appearance_in_the_window_goes_through_Apply_and_is_saved() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            fixture.Host.Show(fixture.NewOwner());
            int requestChanges = 0;
            fixture.Bridge.RequestChanged += (_, _) => requestChanges++;

            fixture.ViewModel.SelectedAppearance = ThemeSettingsViewModel.Appearances.Single(choice => choice.Value == "dark");
            fixture.Settle();

            Assert.Equal(1, requestChanges);
            Assert.Equal("mine", fixture.Bridge.RequestedThemeId);
            Assert.Equal("dark", fixture.Bridge.RequestedAppearance);
            Assert.Equal(Appearance.Dark, fixture.Bridge.Current?.Appearance);
            Assert.Equal("dark", fixture.ViewModel.SelectedAppearance.Value);
        });

    [Fact]
    public Task Flipping_Appearance_while_the_theme_in_use_is_invalid_keeps_its_last_good_version() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            File.WriteAllText(fixture.PathOf("mine"), """{ "name": "Mine", """);
            fixture.Bridge.Reload();
            fixture.Host.Show(fixture.NewOwner());

            fixture.ViewModel.SelectedAppearance = ThemeSettingsViewModel.Appearances.Single(choice => choice.Value == "dark");
            fixture.Settle();

            Assert.Equal("mine", fixture.Bridge.RequestedThemeId);
            Assert.Equal("mine", fixture.Bridge.Current?.Id);
            Assert.Equal(Appearance.Dark, fixture.Bridge.Current?.Appearance);
        });

    [Fact]
    public Task ThemesReloaded_lists_the_new_themes_and_updates_the_in_use_theme() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            fixture.Host.Show(fixture.NewOwner());
            File.WriteAllText(fixture.PathOf("sunset"), """{ "name": "Sunset", "light": { "tokens": { "accent": "#d2691e" } } }""");
            File.WriteAllText(fixture.PathOf("mine"), """{ "name": "Mine", "light": { "tokens": { "accent": "#445566" } } }""");

            fixture.Bridge.Reload();
            fixture.Settle();

            ThemeSettingsViewModel viewModel = fixture.ViewModel;
            Assert.Contains(viewModel.UserRows, row => row.Id == "sunset");
            Assert.Equal("mine", viewModel.InUseId);
            Assert.Equal(new ColorValue("#445566"), viewModel.WorkingTheme!.Light!.Tokens["accent"]);
        });

    [Fact]
    public Task A_settings_file_change_of_theme_reaches_an_open_window() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            fixture.Host.Show(fixture.NewOwner());
            int requestChanges = 0;
            fixture.Bridge.RequestChanged += (_, _) => requestChanges++;

            fixture.Bridge.Apply("ink", "dark");
            fixture.Settle();

            ThemeSettingsViewModel viewModel = fixture.ViewModel;
            Assert.Equal("ink", viewModel.InUseId);
            Assert.Equal("dark", viewModel.SelectedAppearance.Value);
            Assert.Equal("mine", viewModel.Selected?.Id);
            Assert.Equal(1, requestChanges);
            Assert.Equal("ink", fixture.Bridge.RequestedThemeId);
        });

    [Fact]
    public Task Disposing_the_host_unhooks_the_bridge_events_and_closes_the_window() =>
        Headless.Run(() =>
        {
            var fixture = new Fixture();
            try
            {
                fixture.Bridge.Apply("mine", "light");
                fixture.Host.Show(fixture.NewOwner());
                ThemeSettingsWindow window = fixture.Host.Window!;
                fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
                bool closed = false;
                window.Closed += (_, _) => closed = true;

                fixture.Host.Dispose();
                fixture.Settle();

                Assert.True(closed);
                Assert.Null(fixture.Host.Window);
                Assert.Equal(0, Subscribers(fixture.Bridge, nameof(ThemeBridge.RequestChanged)));
                Assert.Equal(0, Subscribers(fixture.Bridge, nameof(ThemeBridge.ThemesReloaded)));
                fixture.Host.Dispose();
            }
            finally
            {
                fixture.Dispose();
            }
        });

    [Fact]
    public Task Trash_gets_the_window_handle() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window owner = fixture.NewOwner();

            fixture.Host.Show(owner);
            fixture.Settle();

            Func<Window> trashOwner = Assert.Single(fixture.TrashOwners);
            Assert.Same(fixture.Host.Window, trashOwner());
        });

    [Fact]
    public Task Owner_close_with_unsaved_edits_waits_on_the_confirm_and_Cancel_keeps_both_windows_open() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow window = fixture.Host.Window!;
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();

            owner.Close();
            fixture.Settle();

            Assert.True(Find<Control>(window, "Confirm").IsEffectivelyVisible);
            Assert.True(owner.IsVisible);
            Assert.True(window.IsVisible);

            Click(window, Find<Button>(window, "CancelButton"));

            Assert.True(owner.IsVisible);
            Assert.True(window.IsVisible);
            Assert.Same(window, fixture.Host.Window);
            Assert.True(fixture.ViewModel.IsDirty);
        });

    [Theory]
    [InlineData("PrimaryButton")]
    [InlineData("SecondaryButton")]
    public Task Owner_close_with_unsaved_edits_goes_ahead_after_Save_or_Discard(string button) =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow window = fixture.Host.Window!;
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            owner.Close();
            fixture.Settle();
            Assert.True(Find<Control>(window, "Confirm").IsEffectivelyVisible);

            Click(window, Find<Button>(window, button));
            fixture.Settle();

            Assert.False(owner.IsVisible);
            Assert.False(window.IsVisible);
            Assert.Null(fixture.Host.Window);
            Assert.Equal(button == "PrimaryButton", File.ReadAllText(fixture.PathOf("mine")).Contains("#3fb950", StringComparison.Ordinal));
        });

    [Fact]
    public Task A_failed_show_leaves_the_next_show_free_to_build_a_window() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window closedOwner = fixture.NewOwner();
            closedOwner.Close();
            fixture.Settle();

            Assert.Throws<InvalidOperationException>(() => fixture.Host.Show(closedOwner));
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            fixture.Settle();

            Assert.True(fixture.Host.Window!.IsVisible);
            Assert.Same(owner, fixture.Host.Window.Owner);
            Assert.Equal(2, fixture.Builds);
        });

    [Fact]
    public Task Owner_close_without_edits_closes_the_settings_window_too() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow window = fixture.Host.Window!;

            owner.Close();
            fixture.Settle();

            Assert.False(owner.IsVisible);
            Assert.False(window.IsVisible);
            Assert.Null(fixture.Host.Window);
        });

    [Fact]
    public Task Shutdown_with_unsaved_edits_asks_and_Cancel_cancels_the_shutdown() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Task<bool> withoutWindow = fixture.Host.ConfirmShutdownAsync();
            Assert.True(withoutWindow.IsCompletedSuccessfully);
            Assert.True(withoutWindow.Result);
            fixture.Bridge.Apply("mine", "light");
            fixture.Host.Show(fixture.NewOwner());
            ThemeSettingsWindow window = fixture.Host.Window!;
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();

            Task<bool> check = fixture.Host.ConfirmShutdownAsync();
            fixture.Settle();
            Assert.False(check.IsCompleted);
            Assert.True(Find<Control>(window, "Confirm").IsEffectivelyVisible);
            Click(window, Find<Button>(window, "CancelButton"));

            Assert.True(check.IsCompletedSuccessfully);
            Assert.False(check.Result);
            Assert.True(window.IsVisible);
            Assert.True(fixture.ViewModel.IsDirty);
        });

    [Theory]
    [InlineData("import")]
    [InlineData("delete")]
    public Task Closing_the_owner_cancels_an_open_import_notice_or_delete_confirm(string dialog) =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Bridge.Apply("mine", "light");
            Window owner = fixture.NewOwner();
            fixture.Host.Show(owner);
            ThemeSettingsWindow window = fixture.Host.Window!;
            ThemeSettingsViewModel viewModel = fixture.ViewModel;
            Task pending;
            if (dialog == "import")
            {
                fixture.FileActions.ThemeToPick = fixture.WriteOutside("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
                pending = viewModel.ImportCommand.ExecuteAsync(null);
            }
            else
            {
                pending = viewModel.DeleteAsync(viewModel.UserRows.Single(row => row.Id == "alpha"));
            }

            fixture.Settle();
            Assert.True(Find<Control>(window, dialog == "import" ? "ImportNotice" : "Confirm").IsEffectivelyVisible);

            owner.Close();
            fixture.Settle();

            Assert.True(pending.IsCompletedSuccessfully);
            Assert.False(owner.IsVisible);
            Assert.False(window.IsVisible);
            Assert.True(File.Exists(fixture.PathOf("alpha")));
            Assert.False(File.Exists(fixture.PathOf("solar")));
        });

    /// <summary>How many handlers the bridge's field-like event <paramref name="name"/> holds.</summary>
    private static int Subscribers(ThemeBridge bridge, string name) =>
        (typeof(ThemeBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge) as Delegate)?.GetInvocationList().Length
        ?? 0;

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? window.GetLogicalDescendants().OfType<T>().First(control => control.Name == name);

    private static void Click(Window window, Visual visual)
    {
        Point point =
            visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control is not in the window.");
        window.MouseMove(point);
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        Settle(window);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A host over a temp themes folder holding valid Mine and Alpha, with its own bridge, a recording trash factory and fake
    /// file actions; disposing it disposes the host, closes the owners and deletes the folders.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-theme-host-" + Guid.NewGuid().ToString("N"));
        private readonly List<Window> _owners = [];

        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(
                PathOf("mine"),
                """{ "name": "Mine", "light": { "tokens": { "accent": "#112233" } }, "dark": { "tokens": { "accent": "#334455" } } }"""
            );
            File.WriteAllText(PathOf("alpha"), """{ "name": "Alpha", "dark": { "tokens": { "accent": "#223344" } } }""");
            Bridge = new ThemeBridge(new ThemeCatalog(_folder, Log), Application.Current!, Log);
            Host = new ThemeSettingsHost(
                Bridge,
                new ThemeCatalog(_folder, Log),
                Log,
                new ThemeSettingsHostSeams
                {
                    Trash = windowOf =>
                    {
                        TrashOwners.Add(windowOf);
                        return new DeletingTrash();
                    },
                    FileActions = FileActions,
                }
            );
        }

        public RecordingAppLog Log { get; } = new();

        public ThemeBridge Bridge { get; }

        public ThemeSettingsHost Host { get; }

        public FakeFileActions FileActions { get; } = new();

        /// <summary>The window getter each trash was made with, one per window the host built.</summary>
        public List<Func<Window>> TrashOwners { get; } = [];

        /// <summary>How many settings windows the host built.</summary>
        public int Builds => TrashOwners.Count;

        /// <summary>The open settings window's view model.</summary>
        public ThemeSettingsViewModel ViewModel => Assert.IsType<ThemeSettingsViewModel>(Host.Window!.DataContext);

        private string OutsideFolder => _folder + "-outside";

        public string PathOf(string id) => Path.Combine(_folder, id + ThemeCatalog.FileSuffix);

        public string WriteOutside(string fileName, string text)
        {
            Directory.CreateDirectory(OutsideFolder);
            string path = Path.Combine(OutsideFolder, fileName);
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>A shown window to own the settings window.</summary>
        public Window NewOwner()
        {
            var owner = new Window();
            owner.Show();
            _owners.Add(owner);
            return owner;
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            if (Host.Window is { } window)
            {
                window.UpdateLayout();
            }

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Host.Dispose();
            foreach (Window owner in _owners)
            {
                owner.Close();
            }

            Bridge.Dispose();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(_folder, recursive: true);
            if (Directory.Exists(OutsideFolder))
            {
                Directory.Delete(OutsideFolder, recursive: true);
            }
        }
    }

    /// <summary>Deletes the file, as a move to the trash would remove it from the folder.</summary>
    private sealed class DeletingTrash : IFileTrash
    {
        public void MoveToTrash(string path) => File.Delete(path);
    }
}
