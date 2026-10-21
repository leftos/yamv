using Yamv.App.Controls;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IDocumentSurface"/> that records what it was asked to show, and raises scroll changes on demand.</summary>
public sealed class FakeDocumentSurface : IDocumentSurface
{
    public event EventHandler<double>? ScrollChanged;

    public event EventHandler<LinkActivatedEventArgs>? LinkActivated
    {
        add { }
        remove { }
    }

    public event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    /// <summary>Every <see cref="ShowAsync"/> call, oldest first.</summary>
    public List<ShownDocument> Shown { get; } = [];

    /// <summary>The width each <see cref="ShowAsync"/> call showed its document at, oldest first; null is Full.</summary>
    public List<double?> ShownWidths => [.. Shown.Select(shown => shown.ContentWidth)];

    /// <summary>Every width passed to <see cref="SetContentWidthAsync"/>, oldest first; null is Full.</summary>
    public List<double?> ContentWidths { get; } = [];

    /// <summary>
    /// Every recorded call in the order it came: a <see cref="ShownDocument"/>, a <see cref="ContentWidthSet"/> or a
    /// <see cref="ThemeCssSet"/>.
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
        var shown = new ShownDocument(show.Document.Html, show.DocumentPath, show.GroupRoot, show.Scroll, show.Fragment, show.ContentWidth);
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

    public Task SetContentWidthAsync(double? px)
    {
        ContentWidths.Add(px);
        Calls.Add(new ContentWidthSet(px));
        return Completion();
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

    public Task OpenFindAsync() => Task.CompletedTask;

    public Task<FindResult> FindAsync(string query, FindDirection direction) => Task.FromResult(new FindResult(0, 0));

    public Task ClearFindAsync() => Task.CompletedTask;

    /// <summary>Raises <see cref="ScrollChanged"/> as the page would.</summary>
    public void RaiseScroll(double fraction) => ScrollChanged?.Invoke(this, fraction);

    /// <summary>Raises <see cref="KeyForwarded"/> as the page would for a shortcut pressed while it had focus.</summary>
    public void RaiseKey(KeyForwardedEventArgs key) => KeyForwarded?.Invoke(this, key);
}

/// <summary>One <see cref="FakeDocumentSurface.ShowAsync"/> call.</summary>
public sealed record ShownDocument(string Html, string DocumentPath, string GroupRoot, double Scroll, string? Fragment, double? ContentWidth);

/// <summary>One <see cref="FakeDocumentSurface.SetContentWidthAsync"/> call; a null width is Full.</summary>
public sealed record ContentWidthSet(double? Px);

/// <summary>One <see cref="FakeDocumentSurface.SetThemeCssAsync"/> call.</summary>
public sealed record ThemeCssSet(string Css);
