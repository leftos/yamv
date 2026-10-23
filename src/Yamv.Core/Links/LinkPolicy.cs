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
            return new Ignore("the link is not an absolute URI");
        }

        if (!uri.IsFile)
        {
            return uri.Scheme.ToLowerInvariant() is "http" or "https" or "mailto"
                ? new OpenExternal(uri)
                : new Ignore($"the scheme '{uri.Scheme}' is not one YAMV opens");
        }

        if (!string.IsNullOrEmpty(uri.Host))
        {
            return new Ignore($"a file: link may not name a host, and '{uri.Host}' names one");
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
            return new Ignore("the link's path is not one the platform accepts");
        }

        if (RealPath.IsUncOrDevice(path))
        {
            return new Ignore($"'{path}' names a network share or a device");
        }

        if (path.AsSpan(Path.GetPathRoot(path)?.Length ?? 0).ContainsAny(ForbiddenNameCharacters))
        {
            return new Ignore($"'{path}' holds a character no file name may hold, such as a ':' naming an alternate data stream");
        }

        ResolvedPath? real = context.Resolve(path);
        if (real is null || RealPath.IsUncOrDevice(real.Path))
        {
            return new Ignore($"'{path}' does not resolve to a local path");
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
            return new Ignore($"'{path}' does not exist");
        }

        ResolvedPath? root = context.Resolve(context.GroupRoot);
        if (root is null || !IsInside(root.Path, real.Path))
        {
            return new Ignore($"'{path}' is outside the document's group root");
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
            return new Ignore($"'{localPath}' names a network share or a device");
        }

        return RealPath.IsLocalFullyQualified(localPath) ? null : new Ignore("the link's path is not one the platform accepts");
    }

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
