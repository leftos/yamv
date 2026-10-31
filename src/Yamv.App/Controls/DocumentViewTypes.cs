using System.Text.Json.Serialization;
using Yamv.Core.Session;

namespace Yamv.App.Controls;

/// <summary>Which match <see cref="DocumentView.FindAsync"/> moves to.</summary>
public enum FindDirection
{
    First,
    Next,
    Previous,
}

/// <summary>The outcome of a find: the one-based index of the current match (0 when none) and the match count.</summary>
public sealed record FindResult(int Current, int Total);

/// <summary>Where the page is scrolled to.</summary>
/// <param name="Fraction">The position as a fraction, 0 to 1, of the scrollable height.</param>
/// <param name="Anchor">
/// The top-level block at the top of the view and the offset within it, or null when the page shows no such block.
/// </param>
public sealed record ScrollPosition(double Fraction, ScrollAnchor? Anchor);

/// <summary>Where an activated link came from.</summary>
public enum LinkSource
{
    /// <summary>A primary or middle click on a link in the shown document.</summary>
    Click,

    /// <summary>The web view started a navigation away from the page.</summary>
    Navigation,

    /// <summary>The web view asked for a new window, as it does for each file dropped on the page.</summary>
    NewWindow,
}

/// <summary>A link the user activated in the page, which the page never follows itself.</summary>
public sealed class LinkActivatedEventArgs(string href, Uri? resolvedUri, int button, LinkSource source, string? documentPath) : EventArgs
{
    /// <summary>The link target as written in the document.</summary>
    public string Href { get; } = href;

    /// <summary>The target made absolute against the document's path, or null when it can't be.</summary>
    public Uri? ResolvedUri { get; } = resolvedUri;

    /// <summary>The DOM mouse button: 0 primary, 1 middle; 0 for a navigation or a new window.</summary>
    public int Button { get; } = button;

    /// <summary>Whether a click, a navigation or a new-window request activated the link.</summary>
    public LinkSource Source { get; } = source;

    /// <summary>The full path of the document shown when the link was activated, as the show stored it; null when none is shown.</summary>
    public string? DocumentPath { get; } = documentPath;
}

/// <summary>An app shortcut pressed while the page had focus, passed on because the host's key handling never sees it.</summary>
public sealed class KeyForwardedEventArgs(string key, bool ctrl, bool meta, bool shift, bool alt) : EventArgs
{
    /// <summary>The DOM <c>KeyboardEvent.key</c> value, such as <c>"o"</c> or <c>"F5"</c>.</summary>
    public string Key { get; } = key;

    public bool Ctrl { get; } = ctrl;

    public bool Meta { get; } = meta;

    public bool Shift { get; } = shift;

    public bool Alt { get; } = alt;
}

/// <summary>A message the page posts with <c>invokeCSharpAction</c>; which fields are set depends on <see cref="Type"/>.</summary>
internal sealed class PageMessage
{
    public string? Type { get; init; }

    public double? Value { get; init; }

    /// <summary>
    /// A <c>scroll</c> message's anchor: the source line of the block at the top of the view. A double, so a line that is
    /// not a whole number drops the anchor rather than the message.
    /// </summary>
    public double? Line { get; init; }

    /// <summary>A <c>scroll</c> message's fraction, 0 to 1, of the anchor block's height above the top of the view.</summary>
    public double? Offset { get; init; }

    public string? Href { get; init; }

    public int? Button { get; init; }

    public int? Current { get; init; }

    public int? Total { get; init; }

    public string? Key { get; init; }

    public bool Ctrl { get; init; }

    public bool Meta { get; init; }

    public bool Shift { get; init; }

    public bool Alt { get; init; }

    public string? Src { get; init; }

    public string? Reason { get; init; }

    public string? Where { get; init; }

    public string? Message { get; init; }

    /// <summary>A <c>drag</c> message's direction: true while files are over the page, false once the drag left it.</summary>
    public bool? Over { get; init; }

    /// <summary>The render a <c>rendered</c>, <c>scroll</c>, <c>link</c> or <c>viewport</c> message belongs to.</summary>
    public int? Generation { get; init; }
}

/// <summary>
/// The options object passed to the page's <c>yamv.render</c>. <c>Fragment</c> is an element id, already decoded (by
/// <c>LinkPolicy</c>). <c>ContentWidth</c> is the CSS value (<c>"760px"</c> or
/// <c>"none"</c>) the page sets once the outgoing document is hidden. <c>DocumentPath</c> tells the page whether it is
/// asked to show again the document it shows, in which case it keeps its own top block and find match and ignores
/// <c>AnchorLine</c>; otherwise it scrolls <c>AnchorOffset</c> of the way down the block at <c>AnchorLine</c>, else to
/// <c>Scroll</c>.
/// </summary>
internal sealed record RenderOptions(
    int Generation,
    string DocumentPath,
    double Scroll,
    int? AnchorLine,
    double AnchorOffset,
    string? Fragment,
    bool HasMermaid,
    bool HasMath,
    string DocBase,
    string RootBase,
    string ContentWidth
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PageMessage))]
[JsonSerializable(typeof(RenderOptions))]
[JsonSerializable(typeof(FindResult))]
[JsonSerializable(typeof(string))]
internal sealed partial class PageJsonContext : JsonSerializerContext;
