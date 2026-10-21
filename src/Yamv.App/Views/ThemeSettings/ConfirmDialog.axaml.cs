using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>Which button closed a <see cref="ConfirmDialog"/>.</summary>
public enum ConfirmResult
{
    /// <summary>Cancel, the dialog's close button or Esc.</summary>
    Cancel,

    /// <summary>The middle button, such as Discard.</summary>
    Secondary,

    /// <summary>The primary button, the default one.</summary>
    Primary,
}

/// <summary>
/// A modal confirm drawn inside its window over a scrim, in the window's theme: a title, one paragraph, and Cancel, an optional
/// middle button and the primary button, which is the default. One confirm is open at a time.
/// </summary>
public partial class ConfirmDialog : UserControl
{
    private TaskCompletionSource<ConfirmResult>? _pending;

    public ConfirmDialog() => InitializeComponent();

    /// <summary>Whether the dialog is open, waiting for an answer.</summary>
    public bool IsOpen => _pending is not null;

    /// <summary>Opens the dialog and completes with the button that closes it.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="body">The paragraph under it.</param>
    /// <param name="primary">The primary button's label.</param>
    /// <param name="secondary">The middle button's label, or null for none.</param>
    /// <exception cref="InvalidOperationException">The dialog is already open.</exception>
    public Task<ConfirmResult> ShowAsync(string title, string body, string primary, string? secondary)
    {
        if (_pending is not null)
        {
            throw new InvalidOperationException("A confirm is already open.");
        }

        DialogTitle.Text = title;
        DialogBody.Text = body;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        SecondaryButton.IsVisible = secondary is not null;
        _pending = new TaskCompletionSource<ConfirmResult>();
        IsVisible = true;
        PrimaryButton.Focus();
        return _pending.Task;
    }

    /// <summary>Closes the open dialog as Cancel; does nothing when none is open.</summary>
    public void Cancel() => Complete(ConfirmResult.Cancel);

    private void Complete(ConfirmResult result)
    {
        TaskCompletionSource<ConfirmResult>? pending = _pending;
        if (pending is null)
        {
            return;
        }

        _pending = null;
        IsVisible = false;
        pending.SetResult(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Complete(ConfirmResult.Cancel);

    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Complete(ConfirmResult.Secondary);

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Complete(ConfirmResult.Primary);
}
