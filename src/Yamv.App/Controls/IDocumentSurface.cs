using Yamv.Core.Rendering;

namespace Yamv.App.Controls;

/// <summary>
/// What a document window asks of the view that draws its document. <see cref="DocumentView"/> is the real one; tests use a fake.
/// Every member is called on the UI thread, and every event is raised there.
/// </summary>
public interface IDocumentSurface
{
    /// <summary>The page scrolled; the value is the position as a fraction, 0 to 1, of the scrollable height.</summary>
    event EventHandler<double>? ScrollChanged;

    /// <summary>The user activated a link, which the page never follows itself.</summary>
    event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>An app shortcut was pressed while the page had focus.</summary>
    event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    /// <summary>
    /// Shows a rendered document at its content width, restoring its fragment if given, else its scroll position. The page
    /// hides the outgoing document before it applies the width, so the incoming one shows once, at its own width.
    /// </summary>
    Task ShowAsync(DocumentShow show);

    /// <summary>Replaces the page's theme block with <paramref name="css"/>.</summary>
    Task SetThemeCssAsync(string css);

    /// <summary>
    /// Changes the shown document's content width in px, live; null makes it full width. A show carries its own width, so
    /// this is for a change while a document stays shown.
    /// </summary>
    Task SetContentWidthAsync(double? px);

    /// <summary>Opens the in-page find bar and focuses its field.</summary>
    Task OpenFindAsync();

    /// <summary>Highlights every case-insensitive match of <paramref name="query"/> and moves to one.</summary>
    Task<FindResult> FindAsync(string query, FindDirection direction);

    /// <summary>Removes every find highlight.</summary>
    Task ClearFindAsync();
}

/// <summary>A document for <see cref="IDocumentSurface.ShowAsync"/> to show, and how.</summary>
/// <param name="Document">The rendered markdown.</param>
/// <param name="DocumentPath">The markdown file's path; relative links and images resolve against it.</param>
/// <param name="GroupRoot">The folder <c>file:</c> images must stay inside.</param>
/// <param name="Scroll">The scroll position as a fraction, 0 to 1, of the scrollable height.</param>
/// <param name="Fragment">An element id to scroll to, which wins over <paramref name="Scroll"/>.</param>
/// <param name="ContentWidth">The text measure in px, or null for full width.</param>
public sealed record DocumentShow(
    RenderedDocument Document,
    string DocumentPath,
    string GroupRoot,
    double Scroll,
    string? Fragment,
    double? ContentWidth
);
