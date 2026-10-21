namespace Yamv.Core.Platform;

/// <summary>
/// The process that opens a file in whatever app the system associates with its type: the shell itself on Windows, which
/// runs the file's association, and <c>open</c> on macOS. Windows is given the file as the program, with no verb set.
/// </summary>
public sealed class OpenFileCommand
{
    private const string Open = "/usr/bin/open";

    private OpenFileCommand(string program, bool useShellExecute, IReadOnlyList<string> argumentList)
    {
        Program = program;
        UseShellExecute = useShellExecute;
        ArgumentList = argumentList;
    }

    /// <summary>The file to open on Windows; the program to start on macOS.</summary>
    public string Program { get; }

    /// <summary>Whether the shell resolves <see cref="Program"/>, which is how Windows runs the file's association.</summary>
    public bool UseShellExecute { get; }

    /// <summary>The arguments one by one, on macOS; empty on Windows, where the file is the program.</summary>
    public IReadOnlyList<string> ArgumentList { get; }

    /// <summary>The command that opens <paramref name="path"/> in its associated app.</summary>
    /// <param name="path">The file to open, kept whole whatever it holds.</param>
    /// <param name="isMac">True on macOS, false on Windows.</param>
    public static OpenFileCommand For(string path, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(path);
        return isMac ? new OpenFileCommand(Open, false, [path]) : new OpenFileCommand(path, true, []);
    }
}
