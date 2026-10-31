using System.Buffers;
using Yamv.Core.Documents;

namespace Yamv.Core.Links;

/// <summary>What a link decision is made from, apart from the link itself.</summary>
/// <param name="GroupRoot">The root folder of the group the document holding the link belongs to, as a full path.</param>
/// <param name="Resolve">
/// Maps a path to its real path, with every link, junction and <c>..</c> resolved, or to null when it cannot be resolved
/// safely; <see cref="RealPath.Resolve(string)"/> outside tests.
/// </param>
public sealed record LinkContext(string GroupRoot, Func<string, ResolvedPath?> Resolve);

/// <summary>
/// The one place that decides what an activated link opens: browser and mail links, documents in YAMV, and other files inside
/// the document's group root in the platform's default app, asking first unless the file is a viewable type.
/// </summary>
public static class LinkPolicy
{
    /// <summary>
    /// The extensions of the files a link opens in the default app without asking: images, PDF, audio and video, and plain
    /// data. Compared case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> ViewableExtensions { get; } =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".svg",
        ".bmp",
        ".ico",
        ".pdf",
        ".mp3",
        ".wav",
        ".mp4",
        ".webm",
        ".mov",
        ".csv",
        ".json",
        ".xml",
        ".yaml",
        ".yml",
        ".log",
    ];

    /// <summary>The <c>mailto</c> query fields a link keeps; any other, such as <c>attach</c>, is dropped.</summary>
    private static readonly string[] MailtoFields = ["to", "cc", "bcc", "subject", "body"];

    /// <summary>The characters no file name may hold on this platform, apart from the folder separators.</summary>
    private static readonly SearchValues<char> ForbiddenNameCharacters = SearchValues.Create([
        .. Path.GetInvalidFileNameChars().Where(character => character != Path.DirectorySeparatorChar && character != Path.AltDirectorySeparatorChar),
    ]);

    /// <summary>Decides what <paramref name="uri"/> opens, from where it points and the document that linked it.</summary>
    /// <param name="uri">The link target as made absolute against the document; null when it could not be resolved.</param>
    /// <param name="context">The document's group root and the real-path resolver.</param>
    public static LinkDecision Decide(Uri? uri, LinkContext context) => Decide(uri, context, static _ => { });

    /// <summary>As <see cref="Decide(Uri?, LinkContext)"/>, calling <paramref name="normalising"/> with each path before it is normalised.</summary>
    internal static LinkDecision Decide(Uri? uri, LinkContext context, Action<string> normalising)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(normalising);

        if (uri is null || !uri.IsAbsoluteUri)
        {
            return new Ignore(IgnoreKind.NotPlainPath, "the link is not an absolute URI");
        }

        if (!uri.IsFile)
        {
            return uri.Scheme.ToLowerInvariant() switch
            {
                "http" or "https" => new OpenExternal(uri, []),
                "mailto" => MailtoDecision(uri),
                _ => new Ignore(IgnoreKind.Scheme, $"the scheme '{uri.Scheme}' is not one YAMV opens"),
            };
        }

        if (!string.IsNullOrEmpty(uri.Host))
        {
            return new Ignore(IgnoreKind.NetworkShare, $"a file: link may not name a host, and '{uri.Host}' names one");
        }

        return DecideLocal(uri, context, normalising);
    }

    /// <summary>Decides a <c>file:</c> link without a host: every check reads the normalised path, never the raw one.</summary>
    private static LinkDecision DecideLocal(Uri uri, LinkContext context, Action<string> normalising)
    {
        if (RawPathRefusal(uri.LocalPath) is { } refusal)
        {
            return refusal;
        }

        string? path = Normalise(uri.LocalPath, normalising);
        if (path is null)
        {
            return new Ignore(IgnoreKind.NotPlainPath, "the link's path is not one the platform accepts");
        }

        if (RealPath.IsUncOrDevice(path))
        {
            return new Ignore(ShareOrDeviceKind(path), $"'{path}' names a network share or a device");
        }

        if (path.AsSpan(Path.GetPathRoot(path)?.Length ?? 0).ContainsAny(ForbiddenNameCharacters))
        {
            return new Ignore(
                IgnoreKind.NotPlainPath,
                $"'{path}' holds a character no file name may hold, such as a ':' naming an alternate data stream"
            );
        }

        ResolvedPath? real = context.Resolve(path);
        if (real is null || RealPath.IsUncOrDevice(real.Path) || !RealPath.IsLocalFullyQualified(real.Path))
        {
            return new Ignore(IgnoreKind.NotPlainPath, $"'{path}' does not resolve to a local path");
        }

        if (OperatingSystem.IsWindows() && WindowsRewriteRefusal(path, real.Path, normalising) is { } rewrite)
        {
            return rewrite;
        }

        return OpensAsDocument(path, real) ? new OpenDocument(real.Path, path, Fragment(uri)) : DecideOtherFile(path, real, context);
    }

    /// <summary>Whether both the link's name and its real path's name are documents, and the real path is no folder.</summary>
    private static bool OpensAsDocument(string path, ResolvedPath real) =>
        !real.IsDirectory && DocumentFileTypes.IsDocument(path) && DocumentFileTypes.IsDocument(real.Path);

    /// <summary>Decides a link to a local file that is not a document, from its normalised path and its real path.</summary>
    private static LinkDecision DecideOtherFile(string path, ResolvedPath real, LinkContext context)
    {
        if (!real.Exists)
        {
            return new Ignore(IgnoreKind.Missing, $"'{path}' does not exist");
        }

        ResolvedPath? root = context.Resolve(context.GroupRoot);
        if (root is null || !IsInside(root.Path, real.Path))
        {
            return new Ignore(IgnoreKind.OutsideRoot, $"'{path}' is outside the document's group root");
        }

        bool viewable = !real.IsDirectory && IsViewable(path) && IsViewable(real.Path);
        return viewable ? new OpenWithDefaultApp(real.Path) : new Confirm(real.Path);
    }

    /// <summary>
    /// Why <paramref name="localPath"/> may not even be normalised, since normalising it could touch a share or read the current
    /// folder; null when it may.
    /// </summary>
    private static Ignore? RawPathRefusal(string localPath)
    {
        if (RealPath.IsUncOrDevice(localPath))
        {
            return new Ignore(ShareOrDeviceKind(localPath), $"'{localPath}' names a network share or a device");
        }

        return RealPath.IsLocalFullyQualified(localPath)
            ? null
            : new Ignore(IgnoreKind.NotPlainPath, "the link's path is not one the platform accepts");
    }

    /// <summary>
    /// The kind of a path <see cref="RealPath.IsUncOrDevice(string)"/> flags: a network share when it names a host, else not a
    /// plain path (a device or an odd prefix).
    /// </summary>
    private static IgnoreKind ShareOrDeviceKind(string path) => RealPath.IsNetworkShare(path) ? IgnoreKind.NetworkShare : IgnoreKind.NotPlainPath;

    /// <summary>
    /// Why the real path <paramref name="realPath"/> of <paramref name="path"/> may not open on Windows, which would rewrite it
    /// on the way to the file (dropping a trailing dot or space, or turning a device name into a device); null when it may.
    /// </summary>
    private static Ignore? WindowsRewriteRefusal(string path, string realPath, Action<string> normalising)
    {
        string[] segments = realPath[(Path.GetPathRoot(realPath)?.Length ?? 0)..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')))
        {
            return new Ignore(IgnoreKind.TrailingDotOrSpace, $"'{path}' has a name ending in a dot or space, which Windows rewrites");
        }

        string? full = Normalise(realPath, normalising);
        return full is not null && string.Equals(full, realPath, StringComparison.Ordinal)
            ? null
            : new Ignore(IgnoreKind.NotPlainPath, $"'{path}' is a path Windows would rewrite");
    }

    /// <summary>
    /// <paramref name="uri"/>, a <c>mailto</c> link, with its address and only the <c>to</c>, <c>cc</c>, <c>bcc</c>,
    /// <c>subject</c> and <c>body</c> fields, each copied as written and in order; every other field, such as an
    /// <c>attach</c> naming a local file, is left out and named in the decision, and a fragment is dropped.
    /// </summary>
    private static LinkDecision MailtoDecision(Uri uri)
    {
        string text = uri.AbsoluteUri;
        int hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            text = text[..hash];
        }

        int question = text.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return Mailto(text, []);
        }

        List<string> kept = [];
        List<string> dropped = [];
        foreach (string field in text[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = field.IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? field : field[..equals];
            if (MailtoFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(field);
            }
            else
            {
                dropped.Add(name);
            }
        }

        string address = text[..question];
        return Mailto(kept.Count == 0 ? address : address + "?" + string.Join('&', kept), dropped);
    }

    /// <summary>Opens the rebuilt <c>mailto</c> link <paramref name="rebuilt"/>, or ignores it when it is no URI.</summary>
    private static LinkDecision Mailto(string rebuilt, IReadOnlyList<string> dropped) =>
        Uri.TryCreate(rebuilt, UriKind.Absolute, out Uri? uri)
            ? new OpenExternal(uri, dropped)
            : new Ignore(IgnoreKind.Scheme, "the mailto link has nothing left to open once its unsafe fields are dropped");

    /// <summary>The full form of <paramref name="localPath"/>, or null when the platform rejects it (a NUL, for one).</summary>
    private static string? Normalise(string localPath, Action<string> normalising)
    {
        try
        {
            return LinkIo.FullPath(localPath, normalising);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="realPath"/> is under <paramref name="realRoot"/>, on a separator boundary. Both are real paths
    /// whose existing segments carry their on-disk case, so the comparison is ordinal.
    /// </summary>
    private static bool IsInside(string realRoot, string realPath)
    {
        string root = Path.TrimEndingDirectorySeparator(realRoot);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return realPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsViewable(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Length > 1 && ViewableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The link's heading, URL-decoded and without its <c>#</c>; null when it names none.</summary>
    private static string? Fragment(Uri uri)
    {
        string fragment = uri.Fragment;
        if (fragment.Length <= 1)
        {
            return null;
        }

        string decoded = Uri.UnescapeDataString(fragment[1..]);
        return decoded.Length == 0 ? null : decoded;
    }
}
