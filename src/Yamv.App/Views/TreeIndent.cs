using Avalonia;
using Avalonia.Data.Converters;

namespace Yamv.App.Views;

/// <summary>
/// The left inset of a folder or document row's content by its depth: 26 px for a row directly under a group (level 1), and
/// 16 px more for each folder level below that. Group headers set their own padding.
/// </summary>
public static class TreeIndent
{
    private const double FirstLevelInset = 26;
    private const double PerLevel = 16;
    private const double RightInset = 8;

    /// <summary>Turns a <c>TreeViewItem.Level</c> of 1 or more into the row content's margin.</summary>
    public static readonly IValueConverter Margin = new FuncValueConverter<int, Thickness>(level => new Thickness(
        FirstLevelInset + (PerLevel * (level - 1)),
        0,
        RightInset,
        0
    ));
}
