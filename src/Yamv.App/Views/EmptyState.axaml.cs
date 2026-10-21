using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yamv.App.Views;

/// <summary>
/// What a window with no documents shows: a dashed <c>md</c> mark, "No documents open", a drop-or-open hint, the
/// <b>Open files…</b> button and its shortcut as two keys, <c>Ctrl</c> <c>O</c> (<c>⌘</c> <c>O</c> on macOS).
/// </summary>
public partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            ModifierKey.Text = "⌘";
        }
    }

    /// <summary>Raised when <b>Open files…</b> is clicked; the window shows the picker.</summary>
    public event EventHandler? OpenFilesRequested;

    private void OnOpenFilesClick(object? sender, RoutedEventArgs e) => OpenFilesRequested?.Invoke(this, EventArgs.Empty);
}
