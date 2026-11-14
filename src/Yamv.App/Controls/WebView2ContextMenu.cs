using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Controls;

/// <summary>
/// Adds <c>Copy as raw</c> to WebView2's own right-click menu, right after its Copy item, whenever the page has a selection.
/// Avalonia.Controls.WebView exposes only the raw <c>ICoreWebView2</c> pointer, so this subscribes to
/// <c>ICoreWebView2_11.add_ContextMenuRequested</c> through the vtable with handler objects built by <see cref="ComWrappers"/>.
/// Every interface id and slot number comes from WebView2.h (Microsoft.Web.WebView2 1.0.4258.31). All calls and callbacks run
/// on the UI thread.
/// </summary>
internal static unsafe partial class WebView2ContextMenu
{
    /// <summary>The menu item's label.</summary>
    internal const string Label = "Copy as raw";

    /// <summary>The <c>Name</c> WebView2 gives its own Copy item.</summary>
    private const string CopyItemName = "copy";

    /// <summary><c>COREWEBVIEW2_CONTEXT_MENU_ITEM_KIND_COMMAND</c> (WebView2.h).</summary>
    private const int CommandKind = 0;

    private const int SOk = 0;

    // ICoreWebView2Vtbl (WebView2.h): CallDevToolsProtocolMethod.
    private const int CallDevToolsProtocolMethodSlot = 36;

    // ICoreWebView2_2Vtbl (WebView2.h): get_Environment.
    private const int GetEnvironmentSlot = 67;

    // ICoreWebView2_11Vtbl (WebView2.h): add_ContextMenuRequested follows CallDevToolsProtocolMethodForSession (99).
    private const int AddContextMenuRequestedSlot = 100;
    private const int RemoveContextMenuRequestedSlot = 101;

    // ICoreWebView2Environment9Vtbl (WebView2.h): CreateContextMenuItem.
    private const int CreateContextMenuItemSlot = 19;

    // ICoreWebView2ContextMenuRequestedEventArgsVtbl (WebView2.h): get_MenuItems, get_ContextMenuTarget.
    private const int GetMenuItemsSlot = 3;
    private const int GetContextMenuTargetSlot = 4;

    // ICoreWebView2ContextMenuTargetVtbl (WebView2.h): get_HasSelection.
    private const int GetHasSelectionSlot = 14;

    // ICoreWebView2ContextMenuItemCollectionVtbl (WebView2.h): get_Count, GetValueAtIndex, InsertValueAtIndex.
    private const int GetCountSlot = 3;
    private const int GetValueAtIndexSlot = 4;
    private const int InsertValueAtIndexSlot = 6;

    // ICoreWebView2ContextMenuItemVtbl (WebView2.h): get_Name, add_CustomItemSelected.
    private const int GetNameSlot = 3;
    private const int AddCustomItemSelectedSlot = 14;

    // Interface ids from WebView2.h; a handler's is its MIDL_INTERFACE line, the IID ComputeVtables exposes its entry under.
    private static readonly Guid CoreWebView2_2Iid = new("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C");
    private static readonly Guid CoreWebView2_11Iid = new("0BE78E56-C193-4051-B943-23B460C08BDB");
    private static readonly Guid Environment9Iid = new("F06F41BF-4B5A-49D8-B9F6-FA16CD29F274");
    private static readonly Guid ContextMenuRequestedHandlerIid = new("04D3FE1D-AB87-42FB-A898-DA241D35B63C");
    private static readonly Guid CustomItemSelectedHandlerIid = new("49E1D0BC-FE9E-4481-B7C2-32324AA21998");
    private static readonly Guid DevToolsCompletedHandlerIid = new("5C4889F0-5EF6-4C5A-952C-D8F1B92D0574");

    /// <summary>
    /// Subscribes the Copy as raw item to the web view's menu when <paramref name="handle"/> is a WebView2 handle; logs why
    /// when it can't. Never throws, since it runs inside the web view package's <c>async void</c> adapter callback.
    /// </summary>
    /// <param name="handle">The native web view's handle; any other platform's handle does nothing.</param>
    /// <param name="chosen">What runs, on the UI thread, when the item is chosen.</param>
    /// <param name="log">Where the attach and every callback report failures.</param>
    /// <returns>The subscription, which removes the item from later menus when disposed; null when nothing was attached.</returns>
    public static IDisposable? Attach(IPlatformHandle? handle, Action chosen, IAppLog log)
    {
        if (handle is not IWindowsWebView2PlatformHandle webView2)
        {
            return null;
        }

        // Each read of CoreWebView2 hands out a new reference, so it is read once and released at the end.
        IntPtr coreWebView2 = IntPtr.Zero;
        IntPtr core2 = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        Subscription? subscription = null;
        try
        {
            coreWebView2 = webView2.CoreWebView2;
            if (coreWebView2 == IntPtr.Zero)
            {
                log.LogWarning("The WebView2 handle has no CoreWebView2 yet; the context menu has no Copy as raw");
                return null;
            }

            subscription = new Subscription(chosen, log);
            Check(Marshal.QueryInterface(coreWebView2, in CoreWebView2_2Iid, out core2), "QueryInterface(ICoreWebView2_2)");
            Check(GetPointer(core2, GetEnvironmentSlot, out environment), "ICoreWebView2_2::get_Environment");
            Check(
                Marshal.QueryInterface(environment, in Environment9Iid, out subscription.Environment9),
                "QueryInterface(ICoreWebView2Environment9)"
            );
            Check(Marshal.QueryInterface(coreWebView2, in CoreWebView2_11Iid, out subscription.Core11), "QueryInterface(ICoreWebView2_11)");
            subscription.Subscribe();
            log.LogInfo("WebView2 context menu: Copy as raw added");
            return subscription;
        }
        // Nothing may escape into the package's async void callback, where it would take the process down.
        catch (Exception exception)
        {
            log.LogWarning($"Could not add Copy as raw to the WebView2 context menu: {exception.Message}", exception);
            subscription?.Dispose();
            return null;
        }
        finally
        {
            Release(environment);
            Release(core2);
            Release(coreWebView2);
        }
    }

    /// <summary>Adds the item to the menu <paramref name="args"/> describes when the page has a selection.</summary>
    private static void AddItem(IntPtr args, IntPtr environment9, ItemSelectedHandler selected)
    {
        IntPtr target = IntPtr.Zero;
        IntPtr items = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        IntPtr selectedPointer = IntPtr.Zero;
        try
        {
            Check(GetPointer(args, GetContextMenuTargetSlot, out target), "ICoreWebView2ContextMenuRequestedEventArgs::get_ContextMenuTarget");
            int hasSelection = 0;
            Check(Slot(target, GetHasSelectionSlot)(target, &hasSelection), "ICoreWebView2ContextMenuTarget::get_HasSelection");
            if (hasSelection == 0)
            {
                return;
            }

            Check(GetPointer(args, GetMenuItemsSlot, out items), "ICoreWebView2ContextMenuRequestedEventArgs::get_MenuItems");
            item = CreateItem(environment9);
            selectedPointer = NativeHandler(selected, CustomItemSelectedHandlerIid, "ICoreWebView2CustomItemSelectedEventHandler");
            long token;
            var addSelected = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(item), AddCustomItemSelectedSlot * IntPtr.Size);
            Check(addSelected(item, selectedPointer, &token), "ICoreWebView2ContextMenuItem::add_CustomItemSelected");
            uint index = IndexAfterCopy(items);
            var insert = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(items), InsertValueAtIndexSlot * IntPtr.Size);
            Check(insert(items, index, item), "ICoreWebView2ContextMenuItemCollection::InsertValueAtIndex");
            selected.Log.LogInfo(index == 0 ? "Copy as raw menu: item added at the top" : "Copy as raw menu: item added after Copy");
        }
        finally
        {
            // The item holds the handler and the menu holds the item; this code keeps neither.
            Release(selectedPointer);
            Release(item);
            Release(items);
            Release(target);
        }
    }

    /// <summary>A new <c>Copy as raw</c> command item; a menu item belongs to one menu, so each menu gets its own.</summary>
    private static IntPtr CreateItem(IntPtr environment9)
    {
        IntPtr item = IntPtr.Zero;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int, IntPtr*, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(environment9), CreateContextMenuItemSlot * IntPtr.Size);
        fixed (char* label = Label)
        {
            Check(create(environment9, label, IntPtr.Zero, CommandKind, &item), "ICoreWebView2Environment9::CreateContextMenuItem");
        }

        return item;
    }

    /// <summary>The index just after the item named <c>copy</c> in <paramref name="items"/>, or 0 when there is none.</summary>
    private static uint IndexAfterCopy(IntPtr items)
    {
        uint count = 0;
        Check(Slot(items, GetCountSlot)(items, &count), "ICoreWebView2ContextMenuItemCollection::get_Count");
        var getAt = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(items), GetValueAtIndexSlot * IntPtr.Size);
        for (uint index = 0; index < count; index++)
        {
            IntPtr item = IntPtr.Zero;
            try
            {
                Check(getAt(items, index, &item), "ICoreWebView2ContextMenuItemCollection::GetValueAtIndex");
                if (string.Equals(ItemName(item), CopyItemName, StringComparison.Ordinal))
                {
                    return index + 1;
                }
            }
            finally
            {
                Release(item);
            }
        }

        return 0;
    }

    /// <summary>The <c>Name</c> of a menu item, with the string WebView2 allocated freed.</summary>
    private static string? ItemName(IntPtr item)
    {
        IntPtr name = IntPtr.Zero;
        try
        {
            Check(Slot(item, GetNameSlot)(item, &name), "ICoreWebView2ContextMenuItem::get_Name");
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    /// <summary>
    /// The native interface <paramref name="iid"/> of a handler object. <see cref="ComWrappers.GetOrCreateComInterfaceForObject"/>
    /// hands out the wrapper's IUnknown, whose vtable holds only QueryInterface, AddRef and Release, so WebView2 is given the
    /// handler interface it calls <c>Invoke</c> on.
    /// </summary>
    /// <returns>A reference the caller releases once WebView2 holds its own.</returns>
    private static IntPtr NativeHandler(object handler, Guid iid, string name)
    {
        IntPtr unknown = HandlerWrappers.Instance.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        try
        {
            Check(Marshal.QueryInterface(unknown, in iid, out IntPtr native), $"QueryInterface({name})");
            return native;
        }
        finally
        {
            Release(unknown);
        }
    }

    private static int GetPointer(IntPtr comObject, int slot, out IntPtr value)
    {
        IntPtr result = IntPtr.Zero;
        int hr = Slot(comObject, slot)(comObject, &result);
        value = result;
        return hr;
    }

    /// <summary>The vtable method at <paramref name="slot"/> of a getter that writes one value through a pointer.</summary>
    private static delegate* unmanaged[Stdcall]<IntPtr, void*, int> Slot(IntPtr comObject, int slot) =>
        (delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{call} returned HRESULT 0x{hr:X8}");
        }
    }

    private static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.Release(comObject);
        }
    }

    /// <summary>
    /// The live <c>ContextMenuRequested</c> subscription: the interfaces it calls, the handler WebView2 holds, and the token
    /// that removes it. Disposing removes the subscription first and only then releases the handler.
    /// </summary>
    private sealed class Subscription(Action chosen, IAppLog log) : IDisposable
    {
        /// <summary>The <c>ICoreWebView2_11</c> the subscription was added on and is removed from.</summary>
        public IntPtr Core11;

        /// <summary>The <c>ICoreWebView2Environment9</c> that creates each menu's item.</summary>
        public IntPtr Environment9;

        private IntPtr _handler;
        private long _token;
        private bool _subscribed;
        private bool _disposed;

        public Action Chosen { get; } = chosen;

        public IAppLog Log { get; } = log;

        /// <summary>Builds the native handler and adds it to <see cref="Core11"/>.</summary>
        public void Subscribe()
        {
            _handler = NativeHandler(new MenuRequestedHandler(this), ContextMenuRequestedHandlerIid, "ICoreWebView2ContextMenuRequestedEventHandler");
            long token;
            var add = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(Core11), AddContextMenuRequestedSlot * IntPtr.Size);
            Check(add(Core11, _handler, &token), "ICoreWebView2_11::add_ContextMenuRequested");
            _token = token;
            _subscribed = true;
        }

        /// <summary>Called by the native handler for each menu about to open; a disposed subscription adds nothing.</summary>
        public void OnMenuRequested(IntPtr args)
        {
            if (!_disposed)
            {
                AddItem(args, Environment9, new ItemSelectedHandler(Chosen, Log));
            }
        }

        public void Dispose()
        {
            // Set first: when remove_ fails, WebView2 may still call the handler, which must then add nothing.
            _disposed = true;
            try
            {
                if (_subscribed)
                {
                    _subscribed = false;
                    var remove = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(Core11), RemoveContextMenuRequestedSlot * IntPtr.Size);
                    Check(remove(Core11, _token), "ICoreWebView2_11::remove_ContextMenuRequested");
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning($"Copy as raw menu: could not remove the WebView2 subscription: {exception.Message}", exception);
            }
            finally
            {
                // The handler is released only after remove_ returned, so WebView2 never calls a freed object.
                Release(_handler);
                Release(Environment9);
                Release(Core11);
                _handler = IntPtr.Zero;
                Environment9 = IntPtr.Zero;
                Core11 = IntPtr.Zero;
            }
        }
    }

    /// <summary>The managed side of the <c>ICoreWebView2ContextMenuRequestedEventHandler</c> WebView2 holds.</summary>
    private sealed class MenuRequestedHandler(Subscription subscription)
    {
        public Subscription Subscription { get; } = subscription;
    }

    /// <summary>The managed side of one menu item's <c>ICoreWebView2CustomItemSelectedEventHandler</c>.</summary>
    private sealed class ItemSelectedHandler(Action chosen, IAppLog log)
    {
        public Action Chosen { get; } = chosen;

        public IAppLog Log { get; } = log;
    }

    /// <summary>The managed side of one <c>ICoreWebView2CallDevToolsProtocolMethodCompletedHandler</c>.</summary>
    private sealed class DevToolsCompletedHandler(string method, IAppLog log)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Method { get; } = method;

        public IAppLog Log { get; } = log;
    }

    /// <summary>
    /// Builds the three handler kinds as native COM objects: IUnknown from the runtime plus one <c>Invoke</c>. A native
    /// reference keeps the managed object alive, so WebView2 never calls into a collected delegate.
    /// </summary>
    private sealed class HandlerWrappers : ComWrappers
    {
        public static readonly HandlerWrappers Instance = new();

        private static readonly ComInterfaceEntry* MenuRequestedEntry = Entry(
            ContextMenuRequestedHandlerIid,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&InvokeMenuRequested
        );

        private static readonly ComInterfaceEntry* ItemSelectedEntry = Entry(
            CustomItemSelectedHandlerIid,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&InvokeItemSelected
        );

        private static readonly ComInterfaceEntry* DevToolsCompletedEntry = Entry(
            DevToolsCompletedHandlerIid,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)&InvokeDevToolsCompleted
        );

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            ComInterfaceEntry* entry = obj switch
            {
                MenuRequestedHandler => MenuRequestedEntry,
                ItemSelectedHandler => ItemSelectedEntry,
                DevToolsCompletedHandler => DevToolsCompletedEntry,
                _ => null,
            };
            count = entry == null ? 0 : 1;
            return entry;
        }

        protected override object CreateObject(IntPtr externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();

        protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();

        /// <summary>One interface entry whose vtable is IUnknown followed by <paramref name="invoke"/>.</summary>
        private static ComInterfaceEntry* Entry(Guid iid, IntPtr invoke)
        {
            GetIUnknownImpl(out IntPtr queryInterface, out IntPtr addRef, out IntPtr release);
            IntPtr* vtable = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(HandlerWrappers), 4 * sizeof(IntPtr));
            vtable[0] = queryInterface;
            vtable[1] = addRef;
            vtable[2] = release;
            vtable[3] = invoke;
            var entry = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(HandlerWrappers), sizeof(ComInterfaceEntry));
            entry->IID = iid;
            entry->Vtable = (IntPtr)vtable;
            return entry;
        }

        /// <summary><c>ICoreWebView2ContextMenuRequestedEventHandler::Invoke(sender, args)</c>.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int InvokeMenuRequested(IntPtr self, IntPtr sender, IntPtr args)
        {
            Subscription? subscription = null;
            try
            {
                subscription = ComInterfaceDispatch.GetInstance<MenuRequestedHandler>((ComInterfaceDispatch*)self).Subscription;
                subscription.OnMenuRequested(args);
            }
            // Nothing may escape into WebView2; the menu opens as it was.
            catch (Exception exception)
            {
                subscription?.Log.LogWarning($"Copy as raw menu: {exception.Message}", exception);
            }

            return SOk;
        }

        /// <summary><c>ICoreWebView2CustomItemSelectedEventHandler::Invoke(sender, args)</c>.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int InvokeItemSelected(IntPtr self, IntPtr sender, IntPtr args)
        {
            ItemSelectedHandler? handler = null;
            try
            {
                handler = ComInterfaceDispatch.GetInstance<ItemSelectedHandler>((ComInterfaceDispatch*)self);
                handler.Chosen();
            }
            // Nothing may escape into WebView2.
            catch (Exception exception)
            {
                handler?.Log.LogWarning($"Copy as raw menu: {exception.Message}", exception);
            }

            return SOk;
        }

        /// <summary><c>ICoreWebView2CallDevToolsProtocolMethodCompletedHandler::Invoke(errorCode, result)</c>.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int InvokeDevToolsCompleted(IntPtr self, int errorCode, IntPtr result)
        {
            DevToolsCompletedHandler? handler = null;
            try
            {
                handler = ComInterfaceDispatch.GetInstance<DevToolsCompletedHandler>((ComInterfaceDispatch*)self);
                if (errorCode < 0)
                {
                    handler.Log.LogWarning(
                        $"Copy as raw menu: DevTools {handler.Method} failed with HRESULT 0x{errorCode:X8}: {Marshal.PtrToStringUni(result)}"
                    );
                }

                handler.Completion.TrySetResult(errorCode >= 0);
            }
            // Nothing may escape into WebView2.
            catch (Exception exception)
            {
                handler?.Log.LogWarning($"Copy as raw menu: {exception.Message}", exception);
                handler?.Completion.TrySetResult(false);
            }

            return SOk;
        }
    }

    /// <summary>
    /// Starts one DevTools protocol call on <paramref name="coreWebView2"/>; the handler's task completes when WebView2 answers.
    /// </summary>
    private static DevToolsCompletedHandler StartDevToolsCall(IntPtr coreWebView2, string method, string parametersJson, IAppLog log)
    {
        var handler = new DevToolsCompletedHandler(method, log);
        IntPtr native = NativeHandler(handler, DevToolsCompletedHandlerIid, "ICoreWebView2CallDevToolsProtocolMethodCompletedHandler");
        try
        {
            var call = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, IntPtr, int>)
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(coreWebView2), CallDevToolsProtocolMethodSlot * IntPtr.Size);
            fixed (char* methodName = method)
            fixed (char* parameters = parametersJson)
            {
                Check(call(coreWebView2, methodName, parameters, native), "ICoreWebView2::CallDevToolsProtocolMethod");
            }
        }
        finally
        {
            // WebView2 holds its own reference until it has called Invoke, which keeps the handler alive until then.
            Release(native);
        }

        return handler;
    }

    /// <summary>The <c>Input.dispatchMouseEvent</c> parameters of a right button <paramref name="type"/> at (x, y).</summary>
    private static string RightMouseEvent(string type, double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"type\":\"{type}\",\"x\":{x},\"y\":{y},\"button\":\"right\",\"clickCount\":1}}");
}

/// <summary>The dev path that opens the menu: a right click sent through the DevTools protocol.</summary>
internal static partial class WebView2ContextMenu
{
    /// <summary>
    /// Opens the web view's own context menu by sending a right button press and release at (<paramref name="x"/>,
    /// <paramref name="y"/>), in CSS pixels, through <c>Input.dispatchMouseEvent</c>. For a hidden-desktop check only.
    /// </summary>
    /// <returns>False when the handle is not WebView2's or a call failed; each failure is logged.</returns>
    public static async Task<bool> DispatchRightClickAsync(IPlatformHandle? handle, double x, double y, IAppLog log)
    {
        if (handle is not IWindowsWebView2PlatformHandle webView2)
        {
            return false;
        }

        // Each read of CoreWebView2 hands out a new reference, so it is read once and released at the end.
        IntPtr coreWebView2 = webView2.CoreWebView2;
        try
        {
            if (coreWebView2 == IntPtr.Zero)
            {
                log.LogWarning("Copy as raw menu: the WebView2 handle has no CoreWebView2 to send the right click to");
                return false;
            }

            foreach (string type in (string[])["mousePressed", "mouseReleased"])
            {
                DevToolsCompletedHandler sent = StartDevToolsCall(coreWebView2, "Input.dispatchMouseEvent", RightMouseEvent(type, x, y), log);
                if (!await sent.Completion.Task)
                {
                    return false;
                }
            }

            return true;
        }
        // A dev aid: a failure is logged and reported as false.
        catch (Exception exception)
        {
            log.LogWarning($"Copy as raw menu: could not send the right click: {exception.Message}", exception);
            return false;
        }
        finally
        {
            Release(coreWebView2);
        }
    }
}
