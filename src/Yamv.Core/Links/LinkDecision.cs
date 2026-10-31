namespace Yamv.Core.Links;

/// <summary>What YAMV does with an activated link, decided by <see cref="LinkPolicy"/>.</summary>
public abstract record LinkDecision;

/// <summary>
/// Opens <paramref name="Uri"/> in the platform's browser or mail app. A consumer hands the OS <c>Uri.AbsoluteUri</c>, never
/// <c>Uri.ToString()</c>, which unescapes <c>%26</c> and would turn an encoded <c>attach</c> inside a kept <c>body</c> into a
/// live field.
/// </summary>
/// <param name="Uri">
/// The absolute <c>http</c>, <c>https</c> or <c>mailto</c> URI the link named; a <c>mailto</c> URI keeps only its address and
/// its <c>to</c>, <c>cc</c>, <c>bcc</c>, <c>subject</c> and <c>body</c> fields, and no fragment.
/// </param>
/// <param name="DroppedFields">The names of the <c>mailto</c> query fields left out, as written and in order; empty otherwise.</param>
public sealed record OpenExternal(Uri Uri, IReadOnlyList<string> DroppedFields) : LinkDecision;

/// <summary>Opens <paramref name="Path"/> in YAMV, scrolled to <paramref name="Fragment"/> when the link names one.</summary>
/// <param name="Path">
/// The document's resolved real path, which need not exist: the path the policy checked, and what the app opens, watches and
/// restores.
/// </param>
/// <param name="LinkPath">
/// The link's path as <c>Path.GetFullPath</c> left it, with links not followed and case as written, for display only (the
/// document's title and tree label).
/// </param>
/// <param name="Fragment">The heading the link names, URL-decoded and without its <c>#</c>; null when it names none.</param>
/// <remarks>
/// A consumer never opens or watches <paramref name="LinkPath"/> and never hands it to the shell: only <paramref name="Path"/> is
/// the path the policy checked.
/// </remarks>
public sealed record OpenDocument(string Path, string LinkPath, string? Fragment) : LinkDecision;

/// <summary>Opens <paramref name="Path"/> in the platform's default app for its type, without asking.</summary>
/// <param name="Path">The file's resolved real path: what is opened is the path the policy checked.</param>
public sealed record OpenWithDefaultApp(string Path) : LinkDecision;

/// <summary>
/// Asks the user before opening <paramref name="Path"/> in the platform's default app; the question names the file and its
/// extension, both read from <paramref name="Path"/>.
/// </summary>
/// <param name="Path">The file's or folder's resolved real path: what is opened is the path the policy checked.</param>
public sealed record Confirm(string Path) : LinkDecision;

/// <summary>Leaves the link alone; <paramref name="Kind"/> says why for the user, <paramref name="Reason"/> is the log line.</summary>
/// <param name="Kind">Which kind of refusal this is, so the app can word what the user sees.</param>
/// <param name="Reason">Why the link does not open.</param>
public sealed record Ignore(IgnoreKind Kind, string Reason) : LinkDecision;

/// <summary>Why <see cref="LinkPolicy"/> left a link alone.</summary>
public enum IgnoreKind
{
    /// <summary>The link's scheme is not one YAMV opens.</summary>
    Scheme,

    /// <summary>The link names a network share: a <c>file:</c> host or a <c>\\host\share</c> path.</summary>
    NetworkShare,

    /// <summary>The link is not a plain local path: not absolute, a device path, a forbidden character, or unresolvable.</summary>
    NotPlainPath,

    /// <summary>The link's file does not exist.</summary>
    Missing,

    /// <summary>The link's file is outside the document's group root.</summary>
    OutsideRoot,

    /// <summary>The link's real path has a name ending in a dot or space, which Windows would rewrite.</summary>
    TrailingDotOrSpace,
}
