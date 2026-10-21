using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yamv.App.ViewModels;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Views;

/// <summary>
/// A document window: the custom title bar, the document column (the document view, the empty state or the missing pane)
/// and the tree sidebar on the right behind a splitter. The sidebar and splitter show only when the view model shows its tree
/// and the user has not hidden the sidebar; hidden, their columns take no width.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The sidebar width a window starts with, in device-independent pixels.</summary>
    public const double DefaultSidebarWidth = 280;

    private const double MinSidebarWidth = 200;
    private const double MaxSidebarWidth = 480;
    private const double MinDocumentWidth = 320;

    private readonly DocumentWindowViewModel? _viewModel;
    private readonly IAppLog? _log;
    private double _sidebarWidth = DefaultSidebarWidth;
    private bool _sidebarShown = true;

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
    /// <param name="log">Where the window reports that it has shown its first content.</param>
    public MainWindow(DocumentWindowViewModel viewModel, Control documentView, IAppLog log)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(documentView);
        ArgumentNullException.ThrowIfNull(log);
        _viewModel = viewModel;
        _log = log;
        DocumentHost.Content = documentView;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        UpdateSidebar();
        Opened += OnOpened;
        Closed += OnClosed;
    }

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            Root.Margin = WindowState == WindowState.Maximized && OperatingSystem.IsWindows() ? OffScreenMargin : default;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentWindowViewModel.ShowTree) or nameof(DocumentWindowViewModel.SidebarHidden))
        {
            UpdateSidebar();
        }
    }

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
        if (_viewModel.SelectedItem is { } row && Tree.TreeContainerFromItem(row) is TreeViewItem container)
        {
            container.Focus(NavigationMethod.Directional);
        }
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

    private void OnOpened(object? sender, EventArgs e) => _ = LogShownAsync();

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

    private void OnClosed(object? sender, EventArgs e) => _viewModel!.PropertyChanged -= OnViewModelPropertyChanged;
}
