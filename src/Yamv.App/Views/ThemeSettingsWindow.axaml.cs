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
/// <c>PreviewHost</c> on the right, and the footer. Its chrome takes the app's theme resources, so it keeps the saved theme
/// until Save. It asks its confirms inside itself (<see cref="ConfirmUnsavedAsync"/>, <see cref="ConfirmDeleteAsync"/>), and
/// holds a close while the theme has unsaved edits until the unsaved confirm is answered.
/// </summary>
public partial class ThemeSettingsWindow : Window
{
    private readonly ThemeSettingsViewModel? _viewModel;
    private readonly IAppLog? _log;
    private bool _closeAllowed;

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

    /// <summary>Holds a close while the theme has unsaved edits and asks the unsaved confirm; closes after Save or Discard.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closeAllowed || _viewModel is not { IsDirty: true, WorkingTheme: { } working })
        {
            return;
        }

        e.Cancel = true;
        if (!Confirm.IsOpen)
        {
            RunInBackground(() => ConfirmCloseAsync(working.Name), "the close confirm");
        }
    }

    /// <summary>Completes a confirm still open when the window closes as Cancel, so what awaits it carries on.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Confirm.Cancel();
        base.OnClosed(e);
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

    /// <summary>Shows the confirm with everything behind its scrim disabled, so Tab cannot reach the list or the footer.</summary>
    private async Task<ConfirmResult> ShowConfirmAsync(string title, string body, string primary, string? secondary)
    {
        WindowContent.IsEnabled = false;
        try
        {
            return await Confirm.ShowAsync(title, body, primary, secondary);
        }
        finally
        {
            WindowContent.IsEnabled = true;
        }
    }

    private async Task ConfirmCloseAsync(string name)
    {
        UnsavedChoice choice = await ConfirmUnsavedAsync(name);
        if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !_viewModel!.TrySave()))
        {
            return;
        }

        _closeAllowed = true;
        Close();
    }

    private void UpdateShownAppearance() =>
        _viewModel!.ShownAppearance = ActualThemeVariant == AvaloniaThemeVariant.Dark ? Appearance.Dark : Appearance.Light;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Confirm.IsOpen)
        {
            Confirm.Cancel();
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
