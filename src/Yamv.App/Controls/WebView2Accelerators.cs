using System.Runtime.InteropServices;
using Avalonia.Platform;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Controls;

/// <summary>
/// Turns off WebView2's browser accelerator keys (Ctrl+F, Ctrl+P, F5, Ctrl+R and the like) so they never trigger
/// browser behaviour inside the document page. Avalonia.Controls.WebView exposes only the raw <c>ICoreWebView2</c>
/// pointer, so this calls <c>ICoreWebView2Settings3.put_AreBrowserAcceleratorKeysEnabled</c> through its vtable.
/// Slot numbers and the interface id come from WebView2.h (Microsoft.Web.WebView2 1.0.4258.31).
/// </summary>
internal static class WebView2Accelerators
{
    private const int GetSettingsSlot = 3;
    private const int PutAreBrowserAcceleratorKeysEnabledSlot = 24;
    private static readonly Guid Settings3Iid = new("fdb5ab74-af33-4854-84f0-0a631deb5eba");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointerMethod(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutBoolMethod(IntPtr self, int value);

    /// <summary>
    /// Disables the accelerator keys when <paramref name="handle"/> is a WebView2 handle; logs why when it can't. Never
    /// throws, since it runs inside the web view package's <c>async void</c> adapter callback.
    /// </summary>
    public static void Disable(IPlatformHandle? handle, IAppLog log)
    {
        if (handle is not IWindowsWebView2PlatformHandle webView2)
        {
            return;
        }

        // Each read of CoreWebView2 hands out a new reference, so it is read once and released with the others.
        IntPtr coreWebView2 = IntPtr.Zero;
        IntPtr settings = IntPtr.Zero;
        IntPtr settings3 = IntPtr.Zero;
        try
        {
            coreWebView2 = webView2.CoreWebView2;
            if (coreWebView2 == IntPtr.Zero)
            {
                log.LogWarning("The WebView2 handle has no CoreWebView2 yet; Ctrl+F, Ctrl+P and F5 may act on the page");
                return;
            }

            if (
                Failed(Method<GetPointerMethod>(coreWebView2, GetSettingsSlot)(coreWebView2, out settings), "ICoreWebView2::get_Settings", log)
                || Failed(Marshal.QueryInterface(settings, in Settings3Iid, out settings3), "QueryInterface(ICoreWebView2Settings3)", log)
                || Failed(
                    Method<PutBoolMethod>(settings3, PutAreBrowserAcceleratorKeysEnabledSlot)(settings3, 0),
                    "ICoreWebView2Settings3::put_AreBrowserAcceleratorKeysEnabled",
                    log
                )
            )
            {
                return;
            }

            log.LogInfo("WebView2 browser accelerator keys disabled");
        }
        // Nothing may escape into the package's async void callback, where it would take the process down.
        catch (Exception exception)
        {
            log.LogWarning("Could not disable the WebView2 browser accelerator keys; Ctrl+F, Ctrl+P and F5 may act on the page", exception);
        }
        finally
        {
            Release(settings3);
            Release(settings);
            Release(coreWebView2);
        }
    }

    private static bool Failed(int hr, string call, IAppLog log)
    {
        if (hr >= 0)
        {
            return false;
        }

        log.LogWarning(
            $"Could not disable the WebView2 browser accelerator keys: {call} returned HRESULT 0x{hr:X8}; Ctrl+F, Ctrl+P and F5 may act on the page"
        );
        return true;
    }

    private static T Method<T>(IntPtr comObject, int slot)
        where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.Release(comObject);
        }
    }
}
