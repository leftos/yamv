using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yamv.Core.Theming;

namespace Yamv.App.ViewModels;

/// <summary>
/// One row of the theme settings window's list: a theme the catalog lists, valid or not, with the colours of its chip and the
/// state of its inline rename.
/// </summary>
public sealed partial class ThemeRowViewModel : ViewModelBase
{
    /// <summary>Creates the row of <paramref name="entry"/>.</summary>
    /// <param name="entry">The catalog's entry for the theme.</param>
    public ThemeRowViewModel(ThemeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        Name = entry.Result.Theme?.Name ?? entry.Id;
        if (entry.Result.Theme is { } theme)
        {
            ResolvedTheme light = ThemeResolver.Resolve(theme, Appearance.Light);
            ResolvedTheme dark = ThemeResolver.Resolve(theme, Appearance.Dark);
            LightBackground = ColorOf(light, "pageBackground");
            LightAccent = ColorOf(light, "accent");
            DarkBackground = ColorOf(dark, "pageBackground");
            DarkAccent = ColorOf(dark, "accent");
        }
    }

    /// <summary>The catalog's entry for the theme.</summary>
    public ThemeEntry Entry { get; }

    /// <summary>The theme's id, its file name without the suffix.</summary>
    public string Id => Entry.Id;

    /// <summary>The theme's name, or for a theme that did not load its file name without the suffix.</summary>
    public string Name { get; }

    /// <summary>The theme's file, or null for a built-in theme.</summary>
    public string? FilePath => Entry.FilePath;

    /// <summary>The theme, or null when it did not load.</summary>
    public ThemeDefinition? Theme => Entry.Result.Theme;

    /// <summary>The errors that kept the theme from loading; empty for a valid theme.</summary>
    public IReadOnlyList<string> Errors => Entry.Result.Errors;

    public bool IsValid => Entry.Result.Theme is not null;

    public bool IsBuiltIn => Entry.Source == ThemeSource.BuiltIn;

    /// <summary>Whether the row's context menu offers Rename: a valid user theme whose id the catalog can take.</summary>
    public bool CanRename => CanDelete && IsValid;

    /// <summary>
    /// Whether the row's context menu offers Delete: any user theme, valid or not, whose id the catalog can take (a file named
    /// after a Windows device, such as <c>con</c>, is listed but cannot be deleted from here).
    /// </summary>
    public bool CanDelete => !IsBuiltIn && ThemeIds.IsValid(Id);

    /// <summary>The light half's page background, for the chip; transparent for a theme that did not load.</summary>
    public Color LightBackground { get; }

    /// <summary>The light half's accent, for the chip's bar.</summary>
    public Color LightAccent { get; }

    /// <summary>The dark half's page background, for the chip.</summary>
    public Color DarkBackground { get; }

    /// <summary>The dark half's accent, for the chip's bar.</summary>
    public Color DarkAccent { get; }

    /// <summary>Whether the row is the theme the window shows.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Whether the row's name is an open text box.</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>The name in the rename text box.</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; } = "";

    /// <summary>The key that finds the row again after the list reloads.</summary>
    internal (ThemeSource Source, string Id) Key => (Entry.Source, Entry.Id);

    private static Color ColorOf(ResolvedTheme theme, string token) =>
        theme.Tokens[token] is ColorValue color ? Color.FromUInt32(color.ToArgb()) : Colors.Transparent;
}
