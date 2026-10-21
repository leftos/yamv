using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;

namespace Yamv.App.ViewModels;

/// <summary>The answer to "this theme has changes that aren't saved".</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>An Appearance choice: the value the app stores (<c>system</c>, <c>light</c>, <c>dark</c>) and its label.</summary>
public sealed record AppearanceChoice(string Value, string Label);

/// <summary>What the theme settings window reaches outside itself through.</summary>
public sealed class ThemeSettingsSeams
{
    /// <summary>Makes a theme the app's theme: its id and the appearance (<c>system</c>, <c>light</c> or <c>dark</c>).</summary>
    public required Action<string, string> Apply { get; init; }

    /// <summary>Where Delete moves a theme's file.</summary>
    public required IFileTrash Trash { get; init; }

    /// <summary>Opens a theme file in the user's editor.</summary>
    public required Action<string> OpenAsText { get; init; }

    /// <summary>Asks what to do with the unsaved changes of the theme named by its argument.</summary>
    public required Func<string, Task<UnsavedChoice>> ConfirmUnsaved { get; init; }

    /// <summary>Asks whether to delete the theme named by its argument.</summary>
    public required Func<string, Task<bool>> ConfirmDelete { get; init; }
}

/// <summary>
/// The theme settings window: the theme list, the theme selected in it, and a working copy of that theme that edits change until
/// Save writes it or Revert drops them. Selecting a valid theme makes it the app's theme; selecting one that did not load only
/// shows its errors. Built-in themes are read-only. Every member is called on the UI thread.
/// </summary>
public sealed partial class ThemeSettingsViewModel : ViewModelBase
{
    private static readonly string[] DerivedProperties =
    [
        nameof(IsBuiltIn),
        nameof(IsInvalid),
        nameof(IsEditable),
        nameof(ShowsEditor),
        nameof(CanEditAsText),
        nameof(SelectedName),
        nameof(Badge),
        nameof(Description),
        nameof(Errors),
        nameof(InvalidNote),
        nameof(TitleDetail),
    ];

    private static readonly string[] TabProperties =
    [
        nameof(SharedTabOn),
        nameof(LightTabOn),
        nameof(DarkTabOn),
        nameof(ShowsMissingHalf),
        nameof(MissingHalfNote),
        nameof(AddHalfLabel),
        nameof(CanAddHalf),
        nameof(CanAddDark),
        nameof(CanAddLight),
        nameof(ShowsHint),
        nameof(Hint),
    ];

    private readonly ThemeCatalog _catalog;
    private readonly ThemeSettingsSeams _seams;
    private readonly IAppLog _log;
    private readonly IReadOnlyList<TokenRowViewModel> _tokenRows;
    private readonly bool _ready;

    /// <summary>Lists the themes and selects the theme in use, without applying it again.</summary>
    /// <param name="catalog">The themes to list and edit.</param>
    /// <param name="seams">The app's side: applying a theme, the trash, the text editor and the confirms.</param>
    /// <param name="themeId">The id of the theme in use.</param>
    /// <param name="appearance">The appearance in use: <c>system</c>, <c>light</c> or <c>dark</c>; anything else is <c>system</c>.</param>
    /// <param name="log">Where failed writes are reported.</param>
    public ThemeSettingsViewModel(ThemeCatalog catalog, ThemeSettingsSeams seams, string themeId, string appearance, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(themeId);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _seams = seams;
        _log = log;
        _tokenRows = [.. ThemeTokens.All.Select(token => new TokenRowViewModel(token, this))];
        TokenGroups = [.. _tokenRows.GroupBy(row => row.Token.Group).Select(group => new TokenGroupViewModel(group.Key, [.. group]))];
        SelectedAppearance = Appearances.FirstOrDefault(choice => choice.Value == appearance) ?? Appearances[0];
        Reload(null);
        ThemeRowViewModel? start = FindValid(themeId);
        InUseId = start is null ? ThemeCatalog.DefaultThemeId : themeId;
        start ??= DefaultRow();
        Show(start, apply: false);
        _ready = true;
    }

    /// <summary>The Appearance choices, in the order the window lists them.</summary>
    public static IReadOnlyList<AppearanceChoice> Appearances { get; } = [new("system", "Match system"), new("light", "Light"), new("dark", "Dark")];

    /// <summary>The built-in themes, in <see cref="ThemeCatalog.BuiltInIds"/> order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ThemeRowViewModel> BuiltInRows { get; private set; } = [];

    /// <summary>The user themes, valid or not, sorted by name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ThemeRowViewModel> UserRows { get; private set; } = [];

    /// <summary>The row the window shows.</summary>
    [ObservableProperty]
    public partial ThemeRowViewModel? Selected { get; private set; }

    /// <summary>The selected theme as its file holds it, or null when it did not load.</summary>
    [ObservableProperty]
    public partial ThemeDefinition? SavedTheme { get; private set; }

    /// <summary>The selected theme with the unsaved edits, or null when it did not load.</summary>
    [ObservableProperty]
    public partial ThemeDefinition? WorkingTheme { get; private set; }

    /// <summary>Whether the working copy has edits that are not saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; private set; }

    /// <summary>The last failed write, as one line for the header, or null.</summary>
    [ObservableProperty]
    public partial string? ErrorLine { get; private set; }

    /// <summary>The Appearance in use; a change applies the theme in use with it.</summary>
    [ObservableProperty]
    public partial AppearanceChoice SelectedAppearance { get; set; }

    /// <summary>The half the app shows now, which the window reads from the application.</summary>
    [ObservableProperty]
    public partial Appearance ShownAppearance { get; set; }

    /// <summary>The token editor's open tab.</summary>
    [ObservableProperty]
    public partial ThemeTab SelectedTab { get; set; } = ThemeTab.Light;

    /// <summary>
    /// The working copy resolved for the preview: the light half on the Light tab, the dark half on the Dark tab and the half the
    /// app shows on the Shared tab, or the half the theme has when it lacks that one; null when no valid theme is shown.
    /// </summary>
    [ObservableProperty]
    public partial ResolvedTheme? PreviewTheme { get; private set; }

    /// <summary>The token editor's rows of <see cref="SelectedTab"/>, grouped as <see cref="ThemeTokens.All"/> orders them.</summary>
    public IReadOnlyList<TokenGroupViewModel> TokenGroups { get; }

    public bool SharedTabOn => SelectedTab == ThemeTab.Shared;

    public bool LightTabOn => SelectedTab == ThemeTab.Light;

    public bool DarkTabOn => SelectedTab == ThemeTab.Dark;

    /// <summary>Whether the open tab is a half the theme lacks, so its rows show inherited and disabled under a note.</summary>
    public bool ShowsMissingHalf => SelectedTab != ThemeTab.Shared && WorkingTheme is { } working && LayerOf(working, SelectedTab) is null;

    /// <summary>The note over the rows of a missing half.</summary>
    public string MissingHalfNote =>
        SelectedTab == ThemeTab.Light
            ? "This theme has no light half. In light mode YAMV shows its dark half."
            : "This theme has no dark half. In dark mode YAMV shows its light half.";

    /// <summary>The button that adds the open tab's missing half: <c>Add dark</c> or <c>Add light</c>.</summary>
    public string AddHalfLabel => SelectedTab == ThemeTab.Light ? "Add light" : "Add dark";

    /// <summary>Whether the selected theme is editable and lacks a dark half.</summary>
    public bool CanAddDark => IsEditable && WorkingTheme is { Dark: null };

    /// <summary>Whether the selected theme is editable and lacks a light half.</summary>
    public bool CanAddLight => IsEditable && WorkingTheme is { Light: null };

    /// <summary>Whether the open tab's half can be added.</summary>
    public bool CanAddHalf =>
        SelectedTab switch
        {
            ThemeTab.Light => CanAddLight,
            ThemeTab.Dark => CanAddDark,
            _ => false,
        };

    /// <summary>Whether the header shows the hint about grey values and the reset arrow: an editable tab of a user theme.</summary>
    public bool ShowsHint => IsEditable && !ShowsMissingHalf;

    /// <summary>The hint's text: where the open tab's grey values come from.</summary>
    public string Hint =>
        SelectedTab == ThemeTab.Shared
            ? "Grey values come from the defaults. ↺ removes a value from this tab."
            : "Grey values come from Shared or the defaults. ↺ removes a value from this tab.";

    /// <summary>The id of the app's theme.</summary>
    public string InUseId { get; private set; }

    public bool IsBuiltIn => Selected?.IsBuiltIn == true;

    public bool IsInvalid => Selected is { IsValid: false };

    /// <summary>Whether the selected theme is a valid user theme, which edits can change.</summary>
    public bool IsEditable => Selected is { IsBuiltIn: false, IsValid: true };

    /// <summary>Whether the token editor shows: the selected theme loaded.</summary>
    public bool ShowsEditor => Selected is { IsValid: true };

    /// <summary>Whether Edit as text is on: a user theme with a file, valid or not.</summary>
    public bool CanEditAsText => Selected is { IsBuiltIn: false, FilePath: not null };

    public string SelectedName => Selected?.Name ?? "";

    /// <summary>The header's badge, upper-cased as drawn: <c>BUILT-IN</c> or <c>YOURS</c>.</summary>
    public string Badge => IsBuiltIn ? "BUILT-IN" : "YOURS";

    /// <summary>The header's line under the name: a built-in theme's description, else the file name and the halves it has.</summary>
    public string Description
    {
        get
        {
            if (Selected is null)
            {
                return "";
            }

            if (IsBuiltIn)
            {
                return SavedTheme?.Description ?? "";
            }

            string file = Path.GetFileName(Selected.FilePath) ?? "";
            return WorkingTheme is { } theme ? $"{file} · {Halves(theme)}" : file;
        }
    }

    /// <summary>The errors of a selected theme that did not load.</summary>
    public IReadOnlyList<string> Errors => Selected?.Errors ?? [];

    /// <summary>What the invalid-theme pane says under the errors.</summary>
    public string InvalidNote =>
        $"Fix the file and save it: YAMV watches the themes folder and loads it again by itself. Until then the app stays on {InUseName}.";

    /// <summary>The title bar's text after "Themes": the theme's name and the half shown, or that it did not load.</summary>
    public string TitleDetail =>
        Selected is null ? ""
        : IsInvalid ? $"{Selected.Name} · didn't load"
        : $"{Selected.Name} · {(ShownAppearance == Appearance.Dark ? "dark" : "light")}";

    private string InUseName => FindValid(InUseId)?.Name ?? InUseId;

    private IEnumerable<ThemeRowViewModel> Rows => BuiltInRows.Concat(UserRows);

    /// <summary>The names of the tokens whose value in the layer of <paramref name="tab"/> differs from the saved theme's.</summary>
    public IReadOnlySet<string> ChangedIn(ThemeTab tab) =>
        new HashSet<string>(ThemeTokens.All.Select(token => token.Name).Where(name => IsChanged(tab, name)), StringComparer.Ordinal);

    /// <summary>
    /// Shows <paramref name="row"/>; a valid theme is also made the app's theme. With unsaved edits, first asks what to do with
    /// them: Save saves and switches, unless the save fails; Discard switches; Cancel stays.
    /// </summary>
    public async Task SelectAsync(ThemeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ReferenceEquals(row, Selected))
        {
            return;
        }

        (ThemeSource, string) key = row.Key;
        if (IsDirty && WorkingTheme is { } working)
        {
            UnsavedChoice choice = await _seams.ConfirmUnsaved(working.Name);
            if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !TrySave()))
            {
                return;
            }
        }

        if (Find(key) is { } current)
        {
            Show(current, apply: true);
        }
    }

    /// <summary>Sets a token in the working copy.</summary>
    /// <exception cref="InvalidOperationException">The selected theme is built in or did not load.</exception>
    public void SetToken(ThemeTab tab, string name, ThemeValue value) => Edit(ThemeEditing.SetToken(RequireEditable(), tab, name, value));

    /// <summary>Removes a token from a tab of the working copy, so it inherits again.</summary>
    /// <exception cref="InvalidOperationException">The selected theme is built in or did not load.</exception>
    public void ResetToken(ThemeTab tab, string name) => Edit(ThemeEditing.ResetToken(RequireEditable(), tab, name));

    /// <summary>Adds an empty light or dark half to the working copy.</summary>
    /// <exception cref="InvalidOperationException">The selected theme is built in or did not load, or already has the half.</exception>
    public void AddVariant(Appearance appearance) => Edit(ThemeEditing.AddVariant(RequireEditable(), appearance));

    /// <summary>
    /// Writes the working copy; returns whether it was written. A failed write keeps the working copy and sets
    /// <see cref="ErrorLine"/>.
    /// </summary>
    public bool TrySave()
    {
        if (!IsEditable || WorkingTheme is not { } working)
        {
            return false;
        }

        ErrorLine = null;
        try
        {
            _catalog.Save(working);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("save", working.Name, exception);
            return false;
        }

        SavedTheme = working;
        IsDirty = false;
        if (ThemeIds.Comparer.Equals(InUseId, working.Id))
        {
            _seams.Apply(working.Id, SelectedAppearance.Value);
        }

        Reload(Selected?.Key);
        NotifyDerived();
        return true;
    }

    /// <summary>Opens the text box that renames <paramref name="row"/>, holding its name, and closes any other row's.</summary>
    public void BeginRename(ThemeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanRename)
        {
            return;
        }

        foreach (ThemeRowViewModel other in Rows.Where(other => other.IsRenaming))
        {
            other.IsRenaming = false;
        }

        row.RenameText = row.Name;
        row.IsRenaming = true;
    }

    /// <summary>Closes the rename text box of <paramref name="row"/> without renaming.</summary>
    public static void CancelRename(ThemeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.IsRenaming = false;
    }

    /// <summary>
    /// Renames <paramref name="row"/>'s theme to <paramref name="newName"/>, its file following the name. A blank name is not
    /// committed and the text box stays open; the unchanged name closes it. A renamed theme in use is applied again under its new id.
    /// </summary>
    public void CommitRename(ThemeRowViewModel row, string newName)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(newName);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        row.IsRenaming = false;
        string name = newName.Trim();
        if (!row.CanRename || name == row.Name)
        {
            return;
        }

        ThemeDefinition renamed;
        ErrorLine = null;
        try
        {
            renamed = _catalog.Rename(row.Id, name);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("rename", row.Name, exception);
            return;
        }

        Renamed(row, renamed);
    }

    /// <summary>
    /// After a yes from the delete confirm, moves <paramref name="row"/>'s file to the trash. Deleting the theme in use applies
    /// Studio and selects it; deleting another selected theme selects the theme in use without applying it again; deleting an
    /// unselected theme keeps the selection.
    /// </summary>
    public async Task DeleteAsync(ThemeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanDelete || !await _seams.ConfirmDelete(row.Name))
        {
            return;
        }

        bool wasSelected = Selected?.Key == row.Key;
        bool wasInUse = row.IsValid && ThemeIds.Comparer.Equals(InUseId, row.Id);
        ErrorLine = null;
        try
        {
            _catalog.Delete(row.Id, _seams.Trash);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("delete", row.Name, exception);
            return;
        }

        Reload(wasSelected ? null : Selected?.Key);
        if (wasInUse)
        {
            InUseId = ThemeCatalog.DefaultThemeId;
            _seams.Apply(InUseId, SelectedAppearance.Value);
            Show(DefaultRow(), apply: false);
        }
        else if (wasSelected)
        {
            Show(FindValid(InUseId) ?? DefaultRow(), apply: false);
        }

        NotifyDerived();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySave();

    private bool CanSave() => IsEditable && IsDirty;

    /// <summary>Drops the unsaved edits.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        WorkingTheme = SavedTheme;
        ErrorLine = null;
        IsDirty = false;
        NotifyDerived();
    }

    /// <summary>Adds the open tab's missing half, empty, so every token in it shows inherited until set.</summary>
    [RelayCommand(CanExecute = nameof(CanAddHalf))]
    private void AddHalf() => AddVariant(SelectedTab == ThemeTab.Light ? Appearance.Light : Appearance.Dark);

    /// <summary>Opens a tab of the token editor.</summary>
    [RelayCommand]
    private void ShowTab(ThemeTab tab) => SelectedTab = tab;

    /// <summary>Writes an editable copy of the selected built-in theme into Yours, then selects and applies it.</summary>
    [RelayCommand(CanExecute = nameof(IsBuiltIn))]
    private void DuplicateToEdit()
    {
        if (Selected?.Theme is not { } theme)
        {
            return;
        }

        ThemeDefinition copy;
        ErrorLine = null;
        try
        {
            copy = _catalog.Duplicate(theme);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("duplicate", theme.Name, exception);
            return;
        }

        Reload(Selected.Key);
        if (Find((ThemeSource.User, copy.Id)) is { } row)
        {
            Show(row, apply: true);
        }
    }

    /// <summary>Opens the selected user theme's file in the user's editor.</summary>
    [RelayCommand(CanExecute = nameof(CanEditAsText))]
    private void EditAsText()
    {
        if (CanEditAsText)
        {
            _seams.OpenAsText(Selected!.FilePath!);
        }
    }

    partial void OnSelectedAppearanceChanged(AppearanceChoice value)
    {
        if (_ready && value is not null)
        {
            _seams.Apply(InUseId, value.Value);
        }
    }

    partial void OnShownAppearanceChanged(Appearance value)
    {
        OnPropertyChanged(nameof(TitleDetail));
        if (SelectedTab == ThemeTab.Shared)
        {
            RefreshTokens();
        }
    }

    partial void OnSelectedTabChanged(ThemeTab value) => RefreshTokens();

    partial void OnSelectedChanged(ThemeRowViewModel? value) => RefreshTokens();

    partial void OnSavedThemeChanged(ThemeDefinition? value) => RefreshTokens();

    partial void OnWorkingThemeChanged(ThemeDefinition? value) => RefreshTokens();

    /// <summary>The tokens <paramref name="tab"/> edits in <paramref name="theme"/>, or null for a half the theme lacks.</summary>
    private static IReadOnlyDictionary<string, ThemeValue>? LayerOf(ThemeDefinition theme, ThemeTab tab) =>
        tab switch
        {
            ThemeTab.Shared => theme.SharedTokens,
            ThemeTab.Light => theme.Light?.Tokens,
            ThemeTab.Dark => theme.Dark?.Tokens,
            _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, "Unknown theme tab."),
        };

    private static ThemeValue? LayerValue(ThemeDefinition? theme, ThemeTab tab, string name) =>
        theme is null ? null : LayerOf(theme, tab)?.GetValueOrDefault(name);

    private bool IsChanged(ThemeTab tab, string name) => !Equals(LayerValue(WorkingTheme, tab, name), LayerValue(SavedTheme, tab, name));

    /// <summary>Shows the working copy's tokens of the open tab in the rows, and what follows from the tab in the header.</summary>
    private void RefreshTokens()
    {
        if (WorkingTheme is { } working)
        {
            bool editable = IsEditable && LayerOf(working, SelectedTab) is not null;
            IReadOnlyList<ThemeTokenRow> rows = ThemeTokenView.Build(working, SelectedTab, ShownAppearance);
            for (int index = 0; index < rows.Count; index++)
            {
                _tokenRows[index].Update(rows[index], IsChanged(SelectedTab, rows[index].Token.Name), editable);
            }
        }

        foreach (string property in TabProperties)
        {
            OnPropertyChanged(property);
        }

        AddHalfCommand.NotifyCanExecuteChanged();
        UpdatePreview();
    }

    /// <summary>Resolves the working copy for the preview, for the half the open tab shows.</summary>
    private void UpdatePreview()
    {
        Appearance half = SelectedTab switch
        {
            ThemeTab.Light => Appearance.Light,
            ThemeTab.Dark => Appearance.Dark,
            _ => ShownAppearance,
        };
        PreviewTheme = !IsInvalid && WorkingTheme is { } working ? ThemeResolver.Resolve(working, half) : null;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is a catalog call's failure that the header shows: the file system's, a theme the
    /// catalog refuses, an id it cannot take or a file that changed under it (a vanished file shadowing a built-in theme).
    /// </summary>
    private static bool IsWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ThemeException or ArgumentException or InvalidOperationException;

    private static string Halves(ThemeDefinition theme) =>
        (theme.Light, theme.Dark) switch
        {
            (not null, not null) => "light and dark",
            (not null, null) => "light only",
            _ => "dark only",
        };

    private void Renamed(ThemeRowViewModel row, ThemeDefinition renamed)
    {
        bool wasSelected = Selected?.Key == row.Key;
        if (wasSelected)
        {
            SavedTheme = renamed;
            WorkingTheme = WorkingTheme is { } working ? working with { Id = renamed.Id, Name = renamed.Name, FilePath = renamed.FilePath } : renamed;
        }

        if (ThemeIds.Comparer.Equals(InUseId, row.Id))
        {
            InUseId = renamed.Id;
            _seams.Apply(renamed.Id, SelectedAppearance.Value);
        }

        Reload(wasSelected ? (ThemeSource.User, renamed.Id) : Selected?.Key);
        NotifyDerived();
    }

    private ThemeDefinition RequireEditable()
    {
        if (IsEditable && WorkingTheme is { } working)
        {
            return working;
        }

        throw new InvalidOperationException(
            IsBuiltIn
                ? $"The built-in theme '{Selected!.Id}' is read-only; duplicate it to edit it."
                : "The selected theme did not load, so it cannot be edited."
        );
    }

    /// <summary>Makes <paramref name="edited"/> the working copy, dirty while its content differs from the saved theme's.</summary>
    private void Edit(ThemeDefinition edited)
    {
        if (ReferenceEquals(edited, WorkingTheme))
        {
            return;
        }

        WorkingTheme = edited;
        IsDirty = SavedTheme is not { } saved || !edited.ContentEquals(saved);
        NotifyDerived();
    }

    private void Show(ThemeRowViewModel row, bool apply)
    {
        if (apply && row.IsValid)
        {
            _seams.Apply(row.Id, SelectedAppearance.Value);
            InUseId = row.Id;
        }

        Select(row);
        SavedTheme = row.Theme;
        WorkingTheme = row.Theme;
        IsDirty = false;
        ErrorLine = null;
        NotifyDerived();
    }

    private void Select(ThemeRowViewModel? row)
    {
        if (Selected is { } previous)
        {
            previous.IsSelected = false;
        }

        Selected = row;
        row?.IsSelected = true;
    }

    /// <summary>Lists the catalog's themes again and selects the row with <paramref name="key"/>, if any.</summary>
    private void Reload((ThemeSource Source, string Id)? key)
    {
        IReadOnlyList<ThemeEntry> entries = _catalog.LoadAll();
        BuiltInRows = [.. entries.Where(entry => entry.Source == ThemeSource.BuiltIn).Select(entry => new ThemeRowViewModel(entry))];
        UserRows =
        [
            .. entries
                .Where(entry => entry.Source == ThemeSource.User)
                .Select(entry => new ThemeRowViewModel(entry))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Id, StringComparer.Ordinal),
        ];
        Select(key is { } wanted ? Find(wanted) : null);
    }

    private ThemeRowViewModel? Find((ThemeSource Source, string Id) key) =>
        Rows.FirstOrDefault(row => row.Entry.Source == key.Source && ThemeIds.Comparer.Equals(row.Id, key.Id));

    private ThemeRowViewModel? FindValid(string id) => Rows.FirstOrDefault(row => row.IsValid && ThemeIds.Comparer.Equals(row.Id, id));

    private ThemeRowViewModel DefaultRow() =>
        FindValid(ThemeCatalog.DefaultThemeId)
        ?? throw new InvalidOperationException($"The theme '{ThemeCatalog.DefaultThemeId}' is not in the catalog.");

    private void Fail(string action, string name, Exception exception)
    {
        _log.LogError($"Cannot {action} the theme '{name}'.", exception);
        ErrorLine = $"Couldn't {action}: {exception.Message}";
    }

    private void NotifyDerived()
    {
        foreach (string property in DerivedProperties)
        {
            OnPropertyChanged(property);
        }

        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        DuplicateToEditCommand.NotifyCanExecuteChanged();
        EditAsTextCommand.NotifyCanExecuteChanged();
    }
}
