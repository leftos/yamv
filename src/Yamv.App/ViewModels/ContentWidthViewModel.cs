using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.App.Controls;
using Yamv.App.Services;
using Yamv.Core.Widths;

namespace Yamv.App.ViewModels;

/// <summary>
/// The content-width popover of one shown document: the slider and its readout, the Saved for segments, Reset to inherited
/// and Clear all overrides here. Moving the slider changes the page live when the selected segment's width reaches the
/// document; <see cref="EndDrag"/> saves the width for the selected segment. Every store call runs off the UI thread, queued
/// behind the window's other width changes (the Ctrl/Cmd+[ and ] steps), so the later action wins in the store and on the
/// page. A read or save that lands while a drag is in progress, or before an ended drag's save has run, updates the segments
/// but leaves the drag alone. A save that fails is logged, springs the slider back and shows <see cref="SaveFailedMessage"/>
/// for 4 s, and the page gets the stored width back even when the popover has gone stale. Once the window's document changes, or
/// <see cref="Close"/> is called, the view model is stale and does nothing more. Every member is called on the UI thread.
/// </summary>
public sealed partial class ContentWidthViewModel : ViewModelBase
{
    /// <summary>The slider's caption when the pane leaves less than 480 px for the text.</summary>
    public const string NarrowCaption = "The window is narrower than 480 px";

    /// <summary>The foot's message after a width could not be saved.</summary>
    public const string SaveFailedMessage = "Could not save the width; see the log.";

    private static readonly TimeSpan FootMessageTime = TimeSpan.FromSeconds(4);

    private readonly ContentWidthHost _host;
    private readonly double _textSpace;
    private WidthScopes _scopes;
    private ContentWidth _storedWidth;
    private ContentWidth _width;
    private bool _moved;
    private int _unsavedDrags;
    private bool _placingSlider;
    private ITimer? _footTimer;
    private int _footMessages;

    /// <summary>Creates the popover of a document whose width is <paramref name="width"/>, from <paramref name="source"/>.</summary>
    /// <param name="host">The document and what the popover uses of its window.</param>
    /// <param name="source">Where the document's width comes from; its segment is selected.</param>
    /// <param name="width">The document's width.</param>
    /// <param name="paneWidth">The document pane's width in px; the slider spans the text space it leaves.</param>
    internal ContentWidthViewModel(ContentWidthHost host, WidthSource source, ContentWidth width, double paneWidth)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _textSpace = ContentWidth.TextSpace(paneWidth);
        _scopes = host.ScopesFor(source);
        ShowSegment(_scopes, width, source);
        host.Window.QueueWidthWork(ReadClearAllCountAsync);
    }

    /// <summary>The document the popover edits the width of.</summary>
    public string DocumentPath => _host.DocumentPath;

    /// <summary>The Saved for segments, deepest folder first and All documents last; one is selected.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WidthScope> Segments { get; private set; } = [];

    /// <summary>The segment the slider edits and a save writes to.</summary>
    [ObservableProperty]
    public partial WidthScope? SelectedSegment { get; private set; }

    /// <summary>
    /// The slider's position, 0 (480 px) to 1 (Full) across the pane's text space. Setting it starts or continues a drag: it
    /// changes the page's width live (when <see cref="DragReachesDocument"/>) and saves nothing; <see cref="EndDrag"/> saves it.
    /// </summary>
    [ObservableProperty]
    public partial double SliderPosition { get; set; }

    /// <summary>The width the slider stands for: <c>760 px</c> or <c>Full</c>.</summary>
    [ObservableProperty]
    public partial string Readout { get; private set; } = "";

    /// <summary>
    /// True while the shown width is a stored px width wider than the pane's text space: the readout shows that width, and the
    /// view draws the knob pinned at the slider's right end, where an enabled slider's position is 1. Moving the slider clears
    /// it; showing a segment's width sets it again.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWiderThanWindow { get; private set; }

    /// <summary>
    /// Where the selected segment inherits its width from, shown as "Inherited from {folder}" under the segments: a folder's
    /// name, or All documents; null when the selected segment holds the width itself.
    /// </summary>
    [ObservableProperty]
    public partial string? InheritedFrom { get; private set; }

    /// <summary>The foot's message, <see cref="SaveFailedMessage"/> for 4 s after a failed save; null when there is none.</summary>
    [ObservableProperty]
    public partial string? FootMessage { get; private set; }

    /// <summary>True once the window shows another document, or none, or the popover was closed; it then does nothing.</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>True when Reset to inherited has an override to remove: the selected segment is a folder with its own width.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool CanReset { get; private set; }

    /// <summary>
    /// True when a width saved for the selected segment reaches the shown document. False when a folder nearer the document
    /// holds a width of its own; a drag then leaves the page as it is, and <see cref="EndDrag"/> still saves the segment.
    /// </summary>
    [ObservableProperty]
    public partial bool DragReachesDocument { get; private set; }

    /// <summary>
    /// How many folders at or below the group root hold a width of their own: what Clear all overrides here removes. Read when
    /// the popover opens and after each change it makes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearAllQuestion))]
    [NotifyCanExecuteChangedFor(nameof(AskClearAllCommand))]
    public partial int ClearAllCount { get; private set; }

    /// <summary>True while Clear all overrides here asks first: the link is replaced by <see cref="ClearAllQuestion"/>, Clear and Cancel.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClearAll { get; private set; }

    /// <summary>What Clear all overrides here asks first: <c>Clear 3 saved widths under yamv?</c>.</summary>
    public string ClearAllQuestion =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Clear {ClearAllCount} saved {(ClearAllCount == 1 ? "width" : "widths")} under {_scopes.ClearAllName}?"
        );

    /// <summary>True when the pane leaves at least 480 px for the text, so the slider can move.</summary>
    public bool IsSliderEnabled => _textSpace >= ContentWidth.MinimumPx;

    /// <summary><see cref="NarrowCaption"/> when the slider is disabled; null when it is enabled.</summary>
    public string? SliderCaption => IsSliderEnabled ? null : NarrowCaption;

    /// <summary>
    /// Ends a drag (the pointer was released, or a key moved the slider): saves the slider's width for the selected segment,
    /// so a segment that inherited its width gets its own. A save that finds the width already the selected segment's when
    /// it runs saves nothing, and the page gets the document's width again. Until the save has run, a read or a change that
    /// lands leaves the slider at the drag's width. Ending no drag does nothing.
    /// </summary>
    public void EndDrag()
    {
        if (IsStale || !IsSliderEnabled || !_moved)
        {
            return;
        }

        _moved = false;
        _unsavedDrags++;
        ContentWidth width = _width;
        _host.Window.QueueWidthWork(() => SaveAsync(width));
    }

    /// <summary>
    /// Moves the slider as a key does: one 40 px grid step narrower or wider (the Ctrl/Cmd+[ and ] step), or to 480 px or Full.
    /// Like a pointer move it changes the page live and saves nothing; <see cref="EndDrag"/> on the key's release saves it.
    /// </summary>
    public void StepSlider(SliderStep step)
    {
        if (IsStale || !IsSliderEnabled)
        {
            return;
        }

        ContentWidth width = step switch
        {
            SliderStep.Narrower => _width.StepDown(_textSpace),
            SliderStep.Wider => _width.StepUp(_textSpace),
            SliderStep.Narrowest => ContentWidth.FromPx(ContentWidth.MinimumPx),
            SliderStep.Full => ContentWidth.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown slider step."),
        };
        _moved = true;
        IsWiderThanWindow = false;
        if (width == _width)
        {
            return;
        }

        PlaceSlider(width);
        if (DragReachesDocument)
        {
            SendToPage(width);
        }
    }

    /// <summary>Asks before Clear all overrides here: shows <see cref="ClearAllQuestion"/> with Clear and Cancel.</summary>
    [RelayCommand(CanExecute = nameof(CanClearAll))]
    public void AskClearAll()
    {
        if (!IsStale)
        {
            IsConfirmingClearAll = true;
        }
    }

    /// <summary>Puts the Clear all overrides here link back without clearing anything.</summary>
    [RelayCommand]
    public void CancelClearAll() => IsConfirmingClearAll = false;

    /// <summary>True when Clear all overrides here has a folder width to remove.</summary>
    public bool CanClearAll => ClearAllCount > 0;

    /// <summary>Selects <paramref name="segment"/>, showing its width; later saves go to it.</summary>
    [RelayCommand]
    public void SelectSegment(WidthScope segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (IsStale)
        {
            return;
        }

        _host.Window.QueueWidthWork(() => ShowSegmentAsync(segment));
    }

    /// <summary>Removes the selected segment's own width, so it inherits again, and shows the document's width that results.</summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    public void Reset()
    {
        if (IsStale || _scopes.Selected.Folder is not string folder)
        {
            return;
        }

        _host.Window.QueueWidthWork(() => ChangeAndShowAsync(() => Widths.Remove(folder), $"remove the content width of {folder}"));
    }

    /// <summary>Removes every folder width at or below the group root, and shows the document's width that results.</summary>
    [RelayCommand]
    public void ClearAll()
    {
        IsConfirmingClearAll = false;
        if (IsStale)
        {
            return;
        }

        string root = _scopes.ClearAllRoot;
        _host.Window.QueueWidthWork(() => ChangeAndShowAsync(() => Widths.ClearAtOrBelow(root), $"clear the content widths at and below {root}"));
    }

    /// <summary>
    /// Closes the popover: it goes stale, its foot timer stops and the window forgets it. A drag that never ended saves
    /// nothing, and the page gets the document's stored width back.
    /// </summary>
    internal void Close()
    {
        MarkStale();
        _host.Window.ForgetContentWidthPopover(this);
    }

    /// <summary>
    /// Marks the popover stale: its document is no longer the window's shown document, or the popover closed. A drag that
    /// never ended saves nothing, and the page, while it still shows the document, gets the document's stored width back.
    /// </summary>
    internal void MarkStale()
    {
        bool dragging = _moved && !IsStale;
        IsStale = true;
        _moved = false;
        _footTimer?.Dispose();
        _footTimer = null;
        if (dragging)
        {
            QueueDocumentWidthResend();
        }
    }

    /// <summary>Reads the selected segment's width again, after a change made outside the popover (a Ctrl/Cmd+[ or ] step).</summary>
    internal void Refresh()
    {
        if (!IsStale)
        {
            _host.Window.QueueWidthWork(() => ShowSegmentAsync(_scopes.Selected));
        }
    }

    private WidthStore Widths => _host.Services.Widths;

    /// <summary>True while a drag is in progress or an ended drag's save has not run: the slider stays at the drag's width.</summary>
    private bool HoldsSlider => _moved || _unsavedDrags > 0;

    partial void OnSliderPositionChanged(double value)
    {
        if (_placingSlider || IsStale || !IsSliderEnabled)
        {
            return;
        }

        ContentWidth width = ContentWidth.FromSlider(value, _textSpace);
        _moved = true;
        IsWiderThanWindow = false;
        if (width == _width)
        {
            return;
        }

        _width = width;
        Readout = ReadoutOf(width);
        if (DragReachesDocument)
        {
            SendToPage(width);
        }
    }

    private async Task SaveAsync(ContentWidth width)
    {
        WidthScope segment = _scopes.Selected;
        string path = DocumentPath;
        // A read or a step that landed since the drag ended may have made the width the selected segment's already.
        bool unchanged = width == _storedWidth;
        (bool saved, (ContentWidth Width, WidthSource Source) document) result;
        try
        {
            result = await Task.Run(() =>
            {
                bool written = unchanged || (segment.Folder is string folder ? Widths.Set(folder, width) : Widths.SetDefault(width));
                return (written, Widths.Resolve(path));
            });
        }
        finally
        {
            _unsavedDrags--;
        }

        if (!result.saved)
        {
            OnSaveFailed($"Cannot save the content width {ReadoutOf(width)} for {segment.Label} of {path}.", path, result.document);
            return;
        }

        if (IsStale)
        {
            return;
        }

        if (!unchanged)
        {
            ShowSegment(_scopes.Select(segment, new WidthSource(segment.Folder)), width, result.document.Source);
        }

        if (!HoldsSlider)
        {
            SendToPage(result.document.Width);
        }

        if (!unchanged)
        {
            await ReadClearAllCountAsync();
        }
    }

    /// <summary>
    /// Logs a save that failed. While the popover is open the slider springs back to the saved width; either way the page,
    /// while it still shows the document, gets the document's stored width back in place of the drag's preview.
    /// </summary>
    private void OnSaveFailed(string logLine, string path, (ContentWidth Width, WidthSource Source) document)
    {
        OnChangeFailed(logLine);
        if (IsStale)
        {
            if (_host.Window.IsShowingContentWidthOf(path))
            {
                SendToPage(document.Width);
            }

            return;
        }

        _moved = false;
        ShowSegment(_scopes, _storedWidth, document.Source);
        SendToPage(document.Width);
    }

    private async Task ShowSegmentAsync(WidthScope segment)
    {
        string path = DocumentPath;
        (ContentWidth width, WidthSource source, WidthSource documentSource) = await Task.Run(() =>
        {
            (ContentWidth resolved, WidthSource from) = segment.Folder is string folder
                ? Widths.ResolveFolder(folder)
                : (Widths.Default, WidthSource.AppDefault);
            return (resolved, from, Widths.Resolve(path).Source);
        });
        // A queued Reset or Clear all may have removed the segment since it was picked.
        if (!IsStale && _scopes.Contains(segment))
        {
            ShowSegment(_scopes.Select(segment, source), width, documentSource);
        }
    }

    /// <summary>Runs a store change, then shows the document's width and segments as they result; called on the UI thread.</summary>
    private async Task ChangeAndShowAsync(Func<bool> change, string what)
    {
        string path = DocumentPath;
        (bool changed, ContentWidth width, WidthSource source) = await Task.Run(() =>
        {
            bool written = change();
            (ContentWidth resolved, WidthSource from) = Widths.Resolve(path);
            return (written, resolved, from);
        });
        if (!changed)
        {
            OnChangeFailed($"Cannot {what} for {path}.");
            return;
        }

        if (IsStale)
        {
            return;
        }

        ShowSegment(_host.ScopesFor(source), width, source);
        if (!HoldsSlider)
        {
            SendToPage(width);
        }

        await ReadClearAllCountAsync();
    }

    /// <summary>Reads how many folder widths Clear all overrides here would remove; a count of none puts the link back.</summary>
    private async Task ReadClearAllCountAsync()
    {
        string root = _scopes.ClearAllRoot;
        int count = await Task.Run(() => Widths.CountAtOrBelow(root));
        if (IsStale)
        {
            return;
        }

        ClearAllCount = count;
        if (count == 0)
        {
            IsConfirmingClearAll = false;
        }
    }

    /// <summary>
    /// Queues a send of the document's stored width to the page, which a drag may have moved. Nothing is queued, and nothing
    /// sent, once the window no longer shows the document.
    /// </summary>
    private void QueueDocumentWidthResend()
    {
        string path = DocumentPath;
        if (!_host.Window.IsShowingContentWidthOf(path))
        {
            return;
        }

        _host.Window.QueueWidthWork(async () =>
        {
            ContentWidth width = await Task.Run(() => Widths.Resolve(path).Width);
            if (_host.Window.IsShowingContentWidthOf(path))
            {
                SendToPage(width);
            }
        });
    }

    /// <summary>
    /// Shows <paramref name="scopes"/>, whose selected segment's width is <paramref name="storedWidth"/>, for a document whose
    /// width comes from <paramref name="documentSource"/>. The slider moves to that width unless a drag is in progress.
    /// </summary>
    private void ShowSegment(WidthScopes scopes, ContentWidth storedWidth, WidthSource documentSource)
    {
        _scopes = scopes;
        _storedWidth = storedWidth;
        Segments = scopes.Segments;
        SelectedSegment = scopes.Selected;
        InheritedFrom = scopes.InheritedFrom;
        CanReset = scopes.CanReset;
        DragReachesDocument = scopes.Reaches(documentSource);
        if (!HoldsSlider)
        {
            PlaceSlider(storedWidth);
        }
    }

    /// <summary>
    /// Moves the slider to <paramref name="width"/> without starting a drag. A px width wider than the text space is marked
    /// <see cref="IsWiderThanWindow"/>, and an enabled slider is placed at its right end (1) for it; a disabled one stays where
    /// <see cref="ContentWidth.ToSlider"/> puts it.
    /// </summary>
    private void PlaceSlider(ContentWidth width)
    {
        _width = width;
        Readout = ReadoutOf(width);
        IsWiderThanWindow = width.Px > _textSpace;
        _placingSlider = true;
        try
        {
            SliderPosition = IsWiderThanWindow && IsSliderEnabled ? 1 : width.ToSlider(_textSpace);
        }
        finally
        {
            _placingSlider = false;
        }
    }

    /// <summary>
    /// Logs a store change that failed, and shows <see cref="SaveFailedMessage"/> for 4 s; once the popover is stale (closed),
    /// a toast in the window says so instead.
    /// </summary>
    private void OnChangeFailed(string logLine)
    {
        _host.Services.Log.LogWarning(logLine);
        if (IsStale)
        {
            _host.Window.Toasts.Show(ToastKind.Warning, DocumentWindowViewModel.WidthNotSavedToast);
            return;
        }

        FootMessage = SaveFailedMessage;
        _footTimer?.Dispose();
        int message = ++_footMessages;
        _footTimer = _host.Services.Time.CreateTimer(_ => OnFootMessageDue(message), null, FootMessageTime, Timeout.InfiniteTimeSpan);
    }

    private void OnFootMessageDue(int message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ClearFootMessage(message);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ClearFootMessage(message));
        }
    }

    private void ClearFootMessage(int message)
    {
        if (message != _footMessages || IsStale)
        {
            return;
        }

        FootMessage = null;
        _footTimer?.Dispose();
        _footTimer = null;
    }

    private void SendToPage(ContentWidth width) => _ = SendToPageAsync(width.Px);

    private async Task SendToPageAsync(double? px)
    {
        try
        {
            await _host.Surface.SetContentWidthAsync(px);
        }
        // The send is not awaited by anyone, so its failure is logged here or nowhere.
        catch (Exception exception)
        {
            _host.Services.Log.LogError($"Cannot send the content width of {DocumentPath} to the page.", exception);
        }
    }

    private static string ReadoutOf(ContentWidth width) => width.Px is double px ? string.Create(CultureInfo.InvariantCulture, $"{px:0} px") : "Full";
}

/// <summary>How a key moves the content-width slider.</summary>
public enum SliderStep
{
    /// <summary>One 40 px grid step narrower: Left, Down or Page Down.</summary>
    Narrower,

    /// <summary>One 40 px grid step wider: Right, Up or Page Up.</summary>
    Wider,

    /// <summary>To 480 px: Home.</summary>
    Narrowest,

    /// <summary>To Full: End.</summary>
    Full,
}

/// <summary>What a <see cref="ContentWidthViewModel"/> uses of its document window.</summary>
/// <param name="DocumentPath">The shown document.</param>
/// <param name="Surface">The view that draws it.</param>
/// <param name="Services">The window's width store, clock and log.</param>
/// <param name="Window">The window: its width-work queue, and whether it still shows the document.</param>
/// <param name="ScopesFor">The document's Saved for segments when its width comes from a given source.</param>
internal sealed record ContentWidthHost(
    string DocumentPath,
    IDocumentSurface Surface,
    DocumentWindowServices Services,
    DocumentWindowViewModel Window,
    Func<WidthSource, WidthScopes> ScopesFor
);
