namespace Yamv.Core.Platform;

/// <summary>What a row asks to reveal: a file, shown selected in its folder, or a folder, opened to show its contents.</summary>
public enum RevealKind
{
    /// <summary>A document file.</summary>
    File,

    /// <summary>A folder: a group's root or a folder row.</summary>
    Folder,
}

/// <summary>
/// The process that shows a path in the platform's file manager: Explorer on Windows, Finder on macOS. Windows takes one
/// argument string in <see cref="Arguments"/>, since Explorer parses <c>/select,</c> itself; macOS takes the separate
/// <see cref="ArgumentList"/> items.
/// </summary>
public sealed class RevealCommand
{
    private const string Explorer = "explorer.exe";
    private const string Open = "/usr/bin/open";

    private RevealCommand(string program, string? arguments, IReadOnlyList<string> argumentList)
    {
        Program = program;
        Arguments = arguments;
        ArgumentList = argumentList;
    }

    /// <summary>The program to start.</summary>
    public string Program { get; }

    /// <summary>The whole argument string, on Windows; null on macOS.</summary>
    public string? Arguments { get; }

    /// <summary>The arguments one by one, on macOS; empty on Windows.</summary>
    public IReadOnlyList<string> ArgumentList { get; }

    /// <summary>
    /// The command that reveals <paramref name="path"/>: an existing file is shown selected in its folder, an existing folder is
    /// opened, and a path that no longer exists opens its nearest ancestor folder that does. Null when no folder of it exists.
    /// </summary>
    /// <param name="path">The file or folder to reveal.</param>
    /// <param name="kind">Whether <paramref name="path"/> is a file or a folder.</param>
    /// <param name="isMac">True on macOS, false on Windows.</param>
    public static RevealCommand? For(string path, RevealKind kind, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (kind == RevealKind.File && File.Exists(path))
        {
            return isMac ? new RevealCommand(Open, null, ["-R", path]) : new RevealCommand(Explorer, $"/select,\"{path}\"", []);
        }

        if (NearestExistingFolder(path) is not string folder)
        {
            return null;
        }

        return isMac ? new RevealCommand(Open, null, [folder]) : new RevealCommand(Explorer, ExplorerFolderArgument(folder), []);
    }

    /// <summary>
    /// The folder as Explorer's argument: quoted, except a root without a space (<c>C:\</c>, <c>\\server\share</c>), passed
    /// bare because a quoted <c>"C:\"</c> ends in <c>\"</c>, which a command line can read as an escaped quote.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    internal static string ExplorerFolderArgument(string folder) =>
        Path.GetPathRoot(folder) == folder && !folder.Contains(' ', StringComparison.Ordinal) ? folder : $"\"{folder}\"";

    /// <summary>The path itself when it is an existing folder, else its nearest ancestor folder that exists, else null.</summary>
    /// <param name="path">A file or folder path, which need not exist.</param>
    public static string? NearestExistingFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? folder = path;
        while (!string.IsNullOrEmpty(folder))
        {
            if (Directory.Exists(folder))
            {
                return folder;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }
}
