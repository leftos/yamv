using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.Services;
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

    [Theory]
    [InlineData(true, "Theme settings (Administrator)")]
    [InlineData(false, "Theme settings")]
    public Task An_elevated_window_shows_the_Administrator_chip_and_says_so_in_its_title(bool elevated, string title) =>
        Headless.Run(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "yamv-theme-admin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            (ThemeSettingsWindow window, _) = BuildAdministratorWindow(folder, "studio", elevated);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(elevated, window.AdministratorChip.IsEffectivelyVisible);
                Assert.Equal(title, window.Title);
                Assert.Equal(elevated, VisibleTexts(window.TitleBar).Contains("Administrator"));
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, recursive: true);
            }
        });

    [Fact]
    public Task A_long_theme_name_trims_before_the_Administrator_chip_does() =>
        Headless.Run(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "yamv-theme-admin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string longName = new('t', 150);
            File.WriteAllText(
                Path.Combine(folder, "long" + ThemeCatalog.FileSuffix),
                $$"""{ "name": "{{longName}}", "light": { "tokens": { "accent": "#112233" } } }"""
            );
            (ThemeSettingsWindow window, _) = BuildAdministratorWindow(folder, "long", elevated: true);
            window.Width = 760;
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Border chip = window.AdministratorChip;
                Assert.Contains(longName, window.TitleDetail.Text, StringComparison.Ordinal);
                double shownWidth = chip.Bounds.Width;
                Assert.True(chip.Bounds.Right <= window.TitleText.Bounds.Width, "the chip runs out of the title area");
                Assert.True(window.TitleText.Bounds.Right <= window.TitleBar.Bounds.Width, "the title area runs out of the bar");
                chip.Measure(Size.Infinity);
                Assert.Equal(chip.DesiredSize.Width - chip.Margin.Left - chip.Margin.Right, shownWidth, precision: 3);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, recursive: true);
            }
        });

    /// <summary>Builds a settings window over the themes in <paramref name="folder"/> as the host does, with <paramref name="themeId"/> selected.</summary>
    private static (ThemeSettingsWindow Window, ThemeSettingsViewModel ViewModel) BuildAdministratorWindow(
        string folder,
        string themeId,
        bool elevated
    )
    {
        var log = new RecordingAppLog();
        return ThemeSettingsHost.BuildWindow(
            new ThemeSettingsWindowParts
            {
                Catalog = new ThemeCatalog(folder, log),
                Apply = (_, _) => { },
                ThemeId = themeId,
                Appearance = "light",
                Log = log,
                Trash = _ => new DeletingTrash([]),
                OpenAsText = _ => { },
                PickImport = _ => Task.FromResult<string?>(null),
                FileActions = new FakeFileActions(),
                Elevated = elevated,
            }
        );
    }

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

    [Fact]
    public Task A_swatch_click_opens_the_picker_with_the_old_and_new_colours_and_the_caption() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Swatch("accent"));

            Assert.True(fixture.PickerOpen);
            Assert.True(fixture.Find<Control>("ColorPicker").IsEffectivelyVisible);
            Assert.Contains("Was #112233. Esc puts it back.", VisibleTexts(fixture.Find<Control>("ColorPicker")));
            Assert.Equal(Color.Parse("#112233"), ((ISolidColorBrush)fixture.Find<Border>("PickerOld").Background!).Color);
            Assert.Equal(Color.Parse("#112233"), ((ISolidColorBrush)fixture.Find<Border>("PickerNew").Background!).Color);
            Assert.Equal("#112233", fixture.Find<TextBox>("PickerHex").Text);
        });

    [Fact]
    public Task A_valid_hex_typed_in_the_picker_sets_the_token_at_once_and_Enter_closes_and_keeps_it() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("quote"));

            fixture.TypeInPicker("#3fb950");

            Assert.Equal(new ColorValue("#3fb950"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
            Assert.True(fixture.Dot("quote").IsEffectivelyVisible);
            Assert.True(fixture.Reset("quote").IsEffectivelyVisible);
            Assert.Equal(Color.Parse("#3fb950"), ((ISolidColorBrush)fixture.Find<Border>("PickerNew").Background!).Color);
            Assert.True(fixture.PickerOpen);

            fixture.Press(Key.Enter);

            Assert.False(fixture.PickerOpen);
            Assert.Equal(new ColorValue("#3fb950"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
        });

    [Fact]
    public Task Esc_in_the_picker_after_a_change_on_an_inherited_token_leaves_it_inherited_and_clean() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("quote"));
            fixture.TypeInPicker("#3fb950");
            Assert.True(fixture.ViewModel.IsDirty);

            fixture.Press(Key.Escape);

            Assert.False(fixture.PickerOpen);
            Assert.False(fixture.ViewModel.WorkingTheme!.Light!.Tokens.ContainsKey("quote"));
            Assert.False(fixture.Reset("quote").IsEffectivelyVisible);
            Assert.False(fixture.Dot("quote").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("DirtyDot").IsEffectivelyVisible);
        });

    [Fact]
    public Task Esc_in_the_picker_after_a_change_on_a_set_token_puts_the_old_value_back() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("accent"));
            fixture.TypeInPicker("#3fb95080");
            Assert.Equal(new ColorValue("#3fb95080"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);

            fixture.Press(Key.Escape);

            Assert.False(fixture.PickerOpen);
            Assert.Equal(new ColorValue("#112233"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);
            Assert.False(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task Esc_in_the_picker_closes_only_the_picker_not_the_window_and_asks_no_confirm() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;
            fixture.Click(fixture.Swatch("accent"));
            fixture.TypeInPicker("#abcdef");

            fixture.Press(Key.Escape);

            Assert.False(fixture.PickerOpen);
            Assert.False(closed);
            Assert.False(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.Equal(new ColorValue("#112233"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);
            Assert.True(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task Opening_the_picker_and_clicking_outside_it_with_no_change_edits_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;
            fixture.Click(fixture.Swatch("quote"));
            Assert.True(fixture.PickerOpen);

            fixture.Click(fixture.Find<Control>("Header"));

            Assert.False(fixture.PickerOpen);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
            Assert.False(fixture.Reset("quote").IsEffectivelyVisible);
            Assert.False(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task An_invalid_hex_in_the_picker_shows_the_error_and_edits_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;
            fixture.Click(fixture.Swatch("accent"));

            fixture.TypeInPicker("#12345");

            TextBlock error = fixture.Find<TextBlock>("PickerError");
            Assert.True(error.IsEffectivelyVisible);
            Assert.Equal(ColorValue.NotAColour("#12345"), error.Text);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);

            fixture.Press(Key.Enter);

            Assert.True(fixture.PickerOpen);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
        });

    [Fact]
    public Task A_press_on_the_saturation_square_sets_the_colour_of_that_position() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("accent"));
            var old = ColorMath.ToHsv(new ColorValue("#112233"), greyHue: 0);

            fixture.PressAt(fixture.Find<Control>("PickerSquare"), 0.25, 0.25);

            Assert.Equal(ColorMath.FromHsv(old with { S = 0.25, V = 0.75 }, byte.MaxValue), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);
            Assert.True(fixture.PickerOpen);
        });

    [Fact]
    public Task A_press_on_the_hue_bar_keeps_saturation_and_value_and_the_alpha_bar_sets_alpha() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("accent"));
            var old = ColorMath.ToHsv(new ColorValue("#112233"), greyHue: 0);

            fixture.PressAt(fixture.Find<Control>("PickerHue"), 0.5, 0.5);
            fixture.PressAt(fixture.Find<Control>("PickerAlpha"), 0, 0.5);

            Assert.Equal(ColorMath.FromHsv(old with { H = 180 }, 0), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);
        });

    [Fact]
    public Task A_built_in_theme_swatch_opens_no_picker_and_edits_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Row("studio"));
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;

            fixture.Click(fixture.Swatch("accent"));

            Assert.False(fixture.PickerOpen);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
        });

    [Fact]
    public Task A_short_hex_prefix_typed_in_the_picker_applies_nothing_before_a_click_outside() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;
            fixture.Click(fixture.Swatch("quote"));

            fixture.TypeInPicker("#3fb");
            fixture.Window.KeyTextInput("9");
            fixture.Settle();
            fixture.Click(fixture.Find<Control>("Header"));

            Assert.False(fixture.PickerOpen);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
            Assert.False(fixture.Reset("quote").IsEffectivelyVisible);
        });

    [Fact]
    public Task A_short_hex_in_the_picker_applies_on_Enter() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;
            fixture.Click(fixture.Swatch("quote"));

            fixture.TypeInPicker("#3fb");

            Assert.Same(before, fixture.ViewModel.WorkingTheme);
            Assert.False(fixture.Find<TextBlock>("PickerError").IsEffectivelyVisible);

            fixture.Press(Key.Enter);

            Assert.False(fixture.PickerOpen);
            Assert.Equal(new ColorValue("#33ffbb"), fixture.ViewModel.WorkingTheme!.Light!.Tokens["quote"]);
        });

    [Fact]
    public Task Closing_the_picker_on_an_inherited_token_changed_back_to_its_value_leaves_it_inherited() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string shown = fixture.Field("quote").Text!;
            fixture.Click(fixture.Swatch("quote"));
            fixture.PressAt(fixture.Find<Control>("PickerSquare"), 0.25, 0.25);
            Assert.True(fixture.ViewModel.WorkingTheme!.Light!.Tokens.ContainsKey("quote"));

            fixture.TypeInPicker(shown);
            fixture.Click(fixture.Find<Control>("Header"));

            Assert.False(fixture.PickerOpen);
            Assert.False(fixture.ViewModel.WorkingTheme!.Light!.Tokens.ContainsKey("quote"));
            Assert.False(fixture.Dot("quote").IsEffectivelyVisible);
            Assert.False(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task Esc_in_the_picker_with_no_change_edits_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ThemeDefinition before = fixture.ViewModel.WorkingTheme!;
            fixture.Click(fixture.Swatch("accent"));
            fixture.Find<TextBox>("PickerHex").Focus();
            fixture.Settle();

            fixture.Press(Key.Escape);

            Assert.False(fixture.PickerOpen);
            Assert.Same(before, fixture.ViewModel.WorkingTheme);
            Assert.False(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task A_drag_on_the_saturation_square_follows_the_pointer() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Swatch("accent"));
            var old = ColorMath.ToHsv(new ColorValue("#112233"), greyHue: 0);

            fixture.Drag(fixture.Find<Control>("PickerSquare"), (0.25, 0.25), (0.75, 0.75));

            Assert.Equal(ColorMath.FromHsv(old with { S = 0.75, V = 0.25 }, byte.MaxValue), fixture.ViewModel.WorkingTheme!.Light!.Tokens["accent"]);
        });

    [Fact]
    public Task The_preview_shows_in_the_preview_host_for_a_valid_theme_with_its_own_resources() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            ThemePreview preview = fixture.Find<ThemePreview>("Preview");

            Assert.True(preview.IsEffectivelyVisible);
            Assert.Same(fixture.ViewModel.PreviewTheme, preview.Resolved);
            Assert.Contains("Release checklist", VisibleTexts(preview));
            Assert.True(preview.TryFindResource("YamvQuote", out object? quote));
            Assert.Equal(Color.FromRgb(0xd0, 0xd7, 0xde), Assert.IsType<SolidColorBrush>(quote).Color);
            Assert.Equal("LIGHT", fixture.Find<TextBlock>("AppearanceTag").Text);
        });

    [Fact]
    public Task An_unsaved_colour_edit_shows_in_the_preview_while_the_window_keeps_its_brush() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            var saved = new SolidColorBrush(Colors.White);
            fixture.Window.Resources["YamvPageBackground"] = saved;
            ThemePreview preview = fixture.Find<ThemePreview>("Preview");

            fixture.ViewModel.SetToken(ThemeTab.Light, "pageBackground", new ColorValue("#3fb950"));
            fixture.Settle();

            var edited = Color.FromRgb(0x3f, 0xb9, 0x50);
            Assert.True(preview.TryFindResource("YamvPageBackground", out object? inPreview));
            Assert.Equal(edited, Assert.IsType<SolidColorBrush>(inPreview).Color);
            Assert.Equal(edited, Assert.IsType<SolidColorBrush>(fixture.Find<DockPanel>("PreviewPage").Background).Color);
            Assert.True(fixture.Window.TryFindResource("YamvPageBackground", out object? inWindow));
            Assert.Same(saved, inWindow);
        });

    [Fact]
    public Task An_h1Size_edit_resizes_the_preview_h1_at_once() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            TextBlock h1 = fixture.Find<TextBlock>("PreviewH1");
            Assert.Equal(2.05 * 13, h1.FontSize, 3);

            fixture.ViewModel.SetToken(ThemeTab.Light, "h1Size", new NumberValue(3));
            fixture.Settle();

            Assert.Equal(39, h1.FontSize, 3);
            Assert.Equal(39 * 1.15, h1.LineHeight, 3);
        });

    [Fact]
    public Task The_preview_tag_names_the_dark_half_on_the_Dark_tab() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Click(fixture.Row("studio"));

            fixture.Click(fixture.Find<Button>("DarkTab"));

            Assert.Equal("DARK", fixture.Find<TextBlock>("AppearanceTag").Text);
            Assert.True(fixture.Find<ThemePreview>("Preview").TryFindResource("YamvPageBackground", out object? page));
            Assert.Equal(Color.FromRgb(0x0f, 0x13, 0x18), Assert.IsType<SolidColorBrush>(page).Color);
        });

    [Fact]
    public Task There_is_no_preview_for_a_theme_that_did_not_load() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();

            fixture.Click(fixture.Row("nord"));

            Assert.False(fixture.Find<ThemePreview>("Preview").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("NoPreview").IsEffectivelyVisible);
        });

    [Fact]
    public Task Import_on_a_valid_file_opens_the_notice_with_its_card_and_what_a_theme_can_change() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string source = fixture.AddImport(
                "warm-solar.yamv-theme.json",
                """{ "name": "Warm Solar", "author": "Ana", "light": { "css": "p { color: red }" }, "dark": {} }"""
            );

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Control notice = fixture.Find<Control>("ImportNotice");
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("Warm Solar by Ana · light and dark · has css", fixture.NoticeLineText);
            List<string> texts = VisibleTexts(notice);
            Assert.Contains("Import a theme", texts);
            Assert.Contains(source, texts);
            Assert.Contains("A theme file can change:", texts);
            Assert.Contains("colours, fonts and sizes, in the document and in the app's title bar, tree and dialogs;", texts);
            Assert.Contains("the document page, with its own css, added after YAMV's stylesheet. It styles the document only, not the app.", texts);
            Assert.Contains("Theme css can't run code: the page's Content-Security-Policy blocks scripts.", texts);
            Assert.Contains(
                "Theme css can make the page load images from the web, which can reveal that you opened a document and which links it"
                    + " holds. Import a theme from someone you trust, as you would a browser extension's styles.",
                texts
            );
            Button import = fixture.Find<Button>("NoticeImport");
            Assert.True(import.IsDefault);
            Assert.True(import.IsFocused);
            Assert.Equal("Import", import.Content);
            Assert.Equal("Cancel", fixture.Find<Button>("NoticeCancel").Content);
            Assert.True(fixture.Find<Control>("NoticeChipLight").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("NoticeChipDark").IsEffectivelyVisible);
        });

    [Fact]
    public Task The_card_says_light_only_and_no_css_while_the_other_half_and_css_say_theirs() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("plain.yamv-theme.json", """{ "name": "Plain", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.Equal("Plain light only · no css", fixture.NoticeLineText);

            fixture.Press(Key.Escape);

            fixture.AddImport("moon.yamv-theme.json", """{ "name": "Moon", "dark": { "css": "p { }" } }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.Equal("Moon dark only · has css", fixture.NoticeLineText);
            Assert.True(fixture.Find<Control>("NoticeChipDark").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("NoticeChipLight").IsEffectivelyVisible);
        });

    [Fact]
    public Task A_long_name_and_author_keep_has_css_in_the_wrapping_card_line() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string name = "A very long warm solar theme name for a long winter";
            string author = "An author whose name is also rather long";
            fixture.AddImport("long.yamv-theme.json", $$"""{ "name": "{{name}}", "author": "{{author}}", "light": { "css": "p { }" } }""");

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            TextBlock line = fixture.Find<TextBlock>("NoticeLine");
            Assert.Equal(TextWrapping.Wrap, line.TextWrapping);
            Assert.Equal($"{name} by {author} · light only · has css", fixture.NoticeLineText);
            Assert.True(line.TextLayout.TextLines.Count > 1, "The card's first line should wrap onto more than one line.");
        });

    [Fact]
    public Task A_taken_id_says_what_the_theme_will_be_added_as_and_a_free_id_says_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("mine.yamv-theme.json", """{ "name": "Mine", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.Contains("Will be added as Mine (2).", VisibleTexts(fixture.Find<Control>("ImportNotice")));

            fixture.Press(Key.Escape);

            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.DoesNotContain(
                VisibleTexts(fixture.Find<Control>("ImportNotice")),
                text => text.StartsWith("Will be added as", StringComparison.Ordinal)
            );
        });

    [Fact]
    public Task A_warning_shows_in_the_notice() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("sun.yamv-theme.json", """{ "name": "Sun", "light": { "tokens": { "unicorn": "#123456" } } }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.Contains("Unknown token 'unicorn' in light is ignored.", VisibleTexts(fixture.Find<Control>("ImportNotice")));
        });

    [Fact]
    public Task The_safe_box_takes_the_apps_code_background_brush() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            ResolvedTheme studio = StudioLight();
            ThemeBrushes.FillChrome(fixture.Window.Resources, studio);
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            SolidColorBrush brush = Assert.IsType<SolidColorBrush>(fixture.Find<Border>("NoticeSafe").Background);
            Assert.Equal(Color.FromUInt32(ChromePalette.From(studio).Colors["codeBackground"]), brush.Color);
        });

    [Fact]
    public Task Enter_on_the_default_Import_button_imports_the_theme_and_so_does_a_click() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            fixture.Press(Key.Enter);

            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);
            Assert.True(File.Exists(fixture.PathOf("solar")));
            Assert.Contains(fixture.ViewModel.UserRows, row => row.Id == "solar");

            fixture.AddImport("lunar.yamv-theme.json", """{ "name": "Lunar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("NoticeImport"));

            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);
            Assert.True(File.Exists(fixture.PathOf("lunar")));
            Assert.Contains(fixture.ViewModel.UserRows, row => row.Id == "lunar");
        });

    [Fact]
    public Task Import_at_the_windows_minimum_height_keeps_the_footer_visible_and_clickable() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string tokens = string.Join(", ", Enumerable.Range(1, 12).Select(n => $"\"w{n}\": \"#123456\""));
            fixture.AddImport("many.yamv-theme.json", $$"""{ "name": "Many", "light": { "tokens": { {{tokens}} } } }""");
            fixture.Window.Height = 480;
            fixture.Settle();

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.True(fixture.Find<Button>("NoticeCancel").IsEffectivelyVisible);
            Button import = fixture.Find<Button>("NoticeImport");
            Assert.True(import.IsEffectivelyVisible);

            fixture.Click(import);

            Assert.True(File.Exists(fixture.PathOf("many")));
            Assert.Contains(fixture.ViewModel.UserRows, row => row.Id == "many");
        });

    [Fact]
    public Task Esc_the_close_button_and_Cancel_each_close_the_notice_importing_nothing() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            fixture.Press(Key.Escape);
            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("NoticeClose"));
            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("NoticeCancel"));
            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);

            Assert.False(File.Exists(fixture.PathOf("solar")));
            Assert.DoesNotContain(fixture.ViewModel.UserRows, row => row.Id == "solar");
        });

    [Fact]
    public Task An_invalid_file_opens_the_notice_with_its_errors_Import_disabled_and_Enter_cancels() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("bad.yamv-theme.json", """{ "light": { "tokens": { "accent": "#12" } } }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Control notice = fixture.Find<Control>("ImportNotice");
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("bad.yamv-theme.json", fixture.NoticeLineText);
            List<string> texts = VisibleTexts(notice);
            Assert.Contains("The theme has no 'name'.", texts);
            Assert.Contains(texts, text => text.Contains("is not a colour", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.StartsWith("Will be added as", StringComparison.Ordinal));
            Assert.False(fixture.Find<Button>("NoticeImport").IsEffectivelyEnabled);
            Assert.False(fixture.Find<Button>("NoticeImport").IsDefault);
            Button cancel = fixture.Find<Button>("NoticeCancel");
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsFocused);

            fixture.Press(Key.Enter);

            Assert.False(notice.IsEffectivelyVisible);
            Assert.False(File.Exists(fixture.PathOf("bad")));
            Assert.DoesNotContain(fixture.ViewModel.UserRows, row => row.Id == "bad");
        });

    [Fact]
    public Task A_theme_with_no_usable_id_shows_its_file_name_and_no_chip() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("con.yamv-theme.json", """{ "name": "Con", "light": { "tokens": { "accent": "#123456" } }, "dark": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Control notice = fixture.Find<Control>("ImportNotice");
            Assert.Equal("con.yamv-theme.json", fixture.NoticeLineText);
            Assert.False(fixture.Find<Control>("NoticeChip").IsEffectivelyVisible);
            Assert.Contains(VisibleTexts(notice), text => text.Contains("no usable theme id", StringComparison.Ordinal));
            Assert.False(fixture.Find<Button>("NoticeImport").IsEffectivelyEnabled);
        });

    [Fact]
    public Task The_notice_disables_the_window_behind_it_until_it_closes() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            Assert.True(fixture.Find<Control>("WindowContent").IsEnabled);

            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            Assert.False(fixture.Find<Control>("WindowContent").IsEnabled);
            Assert.False(fixture.Find<Control>("ThemeList").IsEffectivelyEnabled);

            fixture.Press(Key.Escape);

            Assert.True(fixture.Find<Control>("WindowContent").IsEnabled);
        });

    [Fact]
    public Task Closing_the_window_while_the_notice_is_open_with_no_edits_cancels_the_notice_and_closes() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.Window.Close();
            fixture.Settle();

            Assert.True(closed);
            Assert.False(fixture.Find<Control>("ImportNotice").IsVisible);
            Assert.False(File.Exists(fixture.PathOf("solar")));
        });

    [Fact]
    public Task Closing_with_unsaved_edits_while_the_notice_is_open_cancels_the_notice_and_asks_the_unsaved_confirm() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.Window.Close();
            fixture.Settle();

            Assert.False(closed);
            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.Contains("Unsaved changes", VisibleTexts(fixture.Find<Control>("Confirm")));

            fixture.Click(fixture.Find<Button>("CancelButton"));

            Assert.False(closed);
            Assert.True(fixture.Find<Control>("WindowContent").IsEnabled);
            Assert.False(File.Exists(fixture.PathOf("solar")));
        });

    [Theory]
    [InlineData("PrimaryButton", true)]
    [InlineData("SecondaryButton", true)]
    [InlineData("CancelButton", false)]
    public Task The_close_check_asks_about_unsaved_edits_and_says_whether_the_owner_may_close(string button, bool mayClose) =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            Task<bool> check = fixture.Window.ConfirmCloseAsync();
            fixture.Settle();
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            fixture.Click(fixture.Find<Button>(button));

            Assert.True(check.IsCompletedSuccessfully);
            Assert.Equal(mayClose, check.Result);
            Assert.False(closed);
            Assert.Equal(button == "PrimaryButton", File.ReadAllText(fixture.PathOf("mine")).Contains("#3fb950", StringComparison.Ordinal));

            fixture.Window.Close();
            fixture.Settle();

            Assert.Equal(mayClose, closed);
            Assert.Equal(!mayClose, fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            fixture.Find<ConfirmDialog>("Confirm").Cancel();
        });

    [Fact]
    public Task The_close_confirm_replacing_a_cancelled_notice_keeps_the_window_disabled_while_it_is_open() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            fixture.AddImport("solar.yamv-theme.json", """{ "name": "Solar", "light": {} }""");
            fixture.ViewModel.ImportCommand.Execute(null);
            fixture.Settle();

            fixture.Window.Close();
            fixture.Settle();

            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("WindowContent").IsEnabled);
            Assert.False(fixture.Find<Button>("SaveButton").IsEffectivelyEnabled);

            fixture.Click(fixture.Find<Button>("CancelButton"));
        });

    [Fact]
    public Task The_close_check_Save_writes_the_missing_theme_in_use_again() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            File.Delete(fixture.PathOf("mine"));
            fixture.ViewModel.OnThemesChangedOnDisk();
            fixture.Settle();

            Task<bool> check = fixture.Window.ConfirmCloseAsync();
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("PrimaryButton"));

            Assert.True(check.IsCompletedSuccessfully);
            Assert.True(check.Result);
            Assert.Contains("#3fb950", File.ReadAllText(fixture.PathOf("mine")), StringComparison.Ordinal);
        });

    [Fact]
    public Task After_a_close_check_that_allowed_a_cancelled_owner_close_a_new_edit_asks_again_on_close() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            Task<bool> check = fixture.Window.ConfirmCloseAsync();
            fixture.Settle();
            fixture.Click(fixture.Find<Button>("SecondaryButton"));
            Assert.True(check.Result);
            Assert.False(fixture.ViewModel.IsDirty);
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#445566"));
            fixture.Settle();
            fixture.Window.Close();
            fixture.Settle();

            Assert.False(closed);
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            fixture.Click(fixture.Find<Button>("CancelButton"));
        });

    [Fact]
    public Task ForceClose_closes_a_window_with_unsaved_edits_without_asking() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;

            fixture.Window.ForceClose();
            fixture.Settle();

            Assert.True(closed);
            Assert.False(fixture.Find<Control>("Confirm").IsEffectivelyVisible);
            Assert.DoesNotContain("#3fb950", File.ReadAllText(fixture.PathOf("mine")), StringComparison.Ordinal);
        });

    [Fact]
    public Task ForceClose_while_the_close_confirm_is_open_cancels_it_and_closes() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            fixture.Settle();
            bool closed = false;
            fixture.Window.Closed += (_, _) => closed = true;
            Task<bool> check = fixture.Window.ConfirmCloseAsync();
            fixture.Settle();
            Assert.True(fixture.Find<Control>("Confirm").IsEffectivelyVisible);

            fixture.Window.ForceClose();
            fixture.Settle();

            Assert.True(closed);
            Assert.True(check.IsCompletedSuccessfully);
            Assert.False(check.Result);
        });

    [Fact]
    public Task The_disk_bar_shows_Reload_and_Keep_mine_and_turns_to_the_warning_when_the_file_no_longer_loads() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            Assert.False(fixture.Find<Control>("DiskBar").IsEffectivelyVisible);
            fixture.ViewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
            File.WriteAllText(fixture.PathOf("mine"), """{ "name": "Mine", "light": { "tokens": { "accent": "#445566" } } }""");

            fixture.ViewModel.OnThemesChangedOnDisk();
            fixture.Settle();

            Border bar = fixture.Find<Border>("DiskBar");
            Assert.True(bar.IsEffectivelyVisible);
            Assert.Equal(42, bar.Bounds.Height);
            Assert.Equal(fixture.Window.Bounds.Width, bar.Bounds.Width);
            Assert.DoesNotContain("warn", bar.Classes);
            Assert.Contains("This theme changed on disk", VisibleTexts(bar));
            Button reload = fixture.Find<Button>("ReloadButton");
            Button keepMine = fixture.Find<Button>("KeepMineButton");
            Assert.Equal("Reload", reload.Content);
            Assert.Equal("Keep mine", keepMine.Content);
            Assert.True(reload.IsEffectivelyEnabled);
            Assert.True(keepMine.IsEffectivelyEnabled);

            File.WriteAllText(fixture.PathOf("mine"), """{ "name": "Mine", """);
            fixture.ViewModel.OnThemesChangedOnDisk();
            fixture.Settle();

            Assert.Contains("warn", bar.Classes);
            List<string> texts = VisibleTexts(bar);
            Assert.Contains("This theme changed on disk and no longer loads:", texts);
            Assert.Contains(fixture.ViewModel.DiskError!, texts);
            Assert.False(reload.IsEffectivelyEnabled);
            Assert.True(keepMine.IsEffectivelyEnabled);
            Assert.True(
                fixture.Row("mine").GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RowWarning").IsEffectivelyVisible
            );

            fixture.Click(keepMine);

            Assert.False(bar.IsEffectivelyVisible);
            Assert.True(fixture.ViewModel.IsDirty);
        });

    [Fact]
    public Task OnClosed_completes_a_pending_notice_as_Cancel() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            var inspection = new ThemeInspection(
                "a.yamv-theme.json",
                @"C:\themes\a.yamv-theme.json",
                "A",
                null,
                true,
                false,
                false,
                [],
                [],
                null,
                null,
                null
            );
            Task<bool> pending = fixture.Window.ConfirmImportAsync(inspection);
            fixture.Settle();

            ((TestWindow)fixture.Window).RaiseClosed();

            Assert.True(pending.IsCompletedSuccessfully);
            Assert.False(pending.Result);
            Assert.False(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);
        });

    [Fact]
    public Task Opening_a_second_notice_while_one_is_open_throws() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            var inspection = new ThemeInspection(
                "a.yamv-theme.json",
                @"C:\themes\a.yamv-theme.json",
                "A",
                null,
                true,
                false,
                false,
                [],
                [],
                null,
                null,
                null
            );
            Task<bool> first = fixture.Window.ConfirmImportAsync(inspection);
            fixture.Settle();

            Task<bool> second = fixture.Window.ConfirmImportAsync(inspection);
            fixture.Settle();

            Assert.True(second.IsFaulted);
            Assert.IsType<InvalidOperationException>(second.Exception!.InnerException);
            Assert.True(fixture.Find<Control>("ImportNotice").IsEffectivelyVisible);
            Assert.False(fixture.Find<Control>("WindowContent").IsEnabled);

            fixture.Press(Key.Escape);

            Assert.True(first.IsCompletedSuccessfully);
            Assert.False(first.Result);
            Assert.True(fixture.Find<Control>("WindowContent").IsEnabled);
        });

    /// <summary>Studio light, resolved as the app applies it: the theme whose brushes the window's chrome takes.</summary>
    private static ResolvedTheme StudioLight()
    {
        var catalog = new ThemeCatalog(Path.Combine(Path.GetTempPath(), "yamv-studio-only-" + Guid.NewGuid().ToString("N")), new RecordingAppLog());
        ThemeDefinition studio = catalog.LoadAll().Single(entry => entry.Id == "studio").Result.Theme!;
        return ThemeResolver.Resolve(studio, Appearance.Light);
    }

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

        private readonly string _importFolder = Path.Combine(Path.GetTempPath(), "yamv-theme-import-" + Guid.NewGuid().ToString("N"));

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
                    PickImport = () => Task.FromResult(ImportPath),
                    PickExport = _ => Task.FromResult<string?>(null),
                    ConfirmImport = inspection => window!.ConfirmImportAsync(inspection),
                },
                "mine",
                "light",
                Log
            );
            window = new TestWindow(ViewModel, Log);
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

        /// <summary>The file the Import pick answers with, or null when the pick is cancelled.</summary>
        public string? ImportPath { get; set; }

        /// <summary>Writes <paramref name="text"/> to a file named <paramref name="fileName"/> outside the themes folder, and picks it.</summary>
        public string AddImport(string fileName, string text)
        {
            Directory.CreateDirectory(_importFolder);
            string path = Path.Combine(_importFolder, fileName);
            File.WriteAllText(path, text);
            ImportPath = path;
            return path;
        }

        /// <summary>The card's first line, as its runs read.</summary>
        public string NoticeLineText => string.Concat(Find<TextBlock>("NoticeLine").Inlines!.OfType<Run>().Select(run => run.Text));

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

        /// <summary>Whether the colour picker's popup is open.</summary>
        public bool PickerOpen => Find<Popup>("PickerPopup").IsOpen;

        /// <summary>Focuses the colour picker's hex field and types <paramref name="text"/> over its text, in one input.</summary>
        public void TypeInPicker(string text)
        {
            TextBox field = Find<TextBox>("PickerHex");
            field.Focus();
            field.SelectAll();
            Window.KeyTextInput(text);
            Settle();
        }

        /// <summary>Presses and releases the left button at a fraction of <paramref name="visual"/>'s width and height.</summary>
        public void PressAt(Visual visual, double x, double y)
        {
            Point point =
                visual.TranslatePoint(new Point(visual.Bounds.Width * x, visual.Bounds.Height * y), Window)
                ?? throw new InvalidOperationException("The control is not in the window.");
            Window.MouseMove(point);
            Settle();
            Window.MouseDown(point, MouseButton.Left);
            Settle();
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }

        /// <summary>Presses the left button at one fraction of <paramref name="visual"/>'s size, moves to another and releases there.</summary>
        public void Drag(Visual visual, (double X, double Y) from, (double X, double Y) to)
        {
            Point start = PointIn(visual, from);
            Point end = PointIn(visual, to);
            Window.MouseMove(start);
            Settle();
            Window.MouseDown(start, MouseButton.Left);
            Settle();
            Window.MouseMove(end);
            Settle();
            Window.MouseUp(end, MouseButton.Left);
            Settle();
        }

        private Point PointIn(Visual visual, (double X, double Y) fraction) =>
            visual.TranslatePoint(new Point(visual.Bounds.Width * fraction.X, visual.Bounds.Height * fraction.Y), Window)
            ?? throw new InvalidOperationException("The control is not in the window.");

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
            if (Directory.Exists(_importFolder))
            {
                Directory.Delete(_importFolder, recursive: true);
            }
        }
    }

    /// <summary>A theme settings window whose closed event a test can raise, since an open notice holds the window's close.</summary>
    private sealed class TestWindow(ThemeSettingsViewModel viewModel, RecordingAppLog log) : ThemeSettingsWindow(viewModel, log)
    {
        public void RaiseClosed() => OnClosed(EventArgs.Empty);
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
