using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yamv.App.Platform;

/// <summary>Attaches YAMV, a windowed app, to the console that started it, so help and error lines can be read there.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsConsole
{
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Attaches to the starting process's console, so <see cref="Console"/> writes there. A failed attach is ignored: YAMV
    /// may have been started from Explorer, where there is no console to write to.
    /// </summary>
    public static void Attach() =>
        // Nothing may touch Console.Out or Console.Error before this: .NET opens them over the attached handles on first use.
        _ = AttachConsole(AttachParentProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
