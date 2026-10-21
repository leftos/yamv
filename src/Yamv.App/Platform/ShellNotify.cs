using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yamv.App.Platform;

/// <summary>Tells Explorer that the file associations changed, so its "Open with" list refreshes without a sign-out.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellNotify
{
    private const int AssociationChangedEvent = 0x08000000;
    private const uint IdListFlag = 0;

    /// <summary>Broadcasts <c>SHCNE_ASSOCCHANGED</c>, the notification Explorer refreshes associations from.</summary>
    public static void AssociationsChanged() => SHChangeNotify(AssociationChangedEvent, IdListFlag, item1: 0, item2: 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
