using Yamv.App.Controls;
using Yamv.Core.Session;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IDocumentSurface"/> that records what it was asked to show, and raises scroll changes on demand.</summary>
public sealed class FakeDocumentSurface : IDocumentSurface
{
    public event EventHandler<ScrollPosition>? ScrollChanged;

    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    public event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    public event EventHandler<bool>? DragOverChanged;

    public event EventHandler? ViewportWidthChanged;

    /// <summary>The width <see cref="RaiseViewportWidth"/> last reported; null again once a show starts a new render.</summary>
    public double? ViewportWidth { get; private set; }

    /// <summary>Every <see cref="ShowAsync"/> call, oldest first.</summary>
    public List<ShownDocument> Shown { get; } = [];

    /// <summary>The width each <see cref="ShowAsync"/> call showed its document at, oldest first; null is Full.</summary>
    public List<double?> ShownWidths => [.. Shown.Select(shown => shown.ContentWidth)];

    /// <summary>Every width passed to <see cref="SetContentWidthAsync"/>, oldest first; null is Full.</summary>
    public List<double?> ContentWidths { get; } = [];

    /// <summary>Every value passed to <see cref="SetCardOpenAsync"/>, oldest first.</summary>
    public List<bool> CardOpens { get; } = [];

    /// <summary>
    /// Every recorded call in the order it came: a <see cref="ShownDocument"/>, a <see cref="ContentWidthSet"/>, a
    /// <see cref="ThemeCssSet"/> or a <see cref="CardOpenSet"/>.
    /// </summary>
    public List<object> Calls { get; } = [];

    /// <summary>
    /// When set, the next show or content width call is recorded and then returns this source's task, which the test
    /// completes; <see cref="CallHeld"/> completes when that call arrives. It holds one call, then clears itself.
    /// </summary>
    public TaskCompletionSource? HoldNextCall { get; set; }

    /// <summary>Completes when a call <see cref="HoldNextCall"/> holds has arrived.</summary>
    public TaskCompletionSource CallHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called after each <see cref="ShowAsync"/> is recorded.</summary>
    public Action<ShownDocument>? OnShown { get; set; }

    /// <summary>Every css passed to <see cref="SetThemeCssAsync"/>, oldest first.</summary>
    public List<string> ThemeCss { get; } = [];

    public Task ShowAsync(DocumentShow show)
    {
        var shown = new ShownDocument(
            show.Document.Html,
            show.DocumentPath,
            show.GroupRoot,
            show.Scroll,
            show.Anchor,
            show.Fragment,
            show.ContentWidth
        );
        ViewportWidth = null;
        Shown.Add(shown);
        Calls.Add(shown);
        OnShown?.Invoke(shown);
        return Completion();
    }

    public Task SetThemeCssAsync(string css)
    {
        ThemeCss.Add(css);
        Calls.Add(new ThemeCssSet(css));
        return Task.CompletedTask;
    }

    /// <summary>When set, <see cref="SetContentWidthAsync"/> throws it instead of recording the width.</summary>
    public Exception? ContentWidthFailure { get; set; }

    /// <summary>Whether the page scripts fail: both width and fragment calls record their call and then report it.</summary>
    public bool ScriptsFail { get; set; }

    public async Task<bool> SetContentWidthAsync(double? px)
    {
        if (ContentWidthFailure is { } failure)
        {
            throw failure;
        }

        ContentWidths.Add(px);
        Calls.Add(new ContentWidthSet(px));
        await Completion();
        return !ScriptsFail;
    }

    public Task SetCardOpenAsync(bool open)
    {
        CardOpens.Add(open);
        Calls.Add(new CardOpenSet(open));
        return Task.CompletedTask;
    }

    private Task Completion()
    {
        if (HoldNextCall is not { } hold)
        {
            return Task.CompletedTask;
        }

        HoldNextCall = null;
        CallHeld.TrySetResult();
        return hold.Task;
    }

    /// <summary>Every fragment passed to <see cref="ScrollToFragmentAsync"/>, oldest first.</summary>
    public List<string> ScrolledFragments { get; } = [];

    /// <summary>When set, <see cref="ScrollToFragmentAsync"/> throws it instead of recording the fragment.</summary>
    public Exception? ScrollFailure { get; set; }

    public async Task<bool> ScrollToFragmentAsync(string fragment)
    {
        if (ScrollFailure is { } failure)
        {
            throw failure;
        }

        ScrolledFragments.Add(fragment);
        return !ScriptsFail;
    }

    public Task OpenFindAsync() => Task.CompletedTask;

    public Task<FindResult> FindAsync(string query, FindDirection direction) => Task.FromResult(new FindResult(0, 0));

    public Task ClearFindAsync() => Task.CompletedTask;

    /// <summary>Raises <see cref="ScrollChanged"/> as the page would: the fraction and the anchor, if any.</summary>
    public void RaiseScroll(double fraction, ScrollAnchor? anchor) => ScrollChanged?.Invoke(this, new ScrollPosition(fraction, anchor));

    /// <summary>Raises <see cref="KeyForwarded"/> as the page would for a shortcut pressed while it had focus.</summary>
    public void RaiseKey(KeyForwardedEventArgs key) => KeyForwarded?.Invoke(this, key);

    /// <summary>Raises <see cref="LinkActivated"/> as the page would for an activated link.</summary>
    public void RaiseLink(LinkActivatedEventArgs link) => LinkActivated?.Invoke(this, link);

    /// <summary>Raises <see cref="DragOverChanged"/> as the page would: true for files over it, false for a drag leaving.</summary>
    public void RaiseDragOver(bool over) => DragOverChanged?.Invoke(this, over);

    /// <summary>Whether anything listens to <see cref="ViewportWidthChanged"/>.</summary>
    public bool HasViewportSubscribers => ViewportWidthChanged is not null;

    /// <summary>
    /// Reports <paramref name="width"/> as the shown render's viewport width, as the page would, and raises
    /// <see cref="ViewportWidthChanged"/>.
    /// </summary>
    public void RaiseViewportWidth(double width)
    {
        ViewportWidth = width;
        ViewportWidthChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether anything listens to <see cref="DragOverChanged"/>.</summary>
    public bool HasDragSubscribers => DragOverChanged is not null;

    /// <summary>Whether anything listens to <see cref="LinkActivated"/>.</summary>
    public bool HasLinkSubscribers => LinkActivated is not null;
}

/// <summary>One <see cref="FakeDocumentSurface.ShowAsync"/> call.</summary>
public sealed record ShownDocument(
    string Html,
    string DocumentPath,
    string GroupRoot,
    double Scroll,
    ScrollAnchor? Anchor,
    string? Fragment,
    double? ContentWidth
);

/// <summary>One <see cref="FakeDocumentSurface.SetContentWidthAsync"/> call; a null width is Full.</summary>
public sealed record ContentWidthSet(double? Px);

/// <summary>One <see cref="FakeDocumentSurface.SetThemeCssAsync"/> call.</summary>
public sealed record ThemeCssSet(string Css);

/// <summary>One <see cref="IDocumentSurface.SetCardOpenAsync"/> call.</summary>
public sealed record CardOpenSet(bool Open);
