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

    /// <summary>Asks for a theme file to import; null when the pick was cancelled.</summary>
    public required Func<Task<string?>> PickImport { get; init; }

    /// <summary>Asks where to export a theme, suggesting the file name its argument holds; null when the pick was cancelled.</summary>
    public required Func<string, Task<string?>> PickExport { get; init; }

    /// <summary>Shows the Import notice for the inspected file; true when the user chose Import.</summary>
    public required Func<ThemeInspection, Task<bool>> ConfirmImport { get; init; }
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
        nameof(IsMissing),
        nameof(ShowsEditor),
        nameof(CanEditAsText),
        nameof(CanExport),
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

    /// <summary>The selected theme's row kept with its last valid version while its file no longer loads, or null.</summary>
    private ThemeRowViewModel? _brokenRow;

    /// <summary>The row kept for the theme in use while its file is gone from the themes folder, or null.</summary>
    private ThemeRowViewModel? _missingRow;

    /// <summary>The selected theme as its changed file holds it while the bar is up over unsaved edits, or null.</summary>
    private ThemeDefinition? _diskTheme;

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

    /// <summary>
    /// Whether the window shows the bar that says the selected theme's file changed on disk: while it holds unsaved edits, or
    /// since the file no longer loads.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadFromDiskCommand), nameof(KeepMineCommand))]
    public partial bool ChangedOnDisk { get; private set; }

    /// <summary>The error of the selected theme's changed file when it no longer loads, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskBroken), nameof(DiskBarTitle), nameof(DiskBarText))]
    [NotifyCanExecuteChangedFor(nameof(ReloadFromDiskCommand))]
    public partial string? DiskError { get; private set; }

    /// <summary>Whether the selected theme's changed file no longer loads, so the bar takes the warning colours.</summary>
    public bool DiskBroken => DiskError is not null;

    /// <summary>The bar's bold text, before the error of a file that no longer loads.</summary>
    public string DiskBarTitle => DiskBroken ? "This theme changed on disk and no longer loads:" : "This theme changed on disk";

    /// <summary>The bar's whole text: its title, then the error of a file that no longer loads.</summary>
    public string DiskBarText => DiskError is { } error ? $"{DiskBarTitle} {error}" : DiskBarTitle;

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

    /// <summary>Whether the selected theme is a valid user theme on disk, which edits can change.</summary>
    public bool IsEditable => Selected is { IsBuiltIn: false, IsValid: true, IsMissing: false };

    /// <summary>
    /// Whether the selected row is the theme in use whose file is gone, shown in its last loaded version with any unsaved edits;
    /// only Save, which writes its file again, and Revert are on.
    /// </summary>
    public bool IsMissing => Selected?.IsMissing == true;

    /// <summary>Whether the token editor shows: the selected theme loaded.</summary>
    public bool ShowsEditor => Selected is { IsValid: true };

    /// <summary>Whether Edit as text is on: a user theme with a file on disk, valid or not.</summary>
    public bool CanEditAsText => Selected is { IsBuiltIn: false, FilePath: not null, IsMissing: false };

    /// <summary>Whether Export is on: the selected theme loaded and is not missing on disk.</summary>
    public bool CanExport => ShowsEditor && !IsMissing;

    public string SelectedName => Selected?.Name ?? "";

    /// <summary>The header's badge, upper-cased as drawn: <c>MISSING ON DISK</c>, <c>BUILT-IN</c> or <c>YOURS</c>.</summary>
    public string Badge =>
        IsMissing ? "MISSING ON DISK"
        : IsBuiltIn ? "BUILT-IN"
        : "YOURS";

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

    /// <summary>
    /// Lists the themes again after the themes folder changed on disk, without applying any theme (the app applies the change
    /// itself), and follows the selected theme's file. Content equal to the saved theme changes nothing; a change with no unsaved
    /// edits is shown at once; a change under unsaved edits keeps them and raises the bar (<see cref="ChangedOnDisk"/>) with
    /// Reload and Keep mine; a file that no longer loads keeps the last valid version, edits included, and raises the bar with
    /// <see cref="DiskError"/>. The theme in use, deleted or renamed outside the app, stays listed as a missing row in its last
    /// loaded version, keeping any unsaved edits, which Save writes to its file again and Revert drops; another selected theme that
    /// is gone gives way to the theme in use.
    /// </summary>
    public void OnThemesChangedOnDisk()
    {
        ThemeRowViewModel? previous = Selected;
        string? previousError = previous?.DiskError;
        IReadOnlyList<ThemeEntry> entries = _catalog.LoadAll();
        HoldMissingInUse(entries);
        if (
            previous is { IsValid: true, IsMissing: false }
            && entries.Any(entry => SameKey(entry.Source, entry.Id, previous.Key) && !IsLoaded(entry))
        )
        {
            _brokenRow = previous;
        }

        List(entries, previous?.Key);
        FollowSelected(previousError);
        NotifyDerived();
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
        if (!(IsEditable || IsMissing) || WorkingTheme is not { } working)
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
        ClearDiskBar();
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

        bool wasSelected = Selected is { } selected && SameKey(selected.Entry.Source, selected.Id, row.Key);
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

    /// <summary>Whether Save and Revert are on: unsaved edits on an editable theme, or on the theme in use whose file is gone.</summary>
    private bool CanSave() => (IsEditable || IsMissing) && IsDirty;

    /// <summary>Drops the unsaved edits; with the bar up over a changed file that loads, shows the file as it now holds the theme.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        if (ChangedOnDisk && !DiskBroken && Selected is { } row)
        {
            Show(row, apply: false);
            return;
        }

        WorkingTheme = SavedTheme;
        ErrorLine = null;
        IsDirty = false;
        ClearDiskBar();
        NotifyDerived();
    }

    /// <summary>Drops the unsaved edits and shows the selected theme as its changed file holds it.</summary>
    [RelayCommand(CanExecute = nameof(CanReloadFromDisk))]
    private void ReloadFromDisk()
    {
        if (Selected is { } row)
        {
            Show(row, apply: false);
        }
    }

    private bool CanReloadFromDisk() => ChangedOnDisk && !DiskBroken;

    /// <summary>
    /// Clears the bar and keeps the unsaved edits, measured from now on against the changed file, so the next Save writes the
    /// working copy over it. While the file no longer loads, the saved theme stays the last valid version.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ChangedOnDisk))]
    private void KeepMine()
    {
        if (_diskTheme is { } disk && WorkingTheme is { } working)
        {
            SavedTheme = disk;
            IsDirty = !working.ContentEquals(disk);
        }

        ClearDiskBar();
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

    /// <summary>
    /// Asks for a theme file, shows the Import notice for it and, after Import, copies it into Yours, then selects and applies it
    /// through <see cref="SelectAsync"/>, so unsaved edits are asked about first and Cancel there leaves it imported but not selected.
    /// A file that does not load still gets the notice and is never imported.
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (await _seams.PickImport() is not { } path)
        {
            return;
        }

        string fileName = Path.GetFileName(path);
        ThemeInspection inspection;
        try
        {
            inspection = _catalog.Inspect(path);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("import", fileName, exception);
            return;
        }

        if (!await _seams.ConfirmImport(inspection) || inspection.Errors.Count > 0)
        {
            return;
        }

        ThemeEntry imported;
        ErrorLine = null;
        try
        {
            imported = _catalog.Import(path);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("import", fileName, exception);
            return;
        }

        Reload(Selected?.Key);
        NotifyDerived();
        if (Find((ThemeSource.User, imported.Id)) is { } row)
        {
            await SelectAsync(row);
        }
    }

    /// <summary>
    /// Writes the selected theme's saved file where the user picks, suggesting <c>&lt;id&gt;.yamv-theme.json</c>. With unsaved
    /// edits, first asks what to do with them: Save saves and exports, unless the save fails; Discard reverts them and exports the
    /// saved theme; Cancel exports nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (IsDirty && WorkingTheme is { } working)
        {
            UnsavedChoice choice = await _seams.ConfirmUnsaved(working.Name);
            if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !TrySave()))
            {
                return;
            }

            if (choice == UnsavedChoice.Discard)
            {
                Revert();
            }
        }

        if (SavedTheme is not { } saved || await _seams.PickExport(saved.Id + ThemeCatalog.FileSuffix) is not { } destination)
        {
            return;
        }

        ErrorLine = null;
        try
        {
            _catalog.Export(saved, destination);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            Fail("export", saved.Name, exception);
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
        if (_ready && value is not null && FindValid(InUseId) is not ({ IsMissing: true } or { DiskError: not null }))
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
        bool wasSelected = Selected is { } selected && SameKey(selected.Entry.Source, selected.Id, row.Key);
        if (wasSelected)
        {
            ClearDiskBar();
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
            IsBuiltIn ? $"The built-in theme '{Selected!.Id}' is read-only; duplicate it to edit it."
            : IsMissing ? $"The theme '{Selected!.Id}' is missing on disk, so it cannot be edited."
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
        if (apply && row is { IsValid: true, IsMissing: false, DiskError: null })
        {
            _seams.Apply(row.Id, SelectedAppearance.Value);
            InUseId = row.Id;
            DropStaleMissing();
        }

        Select(row);
        SavedTheme = row.Theme;
        WorkingTheme = row.Theme;
        IsDirty = false;
        ErrorLine = null;
        ClearDiskBar();
        if (row.DiskError is { } error)
        {
            RaiseDiskBar(theme: null, error);
        }

        NotifyDerived();
    }

    /// <summary>
    /// Shows what the list holds for the theme selected before a change on disk, whose row had <paramref name="previousError"/>:
    /// the theme in use when it is gone, the bar when its file newly fails to load, its missing row with the working copy and its
    /// unsaved edits kept, or else its file.
    /// </summary>
    private void FollowSelected(string? previousError)
    {
        if (Selected is not { } current)
        {
            Show(FindValid(InUseId) ?? DefaultRow(), apply: false);
        }
        else if (current.DiskError is { } error)
        {
            if (error != previousError)
            {
                RaiseDiskBar(theme: null, error);
            }
        }
        else if (current.IsMissing)
        {
            ClearDiskBar();
        }
        else
        {
            FollowDisk(current);
        }
    }

    /// <summary>Follows the selected theme's file after a change on disk, when it loads or did not load before either.</summary>
    private void FollowDisk(ThemeRowViewModel row)
    {
        if (row.Theme is not { } theme || SavedTheme is not { } saved)
        {
            Show(row, apply: false);
        }
        else if (theme.ContentEquals(saved))
        {
            ClearDiskBar();
        }
        else if (!IsDirty)
        {
            Show(row, apply: false);
        }
        else
        {
            RaiseDiskBar(theme, error: null);
        }
    }

    private void RaiseDiskBar(ThemeDefinition? theme, string? error)
    {
        _diskTheme = theme;
        DiskError = error;
        ChangedOnDisk = true;
    }

    private void ClearDiskBar()
    {
        _diskTheme = null;
        DiskError = null;
        ChangedOnDisk = false;
    }

    /// <summary>Keeps a row for the theme in use, a user theme, when <paramref name="entries"/> no longer hold its file.</summary>
    private void HoldMissingInUse(IReadOnlyList<ThemeEntry> entries)
    {
        if (
            _missingRow is null
            && FindValid(InUseId) is { IsBuiltIn: false } inUse
            && !entries.Any(entry => SameKey(entry.Source, entry.Id, inUse.Key))
        )
        {
            _missingRow = new ThemeRowViewModel(inUse.Entry) { IsMissing = true };
        }
    }

    /// <summary>Takes the missing row out of the list once another theme is in use.</summary>
    private void DropStaleMissing()
    {
        if (_missingRow is { } missing && !ThemeIds.Comparer.Equals(missing.Id, InUseId))
        {
            _missingRow = null;
            UserRows = [.. UserRows.Where(row => !ReferenceEquals(row, missing))];
        }
    }

    /// <summary>
    /// Puts the broken row in place of its file's invalid row while the file does not load, with the file's errors; ends the hold
    /// once it loads again or is gone.
    /// </summary>
    private void HoldBroken(List<ThemeRowViewModel> user)
    {
        if (_brokenRow is not { } broken)
        {
            return;
        }

        int index = user.FindIndex(row => SameKey(row.Entry.Source, row.Id, broken.Key));
        if (index >= 0 && !user[index].IsValid)
        {
            broken.DiskError = string.Join(" ", user[index].Errors);
            user[index] = broken;
            return;
        }

        broken.DiskError = null;
        _brokenRow = null;
    }

    /// <summary>Adds the missing row to the list while its theme is in use and no file of its id is back.</summary>
    private void HoldMissing(List<ThemeRowViewModel> user)
    {
        if (_missingRow is not { } missing)
        {
            return;
        }

        if (ThemeIds.Comparer.Equals(missing.Id, InUseId) && !user.Exists(row => SameKey(row.Entry.Source, row.Id, missing.Key)))
        {
            user.Add(missing);
            return;
        }

        _missingRow = null;
    }

    private static bool IsLoaded(ThemeEntry entry) => entry.Result.Theme is not null;

    private static bool SameKey(ThemeSource source, string id, (ThemeSource Source, string Id) key) =>
        source == key.Source && ThemeIds.Comparer.Equals(id, key.Id);

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
    private void Reload((ThemeSource Source, string Id)? key) => List(_catalog.LoadAll(), key);

    /// <summary>
    /// Lists <paramref name="entries"/>, with the broken and missing rows held in their places, and selects the row with
    /// <paramref name="key"/>, if any.
    /// </summary>
    private void List(IReadOnlyList<ThemeEntry> entries, (ThemeSource Source, string Id)? key)
    {
        BuiltInRows = [.. entries.Where(entry => entry.Source == ThemeSource.BuiltIn).Select(entry => new ThemeRowViewModel(entry))];
        List<ThemeRowViewModel> user = [.. entries.Where(entry => entry.Source == ThemeSource.User).Select(entry => new ThemeRowViewModel(entry))];
        HoldBroken(user);
        HoldMissing(user);
        UserRows = [.. user.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Id, StringComparer.Ordinal)];
        Select(key is { } wanted ? Find(wanted) : null);
    }

    private ThemeRowViewModel? Find((ThemeSource Source, string Id) key) => Rows.FirstOrDefault(row => SameKey(row.Entry.Source, row.Id, key));

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
        ExportCommand.NotifyCanExecuteChanged();
    }
}
