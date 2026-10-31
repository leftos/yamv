namespace Yamv.Core.Platform;

/// <summary>
/// Reads a file-open failure as another process holding the file: Windows raises a sharing or lock violation for it, Unix an
/// advisory <c>flock(LOCK_EX | LOCK_NB)</c> refused as EWOULDBLOCK. Any other failure is about the file, not a holder.
/// </summary>
internal static class ShareViolation
{
    /// <summary>True when <paramref name="exception"/> says the file is held by another process.</summary>
    public static bool IsShareViolation(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (OperatingSystem.IsWindows())
        {
            const int ErrorSharingViolation = 32;
            const int ErrorLockViolation = 33;
            int win32Error = exception.HResult & 0xFFFF;
            return win32Error is ErrorSharingViolation or ErrorLockViolation;
        }

        // On Unix, .NET raises a failed flock(LOCK_EX | LOCK_NB) as an IOException whose HResult is the raw errno EWOULDBLOCK.
        int eWouldBlock = OperatingSystem.IsLinux() ? 11 : 35;
        return exception.HResult == eWouldBlock;
    }
}
