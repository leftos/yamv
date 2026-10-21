using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.App.Views.ThemeSettings;
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

    [Fact]
    public Task The_editor_shows_the_three_tabs_and_rows_with_their_label_and_token_name() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            Assert.True(fixture.Find<Control>("TokenEditor").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("TabStrip").IsEffectivelyVisible);
            List<string> texts = VisibleTexts(fixture.Window);
            Assert.Contains("Shared", texts);
            Assert.Contains("Light", texts);
            Assert.Contains("Dark", texts);
            Assert.Contains("PAGE", texts);
            Assert.Contains("Selected row", texts);
            Assert.Contains("selectionBackground", texts);
            Assert.Contains("Grey values come from Shared or the defaults. ↺ removes a value from this tab.", texts);
        });

    [Fact]
    public Task An_inherited_value_is_muted_a_set_one_normal_with_a_reset_that_makes_it_inherited() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Assert.Same(fixture.Muted, fixture.Field("text").Foreground);
            Assert.Same(fixture.Normal, fixture.Field("accent").Foreground);
            Assert.False(fixture.Reset("text").IsEffectivelyVisible);
            Button reset = fixture.Reset("accent");
            Assert.True(reset.IsEffectivelyVisible);

            fixture.Click(reset);

            Assert.Same(fixture.Muted, fixture.Field("accent").Foreground);
            Assert.False(fixture.Reset("accent").IsEffectivelyVisible);
            Assert.False(fixture.ViewModel.WorkingTheme!.Light!.Tokens.ContainsKey("accent"));
            Assert.True(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
        });

    [Fact]
    public Task Typing_a_hex_and_Enter_sets_it_and_shows_the_token_dot_and_the_title_bar_dot() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Assert.False(fixture.Dot("quote").IsEffectivelyVisible);
            TextBox quote = fixture.Field("quote");
            quote.Focus();
            quote.SelectAll();
            fixture.Window.KeyTextInput("#3fb950");
            fixture.Settle();

            fixture.Press(Key.Enter);

            Assert.Equal(new ColorValue("#3fb950"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
            Assert.True(fixture.Dot("quote").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
            Assert.True(fixture.Reset("quote").IsEffectivelyVisible);
        });

    [Fact]
    public Task The_Dark_tab_of_a_light_only_theme_shows_the_note_and_Add_dark_over_disabled_rows_until_added() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Find<Button>("DarkTab"));

            Assert.Contains("This theme has no dark half. In dark mode YAMV shows its light half.", VisibleTexts(fixture.Window));
            Button add = fixture.Find<Button>("AddHalfButton");
            Assert.True(add.IsEffectivelyVisible);
            Assert.True(add.IsEffectivelyEnabled);
            Assert.Contains("Add dark", VisibleTexts(add));
            Assert.False(fixture.Field("accent").IsEffectivelyEnabled);

            fixture.Click(add);

            Assert.NotNull(fixture.ViewModel.WorkingTheme?.Dark);
            Assert.False(fixture.Find<Control>("MissingHalfStrip").IsEffectivelyVisible);
            Assert.True(fixture.Field("accent").IsEffectivelyEnabled);
            Assert.Same(fixture.Muted, fixture.Field("accent").Foreground);
        });

    [Fact]
    public Task The_editor_is_absent_for_a_theme_that_did_not_load() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Row("nord"));

            Assert.False(fixture.Find<Control>("TokenEditor").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("TabStrip").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("InvalidPane").IsEffectivelyVisible);
            Assert.DoesNotContain("Selected row", VisibleTexts(fixture.Window));
        });

    [Fact]
    public Task Enter_on_an_inherited_field_with_its_shown_value_sets_it_with_a_reset_arrow() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string shown = fixture.Field("quote").Text!;

            fixture.Type("quote", shown);
            fixture.Press(Key.Enter);

            Assert.Equal(new ColorValue(shown), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
            Assert.True(fixture.Reset("quote").IsEffectivelyVisible);
            Assert.Same(fixture.Normal, fixture.Field("quote").Foreground);
        });

    [Fact]
    public Task Tab_away_from_an_inherited_field_with_its_shown_value_leaves_it_inherited() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string shown = fixture.Field("quote").Text!;

            fixture.Type("quote", shown);
            fixture.Press(Key.Tab);

            Assert.False(fixture.Field("quote").IsFocused);
            Assert.False(fixture.ViewModel.WorkingTheme!.Light!.Tokens.ContainsKey("quote"));
            Assert.False(fixture.Reset("quote").IsEffectivelyVisible);
            Assert.False(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task Tab_away_from_a_changed_field_commits_it() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Type("quote", "#3fb950");
            fixture.Press(Key.Tab);

            Assert.Equal(new ColorValue("#3fb950"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
            Assert.True(fixture.Dot("quote").IsEffectivelyVisible);
        });

    [Fact]
    public Task Esc_in_a_field_puts_the_shown_value_back_without_applying() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;

            fixture.Type("accent", "#3fb950");
            fixture.Press(Key.Escape);

            Assert.Equal("#112233", fixture.Field("accent").Text);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
            Assert.False(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
        });

    [Fact]
    public Task A_built_in_theme_shows_its_rows_disabled_without_reset_arrows() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Row("studio"));

            Assert.False(fixture.Field("accent").IsEffectivelyEnabled);
            Assert.False(fixture.Swatch("accent").IsEffectivelyEnabled);
            Assert.DoesNotContain(
                fixture.Window.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("reset") && button.IsEffectivelyVisible
            );
        });

    [Fact]
    public Task A_swatch_click_raises_PickRequested_with_the_token_and_the_tab() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            List<TokenPickEventArgs> picks = [];
            fixture.Find<TokenEditor>("TokenEditor").PickRequested += (_, args) => picks.Add(args);

            fixture.Click(fixture.Swatch("accent"));

            TokenPickEventArgs pick = Assert.Single(picks);
            Assert.Equal("accent", pick.Token);
            Assert.Equal(ThemeTab.Light, pick.Tab);
        });

    [Fact]
    public Task A_tab_switch_clears_a_row_error() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Type("accent", "#12345");
            fixture.Press(Key.Enter);
            Assert.True(fixture.Error("accent").IsEffectivelyVisible);

            fixture.Click(fixture.Find<Button>("SharedTab"));

            Assert.False(fixture.Error("accent").IsEffectivelyVisible);

            fixture.Click(fixture.Find<Button>("LightTab"));

            Assert.False(fixture.Error("accent").IsEffectivelyVisible);
            Assert.Equal("#112233", fixture.Field("accent").Text);
        });

    [Fact]
    public Task Save_clears_the_token_dot() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
            fixture.Settle();
            Assert.True(fixture.Dot("quote").IsEffectivelyVisible);

            fixture.Click(fixture.Find<Button>("SaveButton"));

            Assert.False(fixture.Dot("quote").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
        });

    [Fact]
    public Task A_theme_switch_clears_the_token_dot() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
            fixture.Settle();
            Assert.True(fixture.Dot("quote").IsEffectivelyVisible);

            fixture.Click(fixture.Row("studio"));
            fixture.Click(fixture.Find<Button>("SecondaryButton"));

            Assert.Equal("studio", fixture.ViewModel.Selected?.Id);
            Assert.False(fixture.Dot("quote").IsEffectivelyVisible);
        });

    [Fact]
    public Task The_Shared_tab_hint_says_grey_values_come_from_the_defaults() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Find<Button>("SharedTab"));

            List<string> texts = VisibleTexts(fixture.Window);
            Assert.Contains("Grey values come from the defaults. ↺ removes a value from this tab.", texts);
            Assert.DoesNotContain("Grey values come from Shared or the defaults. ↺ removes a value from this tab.", texts);

            fixture.Click(fixture.Find<Button>("LightTab"));

            Assert.Contains("Grey values come from Shared or the defaults. ↺ removes a value from this tab.", VisibleTexts(fixture.Window));
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
            Window.Resources["YamvText"] = Normal;
            Window.Resources["YamvTextMuted"] = Muted;
            Window.Show();
            Settle();
        }

        public RecordingAppLog Log { get; } = new();

        public List<(string Id, string Appearance)> Applied { get; } = [];

        public List<string> Trashed { get; } = [];

        /// <summary>The window's <c>YamvText</c> brush, so a test can tell it from the muted one.</summary>
        public SolidColorBrush Normal { get; } = new(Colors.Black);

        /// <summary>The window's <c>YamvTextMuted</c> brush.</summary>
        public SolidColorBrush Muted { get; } = new(Colors.Gray);

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

        /// <summary>The value field of the token row <paramref name="name"/>.</summary>
        public TextBox Field(string name) => InRow<TextBox>(name, "fld");

        /// <summary>The reset arrow of the token row <paramref name="name"/>.</summary>
        public Button Reset(string name) => InRow<Button>(name, "reset");

        /// <summary>The unsaved dot of the token row <paramref name="name"/>.</summary>
        public TextBlock Dot(string name) => InRow<TextBlock>(name, "mod");

        /// <summary>The swatch of the colour token row <paramref name="name"/>.</summary>
        public Button Swatch(string name) => InRow<Button>(name, "swatch");

        /// <summary>The error line under the token row <paramref name="name"/>.</summary>
        public TextBlock Error(string name) => InRow<TextBlock>(name, "err");

        /// <summary>Focuses the field of the token row <paramref name="name"/> and types <paramref name="text"/> over its text.</summary>
        public void Type(string name, string text)
        {
            TextBox field = Field(name);
            field.Focus();
            field.SelectAll();
            Window.KeyTextInput(text);
            Settle();
        }

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

        private T InRow<T>(string name, string className)
            where T : Control =>
            Window
                .GetVisualDescendants()
                .OfType<T>()
                .Single(control => control.Classes.Contains(className) && control.DataContext is TokenRowViewModel row && row.Name == name);

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
