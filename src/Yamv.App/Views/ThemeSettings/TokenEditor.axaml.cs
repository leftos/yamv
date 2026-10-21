using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yamv.App.ViewModels;
using Yamv.Core.Theming;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>
/// The token editor of the theme settings window: the rows of the open tab in their groups, under the note of a half the theme
/// lacks. A field applies its text when it commits (Enter, or focus leaving it) and Esc puts its shown value back; a swatch
/// click raises <see cref="PickRequested"/> and, on an editable row, opens the colour picker.
/// </summary>
public partial class TokenEditor : UserControl
{
    private Pick? _pick;

    public TokenEditor()
    {
        InitializeComponent();
        ColorPicker.ColorChanged += OnPickerColorChanged;
        ColorPicker.CancelRequested += OnPickerCancel;
        ColorPicker.AcceptRequested += (_, _) => PickerPopup.IsOpen = false;
        PickerPopup.Closed += OnPickerClosed;
    }

    /// <summary>A colour row's swatch was clicked.</summary>
    public event EventHandler<TokenPickEventArgs>? PickRequested;

    /// <summary>
    /// Opens the colour picker under <paramref name="swatch"/> on <paramref name="row"/>'s colour. Each change in it sets the token
    /// in the working copy at once; Esc puts the token back as it was (resetting one that was inherited), and a click outside or
    /// Enter keeps the change.
    /// </summary>
    private void OpenPicker(Control swatch, TokenRowViewModel row, ThemeTab tab, ColorValue was)
    {
        _pick = new Pick(row.Name, tab, was, row.IsSet);
        PickerPopup.PlacementTarget = swatch;
        ColorPicker.Show(was);
        PickerPopup.IsOpen = true;
        Dispatcher.UIThread.Post(ColorPicker.FocusHex, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Keeps the picker's change when it closes by Enter or a click outside; a token that was inherited and is back at its
    /// inherited colour is reset, so it stays inherited.
    /// </summary>
    private void OnPickerClosed(object? sender, EventArgs e)
    {
        if (_pick is { Changed: true, WasSet: false } pick && ColorPicker.Color == pick.Was && DataContext is ThemeSettingsViewModel viewModel)
        {
            viewModel.ResetToken(pick.Tab, pick.Token);
        }

        _pick = null;
    }

    private void ClosePicker()
    {
        _pick = null;
        PickerPopup.IsOpen = false;
    }

    private void OnPickerColorChanged(object? sender, ColorValue color)
    {
        if (_pick is { } pick && DataContext is ThemeSettingsViewModel viewModel)
        {
            viewModel.SetToken(pick.Tab, pick.Token, color);
            _pick = pick with { Changed = true };
        }
    }

    private void OnPickerCancel(object? sender, EventArgs e)
    {
        if (_pick is { Changed: true } pick && DataContext is ThemeSettingsViewModel viewModel)
        {
            if (pick.WasSet)
            {
                viewModel.SetToken(pick.Tab, pick.Token, pick.Was);
            }
            else
            {
                viewModel.ResetToken(pick.Tab, pick.Token);
            }
        }

        ClosePicker();
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TokenRowViewModel row } swatch && DataContext is ThemeSettingsViewModel viewModel)
        {
            PickRequested?.Invoke(this, new TokenPickEventArgs(row.Name, viewModel.SelectedTab));
            if (row is { IsEditable: true, Value: ColorValue was })
            {
                OpenPicker(swatch, row, viewModel.SelectedTab, was);
            }
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

    /// <summary>The token the open picker edits, its tab, its colour and whether the tab set it when the picker opened.</summary>
    private sealed record Pick(string Token, ThemeTab Tab, ColorValue Was, bool WasSet)
    {
        /// <summary>Whether the picker has changed the token since it opened.</summary>
        public bool Changed { get; init; }
    }
}
