using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Platform;

/// <summary>
/// Whether this process runs elevated: a split-token elevation, where UAC gave the launch the full administrator token. A process
/// with no split token (UAC off, or the built-in Administrator) and one with the filtered token are not elevated.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeFull = 2;

    /// <summary>
    /// True when the process token's elevation type is <c>TokenElevationTypeFull</c>. A token that cannot be read is logged as a
    /// warning and counts as not elevated.
    /// </summary>
    public static bool IsElevated(IAppLog log)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out TokenHandle token))
        {
            log.LogWarning($"Elevation: OpenProcessToken failed with error {Marshal.GetLastPInvokeError()}; running as not elevated.");
            token.Dispose();
            return false;
        }

        using (token)
        {
            if (!GetTokenInformation(token, TokenElevationTypeClass, out int elevationType, sizeof(int), out _))
            {
                log.LogWarning($"Elevation: GetTokenInformation failed with error {Marshal.GetLastPInvokeError()}; running as not elevated.");
                return false;
            }

            return elevationType == TokenElevationTypeFull;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out TokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(TokenHandle token, int informationClass, out int information, int length, out int returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>A process token, closed when disposed.</summary>
    private sealed class TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TokenHandle()
            : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
