using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yamv.Core.Links;

/// <summary>
/// How <see cref="RealPath"/> and <see cref="LinkPolicy"/> normalise a path or build a <see cref="FileInfo"/>: each call reports
/// its input to a callback first, so a test can see every path that reached one. The folder listing's own normalising of a walked
/// parent (which holds no link) is the one call outside it.
/// </summary>
/// <remarks>
/// On Windows <see cref="FullPath(string, Action{string})"/> calls <c>GetFullPathNameW</c>, a string operation that touches no
/// disk or share (https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfullpathnamew#remarks), rather than
/// <see cref="Path.GetFullPath(string)"/>, which expands a <c>~</c> in its result through <c>GetLongPathNameW</c> and so follows a
/// link in the middle of the path to whatever share it names. Short names stay as written. The <see cref="FileInfo"/> constructor
/// calls <see cref="Path.GetFullPath(string)"/>, so it is only for a local path already checked. A caller checks a path with
/// <see cref="RealPath.IsUncOrDevice(string)"/> and <see cref="RealPath.IsLocalFullyQualified(string)"/> before handing it here.
/// </remarks>
internal static partial class LinkIo
{
    /// <summary>The full form of <paramref name="path"/>, after calling <paramref name="touching"/> with it.</summary>
    /// <exception cref="ArgumentException">The path is empty, holds a NUL, or Windows cannot normalise it.</exception>
    public static string FullPath(string path, Action<string> touching)
    {
        touching(path);
        return OperatingSystem.IsWindows() ? WindowsFullPath(path) : Path.GetFullPath(path);
    }

    /// <summary>A <see cref="FileInfo"/> for <paramref name="path"/>, after calling <paramref name="touching"/> with it.</summary>
    public static FileInfo File(string path, Action<string> touching)
    {
        touching(path);
        return new FileInfo(path);
    }

    /// <summary>
    /// <c>GetFullPathNameW</c>'s answer for <paramref name="path"/>: it returns the length it copied, or the buffer size it needs
    /// (counting the terminating NUL) when the buffer is too small, or zero on failure.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string WindowsFullPath(string path)
    {
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A path to normalise may not be empty or hold a NUL character.", nameof(path));
        }

        char[] buffer = new char[Math.Max(path.Length + 1, 260)];
        uint length = GetFullPathName(path, (uint)buffer.Length, buffer, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFullPathName(path, (uint)buffer.Length, buffer, 0);
        }

        if (length == 0 || length >= buffer.Length)
        {
            throw new ArgumentException($"GetFullPathNameW could not normalise '{path}' (error {Marshal.GetLastPInvokeError()}).", nameof(path));
        }

        return new string(buffer, 0, (int)length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFullPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFullPathName(string fileName, uint bufferLength, [Out] char[] buffer, nint filePart);
}
