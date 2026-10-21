using Yamv.App.Controls;
using Yamv.Core.Rendering;

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

    public event EventHandler<KeyForwardedEventArgs>? KeyForwarded
    {
        add { }
        remove { }
    }

    /// <summary>Every <see cref="ShowAsync"/> call, oldest first.</summary>
    public List<ShownDocument> Shown { get; } = [];

    /// <summary>Called after each <see cref="ShowAsync"/> is recorded.</summary>
    public Action<ShownDocument>? OnShown { get; set; }

    /// <summary>Every css passed to <see cref="SetThemeCssAsync"/>, oldest first.</summary>
    public List<string> ThemeCss { get; } = [];

    public Task ShowAsync(RenderedDocument document, string documentPath, string groupRoot, double scroll, string? fragment)
    {
        var shown = new ShownDocument(document.Html, documentPath, groupRoot, scroll, fragment);
        Shown.Add(shown);
        OnShown?.Invoke(shown);
        return Task.CompletedTask;
    }

    public Task SetThemeCssAsync(string css)
    {
        ThemeCss.Add(css);
        return Task.CompletedTask;
    }

    public Task SetContentWidthAsync(double? px) => Task.CompletedTask;

    public Task OpenFindAsync() => Task.CompletedTask;

    public Task<FindResult> FindAsync(string query, FindDirection direction) => Task.FromResult(new FindResult(0, 0));

    public Task ClearFindAsync() => Task.CompletedTask;

    /// <summary>Raises <see cref="ScrollChanged"/> as the page would.</summary>
    public void RaiseScroll(double fraction) => ScrollChanged?.Invoke(this, fraction);
}

/// <summary>One <see cref="FakeDocumentSurface.ShowAsync"/> call.</summary>
public sealed record ShownDocument(string Html, string DocumentPath, string GroupRoot, double Scroll, string? Fragment);
