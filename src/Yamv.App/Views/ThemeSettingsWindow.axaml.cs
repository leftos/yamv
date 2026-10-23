using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yamv.App.ViewModels;
using Yamv.App.Views.ThemeSettings;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;

namespace Yamv.App.Views;

/// <summary>
/// The theme settings window: the theme list with its Rename and Delete context menu and the Appearance choice, the selected
/// theme's header with the <c>EditorHost</c> for the token editor or the pane of a theme that did not load, the
/// <c>PreviewHost</c> on the right, and the footer, under the bar that says the theme changed on disk. Its chrome takes the app's
/// theme resources, so it keeps the saved theme until Save. It asks its confirms inside itself (<see cref="ConfirmUnsavedAsync"/>,
/// <see cref="ConfirmDeleteAsync"/>), shows the Import notice inside itself (<see cref="ConfirmImportAsync"/>), cancels either
/// when it closes, and holds a close while the theme has unsaved edits until the unsaved confirm is answered
/// (<see cref="ConfirmCloseAsync"/>).
/// </summary>
public partial class ThemeSettingsWindow : Window
{
    private readonly ThemeSettingsViewModel? _viewModel;
    private readonly IAppLog? _log;
    private bool _closeAllowed;
    private bool _closeOwnerAfterCheck;
    private Task<bool>? _closeCheck;
    private int _openModals;

    /// <summary>Creates an empty window; the XAML loader needs it. A real window is made with the other constructor.</summary>
    public ThemeSettingsWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            TitleText.Margin = new Thickness(Yamv.App.Views.TitleBar.MacTrafficLightInset, 0, 0, 0);
        }
    }

    /// <summary>Creates the window of <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The theme list, the selected theme and its working copy.</param>
    /// <param name="log">Where a failure of work the window starts and does not wait for is reported.</param>
    public ThemeSettingsWindow(ThemeSettingsViewModel viewModel, IAppLog log)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(log);
        _viewModel = viewModel;
        _log = log;
        DataContext = viewModel;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => UpdateShownAppearance();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateShownAppearance();
    }

    /// <summary>Asks Save, Discard or Cancel for the unsaved changes of the theme named <paramref name="name"/>.</summary>
    public async Task<UnsavedChoice> ConfirmUnsavedAsync(string name)
    {
        ConfirmResult result = await ShowConfirmAsync("Unsaved changes", $"“{name}” has changes that aren't saved.", "Save", "Discard");
        return result switch
        {
            ConfirmResult.Primary => UnsavedChoice.Save,
            ConfirmResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    /// <summary>Asks whether to move the theme named <paramref name="name"/> to the Recycle Bin (the Trash on macOS).</summary>
    public async Task<bool> ConfirmDeleteAsync(string name)
    {
        string bin = OperatingSystem.IsMacOS() ? "Trash" : "Recycle Bin";
        ConfirmResult result = await ShowConfirmAsync("Delete theme", $"Move “{name}” to the {bin}?", "Delete", null);
        return result == ConfirmResult.Primary;
    }

    /// <summary>
    /// Whatever the close's reason, cancels an open Import notice or confirm (its action is dropped); then, while the theme has
    /// unsaved edits, holds the close and asks the unsaved confirm, closing again after Save or Discard: the owner, when the close
    /// came from the owner's, so the owner's close goes ahead. A close while that confirm is open is held too.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closeAllowed)
        {
            _closeAllowed = false;
            return;
        }

        if (_closeCheck is not null)
        {
            e.Cancel = true;
            return;
        }

        CancelDialogs();
        if (_viewModel is not { IsDirty: true, WorkingTheme: not null })
        {
            return;
        }

        e.Cancel = true;
        _closeOwnerAfterCheck = e.CloseReason == WindowCloseReason.OwnerWindowClosing;
        RunInBackground(CloseAfterCheckAsync, "the close confirm");
    }

    /// <summary>
    /// The check the owner window's close and the app's shutdown wait on: cancels an open Import notice or confirm, then, while
    /// the theme has unsaved edits, asks Save, Discard or Cancel. True when the owner may go on closing, after which this window
    /// closes without asking again; false on Cancel or a failed save. A call while the check is running returns the same check.
    /// </summary>
    internal Task<bool> ConfirmCloseAsync()
    {
        if (_closeCheck is { } running)
        {
            return running;
        }

        Task<bool> check = CheckCloseAsync();
        if (!check.IsCompleted)
        {
            _closeCheck = check;
        }

        return check;
    }

    /// <summary>
    /// Closes the window without the unsaved confirm, after completing an open Import notice or confirm as Cancel: the app's
    /// release calls it, once the shutdown check has run or when the process exits anyway, so unsaved edits never hold a release.
    /// </summary>
    internal void ForceClose()
    {
        CancelDialogs();
        _closeAllowed = true;
        Close();
    }

    /// <summary>Completes a confirm or an Import notice still open when the window closes as Cancel, so what awaits it carries on.</summary>
    protected override void OnClosed(EventArgs e)
    {
        CancelDialogs();
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnClosed(e);
    }

    /// <summary>A new unsaved edit takes back a close the close check allowed, so the next close asks again.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeSettingsViewModel.IsDirty) && _viewModel is { IsDirty: true })
        {
            _closeAllowed = false;
        }
    }

    /// <summary>Runs <paramref name="work"/> without waiting for it, and logs it as an Error if it fails.</summary>
    private async void RunInBackground(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception exception)
        {
            _log!.LogError($"Theme settings: {what} failed.", exception);
        }
    }

    /// <summary>Shows the Import notice for <paramref name="inspection"/>; true when the user chose Import.</summary>
    /// <param name="inspection">The inspected file the notice describes.</param>
    /// <exception cref="InvalidOperationException">The notice is already open.</exception>
    public async Task<bool> ConfirmImportAsync(ThemeInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (ImportNotice.IsOpen)
        {
            throw new InvalidOperationException("An import notice is already open.");
        }

        return await ShowModalAsync(() => ImportNotice.ShowAsync(inspection));
    }

    /// <summary>Shows the confirm with everything behind its scrim disabled, so Tab cannot reach the list or the footer.</summary>
    private Task<ConfirmResult> ShowConfirmAsync(string title, string body, string primary, string? secondary) =>
        ShowModalAsync(() => Confirm.ShowAsync(title, body, primary, secondary));

    /// <summary>Runs <paramref name="show"/> with everything behind its scrim disabled, so Tab cannot reach the list or the footer.</summary>
    /// <remarks>
    /// Counts the open dialogs, so a dialog cancelled and replaced by another (the close confirm after a cancelled notice) does
    /// not enable the window under the new one when its own wait ends.
    /// </remarks>
    private async Task<T> ShowModalAsync<T>(Func<Task<T>> show)
    {
        _openModals++;
        WindowContent.IsEnabled = false;
        try
        {
            return await show();
        }
        finally
        {
            _openModals--;
            WindowContent.IsEnabled = _openModals == 0;
        }
    }

    /// <summary>Completes an open confirm and an open Import notice as Cancel.</summary>
    private void CancelDialogs()
    {
        Confirm.Cancel();
        ImportNotice.Cancel();
    }

    private async Task<bool> CheckCloseAsync()
    {
        try
        {
            CancelDialogs();
            if (_viewModel is { IsDirty: true, WorkingTheme: { } working })
            {
                UnsavedChoice choice = await ConfirmUnsavedAsync(working.Name);
                if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !_viewModel.TrySave()))
                {
                    return false;
                }

                if (choice == UnsavedChoice.Discard)
                {
                    _viewModel.RevertCommand.Execute(null);
                }
            }

            _closeAllowed = true;
            return true;
        }
        finally
        {
            _closeCheck = null;
        }
    }

    /// <summary>
    /// Closes again once the close check passes: the owner when its close was the one held, so it goes ahead and closes this
    /// window with it, else this window.
    /// </summary>
    private async Task CloseAfterCheckAsync()
    {
        bool closeOwner = _closeOwnerAfterCheck;
        _closeOwnerAfterCheck = false;
        if (!await ConfirmCloseAsync())
        {
            return;
        }

        if (closeOwner && Owner is Window owner)
        {
            owner.Close();
        }
        else
        {
            Close();
        }
    }

    private void UpdateShownAppearance() =>
        _viewModel!.ShownAppearance = ActualThemeVariant == AvaloniaThemeVariant.Dark ? Appearance.Dark : Appearance.Light;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (Confirm.IsOpen || ImportNotice.IsOpen))
        {
            CancelDialogs();
            e.Handled = true;
        }
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is Control { DataContext: ThemeRowViewModel row } control
            && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
            && !row.IsRenaming
        )
        {
            RunInBackground(() => _viewModel!.SelectAsync(row), "selecting a theme");
        }
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ThemeRowViewModel row } || _viewModel is null)
        {
            return;
        }

        _viewModel.BeginRename(row);
        Dispatcher.UIThread.Post(() => FocusRenameBox(row), DispatcherPriority.Loaded);
    }

    private void FocusRenameBox(ThemeRowViewModel row)
    {
        TextBox? box = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.Classes.Contains("rename") && box.DataContext == row);
        if (box is not null)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ThemeRowViewModel row } && _viewModel is not null)
        {
            RunInBackground(() => _viewModel.DeleteAsync(row), "deleting a theme");
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ThemeRowViewModel row } box || _viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _viewModel.CommitRename(row, box.Text ?? "");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ThemeSettingsViewModel.CancelRename(row);
            e.Handled = true;
        }
    }

    /// <summary>Losing focus commits a changed name and cancels an unchanged or blank one.</summary>
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ThemeRowViewModel { IsRenaming: true } row } box || _viewModel is null)
        {
            return;
        }

        string text = box.Text ?? "";
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == row.Name)
        {
            ThemeSettingsViewModel.CancelRename(row);
        }
        else
        {
            _viewModel.CommitRename(row, text);
        }
    }
}
