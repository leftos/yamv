using Yamv.Core.Rendering;
using Yamv.Core.Session;

namespace Yamv.App.Controls;

/// <summary>
/// What a document window asks of the view that draws its document. <see cref="DocumentView"/> is the real one; tests use a fake.
/// Every member is called on the UI thread, and every event is raised there.
/// </summary>
public interface IDocumentSurface
{
    /// <summary>
    /// The page scrolled, or a render landed; the value is the position as a fraction of the scrollable height and the anchor.
    /// </summary>
    event EventHandler<ScrollPosition>? ScrollChanged;

    /// <summary>The user activated a link, which the page never follows itself.</summary>
    event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>An app shortcut was pressed while the page had focus.</summary>
    event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    /// <summary>Files are being dragged over the page (true), or a drag left it (false).</summary>
    event EventHandler<bool>? DragOverChanged;

    /// <summary><see cref="ViewportWidth"/> changed to a new width the shown render's page reported.</summary>
    event EventHandler? ViewportWidthChanged;

    /// <summary>
    /// The width the shown render's text column is laid out in, as its page last reported it; null until that render's page
    /// has reported.
    /// </summary>
    double? ViewportWidth { get; }

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
    /// <returns>
    /// False when the host could not run the page script; true when it ran, or when a newer call or a show replaced it.
    /// </returns>
    Task<bool> SetContentWidthAsync(double? px);

    /// <summary>
    /// Tells the page whether a confirm card is up over it, so it holds Escape and Enter and forwards them as keys while one
    /// is. A page that loads again is told again.
    /// </summary>
    Task SetCardOpenAsync(bool open);

    /// <summary>
    /// Scrolls the shown document to the element whose id is <paramref name="fragment"/>, without a render; the fragment is
    /// already decoded (by <c>LinkPolicy</c>). A missing id does nothing.
    /// </summary>
    /// <returns>
    /// False when the host could not run the page script; true when it ran, or when a newer call or a show replaced it.
    /// </returns>
    Task<bool> ScrollToFragmentAsync(string fragment);

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
/// <param name="Scroll">
/// The scroll position as a fraction, 0 to 1, of the scrollable height, used when <paramref name="Anchor"/> finds no block.
/// </param>
/// <param name="Anchor">
/// The block (the first at or after the anchor's line) whose point the anchor's offset down goes to the top of the view, or
/// null for none. A page already showing this document keeps its own top block, or its pin to the top or bottom, instead.
/// </param>
/// <param name="Fragment">
/// An element id to scroll to, already decoded (by <c>LinkPolicy</c>), which wins over <paramref name="Scroll"/> and
/// <paramref name="Anchor"/>; the page decodes it no further.
/// </param>
/// <param name="ContentWidth">The text measure in px, or null for full width.</param>
public sealed record DocumentShow(
    RenderedDocument Document,
    string DocumentPath,
    string GroupRoot,
    double Scroll,
    ScrollAnchor? Anchor,
    string? Fragment,
    double? ContentWidth
);
