using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yamv.App.Controls;
using Yamv.App.ViewModels;
using Yamv.Core.Diagnostics;
using Yamv.Core.Input;
using Yamv.Core.Session;

namespace Yamv.App.Views;

/// <summary>
/// A document window: the custom title bar, the document column (the document view, the empty state or the missing pane)
/// and the tree sidebar on the right behind a splitter. The sidebar and splitter show only when the view model shows its tree
/// and the user has not hidden the sidebar; hidden, their columns take no width. The window runs the keyboard shortcuts, both
/// its own key presses and those the page forwards, and keeps its normal (restored) rectangle for the session.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The sidebar width a window starts with, in device-independent pixels.</summary>
    public const double DefaultSidebarWidth = 280;

    private const double MinSidebarWidth = 200;
    private const double MaxSidebarWidth = 480;
    private const double MinDocumentWidth = 320;

    private readonly DocumentWindowViewModel? _viewModel;
    private readonly IDocumentSurface? _surface;
    private readonly IAppLog? _log;
    private double _sidebarWidth = DefaultSidebarWidth;
    private bool _sidebarShown = true;
    private bool _opened;
    private bool _boundsUpdatePosted;
    private WindowBounds? _normalBounds;
    private double _reportedSidebarWidth;

    /// <summary>Creates an empty window; the XAML loader needs it. A real window is made with the other constructor.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Body.ColumnDefinitions[0].MinWidth = MinDocumentWidth;
        ApplySidebarColumn();
    }

    /// <summary>Creates the window of <paramref name="viewModel"/>, drawing its document with <paramref name="documentView"/>.</summary>
    /// <param name="viewModel">The window's documents, tree and selection.</param>
    /// <param name="documentView">The control that draws the selected document (the view model's surface).</param>
    /// <param name="surface">The view model's surface, whose forwarded keys run the window's shortcuts.</param>
    /// <param name="log">Where the window reports that it has shown its first content.</param>
    public MainWindow(DocumentWindowViewModel viewModel, Control documentView, IDocumentSurface surface, IAppLog log)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(documentView);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(log);
        _viewModel = viewModel;
        _surface = surface;
        _log = log;
        DocumentHost.Content = documentView;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.CloseRequested += OnCloseRequested;
        surface.KeyForwarded += OnKeyForwarded;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        UpdateSidebar();
        Sidebar.PropertyChanged += OnSidebarPropertyChanged;
        PositionChanged += OnPositionChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>Raised when <see cref="SavedBounds"/> may have changed: the window moved, was resized, maximized or restored.</summary>
    public event EventHandler? SavedBoundsChanged;

    /// <summary>Raised when the shown sidebar's width changed, by a splitter drag or any other route.</summary>
    public event EventHandler? SidebarWidthChanged;

    /// <summary>
    /// The sidebar's width in device-independent pixels, 200 to 480; while the sidebar is hidden, the width it gets back
    /// when shown again.
    /// </summary>
    public double SidebarWidth
    {
        get => _sidebarShown ? Body.ColumnDefinitions[2].Width.Value : _sidebarWidth;
        set
        {
            _sidebarWidth = Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth);
            ApplySidebarColumn();
        }
    }

    /// <summary>
    /// The window's normal (restored) rectangle in device-independent pixels, kept while it is maximized or minimized, with
    /// whether it is maximized now; null until the window has been shown. The size is <see cref="TopLevel.ClientSize"/>, which
    /// is what <c>Width</c> and <c>Height</c> set when the rectangle is restored.
    /// </summary>
    public WindowBounds? SavedBounds => _normalBounds is { } bounds ? bounds with { IsMaximized = WindowState == WindowState.Maximized } : null;

    /// <summary>
    /// Places the window, before it is shown, at <paramref name="bounds"/> and maximizes it when the bounds say so; the
    /// rectangle becomes the normal one <see cref="SavedBounds"/> reports even while the window starts maximized.
    /// </summary>
    /// <param name="bounds">
    /// The rectangle already fitted to the screens, in the device-independent pixels of the screen it lies on, whose
    /// <see cref="WindowBounds.Scaling"/> turns its position into screen pixels.
    /// </param>
    public void RestoreBounds(WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)Math.Round(bounds.X * bounds.Scaling), (int)Math.Round(bounds.Y * bounds.Scaling));
        Width = bounds.Width;
        Height = bounds.Height;
        _normalBounds = bounds with { IsMaximized = false };
        if (bounds.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            Root.Margin = WindowState == WindowState.Maximized && OperatingSystem.IsWindows() ? OffScreenMargin : default;
        }

        if (change.Property == WindowStateProperty || change.Property == ClientSizeProperty)
        {
            PostBoundsUpdate();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentWindowViewModel.ShowTree) or nameof(DocumentWindowViewModel.SidebarHidden))
        {
            UpdateSidebar();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>
    /// Up and Down step from document to document in tree order, past group and folder headers, and keep keyboard focus on the
    /// selected document's row.
    /// </summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            _viewModel!.SelectNext();
        }
        else
        {
            _viewModel!.SelectPrevious();
        }

        e.Handled = true;
        FocusSelectedRow();
    }

    /// <summary>Runs a window shortcut before any control sees the key, so focus navigation never takes Ctrl+Tab.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (KeyName(e.Key) is not string key)
        {
            return;
        }

        KeyModifiers modifiers = e.KeyModifiers;
        if (
            RunShortcut(
                key,
                modifiers.HasFlag(KeyModifiers.Control),
                modifiers.HasFlag(KeyModifiers.Meta),
                modifiers.HasFlag(KeyModifiers.Shift),
                modifiers.HasFlag(KeyModifiers.Alt)
            )
        )
        {
            e.Handled = true;
        }
    }

    private void OnKeyForwarded(object? sender, KeyForwardedEventArgs e) => RunShortcut(e.Key, e.Ctrl, e.Meta, e.Shift, e.Alt);

    /// <summary>The key's name as the page's <c>KeyboardEvent.key</c> gives it, for the keys a shortcut uses; else null.</summary>
    private static string? KeyName(Key key) =>
        key switch
        {
            >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
            Key.Tab => "Tab",
            _ => null,
        };

    /// <summary>Runs the view model command of the shortcut these keys make, if any; returns whether one ran.</summary>
    private bool RunShortcut(string key, bool ctrl, bool meta, bool shift, bool alt)
    {
        ShortcutCommand? command = Shortcuts.Match(key, ctrl, meta, shift, alt, OperatingSystem.IsMacOS());
        if (command is null)
        {
            return false;
        }

        bool treeHadFocus = Tree.IsKeyboardFocusWithin;
        RunCommand(command.Value);
        if (treeHadFocus && command is not ShortcutCommand.Reload)
        {
            FocusSelectedRow();
        }

        return true;
    }

    private void RunCommand(ShortcutCommand command)
    {
        switch (command)
        {
            case ShortcutCommand.CloseDocument:
                _viewModel!.CloseSelected();
                break;
            case ShortcutCommand.NextDocument:
                _viewModel!.SelectNext();
                break;
            case ShortcutCommand.PreviousDocument:
                _viewModel!.SelectPrevious();
                break;
            case ShortcutCommand.ToggleSidebar:
                _viewModel!.ToggleSidebar();
                break;
            case ShortcutCommand.Reload:
                _viewModel!.Reload();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown shortcut command.");
        }
    }

    /// <summary>
    /// Puts keyboard focus on the selected document's row; when the tree was just rebuilt and the row has no container yet, tries
    /// once more after the next layout pass.
    /// </summary>
    private void FocusSelectedRow()
    {
        if (!TryFocusSelectedRow())
        {
            Dispatcher.UIThread.Post(() => TryFocusSelectedRow(), DispatcherPriority.Background);
        }
    }

    private bool TryFocusSelectedRow()
    {
        if (_viewModel!.SelectedItem is not { } row)
        {
            return true;
        }

        if (Tree.TreeContainerFromItem(row) is not TreeViewItem container)
        {
            return false;
        }

        container.Focus(NavigationMethod.Directional);
        return true;
    }

    private void UpdateSidebar()
    {
        bool show = _viewModel is { ShowTree: true, SidebarHidden: false };
        if (show == _sidebarShown)
        {
            return;
        }

        if (!show)
        {
            _sidebarWidth = Body.ColumnDefinitions[2].Width.Value;
        }

        _sidebarShown = show;
        ApplySidebarColumn();
    }

    private void ApplySidebarColumn()
    {
        ColumnDefinition column = Body.ColumnDefinitions[2];
        SidebarSplitter.IsVisible = _sidebarShown;
        Sidebar.IsVisible = _sidebarShown;
        column.MinWidth = _sidebarShown ? MinSidebarWidth : 0;
        column.MaxWidth = _sidebarShown ? MaxSidebarWidth : double.PositiveInfinity;
        column.Width = new GridLength(_sidebarShown ? _sidebarWidth : 0, GridUnitType.Pixel);
    }

    /// <summary>Reads the final rectangle before the window's <c>Closing</c> handlers save it, when a read is still posted.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_boundsUpdatePosted)
        {
            UpdateBounds();
        }

        base.OnClosing(e);
    }

    private void OnSidebarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty || !_sidebarShown)
        {
            return;
        }

        double width = Sidebar.Bounds.Width;
        if (width > 0 && width != _reportedSidebarWidth)
        {
            _reportedSidebarWidth = width;
            SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => PostBoundsUpdate();

    /// <summary>
    /// Reads the bounds once the platform has finished the move, resize or state change in progress: maximizing moves and
    /// resizes the window before its state reads maximized, and that rectangle must not replace the normal one.
    /// </summary>
    private void PostBoundsUpdate()
    {
        if (!_opened || _boundsUpdatePosted)
        {
            return;
        }

        _boundsUpdatePosted = true;
        Dispatcher.UIThread.Post(UpdateBounds, DispatcherPriority.Background);
    }

    private void UpdateBounds()
    {
        _boundsUpdatePosted = false;
        if (!_opened)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            double scaling = Screens.ScreenFromWindow(this)?.Scaling ?? DesktopScaling;
            _normalBounds = new WindowBounds(
                Position.X / scaling,
                Position.Y / scaling,
                ClientSize.Width,
                ClientSize.Height,
                IsMaximized: false,
                scaling
            );
        }

        SavedBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _opened = true;
        UpdateBounds();
        _ = LogShownAsync();
    }

    /// <summary>Writes <c>Window: shown</c> once the first document has been shown, or at once for the empty state or missing pane.</summary>
    private async Task LogShownAsync()
    {
        try
        {
            await _viewModel!.WhenRendersSettled();
            _log!.LogInfo("Window: shown");
        }
        // The log line is a dev aid; a failure is logged rather than lost in a discarded task.
        catch (Exception exception)
        {
            _log!.LogError("Window: could not report that it was shown.", exception);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _opened = false;
        _viewModel!.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CloseRequested -= OnCloseRequested;
        _surface!.KeyForwarded -= OnKeyForwarded;
        PositionChanged -= OnPositionChanged;
        Sidebar.PropertyChanged -= OnSidebarPropertyChanged;
    }
}
