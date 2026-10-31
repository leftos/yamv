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
/// for 4 s, and the page gets the stored width back even when the popover has gone stale. A store that throws is recovered
/// the same way, with <see cref="ReadFailedMessage"/> for a read. <see cref="UpdatePaneWidth"/> re-measures the popover when
/// the document pane resizes. Once the window's document changes, or <see cref="Close"/> is called, the view model is stale
/// and does nothing more. Every member is called on the UI thread.
/// </summary>
public sealed partial class ContentWidthViewModel : ViewModelBase
{
    /// <summary>The slider's caption when the pane leaves less than 480 px for the text.</summary>
    public const string NarrowCaption = "The window is narrower than 480 px";

    /// <summary>The foot's message after a width could not be saved.</summary>
    public const string SaveFailedMessage = "Could not save the width; see the log.";

    /// <summary>The foot's message after the store could not be read.</summary>
    public const string ReadFailedMessage = "Could not read the width; see the log.";

    private static readonly TimeSpan FootMessageTime = TimeSpan.FromSeconds(4);

    private readonly ContentWidthHost _host;
    private double _textSpace;
    private WidthScopes _scopes;
    private ContentWidth _storedWidth;
    private ContentWidth _width;
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

    /// <summary>
    /// The foot's message, <see cref="SaveFailedMessage"/> or <see cref="ReadFailedMessage"/>, for 4 s after a store failure;
    /// null when there is none.
    /// </summary>
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

    /// <summary>True while a slider drag is in progress: the slider moved, and the drag has not ended or been cancelled.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; private set; }

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
    /// Ends a drag (the pointer was released, or a key moved the slider): saves the slider's width for the segment selected
    /// now, so a segment that inherited its width gets its own, and a Reset or a pick still queued when the drag ends does not
    /// move the save. A save that finds the width already the segment's when it runs saves nothing, but shows the segment as
    /// the store now has it and reads the Clear all count again, and the page gets the document's width back. Until the save
    /// has run, a read or a change that lands leaves the slider at the drag's width. Ending no drag does nothing.
    /// </summary>
    public void EndDrag()
    {
        if (IsStale || !IsSliderEnabled || !IsDragging)
        {
            return;
        }

        // The drag's width and segment are taken and its save counted before the drag ends, so a re-span the end prompts
        // leaves the slider at the drag's width.
        _unsavedDrags++;
        ContentWidth width = _width;
        WidthScope segment = _scopes.Selected;
        IsDragging = false;
        QueueGuarded(
            () => SaveAsync(width, segment),
            $"save the content width {ReadoutOf(width)} for {segment.Label} of {DocumentPath}",
            read: false
        );
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
        IsDragging = true;
        IsWiderThanWindow = false;
        if (width == _width)
        {
            return;
        }

        PlaceSlider(width);
        if (DragReachesDocument)
        {
            SendPreviewToPage(width);
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

        QueueGuarded(() => ShowSegmentAsync(segment), ReadOf(segment), read: true);
    }

    /// <summary>Removes the selected segment's own width, so it inherits again, and shows the document's width that results.</summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    public void Reset()
    {
        if (IsStale || _scopes.Selected.Folder is not string folder)
        {
            return;
        }

        QueueGuarded(
            () => ChangeAndShowAsync(() => Widths.Remove(folder), $"remove the content width of {folder}"),
            $"remove the content width of {folder} in {DocumentPath}",
            read: false
        );
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
        QueueGuarded(
            () => ChangeAndShowAsync(() => Widths.ClearAtOrBelow(root), $"clear the content widths at and below {root}"),
            $"clear the content widths at and below {root} in {DocumentPath}",
            read: false
        );
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
        bool dragging = IsDragging && !IsStale;
        IsStale = true;
        IsDragging = false;
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
            QueueGuarded(() => ShowSegmentAsync(_scopes.Selected), ReadOf(_scopes.Selected), read: true);
        }
    }

    /// <summary>
    /// Re-measures the popover for a document pane <paramref name="paneWidth"/> px wide: the slider spans the text space the
    /// new pane leaves, so a stored width that no longer fits is marked wider than the window and pinned at the slider's end,
    /// and a pane that leaves less than <see cref="ContentWidth.MinimumPx"/> disables the slider with its caption. A drag in
    /// progress keeps the slider where the drag holds it, unless the resize disables the slider: that ends the drag without
    /// saving and gives the page the stored width. A stale popover, and a width that is not a pane's, change nothing.
    /// </summary>
    public void UpdatePaneWidth(double paneWidth)
    {
        if (IsStale || !double.IsFinite(paneWidth) || paneWidth <= 0)
        {
            return;
        }

        _textSpace = ContentWidth.TextSpace(paneWidth);
        OnPropertyChanged(nameof(IsSliderEnabled));
        OnPropertyChanged(nameof(SliderCaption));
        if (IsDragging && !IsSliderEnabled)
        {
            IsDragging = false;
            if (DragReachesDocument)
            {
                SendCommittedToPage(_storedWidth);
            }
        }

        if (!HoldsSlider)
        {
            PlaceSlider(_storedWidth);
        }
    }

    private IContentWidthStore Widths => _host.Store;

    /// <summary>True while a drag is in progress or an ended drag's save has not run: the slider stays at the drag's width.</summary>
    private bool HoldsSlider => IsDragging || _unsavedDrags > 0;

    partial void OnSliderPositionChanged(double value)
    {
        if (_placingSlider || IsStale || !IsSliderEnabled)
        {
            return;
        }

        ContentWidth width = ContentWidth.FromSlider(value, _textSpace);
        IsDragging = true;
        IsWiderThanWindow = false;
        if (width == _width)
        {
            return;
        }

        _width = width;
        Readout = ReadoutOf(width);
        if (DragReachesDocument)
        {
            SendPreviewToPage(width);
        }
    }

    private async Task SaveAsync(ContentWidth width, WidthScope segment)
    {
        // Every line is inside the try, whose finally gives back the count EndDrag took.
        (ContentWidth Width, WidthSource Source) shown;
        (bool saved, bool wrote, (ContentWidth Width, WidthSource Source) document) result;
        try
        {
            string path = DocumentPath;
            (shown, result) = await Task.Run(() =>
            {
                // One read of the store says where the segment takes its width from now, so a save that finds the dragged width
                // already there writes nothing, and shows the segment as another window left it even when it now inherits.
                (ContentWidth stored, WidthSource source) = ResolveSegment(segment);
                bool same = width == stored;
                bool wrote = !same && (segment.Folder is string folder ? Widths.Set(folder, width) : Widths.SetDefault(width));
                return (same ? (stored, source) : (width, new WidthSource(segment.Folder)), (same || wrote, wrote, Widths.Resolve(path)));
            });
        }
        finally
        {
            _unsavedDrags--;
        }

        if (!result.saved)
        {
            OnSaveFailed($"Cannot save the content width {ReadoutOf(width)} for {segment.Label} of {DocumentPath}.", DocumentPath, result.document);
            return;
        }

        if (result.wrote)
        {
            _host.Window.NoteWidthWrite();
            ReconcileShownDocument();
        }

        if (IsStale)
        {
            return;
        }

        // A Reset or Clear all queued before the release may have rebuilt the segments without the saved segment; the save
        // shows it again, with the width and source it has now.
        WidthScopes scopes = _scopes.Contains(segment) ? _scopes : _host.ScopesFor(new WidthSource(segment.Folder));
        ShowSegment(scopes.Select(segment, shown.Source), shown.Width, result.document.Source);

        if (!HoldsSlider)
        {
            SendCommittedToPage(result.document.Width);
        }

        await ReadClearAllCountAsync();
    }

    /// <summary>
    /// Where <paramref name="segment"/> takes its width from, and that width; read from the store. Called off the UI thread.
    /// </summary>
    private (ContentWidth Width, WidthSource Source) ResolveSegment(WidthScope segment) =>
        segment.Folder is string folder ? Widths.ResolveFolder(folder) : (Widths.Default, WidthSource.AppDefault);

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
                SendCommittedToPage(document.Width);
            }

            return;
        }

        IsDragging = false;
        ShowSegment(_scopes, _storedWidth, document.Source);
        SendCommittedToPage(document.Width);
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

        _host.Window.NoteWidthWrite();
        ReconcileShownDocument();
        if (IsStale)
        {
            return;
        }

        ShowSegment(_host.ScopesFor(source), width, source);
        if (!HoldsSlider)
        {
            SendCommittedToPage(width);
        }

        await ReadClearAllCountAsync();
    }

    /// <summary>
    /// Reads how many folder widths Clear all overrides here would remove; a count of none puts the link back. A store that
    /// throws leaves the count as it was and the popover open, and shows <see cref="ReadFailedMessage"/> in the foot.
    /// </summary>
    private async Task ReadClearAllCountAsync()
    {
        string root = _scopes.ClearAllRoot;
        int count;
        try
        {
            count = await Task.Run(() => Widths.CountAtOrBelow(root));
        }
        catch (Exception exception)
        {
            OnStoreFailed($"read the saved-width count of {DocumentPath}", read: true, exception);
            return;
        }

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
    /// sent, once the window no longer shows the document, or a newer popover whose slider is held owns the page's width.
    /// </summary>
    private void QueueDocumentWidthResend()
    {
        string path = DocumentPath;
        if (!_host.Window.IsShowingContentWidthOf(path))
        {
            return;
        }

        QueueGuarded(
            async () =>
            {
                ContentWidth width = await Task.Run(() => Widths.Resolve(path).Width);
                // A newer popover has the page now: while its slider is held the page shows its drag, not the stored width.
                if (!IsSupersededByNewerPopover() && _host.Window.IsShowingContentWidthOf(path))
                {
                    SendCommittedToPage(width);
                }
            },
            $"resend the content width of {path}",
            read: false
        );
    }

    /// <summary>Queues store work behind the window's other width changes, recovering a store exception instead of the queue.</summary>
    /// <param name="work">The store work.</param>
    /// <param name="operation">What the work does, for the log when it throws; it reads after "Cannot ".</param>
    /// <param name="read">True for a read, whose failure shows <see cref="ReadFailedMessage"/>; false for a write.</param>
    private void QueueGuarded(Func<Task> work, string operation, bool read) =>
        _host.Window.QueueWidthWork(() => RunGuardedAsync(work, operation, read));

    private async Task RunGuardedAsync(Func<Task> work, string operation, bool read)
    {
        try
        {
            await work();
        }
        // The queue only logs and toasts; a store exception is this popover's to recover from.
        catch (Exception exception)
        {
            OnStoreFailed(operation, read, exception);
        }
    }

    /// <summary>The operation a failed read of <paramref name="segment"/>'s width names in the log.</summary>
    private string ReadOf(WidthScope segment) => $"read the width of {segment.Label} in {DocumentPath}";

    /// <summary>
    /// Logs a store exception with the operation it stopped and reports it: a write recovers as a failed save does (the drag
    /// ends, the slider springs back to the stored width, and the page gets that width again when the selected segment reaches
    /// the document), while a read changed neither and only shows its message. A stale popover shows the window's toast.
    /// </summary>
    private void OnStoreFailed(string operation, bool read, Exception exception)
    {
        _host.Services.Log.LogError($"Cannot {operation}.", exception);
        if (read)
        {
            ShowReadFailed();
            return;
        }

        if (IsStale)
        {
            OnStaleWriteFailed();
            return;
        }

        IsDragging = false;
        if (!HoldsSlider)
        {
            PlaceSlider(_storedWidth);
            if (DragReachesDocument)
            {
                SendCommittedToPage(_storedWidth);
            }
        }

        ShowFootMessage(SaveFailedMessage);
    }

    /// <summary>
    /// Reports a failed read, which moved neither the slider nor the page: <see cref="ReadFailedMessage"/> in the foot for 4 s,
    /// or the window's toast once the popover is stale.
    /// </summary>
    private void ShowReadFailed()
    {
        if (IsStale)
        {
            ShowStaleToast();
            return;
        }

        ShowFootMessage(ReadFailedMessage);
    }

    /// <summary>
    /// Recovers a write that failed after the popover went stale: a drag that never ended leaves its preview on the page, so the
    /// page gets the stored width back, and the window's toast stands in for the foot message it can no longer show.
    /// </summary>
    private void OnStaleWriteFailed()
    {
        if (DragReachesDocument && _host.Window.IsShowingContentWidthOf(DocumentPath) && !IsSupersededByNewerPopover())
        {
            SendCommittedToPage(_storedWidth);
        }

        ShowStaleToast();
    }

    private void ShowStaleToast() => _host.Window.Toasts.Show(ToastKind.Warning, DocumentWindowViewModel.WidthNotSavedToast);

    /// <summary>
    /// True when the window shows a newer popover whose slider is held: its drag's preview or its pending save owns the page's
    /// width, so the stored width this popover would send must not overwrite it.
    /// </summary>
    private bool IsSupersededByNewerPopover() =>
        _host.Window.ContentWidthPopover is { } current && !ReferenceEquals(current, this) && current.HoldsSlider;

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
    /// Moves the slider to <paramref name="width"/> without starting a drag. A px width as wide as the text space or wider is marked
    /// <see cref="IsWiderThanWindow"/>, and an enabled slider is placed at its right end (1) for it; a disabled one stays where
    /// <see cref="ContentWidth.ToSlider"/> puts it.
    /// </summary>
    private void PlaceSlider(ContentWidth width)
    {
        _width = width;
        Readout = ReadoutOf(width);
        IsWiderThanWindow = width.Px >= _textSpace;
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

        ShowFootMessage(SaveFailedMessage);
    }

    /// <summary>Shows <paramref name="message"/> in the foot for 4 s, replacing a message still showing.</summary>
    private void ShowFootMessage(string message)
    {
        FootMessage = message;
        _footTimer?.Dispose();
        int due = ++_footMessages;
        _footTimer = _host.Services.Time.CreateTimer(_ => OnFootMessageDue(due), null, FootMessageTime, Timeout.InfiniteTimeSpan);
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

    /// <summary>Sends a width a drag or a slider key is previewing; it is transient, so the window records no shown width.</summary>
    private void SendPreviewToPage(ContentWidth width) => _ = SendPreviewToPageAsync(width.Px);

    /// <summary>Sends a width the store holds, which the window records as the width the page shows.</summary>
    private void SendCommittedToPage(ContentWidth width) => _host.Window.SendPageWidth(DocumentPath, width.Px);

    /// <summary>
    /// Asks the window to bring the document it shows to the width the store resolves after this popover's write. A live popover
    /// owns the page itself and sends the committed width, so this is for a write whose document stopped being the shown one (a
    /// sibling in the same folder follows the change) or whose popover closed while the document stayed shown.
    /// </summary>
    private void ReconcileShownDocument()
    {
        if (!_host.Window.IsShowingContentWidthOf(DocumentPath) || (IsStale && !IsSupersededByNewerPopover()))
        {
            _host.Window.QueueShownWidthReconcile();
        }
    }

    private async Task SendPreviewToPageAsync(double? px)
    {
        bool ran;
        try
        {
            ran = await _host.Surface.SetContentWidthAsync(px);
        }
        // The send is not awaited by anyone, so its failure is logged here or nowhere.
        catch (Exception exception)
        {
            _host.Services.Log.LogError($"Cannot send the content width of {DocumentPath} to the page.", exception);
            ran = false;
        }

        if (!ran)
        {
            _host.Window.Toasts.Show(ToastKind.Warning, DocumentWindowViewModel.WidthNotShownToast);
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

/// <summary>
/// The width store as the popover uses it: the calls that read or change <c>widths.json</c>, so a test can hand the popover
/// one that throws where a file store cannot.
/// </summary>
internal interface IContentWidthStore
{
    /// <summary>The app default: the width of a document no folder at or above has a width for.</summary>
    ContentWidth Default { get; }

    /// <summary>The width for <paramref name="documentPath"/>, and where it comes from.</summary>
    (ContentWidth Width, WidthSource Source) Resolve(string documentPath);

    /// <summary>The width a document in <paramref name="folder"/> uses, and where it comes from.</summary>
    (ContentWidth Width, WidthSource Source) ResolveFolder(string folder);

    /// <summary>The width <paramref name="folder"/> holds itself, or <see langword="null"/> when it inherits one.</summary>
    ContentWidth? Get(string folder);

    /// <summary>Gives <paramref name="folder"/> its own width.</summary>
    bool Set(string folder, ContentWidth width);

    /// <summary>Sets the app default.</summary>
    bool SetDefault(ContentWidth width);

    /// <summary>Removes <paramref name="folder"/>'s own width.</summary>
    bool Remove(string folder);

    /// <summary>Removes the width of <paramref name="groupRoot"/> and of every folder inside it.</summary>
    bool ClearAtOrBelow(string groupRoot);

    /// <summary>How many folders at or below <paramref name="groupRoot"/> hold a width of their own.</summary>
    int CountAtOrBelow(string groupRoot);
}

/// <summary>The stored widths, as <see cref="ContentWidthViewModel"/> uses them.</summary>
/// <param name="store">The file-backed store every instance shares.</param>
internal sealed class WidthStoreAdapter(WidthStore store) : IContentWidthStore
{
    public ContentWidth Default => store.Default;

    public (ContentWidth Width, WidthSource Source) Resolve(string documentPath) => store.Resolve(documentPath);

    public (ContentWidth Width, WidthSource Source) ResolveFolder(string folder) => store.ResolveFolder(folder);

    public ContentWidth? Get(string folder) => store.Get(folder);

    public bool Set(string folder, ContentWidth width) => store.Set(folder, width);

    public bool SetDefault(ContentWidth width) => store.SetDefault(width);

    public bool Remove(string folder) => store.Remove(folder);

    public bool ClearAtOrBelow(string groupRoot) => store.ClearAtOrBelow(groupRoot);

    public int CountAtOrBelow(string groupRoot) => store.CountAtOrBelow(groupRoot);
}

/// <summary>What a <see cref="ContentWidthViewModel"/> uses of its document window.</summary>
/// <param name="DocumentPath">The shown document.</param>
/// <param name="Surface">The view that draws it.</param>
/// <param name="Services">The window's clock and log.</param>
/// <param name="Store">The widths the popover reads and changes.</param>
/// <param name="Window">The window: its width-work queue, and whether it still shows the document.</param>
/// <param name="ScopesFor">The document's Saved for segments when its width comes from a given source.</param>
internal sealed record ContentWidthHost(
    string DocumentPath,
    IDocumentSurface Surface,
    DocumentWindowServices Services,
    IContentWidthStore Store,
    DocumentWindowViewModel Window,
    Func<WidthSource, WidthScopes> ScopesFor
);
