namespace Yamv.Core.Platform;

/// <summary>
/// The command that shows a caution dialog on macOS: <c>osascript</c> with one AppleScript statement. A launcher has no window
/// of its own, so the dialog is macOS's own, titled YAMV and with one OK button.
/// </summary>
public sealed class AlertCommand
{
    private const string OsaScript = "/usr/bin/osascript";
    private const string Title = "YAMV";

    private AlertCommand(string program, IReadOnlyList<string> argumentList)
    {
        Program = program;
        ArgumentList = argumentList;
    }

    /// <summary>The program to start, as <see cref="OpenFileCommand"/> names <c>open</c>: the system's own <c>osascript</c>.</summary>
    public string Program { get; }

    /// <summary>The <c>-e</c> and the AppleScript statement, one argument each, so no shell ever reads the text.</summary>
    public IReadOnlyList<string> ArgumentList { get; }

    /// <summary>The command that shows <paramref name="text"/> in a caution dialog titled YAMV.</summary>
    /// <param name="text">The sentence the dialog shows, kept whole whatever it holds.</param>
    public static AlertCommand For(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string script = $"display dialog \"{Escape(text)}\" with title \"{Title}\" buttons {{\"OK\"}} default button \"OK\" with icon caution";
        return new AlertCommand(OsaScript, ["-e", script]);
    }

    /// <summary>The text with backslashes and double quotes escaped, the two characters an AppleScript string literal reads.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
