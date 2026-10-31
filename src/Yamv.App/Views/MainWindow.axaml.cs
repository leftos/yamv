using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yamv.App.Controls;
using Yamv.App.Platform;
using Yamv.App.Services;
using Yamv.App.ViewModels;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Input;
using Yamv.Core.Links;
using Yamv.Core.Platform;
using Yamv.Core.Session;

namespace Yamv.App.Views;

/// <summary>
/// A document window: the custom title bar, the document column (the document view, the empty state or the missing pane)
/// and the tree sidebar on the right behind a splitter. The sidebar and splitter show only when the view model shows its tree
/// and the user has not hidden the sidebar; hidden, their columns take no width. The window runs the keyboard shortcuts, both
/// its own key presses and those the page forwards, the tree rows' context menus, Open files…, Locate… and files dropped on it,
/// and keeps its normal (restored) rectangle for the session.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A window is not disposed; its link opener, drop overlay and confirm card are disposed when the window closes (OnClosed)."
)]
public partial class MainWindow : Window
{
    /// <summary>The sidebar width a window starts with, in device-independent pixels.</summary>
    public const double DefaultSidebarWidth = 280;

    private const double MinSidebarWidth = 200;
    private const double MaxSidebarWidth = 480;
    private const double MinDocumentWidth = 320;
    private const string OpenFilesFailedToast = "Couldn't open the files; see the log";

    // The ⋯ menu's shadow is not a theme token: the width popover's far and near shadows, in each theme.
    private static readonly Color LightShadowColor = Color.Parse("#1F2328");

    private readonly DocumentWindowViewModel? _viewModel;
    private readonly IDocumentSurface? _surface;
    private readonly IAppLog? _log;
    private readonly LinkOpener? _linkOpener;
    private readonly DropOverlayViewModel? _dropOverlay;
    private readonly ConfirmCardViewModel? _confirmCard;
    private bool? _dragHasDocument;
    private double _sidebarWidth = DefaultSidebarWidth;
    private bool _sidebarShown = true;
    private bool _opened;
    private bool _closing;
    private bool _boundsUpdatePosted;
    private WindowBounds? _normalBounds;
    private double _reportedSidebarWidth;
    private ContentWidthViewModel? _widthPopover;
    private bool _creatingWidthPopover;

    /// <summary>
    /// A viewport report arrived during the open popover's slider drag; the popover re-spans when the drag ends. Cleared when
    /// the popover is forgotten or a column resize re-spans it.
    /// </summary>
    private bool _viewportHeldForDrag;

    /// <summary>
    /// How much narrower than the document column the shown render's page last reported its text column (a classic
    /// scrollbar), never below 0; used only while <see cref="IDocumentSurface.ViewportWidth"/> holds a report.
    /// </summary>
    private double _viewportInset;
    private readonly ToastCenter? _toasts;
    private ToastClickThrough? _toastClickThrough;

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
        FileActions = new AvaloniaFileActions(log, viewModel.Toasts, AvaloniaFileActions.StartProcess, FileActionSeams.Default);
        _confirmCard = new ConfirmCardViewModel();
        _linkOpener = new LinkOpener(
            viewModel,
            viewModel.Toasts,
            log,
            new LinkOpenerSeams(
                uri => Launcher.LaunchUriAsync(uri),
                () => FileActions!,
                LinkPolicy.Decide,
                decide => Task.Run(decide),
                viewModel.Time,
                _confirmCard.AskAsync,
                viewModel.ShowsAdministrator
            ),
            new LinkDropRoute(() => _dropOverlay!.DragSignalledRecently, OpenDropped)
        );
        _toasts = viewModel.Toasts as ToastCenter;
        ToastPopup.PlacementTarget = DocumentColumn;
        ToastPopup.Opened += OnToastPopupOpened;
        ToastPopup.Closed += OnToastPopupClosed;
        ToastHost.DataContext = _toasts;
        if (_toasts is not null)
        {
            ((INotifyCollectionChanged)_toasts.Items).CollectionChanged += OnToastsChanged;
            UpdateToastsActive();
        }

        DocumentHost.Content = documentView;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.CloseRequested += OnCloseRequested;
        surface.KeyForwarded += OnKeyForwarded;
        surface.LinkActivated += OnLinkActivatedCancelCard;
        surface.LinkActivated += _linkOpener.OnLinkActivated;
        ConfirmPopup.PlacementTarget = DocumentColumn;
        ConfirmCard.DataContext = _confirmCard;
        ConfirmCard.Answered += OnConfirmCardAnswered;
        _confirmCard.PropertyChanged += OnConfirmCardPropertyChanged;
        DocumentColumn.SizeChanged += OnDocumentColumnSizeChanged;
        _dropOverlay = new DropOverlayViewModel(viewModel.Time);
        _dropOverlay.PropertyChanged += OnDropOverlayPropertyChanged;
        surface.DragOverChanged += OnPageDragOverChanged;
        surface.ViewportWidthChanged += OnViewportWidthChanged;
        DropPopup.PlacementTarget = Body;
        Body.SizeChanged += OnBodySizeChanged;
        AddDragHandlers(this);
        AddDragHandlers(DropOverlay);
        EmptyState.OpenFilesRequested += OnOpenFilesRequested;
        MissingPane.LocateRequested += OnLocateRequested;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        WindowTitleBar.OpenButton.Click += OnOpenClick;
        WindowTitleBar.ContentWidthButton.Click += OnContentWidthClick;
        WidthPopup.PlacementTarget = WindowTitleBar.ContentWidthButton;
        // A press on ⟷ reaches the button instead of light-dismissing the popup, so its click toggles the popover closed.
        WidthPopup.OverlayInputPassThroughElement = WindowTitleBar.ContentWidthButton;
        WidthPopup.Closed += OnWidthPopupClosed;
        WidthPopover.DataContext = null;
        WidthPopover.AddHandler(KeyDownEvent, OnWidthPopoverKeyDown, RoutingStrategies.Tunnel);
        WindowTitleBar.MoreButton.Click += OnMoreClick;
        MorePopup.PlacementTarget = WindowTitleBar.MoreButton;
        // A press on ⋯ reaches the button instead of light-dismissing the popup, so its click toggles the menu closed.
        MorePopup.OverlayInputPassThroughElement = WindowTitleBar.MoreButton;
        MorePopup.Closed += OnMorePopupClosed;
        ThemeSettingsItem.Click += OnThemeSettingsItemClick;
        MoreMenu.AddHandler(KeyDownEvent, OnMoreMenuKeyDown, RoutingStrategies.Tunnel);
        MoreMenu.ActualThemeVariantChanged += OnMoreMenuThemeChanged;
        UpdateMoreMenuShadow();
        UpdateSidebar();
        Sidebar.PropertyChanged += OnSidebarPropertyChanged;
        PositionChanged += OnPositionChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>The tree menus' reveal item: Reveal in Finder on macOS, Reveal in Explorer elsewhere.</summary>
    public static string RevealMenuHeader { get; } = OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in Explorer";

    /// <summary>The ⋯ menu's key hint for Theme settings…: ⌘, on macOS, Ctrl+, elsewhere.</summary>
    public static string ThemeSettingsKeyHint { get; } = OperatingSystem.IsMacOS() ? "⌘," : "Ctrl+,";

    /// <summary>The title bar's Open… tooltip: with ⌘O on macOS, Ctrl+O elsewhere.</summary>
    public static string OpenToolTip { get; } = OperatingSystem.IsMacOS() ? "Open… (⌘O)" : "Open… (Ctrl+O)";

    /// <summary>The pickers, reveal and clipboard the window's menus, buttons and Ctrl/Cmd+O use; tests replace it.</summary>
    internal IFileActions? FileActions { get; set; }

    /// <summary>
    /// What the window does with its content-width popover once it has shown its first document: nothing, or open it (and then
    /// ask Clear all, or select the document's own folder), writing <c>Window: width popover shown</c>. The hidden-desktop
    /// capture sets it to check the popover against its mock.
    /// </summary>
    internal DevWidthPopover WidthPopoverWhenShown { get; set; }

    /// <summary>
    /// Whether the window shows three sample toasts once it has shown its first document, writing <c>Window: toasts shown</c>.
    /// The hidden-desktop capture sets it to check the toasts against their mock.
    /// </summary>
    internal bool ToastsWhenShown { get; set; }

    /// <summary>
    /// Which form of the drop overlay the window shows once it has shown its first document, writing
    /// <c>Window: drop overlay shown</c>. The hidden-desktop capture sets it to check the overlay against its mock.
    /// </summary>
    internal DevDropOverlay DropOverlayWhenShown { get; set; }

    /// <summary>
    /// Whether the window opens the confirm card for <c>setup.ps1</c> once it has shown its first document, writing
    /// <c>Window: confirm card shown</c>. The hidden-desktop capture sets it to check the card against its mock.
    /// </summary>
    internal bool ConfirmCardWhenShown { get; set; }

    /// <summary>The window's confirm card, which tests ask through.</summary>
    internal ConfirmCardViewModel? ConfirmCardQuestion => _confirmCard;

    /// <summary>
    /// What the window does with its ⋯ menu once it has shown its first document: nothing, open the menu (writing
    /// <c>Window: more menu shown</c>), or ask for the theme settings window as the menu's item does (writing
    /// <c>Window: theme settings shown</c> once that window is open). The hidden-desktop capture sets it.
    /// </summary>
    internal DevThemeSettingsEntry ThemeSettingsEntryWhenShown { get; set; }

    /// <summary>Raised when <see cref="SavedBounds"/> may have changed: the window moved, was resized, maximized or restored.</summary>
    public event EventHandler? SavedBoundsChanged;

    /// <summary>Raised when the shown sidebar's width changed, by a splitter drag or any other route.</summary>
    public event EventHandler? SidebarWidthChanged;

    /// <summary>Raised, with this window as the sender, when the user asks for the theme settings window.</summary>
    public event EventHandler? ThemeSettingsRequested;

    /// <summary>Asks for the theme settings window, as the ⋯ menu's Theme settings… item and Ctrl/Cmd+, do.</summary>
    internal void RequestThemeSettings() => ThemeSettingsRequested?.Invoke(this, EventArgs.Empty);

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

        if (change.Property == IsActiveProperty || change.Property == WindowStateProperty)
        {
            UpdateToastsActive();
        }

        if (change.Property == WindowStateProperty)
        {
            UpdateToastPopup();
            if (WindowState == WindowState.Minimized)
            {
                _dropOverlay?.Ended();
                _confirmCard?.Cancel();
            }

            UpdateDropPopup();
        }
    }

    /// <summary>The toasts' fades run only while the window is active and not minimized.</summary>
    private void UpdateToastsActive() => _toasts?.IsActive = IsActive && WindowState != WindowState.Minimized;

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateToastPopup();

    /// <summary>On Windows, the toast popup's new window lets every click and wheel turn on no card through to the page.</summary>
    private void OnToastPopupOpened(object? sender, EventArgs e)
    {
        if (
            OperatingSystem.IsWindows()
            && TopLevel.GetTopLevel(ToastHost) is { } popupTop
            && popupTop.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle
        )
        {
            _toastClickThrough = ToastClickThrough.Attach(
                handle.Handle,
                screen => popupTop.TranslatePoint(popupTop.PointToClient(screen), ToastHost) is { } point && ToastHost.HasCardAt(point),
                _log!
            );
        }
    }

    private void OnToastPopupClosed(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            _toastClickThrough?.Dispose();
        }

        _toastClickThrough = null;
    }

    /// <summary>
    /// Opens the toast popup while the shown window has a toast and is not minimized, and closes it otherwise; a minimized
    /// window's toasts stay, held, until it is restored.
    /// </summary>
    private void UpdateToastPopup() => ToastPopup.IsOpen = _opened && WindowState != WindowState.Minimized && _toasts is { Items.Count: > 0 };

    private void AddDragHandlers(Interactive target)
    {
        target.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        target.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        target.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void RemoveDragHandlers(Interactive target)
    {
        target.RemoveHandler(DragDrop.DragEnterEvent, OnDragEnter);
        target.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        target.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        target.RemoveHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// The dragged files' local paths, an empty string for a folder or a file that has none, which sorts as refused. Avalonia
    /// releases the data when the drop's handler returns, so a drop reads them inside it.
    /// </summary>
    private static string[] LocalPaths(IDataTransfer data) =>
        data.TryGetFiles() is { } files
            ? [.. files.Select(item => item is IStorageFolder ? string.Empty : item.TryGetLocalPath() ?? string.Empty)]
            : [];

    /// <summary>A drag over the overlay bubbles to the window as well; marking it handled keeps the two from both acting.</summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = _dragHasDocument is null ? OnDragPaths(LocalPaths(e.DataTransfer)) : HoldVerdict();
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HoldVerdict();
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropOverlay!.Left();
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = OnDropPaths(LocalPaths(e.DataTransfer));
        e.Handled = true;
    }

    /// <summary>
    /// Judges a drag by its file names alone. The list is read once per drag, as the read stats each path on the UI thread; the
    /// verdict then serves every enter and move until the overlay hides. A drag with no paths shows nothing, unless the overlay
    /// already shows for it: those files have no local path, so it shows the grey form.
    /// </summary>
    /// <param name="paths">The dragged files' local paths.</param>
    /// <returns>The effect the drag may have here.</returns>
    internal DragDropEffects OnDragPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            _dragHasDocument = paths.Any(DocumentFileTypes.IsDocument);
        }
        else
        {
            _dragHasDocument = _dropOverlay!.State is DropOverlayState.Undecided or DropOverlayState.Refused ? false : null;
        }

        return HoldVerdict();
    }

    /// <summary>Keeps the overlay on the drag's verdict, which also stops a grace a late page signal started.</summary>
    private DragDropEffects HoldVerdict()
    {
        if (_dragHasDocument is not bool anyDocument)
        {
            return DragDropEffects.None;
        }

        _dropOverlay!.Entered(anyDocument);
        return anyDocument ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Takes a drop and hides the overlay.</summary>
    /// <param name="paths">The dropped files' local paths.</param>
    /// <returns>What the drop's source is told happened.</returns>
    internal DragDropEffects OnDropPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            _dropOverlay!.Ended();
            return DragDropEffects.None;
        }

        OpenDropped(paths);
        return DragDropEffects.Copy;
    }

    /// <summary>Opens the documents among <paramref name="paths"/>, refusing the rest, and brings the window forward.</summary>
    /// <param name="paths">The dropped files' local paths, in drop order.</param>
    internal void OpenDropped(IReadOnlyList<string> paths)
    {
        _dropOverlay!.Ended();
        RunLogged(() => OpenDroppedAsync(paths), "Open dropped files", OpenFilesFailedToast);
    }

    /// <summary>Sorts the drop off the UI thread, as it reads the disk, and acts on it back on the UI thread.</summary>
    internal async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        DroppedFiles sorted = await Task.Run(() => DocumentFileTypes.Sort(paths));
        if (!_opened)
        {
            _log!.LogInfo($"Drop: sorted after the window closed; nothing opened: {string.Join(", ", paths)}");
            return;
        }

        _log!.LogInfo($"Drop: {sorted.Documents.Count} document(s) to open, {sorted.Refused} refused: {string.Join(", ", paths)}");
        if (sorted.Documents.Count > 0)
        {
            _viewModel!.Open(sorted.Documents);
        }

        if (sorted.Refused > 0)
        {
            _viewModel!.Toasts.Show(ToastKind.Refusal, DocumentFileTypes.DropHint);
        }

        Activate();
    }

    private void OnPageDragOverChanged(object? sender, bool over)
    {
        if (over)
        {
            _dropOverlay!.PageDragOver();
        }
        else
        {
            _dropOverlay!.PageDragLeft();
        }
    }

    private void OnDropOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _log!.LogInfo($"Drop overlay: {_dropOverlay!.State}");
        if (_dropOverlay.State == DropOverlayState.Hidden)
        {
            _dragHasDocument = null;
        }

        UpdateDropPopup();
    }

    private void OnBodySizeChanged(object? sender, SizeChangedEventArgs e) => SizeDropOverlay();

    private void SizeDropOverlay()
    {
        DropOverlay.Width = Body.Bounds.Width;
        DropOverlay.Height = Body.Bounds.Height;
    }

    /// <summary>
    /// Opens the overlay while a drag is over the shown window, unless it is minimized, closing the width popover and the ⋯ menu:
    /// no pointer input reaches a light dismiss during a drag.
    /// </summary>
    private void UpdateDropPopup()
    {
        if (_dropOverlay is not { } overlay)
        {
            return;
        }

        bool open = _opened && WindowState != WindowState.Minimized && overlay.State != DropOverlayState.Hidden;
        if (open && !DropPopup.IsOpen)
        {
            CloseWidthPopover(refocusDocument: false);
            MorePopup.IsOpen = false;
            SizeDropOverlay();
        }

        DropOverlay.Show(overlay.State);
        DropPopup.IsOpen = open;
    }

    /// <summary>A newer click takes over from the card; the opener then refuses the old click's answer whatever it was.</summary>
    private void OnLinkActivatedCancelCard(object? sender, LinkActivatedEventArgs e)
    {
        if (e?.Source == LinkSource.Click)
        {
            _confirmCard!.Cancel();
        }
    }

    private void OnConfirmCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfirmCardViewModel.IsOpen))
        {
            UpdateConfirmPopup();
        }
    }

    /// <summary>The user answered the card, so keyboard focus goes back to the document it covered.</summary>
    private void OnConfirmCardAnswered(object? sender, bool open)
    {
        _confirmCard!.Answer(open);
        if (DocumentHost.Content is InputElement document)
        {
            document.Focus();
        }
    }

    private void OnDocumentColumnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SizeConfirmCard();
        // The re-span measures with the latest report's inset, so a report held for a drag is applied here; clearing the
        // flag first keeps a drag this re-span cancels from re-spanning a second time.
        _viewportHeldForDrag = false;
        _widthPopover?.UpdatePaneWidth(PaneWidth());
    }

    /// <summary>
    /// The shown render's page reported a new width for its text column: its inset from the column is kept, and an open
    /// popover re-spans to it; during a slider drag it waits for the drag to end, so a report the drag's own preview causes
    /// cannot end it unsaved.
    /// </summary>
    private void OnViewportWidthChanged(object? sender, EventArgs e)
    {
        if (_surface!.ViewportWidth is { } reported)
        {
            _viewportInset = Math.Max(DocumentColumn.Bounds.Width - reported, 0);
        }

        if (_widthPopover is not { } popover)
        {
            return;
        }

        if (popover.IsDragging)
        {
            _viewportHeldForDrag = true;
            return;
        }

        popover.UpdatePaneWidth(PaneWidth());
    }

    /// <summary>
    /// The width the page lays the text column out in: the live document column less the inset (a classic scrollbar) the
    /// shown render's page last reported, so a resize moves it at once; the column alone until that render's page has
    /// reported.
    /// </summary>
    private double PaneWidth() => _surface!.ViewportWidth is null ? DocumentColumn.Bounds.Width : DocumentColumn.Bounds.Width - _viewportInset;

    private void SizeConfirmCard()
    {
        ConfirmCard.Width = DocumentColumn.Bounds.Width;
        ConfirmCard.Height = DocumentColumn.Bounds.Height;
    }

    /// <summary>
    /// Keeps the popup on the card, with Cancel focused, closing the width popover and the ⋯ menu when it opens. A card asked for
    /// while the window is minimized or closed has nothing to show on, so it is cancelled.
    /// </summary>
    private void UpdateConfirmPopup()
    {
        ConfirmCardViewModel card = _confirmCard!;
        if (card.IsOpen && (!_opened || WindowState == WindowState.Minimized))
        {
            card.Cancel();
            return;
        }

        if (card.IsOpen && !ConfirmPopup.IsOpen)
        {
            CloseWidthPopover(refocusDocument: false);
            MorePopup.IsOpen = false;
            SizeConfirmCard();
        }

        ConfirmPopup.IsOpen = card.IsOpen;
        if (card.IsOpen)
        {
            ConfirmCard.StartShow(_viewModel!.Time);
        }
        else
        {
            ConfirmCard.EndShow();
        }

        // The page holds Escape and Enter for the card while it is up, wherever native focus sits; a closing window's page
        // is told nothing, since it is going away with the card.
        if (!_closing)
        {
            _ = _surface!.SetCardOpenAsync(card.IsOpen);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentWindowViewModel.ShowTree) or nameof(DocumentWindowViewModel.SidebarHidden))
        {
            UpdateSidebar();
        }
        else if (e.PropertyName == nameof(DocumentWindowViewModel.SelectedPath))
        {
            _confirmCard!.Cancel();
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
        // A key typed in the card tunnels through the window too. The popup keeps its own focus, so the card can hold focus while
        // the key goes to the tree; only the key's source tells them apart.
        if (_confirmCard!.IsOpen && e.Key is Key.Escape or Key.Enter && !(e.Source is Visual source && ConfirmCard.IsVisualAncestorOf(source)))
        {
            _confirmCard.Cancel();
            e.Handled = true;
            return;
        }

        if (KeyName(e) is not string key)
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

    /// <summary>A key the page forwarded: with a card up, Escape and Enter answer the card before any shortcut runs.</summary>
    private void OnKeyForwarded(object? sender, KeyForwardedEventArgs e)
    {
        if (_confirmCard!.IsOpen && e.Key is "Escape" or "Enter")
        {
            _confirmCard.Cancel();
            return;
        }

        RunShortcut(e.Key, e.Ctrl, e.Meta, e.Shift, e.Alt);
    }

    /// <summary>
    /// The key's name as the page forwards it, for the keys a shortcut uses; else null. <c>[</c> and <c>]</c> are the physical
    /// keys in the US positions, whatever key the layout reports; letters, the comma and Tab are the key the layout reports.
    /// </summary>
    private static string? KeyName(KeyEventArgs e) =>
        e.PhysicalKey switch
        {
            PhysicalKey.BracketLeft => "[",
            PhysicalKey.BracketRight => "]",
            _ => e.Key switch
            {
                >= Key.A and <= Key.Z => ((char)('a' + (e.Key - Key.A))).ToString(),
                Key.Tab => "Tab",
                Key.OemComma => ",",
                _ => null,
            },
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
        if (treeHadFocus && command is not (ShortcutCommand.Reload or ShortcutCommand.OpenFiles or ShortcutCommand.ThemeSettings))
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
            case ShortcutCommand.OpenFiles:
                RunLogged(OpenFilesAsync, "Open files", OpenFilesFailedToast);
                break;
            case ShortcutCommand.NarrowerWidth:
                _viewModel!.StepContentWidth(wider: false, PaneWidth());
                break;
            case ShortcutCommand.WiderWidth:
                _viewModel!.StepContentWidth(wider: true, PaneWidth());
                break;
            case ShortcutCommand.ThemeSettings:
                RequestThemeSettings();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown shortcut command.");
        }
    }

    /// <summary>⟷ toggles the popover: it closes an open one, and a click while one is being made is ignored.</summary>
    private void OnContentWidthClick(object? sender, RoutedEventArgs e)
    {
        if (WidthPopup.IsOpen)
        {
            CloseWidthPopover(refocusDocument: true);
            return;
        }

        if (!_creatingWidthPopover)
        {
            RunLogged(OpenWidthPopoverAsync, "Open the content-width popover", "Couldn't open the width popover; see the log");
        }
    }

    /// <summary>Opens the popover of the shown document, with the slider focused; nothing when no document's width can be edited.</summary>
    private async Task OpenWidthPopoverAsync()
    {
        _creatingWidthPopover = true;
        ContentWidthViewModel? popover;
        try
        {
            popover = await _viewModel!.CreateContentWidthPopoverAsync(PaneWidth());
        }
        finally
        {
            _creatingWidthPopover = false;
        }

        if (popover is null)
        {
            return;
        }

        if (popover.IsStale || !_opened)
        {
            popover.Close();
            return;
        }

        MorePopup.IsOpen = false;
        _widthPopover = popover;
        _viewportHeldForDrag = false;
        // The column may have resized while the popover was created; the popover re-measures to the pane it opens over.
        popover.UpdatePaneWidth(PaneWidth());
        popover.PropertyChanged += OnWidthPopoverPropertyChanged;
        WidthPopover.DataContext = popover;
        WidthPopup.IsOpen = true;
        WindowTitleBar.ContentWidthButton.Classes.Set("open", true);
        WidthPopover.WidthSlider.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// The popover's document stopped being the shown one, or a newer popover replaced it: it closes. A drag that ended
    /// after a viewport report arrived re-spans the popover to it.
    /// </summary>
    private void OnWidthPopoverPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(ContentWidthViewModel.IsDragging)
            && sender is ContentWidthViewModel { IsDragging: false } ended
            && ReferenceEquals(ended, _widthPopover)
            && _viewportHeldForDrag
        )
        {
            _viewportHeldForDrag = false;
            ended.UpdatePaneWidth(PaneWidth());
            return;
        }

        if (
            e.PropertyName == nameof(ContentWidthViewModel.IsStale)
            && sender is ContentWidthViewModel { IsStale: true } popover
            && ReferenceEquals(popover, _widthPopover)
        )
        {
            CloseWidthPopover(refocusDocument: false);
        }
    }

    /// <summary>A light dismiss (a click outside) closed the popup, or one of the window's own closers did.</summary>
    private void OnWidthPopupClosed(object? sender, EventArgs e) => ForgetWidthPopover();

    /// <summary>
    /// Closes the popover; with <paramref name="refocusDocument"/> (Esc, or ⟷ again) keyboard focus goes back to the document.
    /// </summary>
    private void CloseWidthPopover(bool refocusDocument)
    {
        WidthPopup.IsOpen = false;
        ForgetWidthPopover();
        if (refocusDocument && DocumentHost.Content is InputElement document)
        {
            document.Focus();
        }
    }

    /// <summary>Closes the popover's view model, which saves nothing more, and puts ⟷ back to rest; runs once per popover.</summary>
    private void ForgetWidthPopover()
    {
        if (_widthPopover is not { } popover)
        {
            return;
        }

        _widthPopover = null;
        _viewportHeldForDrag = false;
        popover.PropertyChanged -= OnWidthPopoverPropertyChanged;
        popover.Close();
        WidthPopover.DataContext = null;
        WindowTitleBar.ContentWidthButton.Classes.Set("open", false);
    }

    /// <summary>
    /// Keys typed in the popover, which is a window of its own: Esc puts back the Clear all link when it is asking, else closes
    /// the popover; Ctrl/Cmd+[ and ] step the width as they do in the window.
    /// </summary>
    private void OnWidthPopoverKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _widthPopover is { } popover)
        {
            if (popover.IsConfirmingClearAll)
            {
                popover.CancelClearAll();
            }
            else
            {
                CloseWidthPopover(refocusDocument: true);
            }

            e.Handled = true;
            return;
        }

        KeyModifiers modifiers = e.KeyModifiers;
        ShortcutCommand? command = KeyName(e) is string key
            ? Shortcuts.Match(
                key,
                modifiers.HasFlag(KeyModifiers.Control),
                modifiers.HasFlag(KeyModifiers.Meta),
                modifiers.HasFlag(KeyModifiers.Shift),
                modifiers.HasFlag(KeyModifiers.Alt),
                OperatingSystem.IsMacOS()
            )
            : null;
        if (command is ShortcutCommand.NarrowerWidth or ShortcutCommand.WiderWidth)
        {
            RunCommand(command.Value);
            e.Handled = true;
        }
    }

    /// <summary>⋯ toggles its menu.</summary>
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (MorePopup.IsOpen)
        {
            MorePopup.IsOpen = false;
            return;
        }

        OpenMoreMenu();
    }

    /// <summary>Opens the ⋯ menu with its first item focused, closing the width popover.</summary>
    private void OpenMoreMenu()
    {
        if (WidthPopup.IsOpen)
        {
            CloseWidthPopover(refocusDocument: false);
        }

        MorePopup.IsOpen = true;
        WindowTitleBar.MoreButton.Classes.Set("open", true);
        ThemeSettingsItem.Focus(NavigationMethod.Directional);
    }

    /// <summary>A light dismiss (a click outside, or the window losing activation) closed the menu, or one of its closers did.</summary>
    private void OnMorePopupClosed(object? sender, EventArgs e) => WindowTitleBar.MoreButton.Classes.Set("open", false);

    private void OnThemeSettingsItemClick(object? sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        RequestThemeSettings();
    }

    /// <summary>
    /// Keys typed in the ⋯ menu, which is a window of its own: Esc closes it and puts focus back on ⋯; Up and Down move between
    /// its items, which keeps the one item focused. Enter and Space reach the focused item.
    /// </summary>
    private void OnMoreMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            MorePopup.IsOpen = false;
            WindowTitleBar.MoreButton.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            ThemeSettingsItem.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    private void OnMoreMenuThemeChanged(object? sender, EventArgs e) => UpdateMoreMenuShadow();

    /// <summary>Gives the ⋯ menu the width popover's far (0 10 22) and near (0 1 3) shadows for the theme.</summary>
    private void UpdateMoreMenuShadow()
    {
        bool dark = MoreMenu.ActualThemeVariant == ThemeVariant.Dark;
        Color color = dark ? Colors.Black : LightShadowColor;
        MoreMenu.BoxShadow = new BoxShadows(
            new BoxShadow
            {
                OffsetY = 10,
                Blur = 22,
                Color = WithOpacity(color, dark ? 0.70 : 0.26),
            },
            [
                new BoxShadow
                {
                    OffsetY = 1,
                    Blur = 3,
                    Color = WithOpacity(color, dark ? 0.55 : 0.16),
                },
            ]
        );
    }

    private static Color WithOpacity(Color color, double opacity) => Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

    private void OnOpenFilesRequested(object? sender, EventArgs e) => RunLogged(OpenFilesAsync, "Open files", OpenFilesFailedToast);

    private void OnOpenClick(object? sender, RoutedEventArgs e) => RunLogged(OpenFilesAsync, "Open files", OpenFilesFailedToast);

    private void OnLocateRequested(object? sender, EventArgs e) => RunLogged(LocateAsync, "Locate", "Couldn't locate the file; see the log");

    /// <summary>Opens the files the user picks, selecting the first; a cancelled pick changes nothing.</summary>
    private async Task OpenFilesAsync()
    {
        IReadOnlyList<string> paths = await FileActions!.PickDocumentsAsync(this);
        if (paths.Count > 0)
        {
            _viewModel!.Open(paths);
        }
    }

    /// <summary>Replaces the selected document with the file the user picks; a cancelled pick changes nothing.</summary>
    private async Task LocateAsync()
    {
        if (_viewModel!.SelectedPath is not string oldPath)
        {
            return;
        }

        if (await FileActions!.PickReplacementAsync(this, oldPath) is string newPath)
        {
            _viewModel.Replace(oldPath, newPath);
        }
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            FileActions!.Reveal(row.Path, row is DocumentItemViewModel ? RevealKind.File : RevealKind.Folder);
        }
    }

    private void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            RunLogged(() => FileActions!.CopyTextAsync(this, row.Path), "Copy path", "Couldn't copy the path; see the log");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is DocumentItemViewModel row)
        {
            row.CloseCommand.Execute(null);
            RefocusTree();
        }
    }

    private void OnCloseGroupClick(object? sender, RoutedEventArgs e)
    {
        IRelayCommand? closeGroup = RowOf(sender) switch
        {
            GroupItemViewModel group => group.CloseGroupCommand,
            DocumentItemViewModel document => document.CloseGroupCommand,
            _ => null,
        };
        if (closeGroup is not null)
        {
            closeGroup.Execute(null);
            RefocusTree();
        }
    }

    /// <summary>The tree row a menu item belongs to: the data of the row whose context menu holds it.</summary>
    private static TreeItemViewModel? RowOf(object? sender) => (sender as Control)?.DataContext as TreeItemViewModel;

    /// <summary>Puts focus back on the selected row after a menu changed the tree, while the tree is shown.</summary>
    private void RefocusTree()
    {
        if (_viewModel!.ShowTree)
        {
            FocusSelectedRow();
        }
    }

    /// <summary>
    /// Starts <paramref name="action"/> without waiting for it, logging a failure as an error and showing
    /// <paramref name="toast"/> rather than losing the failure.
    /// </summary>
    /// <param name="action">What the user started.</param>
    /// <param name="what">What the log line names the action.</param>
    /// <param name="toast">What the window tells the user failed, when this window still holds its toasts.</param>
    private void RunLogged(Func<Task> action, string what, string toast) => _ = RunLoggedAsync(action, what, toast);

    private async Task RunLoggedAsync(Func<Task> action, string what, string toast)
    {
        try
        {
            await action();
        }
        // A handler's task is discarded, so its failure is logged here or nowhere, and shown in a toast while the window is open.
        catch (Exception exception)
        {
            _log!.LogError($"{what}: failed.", exception);
            _viewModel?.Toasts.Show(ToastKind.Warning, toast);
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
        _closing = !e.Cancel;
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
        UpdateToastPopup();
        _ = LogShownAsync();
    }

    /// <summary>
    /// Opens the popover for a hidden-desktop capture, puts it in the state <see cref="WidthPopoverWhenShown"/> asks for, and
    /// logs the popup's transparency (the level it asked for and the one the platform gave) before the line the capture waits for.
    /// </summary>
    private async Task ShowDevWidthPopoverAsync()
    {
        await OpenWidthPopoverAsync();
        if (_widthPopover is not { } popover)
        {
            _log!.LogInfo("Window: no width popover to show");
            return;
        }

        if (WidthPopoverWhenShown == DevWidthPopover.AskClearAll)
        {
            if (popover.AskClearAllCommand.CanExecute(null))
            {
                popover.AskClearAll();
            }
            else
            {
                _log!.LogInfo("Window: nothing to clear in the width popover.");
            }
        }
        else if (WidthPopoverWhenShown == DevWidthPopover.SelectDocumentFolder)
        {
            popover.SelectSegment(popover.Segments[0]);
        }

        await _viewModel!.WhenRendersSettled();
        var popupTop = TopLevel.GetTopLevel(WidthPopover);
        _log!.LogInfo(
            $"Window: width popover top level {popupTop?.GetType().Name}, "
                + $"transparency asked {string.Join(", ", popupTop?.TransparencyLevelHint ?? [])}, "
                + $"given {popupTop?.ActualTransparencyLevel}"
        );
        _log.LogInfo("Window: width popover shown");
    }

    /// <summary>
    /// Shows the capture's three toasts, oldest first: a refusal with inline code, a failure shown three times, and a plain
    /// failure. Once they are laid out it logs whether the window is active, as the fades run only then, and on Windows how
    /// the toast window answers a hit test in its margin and on a card, before the line the capture waits for.
    /// </summary>
    private async Task ShowDevToastsAsync()
    {
        IToasts toasts = _viewModel!.Toasts;
        toasts.Show(ToastKind.Refusal, "`notes/diagram.png` doesn't exist");
        for (int repeat = 0; repeat < 3; repeat++)
        {
            toasts.Show(ToastKind.Warning, DocumentWindowViewModel.WidthNotSavedToast);
        }

        toasts.Show(ToastKind.Warning, "Couldn't copy the path");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (OperatingSystem.IsWindows())
        {
            LogDevToastHits();
        }

        _log!.LogInfo($"Window: toast popup open {ToastPopup.IsOpen}, window active {IsActive}");
        _log.LogInfo("Window: toasts shown");
    }

    /// <summary>
    /// Logs what the toast window answers, and which window Windows finds, at a point in the toast window's left margin and at
    /// the middle of the first card.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void LogDevToastHits()
    {
        Border? card = ToastHost.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("card"));
        if (
            TopLevel.GetTopLevel(ToastHost) is not { } popupTop
            || popupTop.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle
            || card is null
            || ToastHost.TranslatePoint(new Point(4, ToastHost.Bounds.Height / 2), popupTop) is not { } margin
            || card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), popupTop) is not { } onCard
        )
        {
            _log!.LogInfo("Window: no toast window to hit test");
            return;
        }

        _log!.LogInfo($"Window: toast margin {ToastClickThrough.DescribeHit(handle.Handle, popupTop.PointToScreen(margin))}");
        _log.LogInfo($"Window: toast card {ToastClickThrough.DescribeHit(handle.Handle, popupTop.PointToScreen(onCard))}");
    }

    /// <summary>
    /// Opens the ⋯ menu, or asks for the theme settings window as its item does, for a hidden-desktop capture; once laid out it
    /// writes the line the capture waits for, or for the settings window a line saying none opened.
    /// </summary>
    private async Task ShowDevThemeSettingsEntryAsync()
    {
        if (ThemeSettingsEntryWhenShown == DevThemeSettingsEntry.Menu)
        {
            OpenMoreMenu();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _log!.LogInfo("Window: more menu shown");
            return;
        }

        RequestThemeSettings();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _log!.LogInfo(OwnedWindows.Any(window => window.IsVisible) ? "Window: theme settings shown" : "Window: no theme settings window to show");
    }

    /// <summary>Opens the drop overlay in the form the capture asked for, with no drag, and writes the line the capture waits for.</summary>
    private async Task ShowDevDropOverlayAsync()
    {
        _dropOverlay!.Entered(DropOverlayWhenShown == DevDropOverlay.Open);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _log!.LogInfo("Window: drop overlay shown");
    }

    /// <summary>Opens the confirm card with the words of the approved mock's file, and writes the line the capture waits for.</summary>
    private async Task ShowDevConfirmCardAsync()
    {
        var text = ConfirmText.For("setup.ps1", "setup.ps1", isFolder: false, OperatingSystem.IsMacOS(), _viewModel!.ShowsAdministrator);
        _ = _confirmCard!.AskAsync(text.Title, text.Body);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _log!.LogInfo($"Window: confirm card transparency given {TopLevel.GetTopLevel(ConfirmCard)?.ActualTransparencyLevel}");
        _log.LogInfo("Window: confirm card shown");
    }

    /// <summary>Writes <c>Window: shown</c> once the first document has been shown, or at once for the empty state or missing pane.</summary>
    private async Task LogShownAsync()
    {
        try
        {
            await _viewModel!.WhenRendersSettled();
            _log!.LogInfo("Window: shown");
            if (WidthPopoverWhenShown != DevWidthPopover.None)
            {
                await ShowDevWidthPopoverAsync();
            }

            if (ToastsWhenShown)
            {
                await ShowDevToastsAsync();
            }

            if (ThemeSettingsEntryWhenShown != DevThemeSettingsEntry.None)
            {
                await ShowDevThemeSettingsEntryAsync();
            }

            if (DropOverlayWhenShown != DevDropOverlay.None)
            {
                await ShowDevDropOverlayAsync();
            }

            if (ConfirmCardWhenShown)
            {
                await ShowDevConfirmCardAsync();
            }
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
        CloseWidthPopover(refocusDocument: false);
        MorePopup.IsOpen = false;
        ToastPopup.IsOpen = false;
        DropPopup.IsOpen = false;
        _confirmCard!.Dispose();
        _confirmCard.PropertyChanged -= OnConfirmCardPropertyChanged;
        ConfirmCard.Answered -= OnConfirmCardAnswered;
        DocumentColumn.SizeChanged -= OnDocumentColumnSizeChanged;
        ToastPopup.Opened -= OnToastPopupOpened;
        ToastPopup.Closed -= OnToastPopupClosed;
        if (_toasts is not null)
        {
            ((INotifyCollectionChanged)_toasts.Items).CollectionChanged -= OnToastsChanged;
        }

        WindowTitleBar.OpenButton.Click -= OnOpenClick;
        WindowTitleBar.ContentWidthButton.Click -= OnContentWidthClick;
        WidthPopup.Closed -= OnWidthPopupClosed;
        WindowTitleBar.MoreButton.Click -= OnMoreClick;
        MorePopup.Closed -= OnMorePopupClosed;
        ThemeSettingsItem.Click -= OnThemeSettingsItemClick;
        MoreMenu.ActualThemeVariantChanged -= OnMoreMenuThemeChanged;
        _viewModel!.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CloseRequested -= OnCloseRequested;
        _surface!.KeyForwarded -= OnKeyForwarded;
        _surface.LinkActivated -= OnLinkActivatedCancelCard;
        _surface.LinkActivated -= _linkOpener!.OnLinkActivated;
        _surface.DragOverChanged -= OnPageDragOverChanged;
        _surface.ViewportWidthChanged -= OnViewportWidthChanged;
        _dropOverlay!.PropertyChanged -= OnDropOverlayPropertyChanged;
        _dropOverlay.Dispose();
        Body.SizeChanged -= OnBodySizeChanged;
        RemoveDragHandlers(this);
        RemoveDragHandlers(DropOverlay);
        _linkOpener.Dispose();
        EmptyState.OpenFilesRequested -= OnOpenFilesRequested;
        MissingPane.LocateRequested -= OnLocateRequested;
        PositionChanged -= OnPositionChanged;
        Sidebar.PropertyChanged -= OnSidebarPropertyChanged;
    }
}

/// <summary>What a document window does with its content-width popover once shown, for a hidden-desktop capture.</summary>
internal enum DevWidthPopover
{
    /// <summary>Nothing: the popover opens only from ⟷.</summary>
    None,

    /// <summary>Open the popover.</summary>
    Open,

    /// <summary>Open the popover and ask the Clear all question.</summary>
    AskClearAll,

    /// <summary>Open the popover and select the document's own folder segment.</summary>
    SelectDocumentFolder,
}

/// <summary>Which form of the drop overlay a document window shows once shown, for a hidden-desktop capture.</summary>
internal enum DevDropOverlay
{
    /// <summary>None: the overlay opens only for a drag.</summary>
    None,

    /// <summary>"Drop to open".</summary>
    Open,

    /// <summary>The grey refusal.</summary>
    Refused,
}

/// <summary>What a document window does with its ⋯ menu once shown, for a hidden-desktop capture.</summary>
internal enum DevThemeSettingsEntry
{
    /// <summary>Nothing: the menu opens only from ⋯.</summary>
    None,

    /// <summary>Open the ⋯ menu.</summary>
    Menu,

    /// <summary>Ask for the theme settings window, as the menu's item does.</summary>
    Open,
}
