namespace Yamv.Core.Links;

/// <summary>What YAMV does with an activated link, decided by <see cref="LinkPolicy"/>.</summary>
public abstract record LinkDecision;

/// <summary>Opens <paramref name="Uri"/> in the platform's browser or mail app.</summary>
/// <param name="Uri">The absolute <c>http</c>, <c>https</c> or <c>mailto</c> URI the link named.</param>
public sealed record OpenExternal(Uri Uri) : LinkDecision;

/// <summary>Opens <paramref name="Path"/> in YAMV, scrolled to <paramref name="Fragment"/> when the link names one.</summary>
/// <param name="Path">The document's local path, which need not exist.</param>
/// <param name="Fragment">The heading the link names, URL-decoded and without its <c>#</c>; null when it names none.</param>
public sealed record OpenDocument(string Path, string? Fragment) : LinkDecision;

/// <summary>Opens <paramref name="Path"/> in the platform's default app for its type, without asking.</summary>
/// <param name="Path">The file's resolved real path: what is opened is the path the policy checked.</param>
public sealed record OpenWithDefaultApp(string Path) : LinkDecision;

/// <summary>
/// Asks the user before opening <paramref name="Path"/> in the platform's default app; the question names the file and its
/// extension, both read from <paramref name="Path"/>.
/// </summary>
/// <param name="Path">The file's or folder's resolved real path: what is opened is the path the policy checked.</param>
public sealed record Confirm(string Path) : LinkDecision;

/// <summary>Leaves the link alone; <paramref name="Reason"/> is the log line.</summary>
/// <param name="Reason">Why the link does not open.</param>
public sealed record Ignore(string Reason) : LinkDecision;
