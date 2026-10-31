using Yamv.Core.Platform;

namespace Yamv.Core.Links;

/// <summary>The words of the card that asks before a link opens a file YAMV does not show.</summary>
/// <param name="Title">The question, naming the file or folder.</param>
/// <param name="Body">What the file is and what will open it.</param>
public sealed record ConfirmText(string Title, string Body)
{
    private static readonly Dictionary<string, string> TypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ps1"] = "a PowerShell script",
        [".bat"] = "a batch file",
        [".cmd"] = "a batch file",
        [".sh"] = "a shell script",
        [".command"] = "a shell script",
        [".py"] = "a Python script",
        [".js"] = "a script",
        [".vbs"] = "a script",
        [".exe"] = "a program",
        [".com"] = "a program",
        [".app"] = "an app",
        [".msi"] = "an installer",
        [".pkg"] = "an installer",
        [".scr"] = "a program",
        [".pif"] = "a program",
        [".cpl"] = "a Control Panel item",
        [".hta"] = "a web app",
        [".reg"] = "a registry file",
        [".msc"] = "a management console file",
        [".wsf"] = "a script",
        [".jar"] = "a Java program",
        [".html"] = "a web page",
        [".htm"] = "a web page",
        [".zip"] = "an archive",
        [".7z"] = "an archive",
        [".rar"] = "an archive",
        [".docx"] = "a Word document",
        [".doc"] = "a Word document",
        [".xlsx"] = "an Excel workbook",
        [".xls"] = "an Excel workbook",
        [".pptx"] = "a PowerPoint presentation",
        [".ppt"] = "a PowerPoint presentation",
    };

    /// <summary>The types that only point at something else, which can be any program or address.</summary>
    private static readonly HashSet<string> Shortcuts = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".webloc", ".fileloc" };

    /// <summary>The sentence an elevated instance's card ends with, for anything but a folder.</summary>
    private const string ElevatedSentence = " It will run as Administrator.";

    /// <summary>The words for <paramref name="path"/>, which show it as <paramref name="name"/>.</summary>
    /// <param name="path">The real path, which the type is read from.</param>
    /// <param name="name">The name the card shows, exactly as given; a caller that shows untrusted names sanitises it first.</param>
    /// <param name="isFolder">Whether <paramref name="path"/> is a folder on disk, which a macOS bundle is.</param>
    /// <param name="isMac">Whether the card names macOS and Finder rather than Windows and Explorer.</param>
    /// <param name="elevated">
    /// Whether YAMV runs as the elevated (Administrator) instance. A program, script or file it opens inherits the elevation, so
    /// the card says so; a folder does not, since Explorer hands it to the running non-elevated Explorer, so its card says nothing.
    /// </param>
    public static ConfirmText For(string path, string name, bool isFolder, bool isMac, bool elevated)
    {
        ConfirmText text = Unelevated(path, name, isFolder, isMac);
        return elevated && !isFolder ? text with { Body = text.Body + ElevatedSentence } : text;
    }

    private static ConfirmText Unelevated(string path, string name, bool isFolder, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        string title = $"Open {name}?";
        string extension = Path.GetExtension(Path.TrimEndingDirectorySeparator(path));
        // On macOS a folder with an extension is a bundle, which the system opens as a file rather than showing in Finder.
        if (isFolder && !(isMac && extension.Length > 0))
        {
            return new ConfirmText(title, $"It's a folder. YAMV doesn't show folders; it will open in {(isMac ? "Finder" : "Explorer")}.");
        }

        // The Windows shell reads a name whose last dot is followed by a space as having no extension, so the card says the same.
        if (!isMac && ShellExtension.IsMissing(path))
        {
            extension = string.Empty;
        }

        if (Shortcuts.Contains(extension))
        {
            return new ConfirmText(title, $"It's a shortcut ({extension.ToLowerInvariant()}). It can start any program or open any address.");
        }

        string system = isMac ? "macOS" : "Windows";
        return new ConfirmText(title, $"{TypeSentence(extension)} YAMV doesn't show this type; it will open with the app {system} uses for it.");
    }

    /// <summary>The sentence naming the type: in words when it is listed, else by its lower-cased extension.</summary>
    private static string TypeSentence(string extension)
    {
        if (extension.Length == 0)
        {
            return "It's a file with no type.";
        }

        string shown = extension.ToLowerInvariant();
        return TypeWords.TryGetValue(extension, out string? words) ? $"It's {words} ({shown})." : $"It's a {shown} file.";
    }
}
