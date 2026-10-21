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

        public string PathOf(string id) => Path.Combine(_folder, id + ThemeCatalog.FileSuffix);

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
