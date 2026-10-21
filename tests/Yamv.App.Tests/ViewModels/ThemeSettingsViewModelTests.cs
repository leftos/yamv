using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.ViewModels;

public sealed class ThemeSettingsViewModelTests
{
    private const string MineText = """
        {
          // My own theme
          "name": "Mine",
          "light": { "tokens": { "accent": "#112233" } },
          "dark": { "tokens": { "quote": "#4493f8" } }
        }
        """;

    [Fact]
    public void List_shows_built_ins_in_order_then_yours_sorted_with_the_invalid_one_marked()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        Assert.Equal(["paper", "studio", "ink"], viewModel.BuiltInRows.Select(row => row.Id));
        Assert.All(viewModel.BuiltInRows, row => Assert.True(row.IsBuiltIn));
        Assert.Equal(["Alpha", "Mine", "nord"], viewModel.UserRows.Select(row => row.Name));
        ThemeRowViewModel nord = viewModel.UserRows[2];
        Assert.False(nord.IsValid);
        Assert.NotEmpty(nord.Errors);
        Assert.True(viewModel.UserRows[0].IsValid);
        Assert.True(viewModel.UserRows[1].IsValid);
    }

    [Fact]
    public void Theme_in_use_on_open_is_selected_without_applying_it_again()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.True(viewModel.Selected?.IsSelected);
        Assert.Equal("Mine", viewModel.WorkingTheme?.Name);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Selecting_a_valid_theme_applies_it_once_with_the_appearance()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.SelectAsync(Row(viewModel, "alpha"));

        Assert.Equal([("alpha", "light")], fixture.Applied);
        Assert.Equal("alpha", viewModel.Selected?.Id);
        Assert.Equal("Alpha", viewModel.WorkingTheme?.Name);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task Selecting_the_invalid_theme_does_not_apply_it_and_exposes_its_errors()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.SelectAsync(Row(viewModel, "nord"));

        Assert.Empty(fixture.Applied);
        Assert.True(viewModel.IsInvalid);
        Assert.Null(viewModel.WorkingTheme);
        Assert.Contains(viewModel.Errors, error => error.Contains("accent", StringComparison.Ordinal));
        Assert.EndsWith("Until then the app stays on Mine.", viewModel.InvalidNote, StringComparison.Ordinal);
        Assert.Equal("nord · didn't load", viewModel.TitleDetail);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        Assert.True(viewModel.CanEditAsText);
    }

    [Fact]
    public void SetToken_marks_the_copy_dirty_and_the_token_changed_and_Revert_clears_both()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));

        Assert.True(viewModel.IsDirty);
        Assert.Equal(["accent"], viewModel.ChangedIn(ThemeTab.Light));
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Dark));
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.RevertCommand.CanExecute(null));

        viewModel.RevertCommand.Execute(null);

        Assert.False(viewModel.IsDirty);
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
        Assert.Same(viewModel.SavedTheme, viewModel.WorkingTheme);
        Assert.Equal(new ColorValue("#112233"), viewModel.WorkingTheme?.Light?.Tokens["accent"]);
    }

    [Fact]
    public void Save_changes_only_the_edited_value_in_the_file_and_applies_the_theme_in_use()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));

        viewModel.SaveCommand.Execute(null);

        Assert.Equal(MineText.Replace("#112233", "#3fb950", StringComparison.Ordinal), File.ReadAllText(fixture.PathOf("mine")));
        Assert.Equal([("mine", "light")], fixture.Applied);
        Assert.False(viewModel.IsDirty);
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
        Assert.Null(viewModel.ErrorLine);
    }

    [Theory]
    [InlineData(UnsavedChoice.Save)]
    [InlineData(UnsavedChoice.Discard)]
    [InlineData(UnsavedChoice.Cancel)]
    public async Task Switching_while_dirty_asks_first_and_follows_the_answer(UnsavedChoice choice)
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = choice;

        await viewModel.SelectAsync(Row(viewModel, "alpha"));

        Assert.Equal(["Mine"], fixture.UnsavedAsked);
        string text = File.ReadAllText(fixture.PathOf("mine"));
        if (choice == UnsavedChoice.Cancel)
        {
            Assert.Equal("mine", viewModel.Selected?.Id);
            Assert.True(viewModel.IsDirty);
            Assert.Empty(fixture.Applied);
            Assert.Equal(MineText, text);
            return;
        }

        Assert.Equal("alpha", viewModel.Selected?.Id);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(("alpha", "light"), fixture.Applied[^1]);
        Assert.Equal(choice == UnsavedChoice.Save, text.Contains("#3fb950", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_to_edit_on_Studio_writes_studio_2_named_Studio_copy_and_selects_and_applies_it()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await viewModel.SelectAsync(Row(viewModel, "studio"));
        Assert.True(viewModel.DuplicateToEditCommand.CanExecute(null));

        viewModel.DuplicateToEditCommand.Execute(null);

        Assert.True(File.Exists(fixture.PathOf("studio-2")));
        Assert.Equal("studio-2", viewModel.Selected?.Id);
        Assert.Equal("Studio (copy)", viewModel.Selected?.Name);
        Assert.Contains(viewModel.UserRows, row => row.Id == "studio-2");
        Assert.Equal(("studio-2", "light"), fixture.Applied[^1]);
        Assert.True(viewModel.IsEditable);
    }

    [Fact]
    public async Task Edits_to_a_built_in_theme_throw()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await viewModel.SelectAsync(Row(viewModel, "studio"));

        Assert.Throws<InvalidOperationException>(() => viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950")));
        Assert.Throws<InvalidOperationException>(() => viewModel.ResetToken(ThemeTab.Light, "accent"));
        Assert.Throws<InvalidOperationException>(() => viewModel.AddVariant(Appearance.Dark));
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.CanEditAsText);
    }

    [Fact]
    public void Rename_moves_the_file_and_applies_the_theme_in_use_again_under_its_new_id()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        ThemeRowViewModel mine = Row(viewModel, "mine");
        viewModel.BeginRename(mine);
        Assert.True(mine.IsRenaming);
        Assert.Equal("Mine", mine.RenameText);

        viewModel.CommitRename(mine, "My Studio");

        Assert.False(File.Exists(fixture.PathOf("mine")));
        Assert.True(File.Exists(fixture.PathOf("my-studio")));
        Assert.Equal([("my-studio", "light")], fixture.Applied);
        Assert.Equal("my-studio", viewModel.Selected?.Id);
        Assert.Equal("My Studio", viewModel.Selected?.Name);
        Assert.Equal("my-studio", viewModel.InUseId);
    }

    [Fact]
    public void Rename_to_a_blank_name_is_not_committed()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        ThemeRowViewModel alpha = Row(viewModel, "alpha");
        viewModel.BeginRename(alpha);

        viewModel.CommitRename(alpha, "   ");

        Assert.True(alpha.IsRenaming);
        Assert.True(File.Exists(fixture.PathOf("alpha")));
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Delete_declined_changes_nothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        fixture.DeleteAnswer = false;

        await viewModel.DeleteAsync(Row(viewModel, "alpha"));

        Assert.Equal(["Alpha"], fixture.DeleteAsked);
        Assert.Empty(fixture.Trashed);
        Assert.True(File.Exists(fixture.PathOf("alpha")));
        Assert.Equal(3, viewModel.UserRows.Count);
    }

    [Fact]
    public async Task Delete_of_the_theme_in_use_trashes_its_file_and_falls_back_to_Studio()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.DeleteAsync(Row(viewModel, "mine"));

        Assert.Equal([fixture.PathOf("mine")], fixture.Trashed);
        Assert.Equal([("studio", "light")], fixture.Applied);
        Assert.Equal("studio", viewModel.Selected?.Id);
        Assert.Equal("studio", viewModel.InUseId);
        Assert.DoesNotContain(viewModel.UserRows, row => row.Id == "mine");
    }

    [Fact]
    public async Task Delete_of_the_selected_theme_not_in_use_selects_the_theme_in_use_without_applying_it()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create(themeId: "alpha");
        await viewModel.SelectAsync(Row(viewModel, "nord"));

        await viewModel.DeleteAsync(Row(viewModel, "nord"));

        Assert.Equal([fixture.PathOf("nord")], fixture.Trashed);
        Assert.Equal("alpha", viewModel.Selected?.Id);
        Assert.Equal("alpha", viewModel.InUseId);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Delete_of_an_unselected_row_keeps_the_selection()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.DeleteAsync(Row(viewModel, "alpha"));

        Assert.Equal([fixture.PathOf("alpha")], fixture.Trashed);
        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.True(viewModel.Selected?.IsSelected);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public void A_device_named_file_has_Delete_and_Rename_off()
    {
        using var fixture = new Fixture();
        fixture.WriteDeviceNamedFile();
        ThemeSettingsViewModel viewModel = fixture.Create();

        ThemeRowViewModel device = Row(viewModel, "con");

        Assert.False(device.CanDelete);
        Assert.False(device.CanRename);
    }

    [Fact]
    public void A_rename_whose_file_vanished_shows_the_error_line()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.PathOf("paper"), """{ "name": "My Paper", "light": { "tokens": { "accent": "#445566" } } }""");
        ThemeSettingsViewModel viewModel = fixture.Create();
        ThemeRowViewModel paper = viewModel.UserRows.Single(row => row.Id == "paper");
        File.Delete(fixture.PathOf("paper"));

        viewModel.CommitRename(paper, "Other Paper");

        Assert.StartsWith("Couldn't rename: ", viewModel.ErrorLine, StringComparison.Ordinal);
        Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
    }

    [Fact]
    public void Starting_from_an_id_with_no_valid_row_selects_Studio()
    {
        using var fixture = new Fixture();

        ThemeSettingsViewModel viewModel = fixture.Create(themeId: "nord");

        Assert.Equal("studio", viewModel.Selected?.Id);
        Assert.Equal("studio", viewModel.InUseId);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public void A_reset_of_a_token_the_tab_does_not_set_stays_clean()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        viewModel.ResetToken(ThemeTab.Light, "quote");

        Assert.False(viewModel.IsDirty);
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
    }

    [Fact]
    public void Renaming_the_selected_theme_keeps_its_unsaved_edits()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));

        viewModel.CommitRename(Row(viewModel, "mine"), "My Studio");

        Assert.Equal("my-studio", viewModel.Selected?.Id);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(["accent"], viewModel.ChangedIn(ThemeTab.Light));
        Assert.Equal(new ColorValue("#3fb950"), viewModel.WorkingTheme?.Light?.Tokens["accent"]);
        Assert.Equal("My Studio", viewModel.WorkingTheme?.Name);
    }

    [Fact]
    public async Task A_failed_save_during_a_switch_stays_on_the_edited_theme()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Save;
        fixture.MakeUnwritable("mine");
        try
        {
            await viewModel.SelectAsync(Row(viewModel, "alpha"));
        }
        finally
        {
            fixture.MakeWritable("mine");
        }

        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.True(viewModel.IsDirty);
        Assert.StartsWith("Couldn't save: ", viewModel.ErrorLine, StringComparison.Ordinal);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public void A_save_that_fails_then_succeeds_leaves_no_error_line()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.MakeUnwritable("mine");
        try
        {
            viewModel.SaveCommand.Execute(null);
        }
        finally
        {
            fixture.MakeWritable("mine");
        }

        Assert.NotNull(viewModel.ErrorLine);

        viewModel.SaveCommand.Execute(null);

        Assert.Null(viewModel.ErrorLine);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void Revert_clears_the_error_line()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.MakeUnwritable("mine");
        try
        {
            viewModel.SaveCommand.Execute(null);
        }
        finally
        {
            fixture.MakeWritable("mine");
        }

        viewModel.RevertCommand.Execute(null);

        Assert.Null(viewModel.ErrorLine);
    }

    [Fact]
    public void A_save_that_cannot_write_keeps_the_working_copy_and_shows_the_error_line()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.MakeUnwritable("mine");
        try
        {
            viewModel.SaveCommand.Execute(null);
        }
        finally
        {
            fixture.MakeWritable("mine");
        }

        Assert.StartsWith("Couldn't save: ", viewModel.ErrorLine, StringComparison.Ordinal);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(new ColorValue("#3fb950"), viewModel.WorkingTheme?.Light?.Tokens["accent"]);
        Assert.Equal(MineText, File.ReadAllText(fixture.PathOf("mine")));
        Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public void An_appearance_change_applies_the_theme_in_use_with_the_new_value()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        Assert.Equal("light", viewModel.SelectedAppearance.Value);

        viewModel.SelectedAppearance = ThemeSettingsViewModel.Appearances.Single(choice => choice.Value == "dark");

        Assert.Equal([("mine", "dark")], fixture.Applied);
    }

    [Fact]
    public void A_tab_lists_every_token_in_order_grouped_with_its_value_whether_set_here_its_label_and_name()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        viewModel.SelectedTab = ThemeTab.Light;

        Assert.Equal(["Page", "Chrome", "Code", "Syntax", "Type", "Layout"], viewModel.TokenGroups.Select(group => group.Name));
        Assert.Equal(ThemeTokens.All.Select(token => token.Name), Tokens(viewModel).Select(row => row.Name));
        Assert.All(viewModel.TokenGroups, group => Assert.All(group.Rows, row => Assert.Equal(group.Name, row.Token.Group)));
        TokenRowViewModel accent = Token(viewModel, "accent");
        Assert.True(accent.IsSet);
        Assert.Equal(new ColorValue("#112233"), accent.Value);
        Assert.Equal("#112233", accent.Text);
        TokenRowViewModel selection = Token(viewModel, "selectionBackground");
        Assert.False(selection.IsSet);
        Assert.Equal("Selected row", selection.Label);
        Assert.Equal("selectionBackground", selection.Name);
        Assert.Equal("Selected row\nselectionBackground · --yamv-selection-background", selection.Tooltip);
        Assert.Equal(new ColorValue("#e3edfa"), selection.Value);
        Assert.Equal("15.5", Token(viewModel, "bodySize").Text);

        viewModel.SelectedTab = ThemeTab.Dark;

        Assert.True(Token(viewModel, "quote").IsSet);
        Assert.Equal(new ColorValue("#4493f8"), Token(viewModel, "quote").Value);
        Assert.False(Token(viewModel, "accent").IsSet);
        Assert.Equal(new ColorValue("#4493f8"), Token(viewModel, "accent").Value);
    }

    [Theory]
    [InlineData(Appearance.Light, "#0969da")]
    [InlineData(Appearance.Dark, "#4493f8")]
    public void The_Shared_tab_inherits_the_defaults_of_the_appearance_shown(Appearance shown, string accent)
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Shared;

        viewModel.ShownAppearance = shown;

        Assert.Equal(new ColorValue(accent), Token(viewModel, "accent").Value);
        Assert.False(Token(viewModel, "accent").IsSet);
    }

    [Fact]
    public void Set_then_reset_leaves_the_copy_clean_with_Save_and_Revert_off_and_no_token_dot()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;

        viewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
        Assert.True(viewModel.IsDirty);
        Assert.True(Token(viewModel, "quote").IsChanged);
        Assert.True(Token(viewModel, "quote").CanReset);

        Token(viewModel, "quote").Reset();

        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        Assert.False(Token(viewModel, "quote").IsChanged);
        Assert.False(Token(viewModel, "quote").IsSet);
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
    }

    [Fact]
    public void Set_then_set_back_to_the_saved_value_is_not_dirty()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        TokenRowViewModel accent = Token(viewModel, "accent");

        Assert.True(accent.Commit("#3fb950", fromEnter: true));
        Assert.True(viewModel.IsDirty);
        Assert.True(accent.IsChanged);

        Assert.True(accent.Commit("#112233", fromEnter: true));

        Assert.False(viewModel.IsDirty);
        Assert.False(accent.IsChanged);
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
    }

    [Theory]
    [InlineData("accent", "#12345", "'#12345' is not a colour (#rgb, #rrggbb or #rrggbbaa).")]
    [InlineData("bodySize", "40", "Token 'bodySize' in light: 40 is out of range (10–32).")]
    [InlineData("bodySize", "big", "'big' is not a number.")]
    public void A_bad_value_shows_its_error_on_the_row_and_leaves_the_working_copy(string name, string text, string error)
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        ThemeDefinition? before = viewModel.WorkingTheme;
        TokenRowViewModel row = Token(viewModel, name);

        Assert.False(row.Commit(text, fromEnter: true));

        Assert.Equal(error, row.Error);
        Assert.Same(before, viewModel.WorkingTheme);
        Assert.False(viewModel.IsDirty);

        row.CancelEdit();

        Assert.Null(row.Error);
        Assert.Equal(name == "accent" ? "#112233" : "15.5", row.Text);
    }

    [Fact]
    public void A_light_only_theme_on_the_Dark_tab_offers_Add_dark_which_makes_its_rows_editable_until_Revert()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.PathOf("lightonly"), """{ "name": "Light Only", "light": { "tokens": { "accent": "#8250df" } } }""");
        ThemeSettingsViewModel viewModel = fixture.Create(themeId: "lightonly");
        viewModel.SelectedTab = ThemeTab.Dark;

        Assert.All(Tokens(viewModel), row => Assert.False(row.IsSet));
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsEditable));
        Assert.True(viewModel.CanAddDark);
        Assert.False(viewModel.CanAddLight);
        Assert.True(viewModel.ShowsMissingHalf);
        Assert.Equal("This theme has no dark half. In dark mode YAMV shows its light half.", viewModel.MissingHalfNote);
        Assert.Equal("Add dark", viewModel.AddHalfLabel);

        viewModel.AddHalfCommand.Execute(null);

        Assert.All(Tokens(viewModel), row => Assert.True(row.IsEditable));
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsSet));
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsChanged));
        Assert.True(viewModel.IsDirty);
        Assert.False(viewModel.CanAddDark);
        Assert.False(viewModel.ShowsMissingHalf);

        viewModel.RevertCommand.Execute(null);

        Assert.Null(viewModel.WorkingTheme?.Dark);
        Assert.True(viewModel.CanAddDark);
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsEditable));
    }

    [Fact]
    public void A_dark_only_theme_on_the_Light_tab_offers_Add_light()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create(themeId: "alpha");
        viewModel.SelectedTab = ThemeTab.Light;

        Assert.True(viewModel.CanAddLight);
        Assert.Equal("This theme has no light half. In light mode YAMV shows its dark half.", viewModel.MissingHalfNote);
        Assert.Equal("Add light", viewModel.AddHalfLabel);
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsEditable));

        viewModel.AddHalfCommand.Execute(null);

        Assert.NotNull(viewModel.WorkingTheme?.Light);
        Assert.All(Tokens(viewModel), row => Assert.True(row.IsEditable));
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task A_built_in_theme_has_no_editable_row_and_no_Add()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await viewModel.SelectAsync(Row(viewModel, "paper"));

        foreach (ThemeTab tab in Enum.GetValues<ThemeTab>())
        {
            viewModel.SelectedTab = tab;
            Assert.All(Tokens(viewModel), row => Assert.False(row.IsEditable));
            Assert.All(Tokens(viewModel), row => Assert.False(row.CanReset));
        }

        Assert.Contains(Tokens(viewModel), row => row.IsSet);
        Assert.False(viewModel.CanAddDark);
        Assert.False(viewModel.CanAddLight);
        Assert.False(viewModel.AddHalfCommand.CanExecute(null));
    }

    [Fact]
    public void Enter_on_an_inherited_row_sets_the_shown_value_in_the_tab()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        TokenRowViewModel quote = Token(viewModel, "quote");
        ThemeValue shown = quote.Value;

        Assert.True(quote.Commit(quote.Text, fromEnter: true));

        Assert.Equal(shown, viewModel.WorkingTheme?.Light?.Tokens["quote"]);
        Assert.True(quote.IsSet);
        Assert.True(quote.CanReset);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void Leaving_an_unchanged_inherited_row_sets_nothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        ThemeDefinition? before = viewModel.WorkingTheme;
        TokenRowViewModel quote = Token(viewModel, "quote");

        Assert.True(quote.Commit(quote.Text, fromEnter: false));

        Assert.Same(before, viewModel.WorkingTheme);
        Assert.False(quote.IsSet);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void Enter_on_a_set_row_with_its_own_value_changes_nothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        ThemeDefinition? before = viewModel.WorkingTheme;

        Assert.True(Token(viewModel, "accent").Commit("#112233", fromEnter: true));

        Assert.Same(before, viewModel.WorkingTheme);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void Typed_text_on_the_Light_tab_survives_a_change_of_the_appearance_shown()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        viewModel.ShownAppearance = Appearance.Light;
        TokenRowViewModel quote = Token(viewModel, "quote");
        quote.Text = "#abcdef";

        viewModel.ShownAppearance = Appearance.Dark;

        Assert.Equal("#abcdef", quote.Text);
        Assert.False(quote.IsSet);
    }

    [Fact]
    public void Save_clears_the_token_dots()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        viewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
        Assert.True(Token(viewModel, "quote").IsChanged);

        viewModel.SaveCommand.Execute(null);

        Assert.All(Tokens(viewModel), row => Assert.False(row.IsChanged));
        Assert.Empty(viewModel.ChangedIn(ThemeTab.Light));
    }

    [Fact]
    public async Task A_theme_switch_clears_the_token_dots()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;
        viewModel.SetToken(ThemeTab.Light, "quote", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Discard;

        await viewModel.SelectAsync(Row(viewModel, "paper"));

        Assert.Equal("paper", viewModel.Selected?.Id);
        Assert.All(Tokens(viewModel), row => Assert.False(row.IsChanged));
    }

    [Theory]
    [InlineData(ThemeTab.Light, Appearance.Light, "#112233")]
    [InlineData(ThemeTab.Dark, Appearance.Dark, "#4493f8")]
    public void The_preview_resolves_the_half_of_the_open_tab(ThemeTab tab, Appearance half, string accent)
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        viewModel.SelectedTab = tab;

        ResolvedTheme preview = Assert.IsType<ResolvedTheme>(viewModel.PreviewTheme);
        Assert.Equal(half, preview.Appearance);
        Assert.Equal(new ColorValue(accent), preview.Tokens["accent"]);
    }

    [Fact]
    public void The_Shared_tab_previews_the_half_the_app_shows_and_follows_it()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.ShownAppearance = Appearance.Dark;

        viewModel.SelectedTab = ThemeTab.Shared;

        Assert.Equal(Appearance.Dark, viewModel.PreviewTheme?.Appearance);

        viewModel.ShownAppearance = Appearance.Light;

        Assert.Equal(Appearance.Light, viewModel.PreviewTheme?.Appearance);
    }

    [Fact]
    public async Task A_one_half_theme_previews_the_half_it_has_on_the_other_tab()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await viewModel.SelectAsync(Row(viewModel, "alpha"));

        viewModel.SelectedTab = ThemeTab.Light;

        Assert.Equal(Appearance.Dark, viewModel.PreviewTheme?.Appearance);
        Assert.Equal(new ColorValue("#223344"), viewModel.PreviewTheme?.Tokens["accent"]);
    }

    [Fact]
    public void An_edit_changes_the_preview_at_once_without_applying_the_theme_to_the_app()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SelectedTab = ThemeTab.Light;

        viewModel.SetToken(ThemeTab.Light, "pageBackground", new ColorValue("#3fb950"));

        Assert.Equal(new ColorValue("#3fb950"), viewModel.PreviewTheme?.Tokens["pageBackground"]);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task An_invalid_theme_has_no_preview()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        Assert.NotNull(viewModel.PreviewTheme);

        await viewModel.SelectAsync(Row(viewModel, "nord"));

        Assert.Null(viewModel.PreviewTheme);
    }

    [Fact]
    public async Task Import_Cancelled_ChangesNothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.Empty(fixture.ImportAsked);
        Assert.Equal(3, viewModel.UserRows.Count);
        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.Empty(fixture.Applied);
        Assert.Null(viewModel.ErrorLine);
    }

    [Fact]
    public async Task Import_NoticeDeclined_ImportsNothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        fixture.ImportPick = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);
        fixture.ImportAnswer = false;

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal("Sunset", Assert.Single(fixture.ImportAsked).Name);
        Assert.False(File.Exists(fixture.PathOf("sunset")));
        Assert.Equal(3, viewModel.UserRows.Count);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Import_NoticeDeclined_KeepsAnEarlierError()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        string? earlier = await FailAnExport(fixture, viewModel);
        fixture.ImportPick = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);
        fixture.ImportAnswer = false;

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.Single(fixture.ImportAsked);
        Assert.Equal(earlier, viewModel.ErrorLine);
    }

    [Fact]
    public async Task Import_Accepted_ClearsAnEarlierError()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await FailAnExport(fixture, viewModel);
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Cancel;
        fixture.ImportPick = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(fixture.PathOf("sunset")));
        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.Null(viewModel.ErrorLine);
    }

    /// <summary>
    /// Runs an Export to a path under a file, which fails at once without the replace's retries, and returns the error line it
    /// leaves.
    /// </summary>
    private static async Task<string?> FailAnExport(Fixture fixture, ThemeSettingsViewModel viewModel)
    {
        string file = fixture.WriteOutside("plain.txt", "not a folder");
        fixture.ExportPick = Path.Combine(file, "mine.yamv-theme.json");
        await viewModel.ExportCommand.ExecuteAsync(null);
        Assert.StartsWith("Couldn't export: ", viewModel.ErrorLine, StringComparison.Ordinal);
        return viewModel.ErrorLine;
    }

    [Fact]
    public async Task Import_Accepted_SelectsAndAppliesTheImportedTheme()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        fixture.ImportPick = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal(SunsetText, File.ReadAllText(fixture.PathOf("sunset")));
        Assert.Contains(viewModel.UserRows, row => row.Id == "sunset");
        Assert.Equal(("sunset", "Sunset"), (viewModel.Selected?.Id, viewModel.Selected?.Name));
        Assert.Equal([("sunset", "light")], fixture.Applied);
        Assert.Equal("sunset", viewModel.InUseId);
        Assert.Null(viewModel.ErrorLine);
    }

    [Fact]
    public async Task Import_InvalidFile_AsksThenImportsNothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        fixture.ImportPick = fixture.WriteOutside("broken.yamv-theme.json", """{ "light": {} }""");

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["The theme has no 'name'."], Assert.Single(fixture.ImportAsked).Errors);
        Assert.False(File.Exists(fixture.PathOf("broken")));
        Assert.Equal(3, viewModel.UserRows.Count);
        Assert.Empty(fixture.Applied);
        Assert.Null(viewModel.ErrorLine);
    }

    [Fact]
    public async Task Import_WithUnsavedEdits_CancelInConfirm_LeavesItImportedButNotSelected()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Cancel;
        fixture.ImportPick = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(fixture.PathOf("sunset")));
        Assert.Contains(viewModel.UserRows, row => row.Id == "sunset");
        Assert.Equal(["Mine"], fixture.UnsavedAsked);
        Assert.Equal("mine", viewModel.Selected?.Id);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(new ColorValue("#3fb950"), viewModel.WorkingTheme?.Light?.Tokens["accent"]);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Import_Fails_ShowsCouldntImport()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        string source = fixture.WriteOutside("sunset.yamv-theme.json", SunsetText);
        fixture.ImportPick = source;
        fixture.WhileImportAsked = () => File.Delete(source);

        await viewModel.ImportCommand.ExecuteAsync(null);

        Assert.StartsWith("Couldn't import: ", viewModel.ErrorLine, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.PathOf("sunset")));
        Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task Export_Clean_WritesTheSavedFileToThePickedPath()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        string destination = Path.Combine(fixture.OutsideFolder, "shared.yamv-theme.json");
        fixture.ExportPick = destination;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(MineText, File.ReadAllText(destination));
        Assert.Empty(fixture.UnsavedAsked);
        Assert.Null(viewModel.ErrorLine);
    }

    [Fact]
    public async Task Export_SuggestsIdFileName()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(["mine.yamv-theme.json"], fixture.ExportSuggested);
    }

    [Fact]
    public async Task Export_Dirty_Cancel_ExportsNothing()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Cancel;
        string destination = Path.Combine(fixture.OutsideFolder, "shared.yamv-theme.json");
        fixture.ExportPick = destination;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(["Mine"], fixture.UnsavedAsked);
        Assert.Empty(fixture.ExportSuggested);
        Assert.False(File.Exists(destination));
        Assert.True(viewModel.IsDirty);
        Assert.Equal(MineText, File.ReadAllText(fixture.PathOf("mine")));
    }

    [Fact]
    public async Task Export_Dirty_Save_SavesThenExports()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Save;
        string destination = Path.Combine(fixture.OutsideFolder, "shared.yamv-theme.json");
        fixture.ExportPick = destination;

        await viewModel.ExportCommand.ExecuteAsync(null);

        string saved = MineText.Replace("#112233", "#3fb950", StringComparison.Ordinal);
        Assert.Equal(saved, File.ReadAllText(fixture.PathOf("mine")));
        Assert.Equal(saved, File.ReadAllText(destination));
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task Export_Dirty_Discard_ExportsTheSavedTheme()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        viewModel.SetToken(ThemeTab.Light, "accent", new ColorValue("#3fb950"));
        fixture.UnsavedAnswer = UnsavedChoice.Discard;
        string destination = Path.Combine(fixture.OutsideFolder, "shared.yamv-theme.json");
        fixture.ExportPick = destination;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(MineText, File.ReadAllText(destination));
        Assert.Equal(MineText, File.ReadAllText(fixture.PathOf("mine")));
        Assert.False(viewModel.IsDirty);
        Assert.Same(viewModel.SavedTheme, viewModel.WorkingTheme);
    }

    [Fact]
    public async Task Export_BuiltIn_IsEnabled()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        await viewModel.SelectAsync(Row(viewModel, "studio"));
        string destination = Path.Combine(fixture.OutsideFolder, "studio.yamv-theme.json");
        fixture.ExportPick = destination;

        Assert.True(viewModel.ExportCommand.CanExecute(null));

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(["studio.yamv-theme.json"], fixture.ExportSuggested);
        Assert.Contains("\"Studio\"", File.ReadAllText(destination), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_InvalidTheme_IsDisabled()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        Assert.True(viewModel.ExportCommand.CanExecute(null));

        await viewModel.SelectAsync(Row(viewModel, "nord"));

        Assert.False(viewModel.ExportCommand.CanExecute(null));
        Assert.True(viewModel.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_Fails_ShowsCouldntExport()
    {
        using var fixture = new Fixture();
        ThemeSettingsViewModel viewModel = fixture.Create();
        string destination = Path.Combine(fixture.OutsideFolder, "taken.yamv-theme.json");
        Directory.CreateDirectory(destination);
        fixture.ExportPick = destination;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.StartsWith("Couldn't export: ", viewModel.ErrorLine, StringComparison.Ordinal);
        Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
    }

    private const string SunsetText = """
        {
          // Warm evenings.
          "name": "Sunset",
          "light": { "tokens": { "accent": "#d2691e" } }
        }
        """;

    private static IEnumerable<TokenRowViewModel> Tokens(ThemeSettingsViewModel viewModel) => viewModel.TokenGroups.SelectMany(group => group.Rows);

    private static TokenRowViewModel Token(ThemeSettingsViewModel viewModel, string name) => Tokens(viewModel).Single(row => row.Name == name);

    private static ThemeRowViewModel Row(ThemeSettingsViewModel viewModel, string id) =>
        viewModel.UserRows.Concat(viewModel.BuiltInRows).First(row => row.Id == id);

    /// <summary>A themes folder with two valid user themes, Mine and Alpha, and an invalid nord, and recording seams.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-theme-settings-" + Guid.NewGuid().ToString("N"));
        private string? _deviceFile;

        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PathOf("mine"), MineText);
            File.WriteAllText(PathOf("alpha"), """{ "name": "Alpha", "dark": { "tokens": { "accent": "#223344" } } }""");
            File.WriteAllText(PathOf("nord"), """{ "name": "Nord", "dark": { "tokens": { "accent": "#88c0d" } } }""");
        }

        public RecordingAppLog Log { get; } = new();

        public List<(string Id, string Appearance)> Applied { get; } = [];

        public List<string> Trashed { get; } = [];

        public List<string> UnsavedAsked { get; } = [];

        public List<string> DeleteAsked { get; } = [];

        public UnsavedChoice UnsavedAnswer { get; set; } = UnsavedChoice.Cancel;

        public bool DeleteAnswer { get; set; } = true;

        /// <summary>A folder outside the themes folder, for files to import and export destinations.</summary>
        public string OutsideFolder => _folder + "-outside";

        /// <summary>What the Import picker returns; null is a cancelled pick.</summary>
        public string? ImportPick { get; set; }

        /// <summary>Every inspection the Import notice was asked with, oldest first.</summary>
        public List<ThemeInspection> ImportAsked { get; } = [];

        /// <summary>The Import notice's answer: true is Import.</summary>
        public bool ImportAnswer { get; set; } = true;

        /// <summary>Runs while the Import notice is open, before it answers.</summary>
        public Action? WhileImportAsked { get; set; }

        /// <summary>What the Export picker returns; null is a cancelled pick.</summary>
        public string? ExportPick { get; set; }

        /// <summary>The file name the Export picker suggested on each pick, oldest first.</summary>
        public List<string> ExportSuggested { get; } = [];

        public string PathOf(string id) => Path.Combine(_folder, id + ThemeCatalog.FileSuffix);

        public string WriteOutside(string fileName, string text)
        {
            Directory.CreateDirectory(OutsideFolder);
            string path = Path.Combine(OutsideFolder, fileName);
            File.WriteAllText(path, text);
            return path;
        }

        public ThemeSettingsViewModel Create(string themeId = "mine", string appearance = "light") =>
            new(
                new ThemeCatalog(_folder, Log),
                new ThemeSettingsSeams
                {
                    Apply = (id, shown) => Applied.Add((id, shown)),
                    Trash = new RecordingTrash(Trashed),
                    OpenAsText = _ => { },
                    ConfirmUnsaved = name =>
                    {
                        UnsavedAsked.Add(name);
                        return Task.FromResult(UnsavedAnswer);
                    },
                    ConfirmDelete = name =>
                    {
                        DeleteAsked.Add(name);
                        return Task.FromResult(DeleteAnswer);
                    },
                    PickImport = () => Task.FromResult(ImportPick),
                    PickExport = suggested =>
                    {
                        ExportSuggested.Add(suggested);
                        return Task.FromResult(ExportPick);
                    },
                    ConfirmImport = inspection =>
                    {
                        ImportAsked.Add(inspection);
                        WhileImportAsked?.Invoke();
                        return Task.FromResult(ImportAnswer);
                    },
                },
                themeId,
                appearance,
                Log
            );

        /// <summary>
        /// Stops a save of <paramref name="id"/>: a read-only file on Windows, where a move cannot replace it, and a read-only
        /// folder elsewhere, where a rename ignores the file's own mode.
        /// </summary>
        public void MakeUnwritable(string id)
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(PathOf(id), FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }

        public void MakeWritable(string id)
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(PathOf(id), FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        /// <summary>
        /// Writes a valid theme file named after the device <c>con</c>. Windows opens the device for a plain path to such a
        /// name, so the file is written and removed through the <c>\\?\</c> form, which names the file itself.
        /// </summary>
        public void WriteDeviceNamedFile()
        {
            _deviceFile = PathOf("con");
            File.WriteAllText(Literal(_deviceFile), """{ "name": "Con", "light": { "tokens": { "accent": "#112233" } } }""");
        }

        public void Dispose()
        {
            if (_deviceFile is not null)
            {
                File.Delete(Literal(_deviceFile));
            }

            Directory.Delete(_folder, recursive: true);
            if (Directory.Exists(OutsideFolder))
            {
                Directory.Delete(OutsideFolder, recursive: true);
            }
        }

        private static string Literal(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;
    }

    /// <summary>Records each path it is given and deletes the file, as a move to the trash would remove it from the folder.</summary>
    private sealed class RecordingTrash(List<string> trashed) : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            trashed.Add(path);
            File.Delete(path);
        }
    }
}
