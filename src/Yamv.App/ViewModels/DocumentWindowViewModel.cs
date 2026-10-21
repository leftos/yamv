using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.App.Controls;
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
public sealed partial class DocumentWindowViewModel : ViewModelBase, IDisposable
{
    private const string AppName = "YAMV";
    private static readonly TimeSpan MissingRecheckInterval = TimeSpan.FromSeconds(2);

    private readonly DocumentWindowState _state;
    private readonly IDocumentSurface _surface;
    private readonly WidthStore _widths;
    private readonly Func<string, string> _readText;
    private readonly IAppLog _log;
    private readonly MarkdownRenderer _renderer = new();
    private readonly Dictionary<string, double> _scrollPositions;
    private readonly HashSet<string> _unreadable;
    private readonly HashSet<Task> _renders = [];
    private readonly DocumentWindowServices _services;
    private Task _widthWork = Task.CompletedTask;
    private ContentWidthViewModel? _contentWidthPopover;
    private bool _contentWidthPopoverShowsTree;
    private readonly Dictionary<string, bool> _folderExpansion;
    private readonly ITimer _missingTimer;
    private List<DocumentItemViewModel> _documentRows = [];
    private int _syncDepth;
    private int _renderGeneration;
    private string? _shownPath;
    private List<ShapeRow>? _treeShape;
    private string _sessionKey;
    private bool _closeRequested;
    private bool _disposed;

    /// <summary>Creates the view model of a window over <paramref name="state"/>, and starts the missing-file re-check.</summary>
    /// <param name="state">The window's documents, selection and layout.</param>
    /// <param name="surface">The view that draws the selected document.</param>
    /// <param name="services">The width store, the document reader, the clock and the log the window uses.</param>
    public DocumentWindowViewModel(DocumentWindowState state, IDocumentSurface surface, DocumentWindowServices services)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.Widths);
        ArgumentNullException.ThrowIfNull(services.ReadText);
        ArgumentNullException.ThrowIfNull(services.Time);
        ArgumentNullException.ThrowIfNull(services.Log);
        TimeProvider timeProvider = services.Time;
        _state = state;
        _surface = surface;
        _services = services;
        _widths = services.Widths;
        _readText = services.ReadText;
        _log = services.Log;
        _scrollPositions = new Dictionary<string, double>(state.PathComparer);
        _unreadable = new HashSet<string>(state.PathComparer);
        _folderExpansion = new Dictionary<string, bool>(state.PathComparer);
        _state.Changed += OnStateChanged;
        _surface.ScrollChanged += OnScrollChanged;
        Sync();
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
    /// </summary>
    [RelayCommand]
    public void Reload()
    {
        bool wasMissing = SelectedIsMissing;
        if (_state.RefreshMissing() && wasMissing && !SelectedIsMissing)
        {
            return;
        }

        RenderSelection();
    }

    /// <summary>
    /// Steps the shown document's width one grid value, writing the result to where that width comes from (the folder it is
    /// inherited from, or the app default) and sending it to the page. With no document shown it does nothing; a write that
    /// fails is logged and the page keeps its width. Each press steps once: a step starts after the width change queued before
    /// it has written its width, and starts its page call without waiting for the page to answer. An open content-width
    /// popover on the document reads its width again. <see cref="WhenRendersSettled"/> waits for the steps too.
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
            ContentWidthViewModel? popover = null;
            try
            {
                popover = await OpenContentWidthPopoverAsync(path, paneWidth);
            }
            finally
            {
                created.SetResult(popover);
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

    /// <summary>Replaces a document's path with another (Locate…), keeping its place in the list and its scroll position.</summary>
    /// <param name="oldPath">The path the window holds.</param>
    /// <param name="newPath">The path that takes its place.</param>
    public void Replace(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        string from = Path.GetFullPath(oldPath);
        string to = Path.GetFullPath(newPath);
        if (_state.Documents.Contains(from, _state.PathComparer) && _scrollPositions.Remove(from, out double scroll))
        {
            _scrollPositions[to] = scroll;
        }

        _state.Replace(oldPath, newPath);
    }

    /// <summary>
    /// Remembers a saved session's scroll positions, opens its documents, selects its selected file when it is among them
    /// (else the first document still on disk, else the first), then collapses its collapsed groups and applies its sidebar choice.
    /// </summary>
    /// <param name="session">The session the main window saved last.</param>
    public void Restore(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach ((string path, double scroll) in session.ScrollPositions)
        {
            if (FullPathOrNull(path) is string fullPath)
            {
                _scrollPositions[fullPath] = scroll;
            }
        }

        _state.Add([.. session.OpenFiles.Select(FullPathOrNull).OfType<string>()]);
        if (RestoredSelection(session.SelectedFile) is string selection)
        {
            _state.Select(selection);
        }

        foreach (string group in session.CollapsedGroups)
        {
            if (FullPathOrNull(group) is string groupPath && IsOpenGroup(Path.TrimEndingDirectorySeparator(groupPath)))
            {
                _state.SetGroupExpanded(groupPath, expanded: false);
            }
        }

        if (session.SidebarHidden != _state.SidebarHidden)
        {
            _state.ToggleSidebar();
        }
    }

    /// <summary>The session state of this window, with the scroll positions it remembers.</summary>
    /// <param name="bounds">The window's restored rectangle, or null when none is known.</param>
    /// <param name="sidebarWidth">The sidebar width in device-independent pixels.</param>
    /// <param name="themeId">The id of the theme in use.</param>
    /// <param name="appearance">The light or dark preference: <c>system</c>, <c>light</c> or <c>dark</c>.</param>
    public SessionState BuildSession(WindowBounds? bounds, double sidebarWidth, string themeId, string appearance) =>
        SessionSnapshot.Build(_state, _scrollPositions, bounds, sidebarWidth, themeId, appearance);

    /// <summary>Stops the missing-file re-check and stops following the state and the surface.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _missingTimer.Dispose();
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
        if (!SamePath(previousPath, SelectedPath) || (wasMissing && !SelectedIsMissing))
        {
            RenderSelection();
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

    private void OnScrollChanged(object? sender, double fraction)
    {
        if (_shownPath is not string path || !SamePath(path, _state.SelectedPath))
        {
            return;
        }

        _scrollPositions[path] = fraction;
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

    private void RecheckMissing()
    {
        if (_disposed)
        {
            return;
        }

        _state.RefreshMissing();
        if (SelectedIsUnreadable && !RenderInFlight)
        {
            RenderSelection();
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

    private void RenderSelection()
    {
        int generation = ++_renderGeneration;
        string? path = _state.SelectedPath;
        if (path is null || IsMissing(path))
        {
            return;
        }

        _renders.RemoveWhere(render => render.IsCompleted);
        _renders.Add(RenderAsync(path, generation));
    }

    private async Task RenderAsync(string path, int generation)
    {
        try
        {
            await RenderCoreAsync(path, generation);
        }
        catch (Exception exception)
        {
            _log.LogError($"Cannot show the document {path}.", exception);
        }
    }

    private async Task RenderCoreAsync(string path, int generation)
    {
        RenderedDocument document;
        ContentWidth width;
        try
        {
            (document, width) = await Task.Run(() => (_renderer.Render(_readText(path)), ResolveWidth(path)));
        }
        catch (IOException exception)
        {
            OnReadFailed(path, exception, generation);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            OnReadFailed(path, exception, generation);
            return;
        }

        if (_disposed || generation != _renderGeneration || IsMissing(path) || _state.GroupOf(path) is not GroupNode group)
        {
            return;
        }

        ClearUnreadable(path);
        double scroll = _scrollPositions.GetValueOrDefault(path);
        _shownPath = path;
        // Nothing may be awaited between the check above and this call: a document that is no longer the selection is never shown.
        await _surface.ShowAsync(new DocumentShow(document, path, group.Path, scroll, Fragment: null, width.Px));
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
        var host = new ContentWidthHost(path, _surface, _services, this, from => WidthScopes.For(path, groupRoot, groupIsRepo, from));
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
            double textSpace = ContentWidth.TextSpace(paneWidth);
            (ContentWidth stepped, bool written) = await Task.Run(() =>
            {
                (ContentWidth width, WidthSource source) = _widths.Resolve(path);
                ContentWidth next = wider ? width.StepUp(textSpace) : width.StepDown(textSpace);
                bool saved = source.Folder is string folder ? _widths.Set(folder, next) : _widths.SetDefault(next);
                return (next, saved);
            });
            if (!written)
            {
                _log.LogWarning($"Cannot save the stepped content width of {path}; the page keeps its width.");
                return;
            }

            if (_disposed || !SamePath(path, _shownPath))
            {
                return;
            }

            // Started here so page calls keep the queue's order, but not awaited: a page that never answers must not hold
            // the queue.
            _ = SendSteppedWidthAsync(path, stepped.Px);
            if (_contentWidthPopover is { } popover && SamePath(popover.DocumentPath, path))
            {
                popover.Refresh();
            }
        }
        // The step's task is only awaited by WhenRendersSettled, so its failure is logged here or nowhere.
        catch (Exception exception)
        {
            _log.LogError($"Cannot step the content width of {path}.", exception);
        }
    }

    private async Task SendSteppedWidthAsync(string path, double? px)
    {
        try
        {
            await _surface.SetContentWidthAsync(px);
        }
        // The send is not awaited by anyone, so its failure is logged here or nowhere.
        catch (Exception exception)
        {
            _log.LogError($"Cannot send the stepped content width of {path} to the page.", exception);
        }
    }

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

    private void OnReadFailed(string path, Exception exception, int generation)
    {
        if (_disposed)
        {
            return;
        }

        _state.RefreshMissing();
        if (generation != _renderGeneration || !SamePath(path, _state.SelectedPath))
        {
            return;
        }

        if (IsMissing(path))
        {
            ClearUnreadable(path);
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

    /// <summary>Forgets that <paramref name="path"/> cannot be read: its row's warning glyph goes, and the pane when it is selected.</summary>
    private void ClearUnreadable(string path)
    {
        _unreadable.Remove(path);
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

    private bool IsMissing(string path) => _state.Tree.Documents.Any(document => document.IsMissing && SamePath(document.Path, path));

    private bool SamePath(string? first, string? second) => first is not null && second is not null && _state.PathComparer.Equals(first, second);

    /// <summary>One row of the tree's shape: its kind (repo group, folder group, folder or document), name, path and depth.</summary>
    private readonly record struct ShapeRow(char Kind, string Name, string Path, int Depth);
}

/// <summary>The services a <see cref="DocumentWindowViewModel"/> uses beside its state and surface.</summary>
/// <param name="Widths">The app's one width store, which resolves and saves each document's content width.</param>
/// <param name="ReadText">Reads a document's markdown; called off the UI thread.</param>
/// <param name="Time">The clock of the missing-file re-check.</param>
/// <param name="Log">Where read and width failures are reported.</param>
public sealed record DocumentWindowServices(WidthStore Widths, Func<string, string> ReadText, TimeProvider Time, IAppLog Log);

/// <summary>What kind of session change <see cref="DocumentWindowViewModel.SessionDirty"/> reports.</summary>
/// <param name="scrollOnly">True when only the shown document's scroll position changed.</param>
public sealed class SessionDirtyEventArgs(bool scrollOnly) : EventArgs
{
    /// <summary>True when only the shown document's scroll position changed; false for any other change the session stores.</summary>
    public bool ScrollOnly { get; } = scrollOnly;
}
