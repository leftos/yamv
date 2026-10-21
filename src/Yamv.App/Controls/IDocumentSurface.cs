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

    /// <summary>Shows a rendered document, restoring <paramref name="fragment"/> if given, else <paramref name="scroll"/>.</summary>
    /// <param name="document">The rendered markdown.</param>
    /// <param name="documentPath">The markdown file's path; relative links and images resolve against it.</param>
    /// <param name="groupRoot">The folder <c>file:</c> images must stay inside.</param>
    /// <param name="scroll">The scroll position as a fraction, 0 to 1, of the scrollable height.</param>
    /// <param name="fragment">An element id to scroll to, which wins over <paramref name="scroll"/>.</param>
    Task ShowAsync(RenderedDocument document, string documentPath, string groupRoot, double scroll, string? fragment);

    /// <summary>Replaces the page's theme block with <paramref name="css"/>.</summary>
    Task SetThemeCssAsync(string css);

    /// <summary>Sets the content column's maximum width in px; null makes it full width.</summary>
    Task SetContentWidthAsync(double? px);

    /// <summary>Opens the in-page find bar and focuses its field.</summary>
    Task OpenFindAsync();

    /// <summary>Highlights every case-insensitive match of <paramref name="query"/> and moves to one.</summary>
    Task<FindResult> FindAsync(string query, FindDirection direction);

    /// <summary>Removes every find highlight.</summary>
    Task ClearFindAsync();
}
