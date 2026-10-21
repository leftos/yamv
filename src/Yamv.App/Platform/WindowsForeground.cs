using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Platform;

/// <summary>
/// Lets the main instance take the foreground for a second launch, and brings its window forward when it does not answer.
/// Windows' foreground lock stops a background process from taking focus unless the foreground process allows it first.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsForeground
{
    private const int RestoreCommand = 9;

    /// <summary>Allows the process <paramref name="pid"/> to set the foreground window; a refusal is logged as a warning.</summary>
    public static void AllowForeground(int pid, IAppLog log)
    {
        if (!AllowSetForegroundWindow(pid))
        {
            log.LogWarning($"Second launch: AllowSetForegroundWindow({pid}) failed with error {Marshal.GetLastPInvokeError()}.");
        }
    }

    /// <summary>
    /// Restores the top-level window of the process <paramref name="pid"/> when it is minimized and makes it the foreground
    /// window. False when the process is gone, has no top-level window, or Windows refused the foreground change.
    /// </summary>
    public static bool BringForward(int pid, IAppLog log)
    {
        nint window = MainWindowOf(pid, log);
        if (window == 0)
        {
            return false;
        }

        if (IsIconic(window))
        {
            _ = ShowWindow(window, RestoreCommand);
        }

        return SetForegroundWindow(window);
    }

    private static nint MainWindowOf(int pid, IAppLog log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            log.LogWarning($"Second launch: could not find the window of process {pid}.", ex);
            return 0;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
