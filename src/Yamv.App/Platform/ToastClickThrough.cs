using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Platform;

/// <summary>
/// Lets clicks and the wheel through a toast popup's window everywhere but on its cards. Avalonia's popup window answers
/// <c>WM_NCHITTEST</c> for its whole rectangle, transparent margins and the gaps between cards included; subclassed here, it
/// answers <c>HTTRANSPARENT</c> for a point on no card, so Windows hands the input to the window under it (the page), and
/// <c>HTCLIENT</c> on a card.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class ToastClickThrough : IDisposable
{
    private const uint NonClientHitTest = 0x0084;
    private const uint NonClientDestroy = 0x0082;
    private const nint HitClient = 1;
    private const nint HitTransparent = -1;
    private const uint RootAncestor = 2;

    // Any number unique to this subclass on the window; "YAMV" in ASCII.
    private const nuint SubclassId = 0x5941_4D56;

    private readonly nint _window;
    private readonly Func<PixelPoint, bool> _isOnCard;
    private readonly IAppLog _log;
    private GCHandle _self;
    private bool _attached;

    private ToastClickThrough(nint window, Func<PixelPoint, bool> isOnCard, IAppLog log)
    {
        _window = window;
        _isOnCard = isOnCard;
        _log = log;
    }

    /// <summary>
    /// Subclasses <paramref name="window"/>, the popup's own window, until it is destroyed or this is disposed; null, with a
    /// warning, when Windows refuses.
    /// </summary>
    /// <param name="window">The popup's window handle.</param>
    /// <param name="isOnCard">Whether a point on the screen, in physical pixels, lies on a card.</param>
    /// <param name="log">Where a refusal or a failed hit test is reported.</param>
    public static ToastClickThrough? Attach(nint window, Func<PixelPoint, bool> isOnCard, IAppLog log)
    {
        var clickThrough = new ToastClickThrough(window, isOnCard, log);
        clickThrough._self = GCHandle.Alloc(clickThrough);
        if (SetWindowSubclass(window, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(clickThrough._self)))
        {
            clickThrough._attached = true;
            return clickThrough;
        }

        clickThrough._self.Free();
        log.LogWarning($"Toasts: could not subclass the toast window 0x{window:X}; its margins take clicks meant for the page.");
        return null;
    }

    /// <summary>
    /// For the hidden-desktop check: what <paramref name="window"/> answers to <c>WM_NCHITTEST</c> at <paramref name="screen"/>,
    /// and the window Windows finds there, with its top-level window and whose it is.
    /// </summary>
    /// <param name="window">The popup's window handle.</param>
    /// <param name="screen">The point on the screen, in physical pixels.</param>
    public static string DescribeHit(nint window, PixelPoint screen)
    {
        nint lParam = (nint)(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF));
        nint answer = SendMessageW(window, NonClientHitTest, 0, lParam);
        nint under = WindowFromPoint(new NativePoint(screen.X, screen.Y));
        nint root = GetAncestor(under, RootAncestor);
        _ = GetWindowThreadProcessId(under, out uint process);
        string owner =
            under == window ? "the toast window"
            : process == Environment.ProcessId ? "this process"
            : $"process {process}";
        return $"at {screen}: hit test {answer}, window under it {ClassName(under)} 0x{under:X} ({owner}), "
            + $"its top level {ClassName(root)} 0x{root:X}";
    }

    public void Dispose()
    {
        if (_attached)
        {
            _ = RemoveWindowSubclass(_window, &SubclassProc, SubclassId);
            _attached = false;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        var owner = GCHandle.FromIntPtr((nint)refData).Target as ToastClickThrough;
        if (message == NonClientHitTest && owner?.HitTest(lParam) is { } answer)
        {
            return answer;
        }

        if (message == NonClientDestroy)
        {
            _ = RemoveWindowSubclass(window, &SubclassProc, id);
            owner?._attached = false;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    /// <summary><c>HTCLIENT</c> on a card, <c>HTTRANSPARENT</c> elsewhere; null, logged, when the check failed.</summary>
    private nint? HitTest(nint lParam)
    {
        var screen = new PixelPoint(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
        try
        {
            return _isOnCard(screen) ? HitClient : HitTransparent;
        }
        // An exception must not cross the window procedure; Avalonia's own answer stands in.
        catch (Exception exception)
        {
            _log.LogError("Toasts: the toast window's hit test failed; it takes the click.", exception);
            return null;
        }
    }

    private static string ClassName(nint window)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameW(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "(none)";
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        nint window,
        delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> subclassProc,
        nuint id,
        nuint refData
    );

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(
        nint window,
        delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> subclassProc,
        nuint id
    );

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(nint window, char* className, int maxCount);
}
