using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.App.Controls;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Rendering;
using Yamv.Core.Session;

namespace Yamv.App.ViewModels;

/// <summary>
/// One document window: its tree, its selection and the document its surface shows, kept in step with a
/// <see cref="DocumentWindowState"/>. A selected document is read and rendered off the UI thread and shown only if it is still
/// the selection when the render finishes. Missing documents are re-checked every two seconds and shown again when they
/// reappear. Every member is called on the UI thread.
/// </summary>
public sealed partial class DocumentWindowViewModel : ViewModelBase, IDisposable
{
    private const string AppName = "YAMV";
    private static readonly TimeSpan MissingRecheckInterval = TimeSpan.FromSeconds(2);

    private readonly DocumentWindowState _state;
    private readonly IDocumentSurface _surface;
    private readonly Func<string, string> _readText;
    private readonly IAppLog _log;
    private readonly MarkdownRenderer _renderer = new();
    private readonly Dictionary<string, double> _scrollPositions;
    private readonly HashSet<Task> _renders = [];
    private readonly ITimer _missingTimer;
    private List<DocumentItemViewModel> _documentRows = [];
    private int _syncDepth;
    private int _renderGeneration;
    private string? _shownPath;
    private string _sessionKey;
    private bool _closeRequested;
    private bool _disposed;

    /// <summary>Creates the view model of a window over <paramref name="state"/>, and starts the missing-file re-check.</summary>
    /// <param name="state">The window's documents, selection and layout.</param>
    /// <param name="surface">The view that draws the selected document.</param>
    /// <param name="readText">Reads a document's markdown; called off the UI thread.</param>
    /// <param name="timeProvider">The clock of the missing-file re-check.</param>
    /// <param name="log">Where read failures are reported.</param>
    public DocumentWindowViewModel(
        DocumentWindowState state,
        IDocumentSurface surface,
        Func<string, string> readText,
        TimeProvider timeProvider,
        IAppLog log
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(readText);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);
        _state = state;
        _surface = surface;
        _readText = readText;
        _log = log;
        _scrollPositions = new Dictionary<string, double>(state.PathComparer);
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

    /// <summary>The tree's top-level rows, rebuilt on every change.</summary>
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

    /// <summary>True when the window shows its tree: it is the main window, or it holds two or more documents.</summary>
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
    public partial bool SelectedIsMissing { get; private set; }

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
    }

    /// <summary>Completes once every render started so far has finished or been dropped.</summary>
    internal Task WhenRendersSettled() => Task.WhenAll([.. _renders]);

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
        if (!_disposed)
        {
            _state.RefreshMissing();
        }
    }

    private void Sync()
    {
        _syncDepth++;
        try
        {
            string? selected = _state.SelectedPath;
            GroupNode? group = selected is null ? null : _state.GroupOf(selected);
            (string Name, string Location) title = TitleText.For(selected, group);
            _documentRows = [];
            Items = [.. _state.Tree.Items.Select(item => Row(item, selected))];
            SelectedPath = selected;
            TitleName = title.Name;
            TitleLocation = title.Location;
            WindowTitle = selected is null ? AppName : $"{title.Name} — {AppName}";
            ShowTree = _state.ShowTree;
            SidebarHidden = _state.SidebarHidden;
            IsEmpty = _state.IsEmpty;
            SelectedIsMissing = selected is not null && IsMissing(selected);
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
                return new FolderItemViewModel(folder, Rows(folder.Children, selected));
            case DocumentItem document:
                var row = new DocumentItemViewModel(document, SamePath(document.Path, selected), _state.Close);
                _documentRows.Add(row);
                return row;
            default:
                throw new ArgumentException($"A tree cannot show a {item.GetType().Name}.", nameof(item));
        }
    }

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
        try
        {
            document = await Task.Run(() => _renderer.Render(_readText(path)));
        }
        catch (IOException exception)
        {
            OnReadFailed(path, exception);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            OnReadFailed(path, exception);
            return;
        }

        if (_disposed || generation != _renderGeneration || IsMissing(path) || _state.GroupOf(path) is not GroupNode group)
        {
            return;
        }

        double scroll = _scrollPositions.GetValueOrDefault(path);
        _shownPath = path;
        await _surface.ShowAsync(document, path, group.Path, scroll, fragment: null);
    }

    private void OnReadFailed(string path, Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        _state.RefreshMissing();
        if (!IsMissing(path))
        {
            _log.LogWarning($"Cannot read the document {path}; the window keeps showing what it showed.", exception);
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
}

/// <summary>What kind of session change <see cref="DocumentWindowViewModel.SessionDirty"/> reports.</summary>
/// <param name="scrollOnly">True when only the shown document's scroll position changed.</param>
public sealed class SessionDirtyEventArgs(bool scrollOnly) : EventArgs
{
    /// <summary>True when only the shown document's scroll position changed; false for any other change the session stores.</summary>
    public bool ScrollOnly { get; } = scrollOnly;
}
