using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Yamv.Core.Session;

namespace Yamv.App.Platform;

/// <summary>
/// Where YAMV's main window keeps its client inside the outer window rectangle Windows positions, and the visible outline it
/// draws along that client, so a fitted window can land with its outline flush against the screen's edges.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFrame
{
    // WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS: the style the window has when it is shown.
    private const uint WindowStyle = 0x16CF0000;

    /// <summary>
    /// YAMV's frame at <paramref name="scaling"/>, or null when Windows refuses the measurement, with
    /// <paramref name="error"/> as the failure's code. The client starts <c>AdjustWindowRectExForDpi</c>'s left border right
    /// of the outer corner (8 px at 96 dpi) and level with its top, because Avalonia's extended client area zeroes the top
    /// border; the visible outline is 1 physical px.
    /// </summary>
    public static WindowFrame? At(double scaling, out int error)
    {
        var rect = default(NativeRect);
        if (AdjustWindowRectExForDpi(ref rect, WindowStyle, false, 0, (uint)Math.Round(scaling * 96)))
        {
            error = 0;
            return new WindowFrame(ClientLeft: -rect.Left, ClientTop: 0, Outline: 1);
        }

        error = Marshal.GetLastPInvokeError();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustWindowRectExForDpi(
        ref NativeRect rect,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu,
        uint exStyle,
        uint dpi
    );
}
