using System.Buffers.Binary;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

    private const uint FileShareAll = 0x1 | 0x2 | 0x4;

    private const uint OpenExisting = 3;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS</c>: opens the link itself, folder or file.</summary>
    private const uint OpenReparsePointFlags = 0x0020_0000 | 0x0200_0000;

    private const uint FsctlGetReparsePoint = 0x0009_00A8;

    private const int MaximumReparseDataBufferSize = 16 * 1024;

    private const uint SymbolicLinkTag = 0xA000_000C;

    private const uint MountPointTag = 0xA000_0003;

    private const uint SymlinkFlagRelative = 1;

    /// <summary>Byte offsets in <c>REPARSE_DATA_BUFFER</c>: the name fields follow the 8-byte header.</summary>
    private const int SubstituteNameOffset = 8;

    private const int SubstituteNameLength = 10;

    private const int SymbolicLinkFlags = 16;

    private const int SymbolicLinkPathBuffer = 20;

    private const int MountPointPathBuffer = 16;

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
    /// Null when the path, or a link's target, is a network share or a device path or is not fully qualified (on Windows, rooted
    /// at a drive letter), when the links form a loop or a chain longer than
    /// <see cref="MaxLinkHops"/>, when a folder on the way cannot be read, or when a segment's name matches more than one entry in
    /// its folder ignoring case, none of them exactly.
    /// </returns>
    /// <remarks>
    /// <c>..</c> segments are collapsed by <see cref="LinkIo.FullPath(string, Action{string})"/> before the walk, which on Windows
    /// leaves 8.3 short names as written; the walk expands each one inside its own folder. Links are followed one hop at a
    /// time, and each hop's target is checked before anything touches it. The walk stops at the first segment that does not
    /// exist: nothing past it can be a link, so the rest of the path is kept as it was written.
    /// </remarks>
    public static ResolvedPath? Resolve(string path) => Resolve(path, static _ => { });

    /// <summary>As <see cref="Resolve(string)"/>, calling <paramref name="touching"/> with each path before a file-system call on it.</summary>
    internal static ResolvedPath? Resolve(string path, Action<string> touching)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(touching);
        if (!IsSafeToNormalise(path))
        {
            return null;
        }

        try
        {
            return Walk(LinkIo.FullPath(path, touching), touching);
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
    /// <c>\\.\</c>, <c>\??\</c>), which no file-system call may touch: touching a share sends the user's credentials to its host.
    /// </summary>
    internal static bool IsUncOrDevice(string path) =>
        OperatingSystem.IsWindows()
        && ((path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1])) || path.StartsWith(@"\??\", StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="path"/> is fully qualified and, on Windows, rooted at a drive letter (<c>X:\</c>): the only paths
    /// that may be normalised, since normalising any other one reads the process's current folder.
    /// </summary>
    internal static bool IsLocalFullyQualified(string path) =>
        Path.IsPathFullyQualified(path)
        && (!OperatingSystem.IsWindows() || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])));

    /// <summary>Whether <paramref name="path"/> may be handed to <see cref="LinkIo.FullPath(string, Action{string})"/>.</summary>
    private static bool IsSafeToNormalise(string path) => !IsUncOrDevice(path) && IsLocalFullyQualified(path);

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
    /// the first link's immediate target, to be walked again from its root. Returns null with no restart when a segment names an
    /// ambiguous entry, which leaves the whole path unresolvable.
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
            (Entry? entry, MatchOutcome match) = FindEntry(current, segments[index], touching);
            if (match == MatchOutcome.Ambiguous)
            {
                return (null, null);
            }

            if (entry is null)
            {
                return (new ResolvedPath(Path.Combine([current, .. segments[index..]]), Exists: false, IsDirectory: false), null);
            }

            string candidate = Path.Combine(current, entry.Value.Name);
            if (entry.Value.IsReparsePoint)
            {
                (bool isLink, string? restart) = FollowLink(candidate, segments[(index + 1)..], touching);
                if (isLink)
                {
                    return (null, restart);
                }
            }

            current = candidate;
            isDirectory = entry.Value.IsDirectory;
        }

        return (new ResolvedPath(current, Exists: true, isDirectory), null);
    }

    /// <summary>
    /// The entry of <paramref name="parent"/> that <paramref name="name"/> names, read from the folder's own listing so its name
    /// is the one stored on disk, and which of <see cref="MatchOutcome"/>'s cases it is.
    /// </summary>
    /// <remarks>
    /// An entry whose name matches ordinally wins, so a case-sensitive folder holding <c>Repo</c> and <c>repo</c> gives the one
    /// named; otherwise the single entry that matches ignoring case, which is what a case-insensitive folder opens. A name that
    /// matches more than one entry ignoring case, and none of them exactly, is ambiguous and names no entry at all: resolving it
    /// to whichever entry the OS would pick would open a file the path was not checked as. On Windows a name the listing does not
    /// hold may be an 8.3 short name, so it is expanded to its long name and looked up again.
    /// </remarks>
    private static (Entry? Entry, MatchOutcome Match) FindEntry(string parent, string name, Action<string> touching)
    {
        (Entry? entry, MatchOutcome match) = MatchEntry(parent, name, touching);
        if (match != MatchOutcome.None || !OperatingSystem.IsWindows())
        {
            return (entry, match);
        }

        string? longName = LongName(Path.Combine(parent, name), touching);
        return longName is null || longName == name ? (null, MatchOutcome.None) : MatchEntry(parent, longName, touching);
    }

    /// <summary>
    /// The one entry of <paramref name="parent"/>'s listing that <paramref name="name"/> names, and the outcome that says how many
    /// entries matched it.
    /// </summary>
    private static (Entry? Entry, MatchOutcome Match) MatchEntry(string parent, string name, Action<string> touching)
    {
        touching(parent);
        List<Entry> entries =
        [
            .. new FileSystemEnumerable<Entry>(parent, (ref entry) => new Entry(entry.FileName.ToString(), entry.Attributes), ListAll),
        ];
        (int index, MatchOutcome match) = ChooseMatch([.. entries.Select(entry => entry.Name)], name);
        return (index < 0 ? null : entries[index], match);
    }

    /// <summary>
    /// Which of <paramref name="names"/> the entry <paramref name="name"/> names: the one that matches ordinally, else the single
    /// one that matches ignoring case, with <see cref="MatchOutcome.Ambiguous"/> when several match ignoring case and none exactly.
    /// </summary>
    internal static (int Index, MatchOutcome Match) ChooseMatch(IReadOnlyList<string> names, string name)
    {
        ArgumentNullException.ThrowIfNull(names);
        int single = -1;
        int matches = 0;
        for (int index = 0; index < names.Count; index++)
        {
            if (!string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(names[index], name, StringComparison.Ordinal))
            {
                return (index, MatchOutcome.One);
            }

            single = index;
            matches++;
        }

        return matches switch
        {
            0 => (-1, MatchOutcome.None),
            1 => (single, MatchOutcome.One),
            _ => (-1, MatchOutcome.Ambiguous),
        };
    }

    /// <summary>
    /// Whether the entry at <paramref name="link"/> is a link and, when it is, the path through its target with
    /// <paramref name="rest"/> appended, normalised; a null restart leaves the whole path unresolvable.
    /// </summary>
    private static (bool IsLink, string? Restart) FollowLink(string link, string[] rest, Action<string> touching)
    {
        (bool isLink, string? target) = LinkTarget(link, touching);
        if (!isLink || target is null)
        {
            return (isLink, null);
        }

        string combined = Path.Combine([target, .. rest]);
        return (true, IsSafeToNormalise(combined) ? LinkIo.FullPath(combined, touching) : null);
    }

    /// <summary>
    /// Whether the entry at <paramref name="link"/> is a link and, when it is, its immediate target as a path, read together with
    /// whether it was stored relative to the link; a null target leaves the whole path unresolvable.
    /// </summary>
    private static (bool IsLink, string? Target) LinkTarget(string link, Action<string> touching) =>
        OperatingSystem.IsWindows() ? WindowsLinkTarget(link, touching) : UnixLinkTarget(link, touching);

    /// <summary>The raw <c>readlink</c> target of <paramref name="link"/>, joined to the link's folder when it is relative.</summary>
    private static (bool IsLink, string? Target) UnixLinkTarget(string link, Action<string> touching)
    {
        string? target = LinkIo.File(link, touching).LinkTarget;
        if (target is null)
        {
            return (false, null);
        }

        string joined = Path.Combine(Path.GetDirectoryName(link) ?? string.Empty, target);
        return (true, Path.IsPathFullyQualified(joined) ? joined : null);
    }

    /// <summary>
    /// Reads the reparse point of <paramref name="link"/> itself, never opening its target: a symbolic link or a junction gives its
    /// substitute name as a path, any other tag is no link, and a failed read leaves the path unresolvable.
    /// </summary>
    /// <remarks>
    /// <c>FSCTL_GET_REPARSE_POINT</c> fills a <c>REPARSE_DATA_BUFFER</c> of at most <c>MAXIMUM_REPARSE_DATA_BUFFER_SIZE</c> bytes
    /// (https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer).
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static (bool IsLink, string? Target) WindowsLinkTarget(string link, Action<string> touching)
    {
        touching(link);
        byte[] buffer = new byte[MaximumReparseDataBufferSize];
        using SafeFileHandle handle = CreateFile(@"\\?\" + link, 0, FileShareAll, 0, OpenExisting, OpenReparsePointFlags, 0);
        if (handle.IsInvalid || !DeviceIoControl(handle, FsctlGetReparsePoint, 0, 0, buffer, (uint)buffer.Length, out uint returned, 0))
        {
            return (true, null);
        }

        return ReparseTarget(link, buffer.AsSpan(0, (int)Math.Min(returned, (uint)buffer.Length)));
    }

    /// <summary>
    /// Whether the <c>REPARSE_DATA_BUFFER</c> in <paramref name="data"/>, read from <paramref name="link"/>, is a link and, when
    /// it is, its target as a path: a symbolic link or a junction gives its checked substitute name, any other tag is no link.
    /// </summary>
    internal static (bool IsLink, string? Target) ReparseTarget(string link, ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(uint))
        {
            return (true, null);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            SymbolicLinkTag => (true, SymbolicLinkTarget(link, data)),
            MountPointTag => (true, AbsoluteTarget(SubstituteName(data, MountPointPathBuffer))),
            _ => (false, null),
        };
    }

    /// <summary>A symbolic link's target: joined to the link as the OS joins it when stored relative, else absolute.</summary>
    private static string? SymbolicLinkTarget(string link, ReadOnlySpan<byte> data)
    {
        string? name = SubstituteName(data, SymbolicLinkPathBuffer);
        if (name is null)
        {
            return null;
        }

        bool relative = (BinaryPrimitives.ReadUInt32LittleEndian(data[SymbolicLinkFlags..]) & SymlinkFlagRelative) != 0;
        return relative ? RelativeTarget(link, name) : AbsoluteTarget(name);
    }

    /// <summary>
    /// The substitute name of a reparse buffer whose path buffer starts at byte <paramref name="pathBuffer"/>; null when it is
    /// empty or does not fit the bytes read.
    /// </summary>
    private static string? SubstituteName(ReadOnlySpan<byte> data, int pathBuffer)
    {
        if (data.Length < pathBuffer)
        {
            return null;
        }

        int start = pathBuffer + BinaryPrimitives.ReadUInt16LittleEndian(data[SubstituteNameOffset..]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[SubstituteNameLength..]);
        return length == 0 || length % 2 != 0 || start + length > data.Length ? null : Encoding.Unicode.GetString(data.Slice(start, length));
    }

    /// <summary>An absolute target, <c>\??\X:\…</c>, as <c>X:\…</c>; null for every other form (UNC, volume, GLOBALROOT, …).</summary>
    private static string? AbsoluteTarget(string? name)
    {
        const string NtPrefix = @"\??\";
        if (name is null || !name.StartsWith(NtPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string path = name[NtPrefix.Length..];
        return IsLocalFullyQualified(path) ? path : null;
    }

    /// <summary>
    /// A relative target joined to <paramref name="link"/>: one starting with a single <c>\</c> is rooted at the link's drive
    /// root, any other rooted one (<c>C:\x</c>, <c>\\host</c>) is null since the OS would not open it, and the rest are combined
    /// with the link's folder; the caller checks the result before normalising it.
    /// </summary>
    internal static string? RelativeTarget(string link, string name)
    {
        if (name.StartsWith('\\') && !name.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Path.GetPathRoot(link) + name[1..];
        }

        return Path.IsPathRooted(name) ? null : Path.Combine(Path.GetDirectoryName(link) ?? string.Empty, name);
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

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[] longPath, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile
    );

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint inBuffer,
        uint inBufferSize,
        [Out] byte[] outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped
    );

    /// <summary>What a folder's listing holds for a name.</summary>
    internal enum MatchOutcome
    {
        /// <summary>The listing holds the name exactly, or one entry that matches it ignoring case.</summary>
        One,

        /// <summary>The listing holds no entry that matches the name, even ignoring case.</summary>
        None,

        /// <summary>The listing holds more than one entry that matches ignoring case, and none of them exactly.</summary>
        Ambiguous,
    }

    /// <summary>A folder entry as its parent's listing holds it.</summary>
    private readonly record struct Entry(string Name, FileAttributes Attributes)
    {
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    }
}
