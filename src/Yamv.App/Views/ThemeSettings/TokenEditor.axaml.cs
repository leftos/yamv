using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yamv.App.ViewModels;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>
/// The token editor of the theme settings window: the rows of the open tab in their groups, under the note of a half the theme
/// lacks. A field applies its text when it commits (Enter, or focus leaving it) and Esc puts its shown value back; a swatch
/// click raises <see cref="PickRequested"/>.
/// </summary>
public partial class TokenEditor : UserControl
{
    public TokenEditor() => InitializeComponent();

    /// <summary>A colour row's swatch was clicked.</summary>
    public event EventHandler<TokenPickEventArgs>? PickRequested;

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TokenRowViewModel row } && DataContext is ThemeSettingsViewModel viewModel)
        {
            PickRequested?.Invoke(this, new TokenPickEventArgs(row.Name, viewModel.SelectedTab));
        }
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TokenRowViewModel row } box)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            row.Commit(box.Text ?? "", fromEnter: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.CancelEdit();
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TokenRowViewModel { IsEditable: true } row } box)
        {
            row.Commit(box.Text ?? "", fromEnter: false);
        }
    }
}
