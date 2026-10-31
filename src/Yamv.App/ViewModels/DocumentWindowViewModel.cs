using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.App.Controls;
using Yamv.App.Services;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Rendering;
using Yamv.Core.Session;
using Yamv.Core.Widths;

namespace Yamv.App.ViewModels;

/// <summary>
/// One document window: its tree, its selection and the document its surface shows, kept in step with a
/// <see cref="DocumentWindowState"/>. A selected document is read and rendered, and its content width resolved, off the UI
/// thread; it is shown, with its width, only if it is still the selection when both finish. Missing documents are re-checked
/// every two seconds and shown again when they reappear. Every member is called on the UI thread.
/// </summary>
/// <remarks>
/// The open documents are followed through the window's <see cref="IDocumentChanges"/>: a report on the selected document
/// reads and renders it again at its scroll position, and a report on another re-checks only whether it is on disk. The
/// watched documents are updated before a newly opened document is read, so a save in the single pool dispatch between that
/// read and the watcher stamping the file is not reported, and is picked up by the next change.
/// </remarks>
public sealed partial class DocumentWindowViewModel : ViewModelBase, IDisposable, ILinkDocuments
{
    private const string AppName = "YAMV";
    private static readonly TimeSpan MissingRecheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeleteGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InUseLimit = TimeSpan.FromSeconds(10);

    /// <summary>The toast of a content-width change that could not be saved, from a step or a closed popover.</summary>
    internal const string WidthNotSavedToast = "Couldn't save the width; see the log";

    /// <summary>The toast of a content-width change that was saved but could not be sent to the page.</summary>
    internal const string WidthNotShownToast = "Couldn't apply the width; see the log";

    private readonly DocumentWindowState _state;
    private readonly IDocumentSurface _surface;
    private readonly WidthStore _widths;
    private readonly Func<string, string> _readText;
    private readonly IAppLog _log;
    private readonly MarkdownRenderer _renderer = new();
    private readonly Dictionary<string, double> _scrollPositions;

    /// <summary>Per document, the block the page last reported at the top of its view and the offset within it.</summary>
    private readonly Dictionary<string, ScrollAnchor> _scrollAnchors;
    private readonly HashSet<string> _unreadable;
    private readonly Dictionary<string, DateTimeOffset> _inUseSince;
    private readonly HashSet<Task> _renders = [];
    private readonly DocumentWindowServices _services;
    private Task _widthWork = Task.CompletedTask;
    private ContentWidthViewModel? _contentWidthPopover;
    private bool _contentWidthPopoverShowsTree;
    private readonly Dictionary<string, bool> _folderExpansion;
    private readonly ITimer _missingTimer;
    private readonly IDocumentChanges? _changes;
    private List<string> _watchedDocuments = [];
    private string? _shownHtml;
    private double? _shownWidth;

    /// <summary>How many width changes have written the store; a render that resolved its width before one reconciles the page.</summary>
    private int _widthWrites;

    /// <summary>How many times the shown width changed; a send that started before the latest change never restores an older one.</summary>
    private int _shownWidthStamps;

    private ITimer? _graceTimer;
    private string? _gracePath;
    private List<DocumentItemViewModel> _documentRows = [];
    private int _syncDepth;
    private int _renderGeneration;
    private string? _shownPath;

    /// <summary>
    /// The document a read the user started is still due for, so a background read that replaces it, or a state change that
    /// starts it, still reports a failure.
    /// </summary>
    private string? _userReadPath;

    /// <summary>A link's fragment and the render generation whose first show scrolls to it; null when none waits.</summary>
    private (string Fragment, int Generation)? _pendingFragment;
    private List<ShapeRow>? _treeShape;
    private string _sessionKey;
    private bool _closeRequested;
    private bool _disposed;

    /// <summary>Creates the view model of a window over <paramref name="state"/>, and starts the missing-file re-check.</summary>
    /// <param name="state">The window's documents, selection and layout.</param>
    /// <param name="surface">The view that draws the selected document.</param>
    /// <param name="services">
    /// The width store, the document reader, the clock, the log, where the window's toasts go and what reports the documents'
    /// changes, which the view model takes ownership of.
    /// </param>
    public DocumentWindowViewModel(DocumentWindowState state, IDocumentSurface surface, DocumentWindowServices services)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.Widths);
        ArgumentNullException.ThrowIfNull(services.ReadText);
        ArgumentNullException.ThrowIfNull(services.Time);
        ArgumentNullException.ThrowIfNull(services.Log);
        ArgumentNullException.ThrowIfNull(services.Toasts);
        TimeProvider timeProvider = services.Time;
        _state = state;
        _surface = surface;
        _services = services;
        _widths = services.Widths;
        _readText = services.ReadText;
        _log = services.Log;
        _scrollPositions = new Dictionary<string, double>(state.PathComparer);
        _scrollAnchors = new Dictionary<string, ScrollAnchor>(state.PathComparer);
        _unreadable = new HashSet<string>(state.PathComparer);
        _inUseSince = new Dictionary<string, DateTimeOffset>(state.PathComparer);
        _folderExpansion = new Dictionary<string, bool>(state.PathComparer);
        _state.Changed += OnStateChanged;
        _surface.ScrollChanged += OnScrollChanged;
        _changes = services.Changes;
        _changes?.Changed += OnDocumentsChangedReported;
        Toasts = services.Toasts;

        Sync();
        WatchDocuments();
        _sessionKey = SessionKey();
        _missingTimer = timeProvider.CreateTimer(_ => OnMissingRecheckDue(), null, MissingRecheckInterval, MissingRecheckInterval);
    }

    /// <summary>
    /// Raised on every change the session stores: documents, selection, collapsed groups and sidebar with
    /// <see cref="SessionDirtyEventArgs.ScrollOnly"/> false, and the shown document's scroll with it true.
    /// </summary>
    public event EventHandler<SessionDirtyEventArgs>? SessionDirty;

    /// <summary>Raised when the last document of a window that does not persist was closed, so the window closes too.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Where the window shows that an operation the user started failed; the window draws a <see cref="ToastCenter"/>.</summary>
    public IToasts Toasts { get; }

    /// <summary>The window's clock.</summary>
    internal TimeProvider Time => _services.Time;

    /// <summary>
    /// The tree's top-level rows. They are rebuilt when the tree's shape changes; otherwise the same rows are kept and their
    /// selection, missing flag and expansion updated.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<TreeItemViewModel> Items { get; private set; } = [];

    /// <summary>
    /// The selected tree row. Setting a document row selects that document; setting a group or folder row is undone, so the
    /// highlighted row is always the selected document's.
    /// </summary>
    [ObservableProperty]
    public partial TreeItemViewModel? SelectedItem { get; set; }

    /// <summary>The selected document's full path, or null when the window shows none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditContentWidth))]
    public partial string? SelectedPath { get; private set; }

    /// <summary>The selected document's file name, or the app name when there is none.</summary>
    [ObservableProperty]
    public partial string TitleName { get; private set; } = AppName;

    /// <summary>The selected document's group and folders, separated by slashes.</summary>
    [ObservableProperty]
    public partial string TitleLocation { get; private set; } = "";

    /// <summary>The window's own title, for the taskbar and window switcher.</summary>
    [ObservableProperty]
    public partial string WindowTitle { get; private set; } = AppName;

    /// <summary>True when the window shows its tree: it holds two or more documents, or it is the main window and holds one.</summary>
    [ObservableProperty]
    public partial bool ShowTree { get; private set; }

    /// <summary>True when the user hid the sidebar.</summary>
    [ObservableProperty]
    public partial bool SidebarHidden { get; private set; }

    /// <summary>True when the window holds no document, so it shows the empty state.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>True when the selected document is no longer on disk, so the window shows the missing pane instead of the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditContentWidth))]
    public partial bool SelectedIsMissing { get; private set; }

    /// <summary>True when the selected document exists but cannot be read, so the window shows the can't-read pane instead of the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditContentWidth))]
    public partial bool SelectedIsUnreadable { get; private set; }

    /// <summary>Why the selected document cannot be read, as the can't-read pane says it; empty when it can.</summary>
    [ObservableProperty]
    public partial string UnreadableReason { get; private set; } = "";

    /// <summary>Closes the selected document.</summary>
    [RelayCommand]
    public void CloseSelected()
    {
        if (_state.SelectedPath is string path)
        {
            _state.Close(path);
        }
    }

    /// <summary>Selects the next document in tree order.</summary>
    [RelayCommand]
    public void SelectNext() => _state.SelectNext();

    /// <summary>Selects the previous document in tree order.</summary>
    [RelayCommand]
    public void SelectPrevious() => _state.SelectPrevious();

    /// <summary>Hides the sidebar when it is shown, and shows it when it is hidden.</summary>
    [RelayCommand]
    public void ToggleSidebar() => _state.ToggleSidebar();

    /// <summary>
    /// Checks which documents are on disk, then reads and renders the selected document again, keeping its scroll position.
    /// Asked for by the user, it shows at once what it finds: a gone file shows the missing pane and a failed read the
    /// can't-read pane, with no delete grace and no wait while the file is in use.
    /// </summary>
    [RelayCommand]
    public void Reload()
    {
        EndGrace();
        bool wasMissing = SelectedIsMissing;
        // RefreshMissing raises Changed itself, and that starts the render of a file that came back.
        _userReadPath = wasMissing ? SelectedPath : null;
        if (_state.RefreshMissing() && wasMissing && !SelectedIsMissing)
        {
            return;
        }

        _userReadPath = null;
        RenderSelection(RenderOrigin.Show, userStarted: true);
    }

    /// <summary>
    /// Steps the shown document's width one grid value from the width its page shows, writing the result to where that width
    /// comes from (the folder it is inherited from, or the app default) and sending it to the page. A page showing Full steps
    /// from Full, and a document whose page is not the one shown steps from the stored width. With no document shown it does
    /// nothing; a write that fails is logged and the page keeps its width. Each press steps once: a step starts after the width
    /// change queued before it has written its width, and starts its page call without waiting for the page to answer. An open
    /// content-width popover on the document reads its width again. <see cref="WhenRendersSettled"/> waits for the steps too.
    /// </summary>
    /// <param name="wider">True to step wider (Ctrl/Cmd+]), false to step narrower (Ctrl/Cmd+[).</param>
    /// <param name="paneWidth">The document pane's width in px; the step compares against the text space it leaves.</param>
    public void StepContentWidth(bool wider, double paneWidth)
    {
        if (ShownEditablePath() is not string path)
        {
            return;
        }

        QueueWidthWork(() => StepContentWidthAsync(path, wider, paneWidth));
    }

    /// <summary>
    /// True when the window shows a document whose width can be edited: one is selected and it is neither missing nor
    /// unreadable. The ⟷ button is enabled by it.
    /// </summary>
    public bool CanEditContentWidth => SelectedPath is not null && !SelectedIsMissing && !SelectedIsUnreadable;

    /// <summary>
    /// Creates the content-width popover of the shown document, once the width changes queued before it have finished, or
    /// null when no document is shown (the window is empty, or its document is missing, unreadable or not shown yet) or the
    /// selection moved meanwhile. The popover before it, if any, goes stale; so does this one when the window's document
    /// changes or stops being shown.
    /// </summary>
    /// <param name="paneWidth">The document pane's width in px; the slider spans the text space it leaves.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paneWidth"/> is not a finite number above zero.</exception>
    public Task<ContentWidthViewModel?> CreateContentWidthPopoverAsync(double paneWidth)
    {
        if (!double.IsFinite(paneWidth) || paneWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paneWidth), paneWidth, "The pane width must be a finite number of px above zero.");
        }

        if (ShownEditablePath() is not string path)
        {
            return Task.FromResult<ContentWidthViewModel?>(null);
        }

        var created = new TaskCompletionSource<ContentWidthViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueWidthWork(async () =>
        {
            try
            {
                created.SetResult(await OpenContentWidthPopoverAsync(path, paneWidth));
            }
            // RunLogged on the window reports this; the queue's own toast would call it a failed save.
            catch (Exception exception)
            {
                created.SetException(exception);
            }
        });
        return created.Task;
    }

    /// <summary>Removes a missing document from the window.</summary>
    [RelayCommand]
    public void RemoveMissing(string path) => _state.Close(path);

    /// <summary>Adds documents to the window and selects the first one given.</summary>
    [RelayCommand]
    public void Open(IEnumerable<string> paths) => _state.Add(paths);

    /// <summary>The root folder of the group holding <paramref name="documentPath"/>, or null when the document is not open.</summary>
    /// <param name="documentPath">The document that holds a link.</param>
    public string? GroupRootOf(string documentPath) => _state.GroupOf(documentPath)?.Path;

    /// <summary>Whether <paramref name="documentPath"/> is the selected document.</summary>
    /// <param name="documentPath">The path the document was shown with, as the surface reports it.</param>
    public bool IsShown(string documentPath) => string.Equals(SelectedPath, documentPath, StringComparison.Ordinal);

    /// <summary>
    /// Opens a document a link named in this window and selects it as <see cref="Open"/> does, a missing file becoming a
    /// missing row; its first show scrolls to <paramref name="fragment"/>. A link to the shown document scrolls it without a
    /// render.
    /// </summary>
    /// <param name="path">The document's real path, as the link policy decided it.</param>
    /// <param name="fragment">The heading to scroll to, already decoded; null for none.</param>
    public void OpenLinkedDocument(string path, string? fragment)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_disposed)
        {
            return;
        }

        if (SamePath(path, ShownSelection()))
        {
            if (fragment is not null)
            {
                _renders.RemoveWhere(render => render.IsCompleted);
                _renders.Add(ScrollToFragmentAsync(fragment));
            }

            return;
        }

        // Tagged before the add, whose selection change renders at once: that render takes the fragment, and no other does.
        int generation = _renderGeneration;
        _pendingFragment = fragment is null ? null : (fragment, generation + 1);
        try
        {
            _state.Add([path]);
        }
        catch
        {
            _pendingFragment = null;
            throw;
        }

        if (_renderGeneration != generation + 1 || !SamePath(_state.SelectedPath, path))
        {
            _pendingFragment = null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is one of this window's documents.</summary>
    public bool HasDocument(string path) => _state.Documents.Contains(Path.GetFullPath(path), _state.PathComparer);

    /// <summary>Selects an open document, as the tree does.</summary>
    public void Select(string path) => _state.Select(path);

    /// <summary>
    /// Replaces a document's path with another (Locate…), keeping its place in the list, its scroll position and its scroll anchor.
    /// </summary>
    /// <param name="oldPath">The path the window holds.</param>
    /// <param name="newPath">The path that takes its place.</param>
    public void Replace(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        string from = Path.GetFullPath(oldPath);
        string to = Path.GetFullPath(newPath);
        if (_state.Documents.Contains(from, _state.PathComparer))
        {
            if (_scrollPositions.Remove(from, out double scroll))
            {
                _scrollPositions[to] = scroll;
            }

            if (_scrollAnchors.Remove(from, out ScrollAnchor? anchor))
            {
                _scrollAnchors[to] = anchor;
            }
        }

        _state.Replace(oldPath, newPath);
    }

    /// <summary>
    /// Remembers a saved session's scroll positions and anchors, opens its documents, selects its selected file when it is among them
    /// (else the first document still on disk, else the first), then collapses its collapsed groups and applies its sidebar choice.
    /// </summary>
    /// <param name="session">The session the main window saved last.</param>
    public void Restore(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RememberByFullPath(session.ScrollPositions, _scrollPositions);
        RememberByFullPath(session.ScrollAnchors, _scrollAnchors);
        _state.Add([.. session.OpenFiles.Select(FullPathOrNull).OfType<string>()]);
        if (RestoredSelection(session.SelectedFile) is string selection)
        {
            _state.Select(selection);
        }

        CollapseRestoredGroups(session.CollapsedGroups);
        if (session.SidebarHidden != _state.SidebarHidden)
        {
            _state.ToggleSidebar();
        }
    }

    /// <summary>Copies a saved session's per-document values into <paramref name="target"/>, keyed by full path.</summary>
    private void RememberByFullPath<T>(IReadOnlyDictionary<string, T> saved, Dictionary<string, T> target)
    {
        foreach ((string path, T value) in saved)
        {
            if (FullPathOrNull(path) is string fullPath)
            {
                target[fullPath] = value;
            }
        }
    }

    private void CollapseRestoredGroups(IReadOnlyList<string> groups)
    {
        foreach (string group in groups)
        {
            if (FullPathOrNull(group) is string groupPath && IsOpenGroup(Path.TrimEndingDirectorySeparator(groupPath)))
            {
                _state.SetGroupExpanded(groupPath, expanded: false);
            }
        }
    }

    /// <summary>The session state of this window, with the scroll positions and anchors of the documents it holds.</summary>
    /// <param name="bounds">The window's restored rectangle, or null when none is known.</param>
    /// <param name="sidebarWidth">The sidebar width in device-independent pixels.</param>
    public SessionState BuildSession(WindowBounds? bounds, double sidebarWidth) =>
        SessionSnapshot.Build(_state, _scrollPositions, _scrollAnchors, bounds, sidebarWidth);

    /// <summary>Stops the missing-file re-check and the watching of the documents, and stops following the state and the surface.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_changes is not null)
        {
            _changes.Changed -= OnDocumentsChangedReported;
            _changes.Dispose();
        }

        _missingTimer.Dispose();
        (Toasts as IDisposable)?.Dispose();
        EndGrace();
        _state.Changed -= OnStateChanged;
        _surface.ScrollChanged -= OnScrollChanged;
        _contentWidthPopover?.MarkStale();
        _contentWidthPopover = null;
    }

    /// <summary>
    /// Completes once every render and width change started so far, and every width change those queued, has finished or
    /// been dropped.
    /// </summary>
    internal async Task WhenRendersSettled()
    {
        Task tail;
        do
        {
            tail = _widthWork;
            await Task.WhenAll([.. _renders, tail]);
        } while (!ReferenceEquals(tail, _widthWork));
    }

    partial void OnSelectedPathChanged(string? value) => RetireStalePopover();

    partial void OnSelectedIsMissingChanged(bool value) => RetireStalePopover();

    partial void OnSelectedIsUnreadableChanged(bool value) => RetireStalePopover();

    partial void OnShowTreeChanged(bool value) => RetireStalePopover();

    /// <summary>
    /// Marks the content-width popover stale once its document is no longer the shown document, or the tree it took its group
    /// root from appeared or went.
    /// </summary>
    private void RetireStalePopover()
    {
        if (
            _contentWidthPopover is not { } popover
            || (CanEditContentWidth && SamePath(popover.DocumentPath, SelectedPath) && ShowTree == _contentWidthPopoverShowsTree)
        )
        {
            return;
        }

        popover.MarkStale();
        _contentWidthPopover = null;
    }

    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        if (_syncDepth > 0)
        {
            return;
        }

        if (value is DocumentItemViewModel document)
        {
            _state.Select(document.Path);
        }
        else if (value is not null || _state.SelectedPath is not null)
        {
            Dispatcher.UIThread.Post(RestoreSelectedRow);
        }
    }

    private void RestoreSelectedRow()
    {
        if (_disposed)
        {
            return;
        }

        _syncDepth++;
        try
        {
            SelectedItem = _documentRows.FirstOrDefault(row => row.IsSelected);
        }
        finally
        {
            _syncDepth--;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        string? previousPath = SelectedPath;
        bool wasMissing = SelectedIsMissing;
        Sync();
        WatchDocuments();
        if (!SamePath(previousPath, SelectedPath) || (wasMissing && !SelectedIsMissing))
        {
            RenderSelection(RenderOrigin.Show, userStarted: !SamePath(previousPath, SelectedPath));
        }

        if (_state.WindowShouldClose && !_closeRequested)
        {
            _closeRequested = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        _closeRequested = _state.WindowShouldClose;
        string sessionKey = SessionKey();
        if (!string.Equals(sessionKey, _sessionKey, StringComparison.Ordinal))
        {
            _sessionKey = sessionKey;
            SessionDirty?.Invoke(this, new SessionDirtyEventArgs(scrollOnly: false));
        }
    }

    private void OnScrollChanged(object? sender, ScrollPosition position)
    {
        if (_shownPath is not string path || !SamePath(path, _state.SelectedPath))
        {
            return;
        }

        _scrollPositions[path] = position.Fraction;
        if (position.Anchor is { } anchor)
        {
            _scrollAnchors[path] = anchor;
        }
        else
        {
            _scrollAnchors.Remove(path);
        }

        SessionDirty?.Invoke(this, new SessionDirtyEventArgs(scrollOnly: true));
    }

    /// <summary>What the session stores of the state, apart from scroll: a change in it is a change to save.</summary>
    private string SessionKey() =>
        string.Join(
            '\n',
            [
                string.Join('\t', _state.Documents),
                _state.SelectedPath ?? "",
                string.Join('\t', _state.CollapsedGroups),
                _state.SidebarHidden ? "hidden" : "shown",
            ]
        );

    private void OnMissingRecheckDue()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RecheckMissing();
        }
        else
        {
            Dispatcher.UIThread.Post(RecheckMissing);
        }
    }

    /// <summary>
    /// The retry tick: re-checks which documents are on disk, giving the shown page whose file is gone its delete grace instead
    /// of the missing pane, and reads again a selection that cannot be read or is kept while in use.
    /// </summary>
    private void RecheckMissing()
    {
        if (_disposed)
        {
            return;
        }

        string? shown = ShownSelection();
        ProbeMissingExcept(shown);
        StartGraceIfGone(shown);
        bool keptWhileInUse = _state.SelectedPath is string selected && _inUseSince.ContainsKey(selected);
        if ((SelectedIsUnreadable || keptWhileInUse) && !RenderInFlight)
        {
            RenderSelection(RenderOrigin.Reload, userStarted: false);
        }
    }

    /// <summary>
    /// Re-checks whether the open documents are on disk, leaving alone <paramref name="kept"/> and the document whose delete
    /// grace runs; true when a missing flag changed.
    /// </summary>
    private bool ProbeMissingExcept(string? kept)
    {
        List<string> probed = [.. _state.Documents.Where(path => !SamePath(path, kept) && !SamePath(path, _gracePath))];
        return _state.RefreshMissing(probed);
    }

    /// <summary>The selected document while its page is the one shown and it is neither missing nor unreadable; else null.</summary>
    private string? ShownSelection() =>
        _state.SelectedPath is string path && SamePath(path, _shownPath) && !IsMissing(path) && !_unreadable.Contains(path) ? path : null;

    /// <summary>Starts the delete grace of the shown page <paramref name="shown"/> when its file is gone and no grace runs for it.</summary>
    private void StartGraceIfGone(string? shown)
    {
        if (shown is not null && !SamePath(shown, _gracePath) && !_state.ExistsOnDisk(shown))
        {
            StartGrace(shown);
        }
    }

    /// <summary>
    /// Tells the watcher the open documents when they differ from those it was last told. A failure is logged, and the
    /// watcher is told again only once the documents change.
    /// </summary>
    private void WatchDocuments()
    {
        if (_changes is null || _state.Documents.SequenceEqual(_watchedDocuments, _state.PathComparer))
        {
            return;
        }

        List<string> documents = [.. _state.Documents];
        _watchedDocuments = documents;
        try
        {
            _changes.SetPaths(documents);
        }
        // Watching only makes reloads prompt: a window that cannot watch still shows, re-checks and reloads its documents.
        catch (Exception exception)
        {
            _log.LogError("Cannot watch the open documents for changes; they are re-read only when reloaded or re-checked.", exception);
        }
    }

    /// <summary>Runs on the watcher's pool thread: it only posts the report to the UI thread, never waiting on it.</summary>
    private void OnDocumentsChangedReported(object? sender, DocumentsChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlySet<string> paths = e.Paths;
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnDocumentsChanged(paths);
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnDocumentsChanged(paths));
        }
    }

    /// <summary>
    /// Handles a change report on the UI thread, probing only the open documents <paramref name="paths"/> names and leaving
    /// alone the one whose delete grace runs. The selected document, when named and not missing, is read and rendered again,
    /// so a shown page whose file is gone gets its delete grace; a missing selection is probed, and one that came back is
    /// rendered once, by the state change that shows it. Every other named document is only re-checked for whether it is on
    /// disk. Paths that are not open are ignored.
    /// </summary>
    private void OnDocumentsChanged(IReadOnlySet<string> paths)
    {
        if (_disposed)
        {
            return;
        }

        string? selected = _state.SelectedPath;
        bool selectedWasMissing = SelectedIsMissing;
        List<string> changed = [.. _state.Documents.Where(document => paths.Any(path => SamePath(path, document)))];
        List<string> probed = [.. changed.Where(path => !SamePath(path, _gracePath) && (selectedWasMissing || !SamePath(path, selected)))];
        if (probed.Count > 0)
        {
            _state.RefreshMissing(probed);
        }

        if (!selectedWasMissing && changed.Any(path => SamePath(path, selected)))
        {
            RenderSelection(RenderOrigin.Reload, userStarted: false);
        }
    }

    /// <summary>
    /// Keeps the shown page of <paramref name="path"/>, whose file was found gone by a reload, and re-checks it once after
    /// <see cref="DeleteGrace"/>, so an editor that deletes and re-creates a file on save never shows the missing pane. A
    /// grace already running for it is kept as it is, and a document already missing gets none.
    /// </summary>
    private void StartGrace(string path)
    {
        if (SamePath(_gracePath, path) || IsMissing(path))
        {
            return;
        }

        EndGrace();
        _gracePath = path;
        _graceTimer = _services.Time.CreateTimer(_ => OnGraceDue(), null, DeleteGrace, Timeout.InfiniteTimeSpan);
    }

    private void EndGrace()
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
        _gracePath = null;
    }

    private void OnGraceDue()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RecheckAfterGrace();
        }
        else
        {
            Dispatcher.UIThread.Post(RecheckAfterGrace);
        }
    }

    /// <summary>
    /// Ends the delete grace: the document's missing flag is read again, and a selected document still on disk is read again,
    /// with no second grace; one gone shows the missing pane.
    /// </summary>
    private void RecheckAfterGrace()
    {
        if (_disposed || _gracePath is not string path)
        {
            return;
        }

        EndGrace();
        _state.RefreshMissing([path]);
        if (SamePath(path, _state.SelectedPath) && !IsMissing(path))
        {
            RenderSelection(RenderOrigin.ReloadAfterGrace, userStarted: false);
        }
    }

    /// <summary>
    /// True while a render this window started has not finished. A retry tick waits for it, so a read slower than the tick is
    /// never cancelled by a newer one and never parks a second pool thread; Retry and the reload shortcut still read at once.
    /// </summary>
    private bool RenderInFlight => _renders.Any(render => !render.IsCompleted);

    private void Sync()
    {
        _syncDepth++;
        try
        {
            string? selected = _state.SelectedPath;
            if (!SamePath(selected, SelectedPath))
            {
                if (SelectedPath is string previouslySelected)
                {
                    ClearUnreadable(previouslySelected);
                }

                if (selected is not null)
                {
                    ExpandFoldersAbove(_state.Tree.Items, selected);
                }
            }

            GroupNode? group = selected is null ? null : _state.GroupOf(selected);
            (string Name, string Location) title = TitleText.For(selected, group);
            List<ShapeRow> shape = TreeShape(_state.Tree.Items);
            if (_treeShape is not null && shape.SequenceEqual(_treeShape))
            {
                UpdateRows(_state.Tree.Items, Items, selected);
            }
            else
            {
                _documentRows = [];
                Items = [.. _state.Tree.Items.Select(item => Row(item, selected))];
                _treeShape = shape;
            }

            SelectedPath = selected;
            TitleName = title.Name;
            TitleLocation = title.Location;
            WindowTitle = selected is null ? AppName : $"{title.Name} — {AppName}";
            ShowTree = _state.ShowTree;
            SidebarHidden = _state.SidebarHidden;
            IsEmpty = _state.IsEmpty;
            SelectedIsMissing = selected is not null && IsMissing(selected);
            if (SelectedIsMissing && selected is not null)
            {
                ClearUnreadable(selected);
            }

            SelectedIsUnreadable = selected is not null && _unreadable.Contains(selected);
            SelectedItem = _documentRows.FirstOrDefault(row => row.IsSelected);
        }
        finally
        {
            _syncDepth--;
        }
    }

    private TreeItemViewModel Row(TreeItem item, string? selected)
    {
        switch (item)
        {
            case GroupItem group:
                return new GroupItemViewModel(group, Rows(group.Children, selected), _state.SetGroupExpanded, _state.CloseGroup);
            case FolderItem folder:
                return new FolderItemViewModel(folder, Rows(folder.Children, selected), IsFolderExpanded(folder.Path), SetFolderExpanded);
            case DocumentItem document:
                var row = new DocumentItemViewModel(document, SamePath(document.Path, selected), _state.Close, _state.CloseGroup)
                {
                    IsUnreadable = _unreadable.Contains(document.Path),
                };
                _documentRows.Add(row);
                return row;
            default:
                throw new ArgumentException($"A tree cannot show a {item.GetType().Name}.", nameof(item));
        }
    }

    /// <summary>Marks every folder above the document at <paramref name="path"/> expanded; true when the document was found.</summary>
    private bool ExpandFoldersAbove(IReadOnlyList<TreeItem> items, string path)
    {
        foreach (TreeItem item in items)
        {
            switch (item)
            {
                case DocumentItem document when SamePath(document.Path, path):
                    return true;
                case GroupItem group when ExpandFoldersAbove(group.Children, path):
                    return true;
                case FolderItem folder when ExpandFoldersAbove(folder.Children, path):
                    _folderExpansion[folder.Path] = true;
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The tree's shape: every row's kind, name, path and depth in tree order. While it stays the same the rows are kept, so
    /// the tree view keeps its row controls and keyboard focus.
    /// </summary>
    private static List<ShapeRow> TreeShape(IReadOnlyList<TreeItem> items)
    {
        List<ShapeRow> shape = [];
        AppendShape(shape, items, depth: 0);
        return shape;
    }

    private static void AppendShape(List<ShapeRow> shape, IReadOnlyList<TreeItem> items, int depth)
    {
        foreach (TreeItem item in items)
        {
            (char kind, IReadOnlyList<TreeItem> children) = item switch
            {
                GroupItem group => (group.IsRepo ? 'R' : 'G', group.Children),
                FolderItem folder => ('F', folder.Children),
                _ => ('D', (IReadOnlyList<TreeItem>)[]),
            };
            shape.Add(new ShapeRow(kind, item.Name, item.Path, depth));
            AppendShape(shape, children, depth + 1);
        }
    }

    /// <summary>Copies the selection, missing flags and group expansion of a tree of the same shape onto the existing rows.</summary>
    private void UpdateRows(IReadOnlyList<TreeItem> items, IReadOnlyList<TreeItemViewModel> rows, string? selected)
    {
        for (int index = 0; index < items.Count; index++)
        {
            switch (items[index], rows[index])
            {
                case (GroupItem group, GroupItemViewModel row):
                    row.ShowExpanded(group.IsExpanded);
                    UpdateRows(group.Children, row.Children, selected);
                    break;
                case (FolderItem folder, FolderItemViewModel row):
                    row.IsExpanded = IsFolderExpanded(folder.Path);
                    UpdateRows(folder.Children, row.Children, selected);
                    break;
                case (DocumentItem document, DocumentItemViewModel row):
                    row.IsMissing = document.IsMissing;
                    row.IsUnreadable = _unreadable.Contains(document.Path);
                    row.IsSelected = SamePath(document.Path, selected);
                    break;
                default:
                    throw new InvalidOperationException($"The tree row at {items[index].Path} does not match its row view model.");
            }
        }
    }

    private bool IsFolderExpanded(string folderPath) => _folderExpansion.GetValueOrDefault(folderPath, true);

    private void SetFolderExpanded(string folderPath, bool expanded) => _folderExpansion[folderPath] = expanded;

    private List<TreeItemViewModel> Rows(IReadOnlyList<TreeItem> items, string? selected) => [.. items.Select(item => Row(item, selected))];

    private string? RestoredSelection(string? savedSelection)
    {
        IReadOnlyList<string> documents = _state.Documents;
        if (savedSelection is not null && FullPathOrNull(savedSelection) is string savedPath)
        {
            string? saved = documents.FirstOrDefault(path => SamePath(path, savedPath));
            if (saved is not null)
            {
                return saved;
            }
        }

        return documents.FirstOrDefault(path => !IsMissing(path)) ?? (documents.Count > 0 ? documents[0] : null);
    }

    /// <summary>Reads and renders the selected document off the UI thread, dropping any render started before.</summary>
    /// <param name="requested">
    /// Why the document is read. A reload counts as one only when the selection's page is the one shown and it is not
    /// unreadable; otherwise the read is a first show.
    /// </param>
    /// <param name="userStarted">
    /// Whether the user just asked for this read: a new selection or the Reload command, never a change report, a re-check or
    /// a file that came back. Only a user's read that is still current shows its failure in a toast.
    /// </param>
    private void RenderSelection(RenderOrigin requested, bool userStarted)
    {
        int generation = ++_renderGeneration;
        string? path = _state.SelectedPath;
        if (path is null || IsMissing(path))
        {
            return;
        }

        if (userStarted)
        {
            _userReadPath = path;
        }

        bool showsPage = SamePath(_shownPath, path) && !_unreadable.Contains(path);
        RenderOrigin origin = showsPage ? requested : RenderOrigin.Show;
        _renders.RemoveWhere(render => render.IsCompleted);
        _renders.Add(RenderAsync(path, generation, origin, SamePath(_userReadPath, path)));
    }

    private async Task RenderAsync(string path, int generation, RenderOrigin origin, bool userStarted)
    {
        try
        {
            await RenderCoreAsync(path, generation, origin);
        }
        catch (Exception exception)
        {
            _log.LogError($"Cannot show the document {path}.", exception);
            if (userStarted && generation == _renderGeneration && !_disposed && SamePath(path, _state.SelectedPath))
            {
                Toasts.Show(ToastKind.Warning, $"Couldn't show {ToastValue.Code(Path.GetFileName(path))}; see the log");
            }
        }
        finally
        {
            if (generation == _renderGeneration)
            {
                _userReadPath = null;
            }
        }
    }

    private async Task RenderCoreAsync(string path, int generation, RenderOrigin origin)
    {
        RenderedDocument document;
        ContentWidth width;
        int widthWrites = _widthWrites;
        try
        {
            (document, width) = await Task.Run(() => (_renderer.Render(_readText(path)), ResolveWidth(path)));
        }
        catch (IOException exception)
        {
            OnReadFailed(path, exception, generation, origin);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            OnReadFailed(path, exception, generation, origin);
            return;
        }

        if (_disposed || generation != _renderGeneration || IsMissing(path) || _state.GroupOf(path) is not GroupNode group)
        {
            return;
        }

        ClearUnreadable(path);
        double scroll = _scrollPositions.GetValueOrDefault(path);
        ScrollAnchor? anchor = _scrollAnchors.GetValueOrDefault(path);
        if (!MarkShown(path, document.Html, width.Px, origin))
        {
            return;
        }

        // Nothing may be awaited between the check above and this call: a document that is no longer the selection is never shown.
        Task show = _surface.ShowAsync(new DocumentShow(document, path, group.Path, scroll, anchor, TakePendingFragment(generation), width.Px));
        if (widthWrites != _widthWrites)
        {
            // The store changed while this render resolved its width, so the page it just showed may not hold the stored one.
            QueueWidthWork(SendShownWidthWhenChangedAsync);
        }

        await show;
    }

    /// <summary>The link fragment waiting for the render of <paramref name="generation"/>, or null; the slot is emptied either way.</summary>
    private string? TakePendingFragment(int generation)
    {
        (string Fragment, int Generation)? pending = _pendingFragment;
        _pendingFragment = null;
        return pending is { } slot && slot.Generation == generation ? slot.Fragment : null;
    }

    /// <summary>Scrolls the shown document to a link's fragment, logging a failure and showing it in a toast.</summary>
    private async Task ScrollToFragmentAsync(string fragment)
    {
        bool ran;
        try
        {
            ran = await _surface.ScrollToFragmentAsync(fragment);
        }
        catch (Exception exception)
        {
            _log.LogError($"Cannot scroll the shown document to the heading {fragment}.", exception);
            ran = false;
        }

        if (!ran)
        {
            Toasts.Show(ToastKind.Warning, $"Couldn't scroll to {ToastValue.Code(fragment)}; see the log");
        }
    }

    /// <summary>
    /// Records <paramref name="path"/> as the shown document, ending its delete grace since it was read, and returns false when
    /// it is a reload of the page already shown with the same HTML at the same width, which needs no new show.
    /// </summary>
    private bool MarkShown(string path, string html, double? widthPx, RenderOrigin origin)
    {
        if (SamePath(_gracePath, path))
        {
            EndGrace();
        }

        bool unchanged =
            origin != RenderOrigin.Show
            && SamePath(_shownPath, path)
            && string.Equals(_shownHtml, html, StringComparison.Ordinal)
            && _shownWidth == widthPx;
        _shownPath = path;
        _shownHtml = html;
        ShowWidth(widthPx);
        return !unchanged;
    }

    /// <summary>Records the width the page shows and stamps the change, so a send that started before it never reverts it.</summary>
    private void ShowWidth(double? px)
    {
        _shownWidth = px;
        _shownWidthStamps++;
    }

    /// <summary>
    /// Queues <paramref name="work"/> behind every width change queued before it, so steps and popover changes reach the store
    /// and the page in the order the user made them. The work starts on the UI thread; a failure is logged.
    /// </summary>
    internal void QueueWidthWork(Func<Task> work)
    {
        // The queue's tail is set before the work starts, so work it queues before its first await runs after it.
        Task previous = _widthWork;
        var run = new Task<Task>(() => RunAfterAsync(previous, work));
        _widthWork = run.Unwrap();
        run.RunSynchronously();
    }

    /// <summary>The open content-width popover, or null when there is none.</summary>
    internal ContentWidthViewModel? ContentWidthPopover => _contentWidthPopover;

    /// <summary>True while <paramref name="path"/> is the shown document and its width can be edited.</summary>
    internal bool IsShowingContentWidthOf(string path) => SamePath(path, ShownEditablePath());

    /// <summary>Forgets <paramref name="popover"/> when it is the open one; it closed.</summary>
    internal void ForgetContentWidthPopover(ContentWidthViewModel popover)
    {
        if (ReferenceEquals(popover, _contentWidthPopover))
        {
            _contentWidthPopover = null;
        }
    }

    /// <summary>Runs <paramref name="work"/> once <paramref name="previous"/>, which never faults, has finished.</summary>
    private async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        await previous;
        try
        {
            await work();
        }
        // The queue is only awaited by WhenRendersSettled, so a failure is logged here or nowhere.
        catch (Exception exception)
        {
            _log.LogError("Cannot change the content width.", exception);
            Toasts.Show(ToastKind.Warning, WidthNotSavedToast);
        }
    }

    /// <summary>
    /// The shown document when its width can be edited: the window is not disposed, and the document is the selection and
    /// neither missing nor unreadable.
    /// </summary>
    private string? ShownEditablePath() =>
        !_disposed && _shownPath is string path && SamePath(path, _state.SelectedPath) && CanEditContentWidth ? path : null;

    private async Task<ContentWidthViewModel?> OpenContentWidthPopoverAsync(string path, double paneWidth)
    {
        (ContentWidth width, WidthSource source) = await Task.Run(() => _widths.Resolve(path));
        if (!SamePath(path, ShownEditablePath()) || _state.GroupOf(path) is not GroupNode group)
        {
            return null;
        }

        // A window without a tree (a one-file viewer) shows no group, so its document's folder is its own group root.
        string folder = Path.GetDirectoryName(path) ?? path;
        string groupRoot = ShowTree ? group.Path : folder;
        bool groupIsRepo = group.IsRepo && SamePath(group.Path, groupRoot);
        var host = new ContentWidthHost(
            path,
            _surface,
            _services,
            new WidthStoreAdapter(_widths),
            this,
            from => WidthScopes.For(path, groupRoot, groupIsRepo, from)
        );
        var popover = new ContentWidthViewModel(host, source, width, paneWidth);
        _contentWidthPopover?.MarkStale();
        _contentWidthPopover = popover;
        _contentWidthPopoverShowsTree = ShowTree;
        return popover;
    }

    private async Task StepContentWidthAsync(string path, bool wider, double paneWidth)
    {
        try
        {
            ContentWidth? shown = null;
            if (SamePath(path, _shownPath))
            {
                shown = _shownWidth is double px ? ContentWidth.FromPx(px) : ContentWidth.Full;
            }

            (ContentWidth next, bool written) = await Task.Run(() => WriteSteppedWidth(path, shown, wider, ContentWidth.TextSpace(paneWidth)));
            if (!written)
            {
                _log.LogWarning($"Cannot save the stepped content width of {path}; the page keeps its width.");
                Toasts.Show(ToastKind.Warning, WidthNotSavedToast);
                return;
            }

            NoteWidthWrite();
            if (SamePath(path, _shownPath))
            {
                SendPageWidth(path, next.Px);
            }
            else
            {
                await SendShownWidthWhenChangedAsync();
            }

            if (_contentWidthPopover is { } popover && SamePath(popover.DocumentPath, path))
            {
                popover.Refresh();
            }
        }
        // The step's task is only awaited by WhenRendersSettled, so its failure is logged here or nowhere.
        catch (Exception exception)
        {
            _log.LogError($"Cannot step the content width of {path}.", exception);
            Toasts.Show(ToastKind.Warning, WidthNotSavedToast);
        }
    }

    /// <summary>
    /// Writes the step's width to where the document's width comes from, from <paramref name="shown"/> when the page shows the
    /// document, and returns the width and whether the store saved it; called off the UI thread.
    /// </summary>
    private (ContentWidth Next, bool Written) WriteSteppedWidth(string path, ContentWidth? shown, bool wider, double textSpace)
    {
        (ContentWidth width, WidthSource source) = _widths.Resolve(path);
        ContentWidth start = shown ?? width;
        ContentWidth next = wider ? start.StepUp(textSpace) : start.StepDown(textSpace);
        bool written = source.Folder is string folder ? _widths.Set(folder, next) : _widths.SetDefault(next);
        return (next, written);
    }

    /// <summary>
    /// Sends the width the store now resolves to the page when it differs from the width the page shows: after a step's write,
    /// which may have landed while another document, or a render that resolved the width before the write, was being shown.
    /// </summary>
    private async Task SendShownWidthWhenChangedAsync()
    {
        if (_disposed || _shownPath is not string shownPath)
        {
            return;
        }

        ContentWidth width;
        try
        {
            width = await Task.Run(() => _widths.Resolve(shownPath).Width);
        }
        // Any failure of the store leaves the page at its width: the send is only a correction.
        catch (Exception exception)
        {
            _log.LogError($"Cannot resolve the content width of {shownPath} to send it to the page.", exception);
            return;
        }

        if (_disposed || !SamePath(shownPath, _shownPath) || width.Px == _shownWidth)
        {
            return;
        }

        SendPageWidth(shownPath, width.Px);
    }

    /// <summary>
    /// Sends a committed width to the page and records it as what the page shows while <paramref name="path"/> is the shown
    /// document, so the next step starts from it before the page answers. A disposed window, or one showing another document,
    /// sends nothing: a width for one document never reaches a page that shows another. A send that a newer change replaced, or
    /// that answers after one, never writes the shown width: only a failed send restores the value this one replaced, and only
    /// while no newer change has stamped the shown width since.
    /// </summary>
    internal void SendPageWidth(string path, double? px)
    {
        if (_disposed || !SamePath(path, _shownPath))
        {
            return;
        }

        double? previous = _shownWidth;
        ShowWidth(px);
        _ = SendCommittedWidthAsync(path, px, _shownWidthStamps, previous);
    }

    /// <summary>Starts the page's width call; its failure is logged and toasted here, since nothing awaits it.</summary>
    private async Task SendCommittedWidthAsync(string path, double? px, int sent, double? previous)
    {
        bool ran;
        try
        {
            ran = await _surface.SetContentWidthAsync(px);
        }
        catch (Exception exception)
        {
            _log.LogError($"Cannot send the content width of {path} to the page.", exception);
            ran = false;
        }

        if (ran)
        {
            return;
        }

        if (sent == _shownWidthStamps)
        {
            ShowWidth(previous);
        }

        Toasts.Show(ToastKind.Warning, WidthNotShownToast);
    }

    /// <summary>
    /// Records that a width change wrote the store, so a render that resolved its width before the write sends the stored width
    /// once its show has landed.
    /// </summary>
    internal void NoteWidthWrite() => _widthWrites++;

    /// <summary>
    /// Queues the reconcile behind the width change that wrote the store: a shown document, or a sibling of the one whose width
    /// changed, is brought to the width the store now resolves.
    /// </summary>
    internal void QueueShownWidthReconcile() => QueueWidthWork(SendShownWidthWhenChangedAsync);

    /// <summary>The width stored for <paramref name="path"/>, the app default when the store fails; called off the UI thread.</summary>
    private ContentWidth ResolveWidth(string path)
    {
        try
        {
            return _widths.Resolve(path).Width;
        }
        // Any failure of the store must still let the document show, at the app default.
        catch (Exception exception)
        {
            _log.LogError($"Cannot resolve the content width of {path}; showing it at the app default.", exception);
            return ContentWidth.AppDefault;
        }
    }

    private void OnReadFailed(string path, Exception exception, int generation, RenderOrigin origin)
    {
        if (_disposed)
        {
            return;
        }

        bool current = generation == _renderGeneration && SamePath(path, _state.SelectedPath);
        if (origin == RenderOrigin.Reload && IsGone(exception))
        {
            KeepPageOfGoneFile(path, current);
            return;
        }

        if (IsGone(exception) && SamePath(path, _gracePath))
        {
            EndGrace();
        }

        _state.RefreshMissing([path]);
        if (current)
        {
            ShowReadFailure(path, exception, origin);
        }
    }

    /// <summary>
    /// Shows why the selected document's current read failed: the missing pane when it is gone, nothing while a reload of its
    /// shown page finds it in use for less than <see cref="InUseLimit"/>, else the can't-read pane.
    /// </summary>
    private void ShowReadFailure(string path, Exception exception, RenderOrigin origin)
    {
        if (IsMissing(path))
        {
            ClearUnreadable(path);
            return;
        }

        if (origin != RenderOrigin.Show && KeepsPageWhileInUse(path, exception))
        {
            return;
        }

        if (_unreadable.Add(path))
        {
            _log.LogWarning($"Cannot read the document {path}.", exception);
        }

        SetRowUnreadable(path, true);
        SelectedIsUnreadable = true;
        UnreadableReason = ReadFailure.ReasonFor(exception);
    }

    /// <summary>
    /// Handles a reload of the shown page whose file is gone: the page stays, and the current read starts the delete grace,
    /// after which the file's read shows the page or the missing pane shows. A read the selection left behind greys its
    /// document's row at once; one a newer read of the same selection replaced leaves it to that read.
    /// </summary>
    private void KeepPageOfGoneFile(string path, bool current)
    {
        if (current)
        {
            StartGrace(path);
        }
        else if (!SamePath(path, _state.SelectedPath))
        {
            _state.RefreshMissing([path]);
        }
    }

    /// <summary>
    /// True when a reload of the shown page of <paramref name="path"/> failed because another program holds the file, and
    /// its reloads have kept failing so for less than <see cref="InUseLimit"/>: the page stays unchanged, and the next report
    /// or retry tick reads it again.
    /// </summary>
    private bool KeepsPageWhileInUse(string path, Exception exception)
    {
        if (ReadFailure.ReasonFor(exception) != ReadFailure.InUseByAnotherProgram)
        {
            return false;
        }

        DateTimeOffset now = _services.Time.GetUtcNow();
        if (!_inUseSince.TryGetValue(path, out DateTimeOffset since))
        {
            since = now;
            _inUseSince[path] = since;
        }

        return now - since < InUseLimit;
    }

    /// <summary>
    /// Forgets that <paramref name="path"/> cannot be read, and since when it has been in use: its row's warning glyph goes,
    /// and the pane when it is selected.
    /// </summary>
    private void ClearUnreadable(string path)
    {
        _unreadable.Remove(path);
        _inUseSince.Remove(path);
        SetRowUnreadable(path, false);
        if (SamePath(path, _state.SelectedPath))
        {
            SelectedIsUnreadable = false;
            UnreadableReason = "";
        }
    }

    private void SetRowUnreadable(string path, bool unreadable)
    {
        foreach (DocumentItemViewModel row in _documentRows)
        {
            if (SamePath(row.Path, path))
            {
                row.IsUnreadable = unreadable;
            }
        }
    }

    private bool IsOpenGroup(string groupPath) => _state.Groups.Any(open => _state.PathComparer.Equals(open.Path, groupPath));

    /// <summary>The full form of a path read from the session file, or null (with a warning) when it is not a valid path.</summary>
    private string? FullPathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _log.LogWarning($"Skipping the saved path '{path}': it is not a valid path.", exception);
            return null;
        }
    }

    /// <summary>True when a read failed because the file or its folder is no longer on disk.</summary>
    private static bool IsGone(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException;

    private bool IsMissing(string path) => _state.Tree.Documents.Any(document => document.IsMissing && SamePath(document.Path, path));

    private bool SamePath(string? first, string? second) => first is not null && second is not null && _state.PathComparer.Equals(first, second);

    /// <summary>Why a document is read: to show it, or to read again the page already shown.</summary>
    private enum RenderOrigin
    {
        /// <summary>
        /// A first show, or the user's Reload command: any failure shows its pane at once, and the page is always sent again.
        /// </summary>
        Show,

        /// <summary>
        /// A reload of the shown page after a change report or a retry tick: a gone file gets the delete grace, a file in use
        /// keeps the page for a while, and the same HTML at the same width is not sent again.
        /// </summary>
        Reload,

        /// <summary>The read after the delete grace: as <see cref="Reload"/>, with no second grace.</summary>
        ReloadAfterGrace,
    }

    /// <summary>One row of the tree's shape: its kind (repo group, folder group, folder or document), name, path and depth.</summary>
    private readonly record struct ShapeRow(char Kind, string Name, string Path, int Depth);
}

/// <summary>The services a <see cref="DocumentWindowViewModel"/> uses beside its state and surface.</summary>
/// <param name="Widths">The app's one width store, which resolves and saves each document's content width.</param>
/// <param name="ReadText">Reads a document's markdown; called off the UI thread.</param>
/// <param name="Time">The clock of the missing-file re-check.</param>
/// <param name="Log">Where read and width failures are reported.</param>
/// <param name="Changes">
/// What reports the open documents' changes on disk; the view model owns it and disposes it. Null for a window that does not
/// follow its documents.
/// </param>
/// <param name="Toasts">Where the window shows that an operation the user started failed; the view model owns it and disposes it.</param>
public sealed record DocumentWindowServices(
    WidthStore Widths,
    Func<string, string> ReadText,
    TimeProvider Time,
    IAppLog Log,
    IToasts Toasts,
    IDocumentChanges? Changes = null
);

/// <summary>What kind of session change <see cref="DocumentWindowViewModel.SessionDirty"/> reports.</summary>
/// <param name="scrollOnly">True when only the shown document's scroll position changed.</param>
public sealed class SessionDirtyEventArgs(bool scrollOnly) : EventArgs
{
    /// <summary>True when only the shown document's scroll position changed; false for any other change the session stores.</summary>
    public bool ScrollOnly { get; } = scrollOnly;
}
