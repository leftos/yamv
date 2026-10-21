namespace Yamv.Core.Input;

/// <summary>A window command a keyboard shortcut runs.</summary>
public enum ShortcutCommand
{
    /// <summary>Close the selected document (Ctrl/Cmd+W).</summary>
    CloseDocument,

    /// <summary>Select the next document in tree order (Ctrl+Tab).</summary>
    NextDocument,

    /// <summary>Select the previous document in tree order (Ctrl+Shift+Tab).</summary>
    PreviousDocument,

    /// <summary>Hide or show the sidebar (Ctrl/Cmd+B).</summary>
    ToggleSidebar,

    /// <summary>Read and render the selected document again (Ctrl/Cmd+R).</summary>
    Reload,

    /// <summary>Pick files and add them to the window (Ctrl/Cmd+O).</summary>
    OpenFiles,
}

/// <summary>Maps a key press to the window command it runs, the same way for a native key and one the page forwarded.</summary>
public static class Shortcuts
{
    /// <summary>
    /// The command <paramref name="key"/> runs with these modifiers, or null. The command key is Ctrl on Windows and Cmd (Meta)
    /// on macOS, exactly one of the two and never with Alt; letters match either case, with Shift ignored. Tab takes Ctrl on
    /// both platforms: alone it is the next document, with Shift the previous.
    /// </summary>
    /// <param name="key">The key's name as the DOM <c>KeyboardEvent.key</c> gives it: <c>"w"</c>, <c>"W"</c>, <c>"Tab"</c>.</param>
    /// <param name="ctrl">True when Ctrl is held.</param>
    /// <param name="meta">True when Meta (Cmd on macOS, the Windows key on Windows) is held.</param>
    /// <param name="shift">True when Shift is held.</param>
    /// <param name="alt">True when Alt (Option on macOS) is held.</param>
    /// <param name="isMac">True on macOS.</param>
    public static ShortcutCommand? Match(string key, bool ctrl, bool meta, bool shift, bool alt, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (alt)
        {
            return null;
        }

        if (key == "Tab")
        {
            return ctrl && !meta ? (shift ? ShortcutCommand.PreviousDocument : ShortcutCommand.NextDocument) : null;
        }

        bool command = isMac ? meta && !ctrl : ctrl && !meta;
        return command ? Letter(key) : null;
    }

    private static ShortcutCommand? Letter(string key) =>
        key.ToUpperInvariant() switch
        {
            "W" => ShortcutCommand.CloseDocument,
            "B" => ShortcutCommand.ToggleSidebar,
            "R" => ShortcutCommand.Reload,
            "O" => ShortcutCommand.OpenFiles,
            _ => null,
        };
}
