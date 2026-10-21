using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yamv.Core.Links;

/// <summary>A path as <see cref="RealPath.Resolve(string)"/> found it on disk.</summary>
/// <param name="Path">
/// The real path: every existing segment as stored on disk (its long name, in its on-disk case) with every link followed,
/// and the part that does not exist kept as it was written.
/// </param>
/// <param name="Exists">Whether the whole path exists.</param>
/// <param name="IsDirectory">Whether the path exists and is a folder.</param>
public sealed record ResolvedPath(string Path, bool Exists, bool IsDirectory);

/// <summary>
/// Resolves a path's symbolic links, junctions, <c>..</c> segments, short names and case, so a check that a file is inside a
/// folder, or of what type it is, reads the file the OS would open.
/// </summary>
public static partial class RealPath
{
    /// <summary>How many links one path may pass through before it counts as a loop and is unresolvable.</summary>
    public const int MaxLinkHops = 40;

    private static readonly EnumerationOptions ListAll = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The real path of <paramref name="path"/>, or null when it cannot be resolved safely.</summary>
    /// <param name="path">A file or folder path, which need not exist.</param>
    /// <returns>
    /// Null when a link points at a network share or a device, when the links form a loop or a chain longer than
    /// <see cref="MaxLinkHops"/>, or when a folder on the way cannot be read.
    /// </returns>
    /// <remarks>
    /// <c>..</c> segments are collapsed by <see cref="Path.GetFullPath(string)"/> before the walk. Links are followed one hop at a
    /// time, and each hop's target is checked before anything touches it. The walk stops at the first segment that does not
    /// exist: nothing past it can be a link, so the rest of the path is kept as it was written.
    /// </remarks>
    public static ResolvedPath? Resolve(string path) => Resolve(path, static _ => { });

    /// <summary>As <see cref="Resolve(string)"/>, calling <paramref name="touching"/> with each path before a file-system call on it.</summary>
    internal static ResolvedPath? Resolve(string path, Action<string> touching)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(touching);
        try
        {
            return Walk(Path.GetFullPath(path), touching);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a Windows network share or device path (<c>\\server\share</c>, <c>\\?\</c>,
    /// <c>\\.\</c>), which no file-system call may touch: touching a share sends the user's credentials to its host.
    /// </summary>
    internal static bool IsUncOrDevice(string path) =>
        OperatingSystem.IsWindows() && path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    private static bool IsSeparator(char character) => character is '\\' or '/';

    /// <summary>Walks <paramref name="full"/>, restarting from each link's target, until it ends or passes the hop limit.</summary>
    private static ResolvedPath? Walk(string full, Action<string> touching)
    {
        string pending = full;
        for (int hops = 0; hops <= MaxLinkHops; hops++)
        {
            if (IsUncOrDevice(pending))
            {
                return null;
            }

            (ResolvedPath? resolved, string? restart) = WalkToFirstLink(pending, touching);
            if (restart is null)
            {
                return resolved;
            }

            pending = restart;
        }

        return null;
    }

    /// <summary>
    /// Walks <paramref name="full"/> segment by segment: returns the result when no segment is a link, else the path through
    /// the first link's immediate target, to be walked again from its root.
    /// </summary>
    private static (ResolvedPath? Resolved, string? Restart) WalkToFirstLink(string full, Action<string> touching)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        string[] segments = full[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            touching(current);
            bool exists = Directory.Exists(current);
            return (new ResolvedPath(current, exists, exists), null);
        }

        bool isDirectory = false;
        for (int index = 0; index < segments.Length; index++)
        {
            Entry? entry = FindEntry(current, segments[index], touching);
            if (entry is null)
            {
                return (new ResolvedPath(Path.Combine([current, .. segments[index..]]), Exists: false, IsDirectory: false), null);
            }

            string candidate = Path.Combine(current, entry.Value.Name);
            string? target = entry.Value.IsReparsePoint ? LinkTarget(candidate, entry.Value.IsDirectory, touching) : null;
            if (target is not null)
            {
                return (null, Path.GetFullPath(Path.Combine([target, .. segments[(index + 1)..]])));
            }

            current = candidate;
            isDirectory = entry.Value.IsDirectory;
        }

        return (new ResolvedPath(current, Exists: true, isDirectory), null);
    }

    /// <summary>
    /// The entry of <paramref name="parent"/> that <paramref name="name"/> names, read from the folder's own listing so its name
    /// is the one stored on disk; null when there is none.
    /// </summary>
    /// <remarks>
    /// An entry whose name matches ordinally wins, so a case-sensitive folder holding <c>Repo</c> and <c>repo</c> gives the one
    /// named; otherwise the single entry that matches ignoring case, which is what a case-insensitive folder opens. On Windows a
    /// name the listing does not hold may be an 8.3 short name, so it is expanded to its long name and looked up again.
    /// </remarks>
    private static Entry? FindEntry(string parent, string name, Action<string> touching)
    {
        Entry? entry = MatchEntry(parent, name, touching);
        if (entry is not null || !OperatingSystem.IsWindows())
        {
            return entry;
        }

        string? longName = LongName(Path.Combine(parent, name), touching);
        return longName is null || longName == name ? null : MatchEntry(parent, longName, touching);
    }

    private static Entry? MatchEntry(string parent, string name, Action<string> touching)
    {
        touching(parent);
        List<Entry> matches =
        [
            .. new FileSystemEnumerable<Entry>(parent, (ref entry) => new Entry(entry.FileName.ToString(), entry.Attributes), ListAll)
            {
                ShouldIncludePredicate = (ref entry) => entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase),
            },
        ];
        foreach (Entry match in matches)
        {
            if (match.Name == name)
            {
                return match;
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The full path of the immediate target of the link at <paramref name="path"/>; null when it is no link.</summary>
    private static string? LinkTarget(string path, bool isDirectory, Action<string> touching)
    {
        touching(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
    }

    /// <summary>The long name of the last segment of <paramref name="path"/>, or null when Windows finds none.</summary>
    /// <remarks>
    /// <c>GetLongPathNameW</c> returns the length it copied, or zero when the path does not exist; the <c>\\?\</c> prefix lifts the
    /// MAX_PATH limit (https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getlongpathnamew).
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static string? LongName(string path, Action<string> touching)
    {
        touching(path);
        char[] buffer = new char[short.MaxValue + 1];
        uint length = GetLongPathName(@"\\?\" + path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? null : Path.GetFileName(new string(buffer, 0, (int)length));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[] longPath, uint bufferLength);

    /// <summary>A folder entry as its parent's listing holds it.</summary>
    private readonly record struct Entry(string Name, FileAttributes Attributes)
    {
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    }
}
