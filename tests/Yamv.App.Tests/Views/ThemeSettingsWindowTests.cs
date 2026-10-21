using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.Views;

public sealed class ThemeSettingsWindowTests
{
    [Fact]
    public Task List_shows_the_built_in_and_yours_sections_and_their_rows() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            List<string> texts = VisibleTexts(fixture.Window);

            Assert.Contains("BUILT-IN", texts);
            Assert.Contains("YOURS", texts);
            Assert.Contains("Paper", texts);
            Assert.Contains("Studio", texts);
            Assert.Contains("Ink", texts);
            Assert.Contains("Mine", texts);
            Assert.Contains("nord", texts);
            Assert.Equal("Theme settings", fixture.Window.Title);
            Assert.False(fixture.Window.CanMinimize);
            Assert.False(fixture.Window.CanMaximize);
        });

    [Fact]
    public Task Selecting_the_invalid_row_shows_the_error_pane_and_hides_the_editor_host() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Assert.True(fixture.Find<ContentControl>("EditorHost").IsEffectivelyVisible);

            fixture.Click(fixture.Row("nord"));

            Assert.True(fixture.Find<Control>("InvalidPane").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("NoPreview").IsEffectivelyVisible);
            Assert.False(fixture.Find<ContentControl>("EditorHost").IsEffectivelyVisible);
            Assert.False(fixture.Find<ContentControl>("PreviewHost").IsEffectivelyVisible);
            List<string> texts = VisibleTexts(fixture.Window);
            Assert.Contains("This theme didn't load", texts);
            Assert.Contains(texts, text => text.Contains("'accent'", StringComparison.Ordinal));
            Assert.Contains("nord · didn't load", texts);
            Assert.False(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);
            Assert.False(fixture.Find<Button>("RevertButton").IsEffectivelyEnabled);
            Assert.True(fixture.Find<Button>("EditAsTextButton").IsEffectivelyEnabled);
        });

    [Fact]
    public Task A_dirty_working_copy_shows_the_title_bar_dot_and_enables_Save_and_Revert() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Assert.False(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
            Assert.False(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);

            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();

            Assert.True(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
            Assert.True(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);
            Assert.True(fixture.Find<Button>("RevertButton").IsEffectivelyEnabled);
        });

    [Fact]
    public Task A_built_in_selection_shows_Duplicate_to_edit_and_a_disabled_Edit_as_text() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Row("studio"));

            Assert.True(fixture.Find<Button>("DuplicateButton").IsEffectivelyVisible);
            Assert.False(fixture.Find<Button>("SaveButton").IsEffectivelyVisible);
            Assert.False(fixture.Find<Button>("EditAsTextButton").IsEffectivelyEnabled);
            Assert.True(fixture.Find<Control>("LockNote").IsEffectivelyVisible);
            Assert.Contains("BUILT-IN", VisibleTexts(fixture.Find<Control>("Header")));
        });

    [Fact]
    public Task Closing_while_dirty_shows_the_unsaved_confirm_and_closes_only_after_Discard() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.Window.Close();
            fixture.Settle();

            Assert.False(closed);
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.Contains("Unsaved changes", VisibleTexts(fixture.Find<Control>("Confirm")));
            Assert.Contains("“Mine” has changes that aren't saved.", VisibleTexts(fixture.Find<Control>("Confirm")));

            fixture.Click(fixture.Find<Button>("SecondaryButton"));

            Assert.True(closed);
            Assert.DoesNotContain("#3fb950", File.ReadAllText(fixture.PathOf("mine")), StringComparison.Ordinal);
        });

    [Fact]
    public Task Cancel_on_the_delete_confirm_leaves_the_file() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Task deleting = fixture.ViewModel.DeleteAsync(fixture.ViewModel.UserRows.Single(row => row.Id == "mine"));
            fixture.Settle();
            Control confirm = fixture.Find<Control>("Confirm");
            Assert.True(confirm.IsEffectivelyVisible);
            Assert.Contains("Delete theme", VisibleTexts(confirm));
            Assert.Contains(VisibleTexts(confirm), text => text.StartsWith("Move “Mine” to the ", StringComparison.Ordinal));
            Assert.False(fixture.Find<Button>("SecondaryButton").IsEffectivelyVisible);

            fixture.Click(fixture.Find<Button>("CancelButton"));

            Assert.True(deleting.IsCompletedSuccessfully);
            Assert.False(confirm.IsEffectivelyVisible);
            Assert.True(File.Exists(fixture.PathOf("mine")));
            Assert.Empty(fixture.Trashed);
        });

    [Fact]
    public Task Esc_on_a_confirm_is_Cancel() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));

            fixture.Click(fixture.Row("studio"));
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            fixture.Press(Key.Escape);

            Assert.False(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.Equal("mine", fixture.ViewModel.Selected?.Id);
            Assert.True(fixture.ViewModel.IsDirty);
            Assert.Empty(fixture.Applied);
        });

    [Fact]
    public Task An_open_confirm_disables_the_window_behind_it_until_it_closes() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            Assert.True(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);

            fixture.Window.Close();
            fixture.Settle();

            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.False(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);
            Assert.False(fixture.Find<Control>("AppearanceBox").IsEffectivelyEnabled);

            fixture.Click(fixture.Find<Button>("CancelButton"));

            Assert.True(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);
        });

    [Fact]
    public Task Cancel_on_the_close_confirm_keeps_the_window_open_and_Save_saves_then_closes() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.Window.Close();
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("CancelButton"));

            Assert.False(closed);
            Assert.False(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.True(fixture.ViewModel.IsDirty);

            fixture.Window.Close();
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("PrimaryButton"));

            Assert.True(closed);
            Assert.Contains("#3fb950", File.ReadAllText(fixture.PathOf("mine")), StringComparison.Ordinal);
        });

    private static List<string> VisibleTexts(Visual root) =>
        [
            .. root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Text is not null)
                .Select(text => text.Text!),
        ];

    /// <summary>A shown theme settings window over a temp themes folder holding a valid Mine, in use, and an invalid nord.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-theme-window-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PathOf("mine"), """{ "name": "Mine", "light": { "tokens": { "accent": "#112233" } } }""");
            File.WriteAllText(PathOf("nord"), """{ "name": "Nord", "dark": { "tokens": { "accent": "#88c0d" } } }""");
            ThemeSettingsWindow? window = null;
            ViewModel = new ThemeSettingsViewModel(
                new ThemeCatalog(_folder, Log),
                new ThemeSettingsSeams
                {
                    Apply = (id, appearance) => Applied.Add((id, appearance)),
                    Trash = new DeletingTrash(Trashed),
                    OpenAsText = _ => { },
                    ConfirmUnsaved = name => window!.ConfirmUnsavedAsync(name),
                    ConfirmDelete = name => window!.ConfirmDeleteAsync(name),
                },
                "mine",
                "light",
                Log
            );
            window = new ThemeSettingsWindow(ViewModel, Log);
            Window = window;
            Window.Show();
            Settle();
        }

        public RecordingAppLog Log { get; } = new();

        public List<(string Id, string Appearance)> Applied { get; } = [];

        public List<string> Trashed { get; } = [];

        public ThemeSettingsViewModel ViewModel { get; }

        public ThemeSettingsWindow Window { get; }

        public string PathOf(string id) => Path.Combine(_folder, id + ThemeCatalog.FileSuffix);

        public T Find<T>(string name)
            where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
            ?? Window.GetLogicalDescendants().OfType<T>().First(control => control.Name == name);

        public Panel Row(string id) =>
            Window
                .GetVisualDescendants()
                .OfType<Panel>()
                .Single(panel => panel.Classes.Contains("row") && panel.DataContext is ThemeRowViewModel row && row.Id == id);

        public void Click(Visual visual)
        {
            Point point =
                visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), Window)
                ?? throw new InvalidOperationException("The control is not in the window.");
            Window.MouseMove(point);
            Settle();
            Window.MouseDown(point, MouseButton.Left);
            Settle();
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }

        public void Press(Key key)
        {
            Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            Settle();
        }

        /// <summary>Runs queued UI work, a layout pass and a render tick, so bounds reflect visibility changes before the next input.</summary>
        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            if (ViewModel.IsDirty)
            {
                ViewModel.RevertCommand.Execute(null);
            }

            Window.Close();
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>Records each path it is given and deletes the file.</summary>
    private sealed class DeletingTrash(List<string> trashed) : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            trashed.Add(path);
            File.Delete(path);
        }
    }
}
