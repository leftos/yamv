using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Controls;

/// <summary>
/// Adds <c>Copy as raw</c> to WKWebView's own right-click menu, right after its Copy item, which WebKit offers only when the
/// page has a selection. The view's class is swapped, once per view, for a runtime subclass whose
/// <c>willOpenMenu:withEvent:</c> runs WebKit's own first and then inserts the item; the item's target is a helper object
/// whose <c>yamvCopyRaw:</c> runs the view's <c>chosen</c>. Everything runs on the main (UI) thread, where AppKit calls it.
/// </summary>
internal static unsafe partial class WKWebViewContextMenu
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string ViewClassName = "YamvWKWebView";
    private const string TargetClassName = "YamvCopyRawTarget";

    /// <summary>The value of WebKit's <c>_WKMenuItemIdentifierCopy</c> constant, the identifier of its Copy item.</summary>
    private const string CopyIdentifier = "WKMenuItemIdentifierCopy";

    /// <summary>The views given the Copy as raw item, by view pointer.</summary>
    private static readonly Dictionary<nint, ViewEntry> Entries = [];

    /// <summary>The runtime subclass every attached view takes, and the class it was made from; zero until the first attach.</summary>
    private static nint s_viewClass;
    private static nint s_baseClass;

    /// <summary>The class of the helper objects the items target; zero until the first attach.</summary>
    private static nint s_targetClass;

    /// <summary>Where callbacks without a view entry report failures: the log of the latest attach.</summary>
    private static IAppLog? s_log;

    /// <summary>
    /// Gives the view behind <paramref name="handle"/> the Copy as raw item when it is a WKWebView handle on macOS; logs why
    /// when it can't. Never throws, since it runs inside the web view package's <c>async void</c> adapter callback.
    /// </summary>
    /// <param name="handle">The native web view's handle; any other platform's handle does nothing.</param>
    /// <param name="chosen">What runs, on the UI thread, when the item is chosen.</param>
    /// <param name="log">Where the attach and every callback report failures.</param>
    /// <returns>The attachment, which takes the item out of later menus when disposed; null when nothing was attached.</returns>
    public static IDisposable? Attach(IPlatformHandle? handle, Action chosen, IAppLog log)
    {
        if (!OperatingSystem.IsMacOS() || handle is not IAppleWKWebViewPlatformHandle apple)
        {
            return null;
        }

        try
        {
            return AttachView(apple.WKWebView, chosen, log);
        }
        // Nothing may escape into the package's async void callback, where it would take the process down.
        catch (Exception exception)
        {
            log.LogWarning($"Copy as raw menu (macOS): could not attach: {exception.Message}", exception);
            return null;
        }
    }

    [SupportedOSPlatform("macos")]
    private static Attachment? AttachView(nint view, Action chosen, IAppLog log)
    {
        if (view == 0)
        {
            log.LogWarning("Copy as raw menu (macOS): the handle has no WKWebView yet; the context menu has no Copy as raw");
            return null;
        }

        s_log = log;
        nint viewClass = ObjectGetClass(view);
        if (s_viewClass == 0)
        {
            CreateClasses(viewClass);
        }

        if (viewClass != s_viewClass)
        {
            if (viewClass != s_baseClass)
            {
                log.LogWarning("Copy as raw menu (macOS): the web view is not of the class the menu was built for; the menu is left as it was");
                return null;
            }

            _ = ObjectSetClass(view, s_viewClass);
        }

        nint target = MsgSend(MsgSend(s_targetClass, Selector("alloc")), Selector("init"));
        if (target == 0)
        {
            throw new InvalidOperationException("could not create the menu item's target");
        }

        if (Entries.Remove(view, out ViewEntry? stale))
        {
            Release(stale.Target);
        }

        Entries[view] = new ViewEntry(target, chosen, log);
        log.LogInfo("WKWebView context menu: Copy as raw added");
        return new Attachment(view, target);
    }

    /// <summary>
    /// Registers the view subclass of <paramref name="baseClass"/> and the target class, once per process. Both pairs are
    /// built before either is registered; on a failure every allocated pair is disposed, so a later attach can try again.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static void CreateClasses(nint baseClass)
    {
        nint viewClass = 0;
        nint targetClass = 0;
        try
        {
            viewClass = AllocateClass(baseClass, ViewClassName);
            delegate* unmanaged<nint, nint, nint, nint, void> willOpenMenu = &WillOpenMenu;
            AddMethod(viewClass, "willOpenMenu:withEvent:", (nint)willOpenMenu, "v@:@@");
            targetClass = AllocateClass(ClassNamed("NSObject"), TargetClassName);
            delegate* unmanaged<nint, nint, nint, void> copyRaw = &CopyRaw;
            AddMethod(targetClass, "yamvCopyRaw:", (nint)copyRaw, "v@:@");
        }
        catch
        {
            DisposeClass(targetClass);
            DisposeClass(viewClass);
            throw;
        }

        RegisterClassPair(viewClass);
        RegisterClassPair(targetClass);
        s_baseClass = baseClass;
        s_viewClass = viewClass;
        s_targetClass = targetClass;
    }

    private static nint AllocateClass(nint superclass, string name)
    {
        nint cls = AllocateClassPair(superclass, name, 0);
        return cls != 0 ? cls : throw new InvalidOperationException($"could not create the runtime class {name}");
    }

    /// <summary>Disposes a class pair that was allocated and not registered; zero does nothing.</summary>
    private static void DisposeClass(nint cls)
    {
        if (cls != 0)
        {
            DisposeClassPair(cls);
        }
    }

    private static void AddMethod(nint cls, string selector, nint implementation, string types)
    {
        if (ClassAddMethod(cls, Selector(selector), implementation, types) == 0)
        {
            throw new InvalidOperationException($"could not add {selector}");
        }
    }

    /// <summary>The subclass's <c>willOpenMenu:withEvent:</c>: WebKit's own, then the item after Copy.</summary>
    [UnmanagedCallersOnly]
    private static void WillOpenMenu(nint self, nint command, nint menu, nint menuEvent)
    {
        try
        {
            var super = new ObjcSuper(self, s_baseClass);
            MsgSendSuper(&super, command, menu, menuEvent);
            if (Entries.TryGetValue(self, out ViewEntry? entry))
            {
                InsertAfterCopy(menu, entry.Target);
            }
        }
        // Nothing may escape into AppKit; the menu opens as it was.
        catch (Exception exception)
        {
            s_log?.LogWarning($"Copy as raw menu (macOS): {exception.Message}", exception);
        }
    }

    /// <summary>Inserts the item after the menu's Copy item; a menu without one (no selection) gets nothing.</summary>
    private static void InsertAfterCopy(nint menu, nint target)
    {
        nint copyAt = CopyIndex(menu);
        if (copyAt < 0)
        {
            return;
        }

        nint title = NSString(WebView2ContextMenu.Label);
        nint keyEquivalent = NSString(string.Empty);
        nint allocated = MsgSend(ClassNamed("NSMenuItem"), Selector("alloc"));
        nint item = MsgSendThree(allocated, Selector("initWithTitle:action:keyEquivalent:"), title, Selector("yamvCopyRaw:"), keyEquivalent);
        if (item == 0)
        {
            return;
        }

        MsgSendVoid(item, Selector("setTarget:"), target);
        MsgSendInsert(menu, Selector("insertItem:atIndex:"), item, copyAt + 1);

        // The menu retains the item; the alloc reference is this code's to give back.
        Release(item);
    }

    /// <summary>The index of the item whose identifier is WebKit's Copy; -1 when there is none.</summary>
    private static nint CopyIndex(nint menu)
    {
        nint count = MsgSend(menu, Selector("numberOfItems"));
        for (nint index = 0; index < count; index++)
        {
            nint item = MsgSendIndex(menu, Selector("itemAtIndex:"), index);
            if (item == 0)
            {
                continue;
            }

            nint identifier = MsgSend(item, Selector("identifier"));
            if (identifier != 0 && Marshal.PtrToStringUTF8(MsgSend(identifier, Selector("UTF8String"))) == CopyIdentifier)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The target class's <c>yamvCopyRaw:</c>: runs the <c>chosen</c> of the view whose target sent it.</summary>
    [UnmanagedCallersOnly]
    private static void CopyRaw(nint self, nint command, nint sender)
    {
        try
        {
            foreach (ViewEntry entry in Entries.Values)
            {
                if (entry.Target == self)
                {
                    entry.Chosen();
                    return;
                }
            }
        }
        // Nothing may escape into AppKit.
        catch (Exception exception)
        {
            s_log?.LogWarning($"Copy as raw menu (macOS): {exception.Message}", exception);
        }
    }

    /// <summary>An autoreleased <c>NSString</c> of <paramref name="text"/>.</summary>
    private static nint NSString(string text)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return MsgSendVoidPointer(ClassNamed("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static void Release(nint obj)
    {
        if (obj != 0)
        {
            _ = MsgSend(obj, Selector("release"));
        }
    }

    /// <summary>One attached view: the helper object its items target, what choosing one runs, and its log.</summary>
    private sealed record ViewEntry(nint Target, Action Chosen, IAppLog Log);

    /// <summary>Takes one view out of the map and frees its target; the view keeps its subclass, which then adds nothing.</summary>
    private sealed class Attachment(nint view, nint target) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (Entries.TryGetValue(view, out ViewEntry? entry) && entry.Target == target)
                {
                    _ = Entries.Remove(view);
                    Release(target);
                }
            }
            catch (Exception exception)
            {
                s_log?.LogWarning($"Copy as raw menu (macOS): could not detach: {exception.Message}", exception);
            }
        }
    }

    /// <summary>The <c>objc_super</c> struct <c>objc_msgSendSuper</c> takes: the receiver and the class to start the lookup at.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ObjcSuper(nint receiver, nint superClass)
    {
        public readonly nint Receiver = receiver;
        public readonly nint SuperClass = superClass;
    }

    private static nint Selector(string name) => SelectorNamed(name);

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ClassNamed(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SelectorNamed(string name);

    [LibraryImport(ObjC, EntryPoint = "object_getClass")]
    private static partial nint ObjectGetClass(nint obj);

    [LibraryImport(ObjC, EntryPoint = "object_setClass")]
    private static partial nint ObjectSetClass(nint obj, nint cls);

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC, EntryPoint = "objc_disposeClassPair")]
    private static partial void DisposeClassPair(nint cls);

    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static partial void RegisterClassPair(nint cls);

    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    private static partial byte ClassAddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSend(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSendVoidPointer(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSendIndex(nint receiver, nint selector, nint index);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendVoid(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendInsert(nint receiver, nint selector, nint item, nint index);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSendThree(nint receiver, nint selector, nint first, nint second, nint third);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSendSuper")]
    private static partial void MsgSendSuper(ObjcSuper* super, nint selector, nint menu, nint menuEvent);
}
