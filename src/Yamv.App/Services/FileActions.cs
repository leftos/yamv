using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Yamv.App.ViewModels;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Links;
using Yamv.Core.Platform;

namespace Yamv.App.Services;

/// <summary>The file types the theme pickers offer: YAMV themes first, then any JSON file.</summary>
public static class ThemeFileTypes
{
    /// <summary>The picker filters, in the order the picker lists them.</summary>
    public static IReadOnlyList<FilePickerFileType> Filters { get; } =
    [
        new("YAMV theme") { Patterns = ["*.yamv-theme.json"], AppleUniformTypeIdentifiers = ["public.json"] },
        new("JSON") { Patterns = ["*.json"], AppleUniformTypeIdentifiers = ["public.json"] },
    ];
}

/// <summary>
/// The platform's file actions: the window's storage provider for the pickers, Explorer or Finder for reveal, the app the
/// system associates with a file for opening it, and the window's clipboard. A failure or refusal of an action the user started
/// is logged as a warning and shown in a toast as well.
/// </summary>
/// <param name="log">Where warnings go.</param>
/// <param name="toasts">Where a failure or refusal of a user's action is shown as well; null for a window with no toasts, whose failures are only logged.</param>
/// <param name="startProcess">Starts a process and lets it run on its own; <see cref="StartProcess"/> outside tests.</param>
/// <param name="seams">The platform and the way a process is waited for; <see cref="FileActionSeams.Default"/> outside tests.</param>
internal sealed class AvaloniaFileActions(IAppLog log, IToasts? toasts, Action<ProcessStartInfo> startProcess, FileActionSeams seams) : IFileActions
{
    /// <summary>The longest <c>open</c> is waited for; it hands the file off and returns well before this.</summary>
    private static readonly TimeSpan MacOpenTimeout = TimeSpan.FromSeconds(2);

    private const string JsonExtension = ".json";

    private const string ConfirmedLogPrefix = "Open confirmed link:";

    /// <summary>
    /// The program extensions the Windows shell tries on a path with no extension of its own, before the path itself: opening
    /// <c>foo</c> ran <c>foo.cmd</c> beside it, while opening <c>foo.txt</c> left <c>foo.txt.cmd</c> alone.
    /// </summary>
    private static readonly string[] ProgramExtensions = [".pif", ".com", ".exe", ".bat", ".cmd", ".lnk"];

    /// <summary>The document pickers' filters, in the order the picker lists them, built from <see cref="DocumentFileTypes"/>.</summary>
    private static readonly IReadOnlyList<FilePickerFileType> DocumentFilters =
    [
        new("Markdown and text")
        {
            Patterns = [.. DocumentFileTypes.Patterns],
            AppleUniformTypeIdentifiers = ["net.daringfireball.markdown", "public.plain-text"],
        },
        new("All files") { Patterns = ["*"], AppleUniformTypeIdentifiers = ["public.item"] },
    ];

    /// <summary>Starts the process and lets it run on its own, without waiting for it or reading its exit code.</summary>
    public static void StartProcess(ProcessStartInfo start) => Process.Start(start)?.Dispose();

    public async Task<IReadOnlyList<string>> PickDocumentsAsync(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var options = new FilePickerOpenOptions
        {
            Title = "Open files",
            AllowMultiple = true,
            FileTypeFilter = DocumentFilters,
        };
        return LocalPaths(await owner.StorageProvider.OpenFilePickerAsync(options));
    }

    public async Task<string?> PickReplacementAsync(TopLevel owner, string oldPath)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(oldPath);
        IStorageProvider storage = owner.StorageProvider;
        string? folder = RevealCommand.NearestExistingFolder(oldPath);
        using IStorageFolder? start = folder is null ? null : await storage.TryGetFolderFromPathAsync(folder);
        var options = new FilePickerOpenOptions
        {
            Title = $"Locate {Path.GetFileName(oldPath)}",
            AllowMultiple = false,
            FileTypeFilter = DocumentFilters,
            SuggestedStartLocation = start,
        };
        return LocalPaths(await storage.OpenFilePickerAsync(options)).FirstOrDefault();
    }

    public async Task<string?> PickThemeAsync(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var options = new FilePickerOpenOptions
        {
            Title = "Import a theme",
            AllowMultiple = false,
            FileTypeFilter = ThemeFileTypes.Filters,
        };
        return LocalPaths(await owner.StorageProvider.OpenFilePickerAsync(options)).FirstOrDefault();
    }

    public async Task<string?> PickThemeDestinationAsync(TopLevel owner, string suggestedName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(suggestedName);
        var options = new FilePickerSaveOptions
        {
            Title = "Export theme",
            SuggestedFileName = suggestedName,
            DefaultExtension = "yamv-theme.json",
            FileTypeChoices = ThemeFileTypes.Filters,
            ShowOverwritePrompt = true,
        };
        using IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(options);
        if (file is null)
        {
            return null;
        }

        string? path = file.TryGetLocalPath();
        if (path is null)
        {
            log.LogWarning($"Export: {file.Name} has no local path; nothing is exported.");
        }

        return path;
    }

    public void Reveal(string path, RevealKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (RevealCommand.For(path, kind, seams.Platform == FilePlatform.MacOS) is not { } command)
        {
            log.LogWarning($"Reveal: no folder of {path} exists.");
            string folder = kind == RevealKind.File ? Path.GetDirectoryName(path) ?? path : path;
            toasts?.Show(ToastKind.Warning, $"{ToastValue.Code(folder)} no longer exists");
            return;
        }

        // Explorer returns 1 even when it succeeds, so the exit code says nothing and is not read.
        var start = new ProcessStartInfo(command.Program) { UseShellExecute = false };
        if (command.Arguments is string arguments)
        {
            start.Arguments = arguments;
        }

        foreach (string argument in command.ArgumentList)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            startProcess(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            log.LogWarning($"Reveal: could not start {command.Program} for {path}.", exception);
            string fileManager = seams.Platform == FilePlatform.MacOS ? "Finder" : "Explorer";
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } fileName ? fileName : path;
            toasts?.Show(ToastKind.Warning, $"Couldn't open {fileManager} for {ToastValue.Code(name)}");
        }
    }

    public void OpenWithDefaultApp(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Only a fully qualified path can be opened: {path}", nameof(path));
        }

        if (!string.Equals(Path.GetExtension(path), JsonExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only a .json file can be opened as text: {path}", nameof(path));
        }

        OpenInDefaultApp(path, "Edit as text:");
    }

    public void OpenViewable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsViewablePath(path, seams.Platform == FilePlatform.Windows))
        {
            throw new ArgumentException($"Only a fully qualified local file of a viewable type can be opened from a link: {path}", nameof(path));
        }

        OpenInDefaultApp(path, "Open link:");
    }

    public void OpenConfirmed(string path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        bool windows = seams.Platform == FilePlatform.Windows;
        if (!IsPlainLocalPath(path, windows))
        {
            throw new ArgumentException($"Only a fully qualified local path can be opened from a link: {path}", nameof(path));
        }

        if (isDirectory && windows)
        {
            OpenFolderOnWindows(path);
            return;
        }

        OpenInDefaultApp(path, ConfirmedLogPrefix);
    }

    /// <summary>
    /// Opens the folder in the Explorer of the Windows folder, past the shell, which may run a program named like the folder
    /// instead.
    /// </summary>
    /// <param name="path">The folder to open, already checked.</param>
    private void OpenFolderOnWindows(string path)
    {
        string explorer = Path.Combine(seams.WindowsFolder, "explorer.exe");
        var start = new ProcessStartInfo(explorer, RevealCommand.ExplorerFolderArgument(path)) { UseShellExecute = false };
        StartOnWindows(start, path, ConfirmedLogPrefix);
    }

    /// <summary>Whether a link may open <paramref name="path"/> without asking: a plain local path of a viewable type.</summary>
    private static bool IsViewablePath(string path, bool windows) =>
        IsPlainLocalPath(path, windows) && LinkPolicy.ViewableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is one a link may name: fully qualified, with no control character, and on Windows rooted at
    /// a drive letter (so never a share or device path), with no <c>:</c> after the root (an alternate data stream) and no name
    /// ending in a dot or a space, which Windows would rewrite.
    /// </summary>
    private static bool IsPlainLocalPath(string path, bool windows) =>
        Path.IsPathFullyQualified(path) && !path.Any(character => character < ' ') && (!windows || IsPlainWindowsPath(path));

    /// <summary>
    /// Whether <paramref name="path"/> starts at a drive root such as <c>C:\</c>, has no <c>:</c> after it, and has no name
    /// ending in a dot or a space.
    /// </summary>
    private static bool IsPlainWindowsPath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] is '\\' or '/'
        && !path.AsSpan(3).Contains(':')
        && !path.Split('\\', '/').Any(name => name.EndsWith('.') || name.EndsWith(' '));

    /// <summary>Opens <paramref name="path"/> in its default app, with no verb and no arguments.</summary>
    /// <param name="path">The file to open, already checked.</param>
    /// <param name="logPrefix">What the log lines start with, naming the action.</param>
    /// <exception cref="IOException">No app would open the file, or it has no extension and Windows might run a program beside it.</exception>
    private void OpenInDefaultApp(string path, string logPrefix)
    {
        FilePlatform platform = seams.Platform;
        if (platform == FilePlatform.Other)
        {
            throw new PlatformNotSupportedException($"Opening {path} in its default app is not supported on this platform.");
        }

        if (
            platform == FilePlatform.Windows
            && Path.GetExtension(path).Length == 0
            && ProgramExtensions.Any(extension => seams.FileExists(path + extension))
        )
        {
            throw new IOException($"{path} has a program of the same name beside it, which Windows might run instead.");
        }

        var command = OpenFileCommand.For(path, platform == FilePlatform.MacOS);
        var start = new ProcessStartInfo(command.Program) { UseShellExecute = command.UseShellExecute };
        foreach (string argument in command.ArgumentList)
        {
            start.ArgumentList.Add(argument);
        }

        if (platform == FilePlatform.Windows)
        {
            StartOnWindows(start, path, logPrefix);
            return;
        }

        RunOpen(start, path, logPrefix);
    }

    /// <summary>Starts the open, whose failure is the window's: no app is associated with the file.</summary>
    /// <param name="start">The open to start.</param>
    /// <param name="path">The file being opened, named in the message.</param>
    /// <param name="logPrefix">What the log line starts with.</param>
    /// <exception cref="IOException">It refused to start.</exception>
    private void StartOnWindows(ProcessStartInfo start, string path, string logPrefix)
    {
        try
        {
            startProcess(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            log.LogWarning($"{logPrefix} could not open {path}.", exception);
            throw new IOException($"Could not open {path} in its default app.", exception);
        }
    }

    /// <summary>
    /// Runs <c>open</c> and reads what it said: it returns once the hand-off is done, so a non-zero exit means no app took
    /// the file. A run that outlives the wait is logged and left alone, since the file may still open.
    /// </summary>
    /// <param name="start">The <c>open</c> to run.</param>
    /// <param name="path">The file being opened, named in the messages.</param>
    /// <param name="logPrefix">What the log lines start with.</param>
    /// <exception cref="IOException"><c>open</c> reported no app would take the file.</exception>
    private void RunOpen(ProcessStartInfo start, string path, string logPrefix)
    {
        ProcessOutcome outcome = seams.Run(start, MacOpenTimeout);
        if (outcome.TimedOut)
        {
            log.LogWarning($"{logPrefix} open did not finish within {MacOpenTimeout.TotalSeconds:0} s for {path}.");
            return;
        }

        if (outcome.ExitCode != 0)
        {
            log.LogWarning($"{logPrefix} open exited {outcome.ExitCode} for {path}: {outcome.StandardError.Trim()}");
            throw new IOException($"Could not open {path} in its default app: {outcome.StandardError.Trim()}");
        }
    }

    public async Task CopyTextAsync(TopLevel owner, string text)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await CopyTextToAsync(owner.Clipboard, text);
    }

    /// <summary>
    /// Puts <paramref name="text"/> on <paramref name="clipboard"/>, logging a window with no clipboard as a warning and showing
    /// it in a toast as well.
    /// </summary>
    /// <param name="clipboard">The clipboard to copy to; null for a window that has none.</param>
    /// <param name="text">The text to copy.</param>
    internal async Task CopyTextToAsync(IClipboard? clipboard, string text)
    {
        if (clipboard is not { } target)
        {
            log.LogWarning("Copy path: the window has no clipboard.");
            toasts?.Show(ToastKind.Warning, "Couldn't copy the path: no clipboard");
            return;
        }

        await target.SetTextAsync(text);
    }

    /// <summary>The local paths of the picked files, in pick order, keeping each pick's name for <see cref="KeepLocal"/>.</summary>
    private List<string> LocalPaths(IReadOnlyList<IStorageFile> files)
    {
        List<(string Name, string? Path)> picks = [];
        foreach (IStorageFile file in files)
        {
            using (file)
            {
                picks.Add((file.Name, file.TryGetLocalPath()));
            }
        }

        return KeepLocal(picks);
    }

    /// <summary>
    /// The local paths of <paramref name="picks"/>, in pick order; a pick with no local path is logged and left out, and the
    /// refusal is shown in one toast for the files picked. Avalonia's storage interfaces cannot be implemented in tests, so the
    /// tests call this rather than <see cref="LocalPaths"/>.
    /// </summary>
    /// <param name="picks">Each picked file's name and its local path, or null for a file with none.</param>
    internal List<string> KeepLocal(IReadOnlyList<(string Name, string? Path)> picks)
    {
        List<string> paths = [];
        List<string> refused = [];
        foreach ((string name, string? path) in picks)
        {
            if (path is not null)
            {
                paths.Add(path);
            }
            else
            {
                log.LogWarning($"Pick: {name} has no local path and is not opened.");
                refused.Add(name);
            }
        }

        if (refused.Count == 1)
        {
            toasts?.Show(ToastKind.Refusal, $"YAMV can't open {ToastValue.Code(refused[0])}: it isn't a local file");
        }
        else if (refused.Count > 1)
        {
            toasts?.Show(ToastKind.Refusal, $"YAMV can't open {refused.Count} picked files: they aren't local files");
        }

        return paths;
    }
}

/// <summary>The file work a document window hands to the platform: the pickers, revealing a path, opening a file and copying text.</summary>
internal interface IFileActions
{
    /// <summary>Asks the user for documents to open; empty when the pick was cancelled.</summary>
    /// <param name="owner">The window the picker belongs to.</param>
    Task<IReadOnlyList<string>> PickDocumentsAsync(TopLevel owner);

    /// <summary>Asks the user for the file that takes the place of <paramref name="oldPath"/>; null when cancelled.</summary>
    /// <param name="owner">The window the picker belongs to.</param>
    /// <param name="oldPath">The path the window holds, which need not exist.</param>
    Task<string?> PickReplacementAsync(TopLevel owner, string oldPath);

    /// <summary>Asks the user for one theme file to import; null when cancelled.</summary>
    /// <param name="owner">The window the picker belongs to.</param>
    Task<string?> PickThemeAsync(TopLevel owner);

    /// <summary>Asks the user where to export a theme; null when cancelled. The platform's picker confirms an overwrite.</summary>
    /// <param name="owner">The window the picker belongs to.</param>
    /// <param name="suggestedName">The file name the picker suggests.</param>
    Task<string?> PickThemeDestinationAsync(TopLevel owner, string suggestedName);

    /// <summary>Shows <paramref name="path"/> in Explorer or Finder.</summary>
    /// <param name="path">The file or folder to reveal.</param>
    /// <param name="kind">Whether <paramref name="path"/> is a file or a folder.</param>
    void Reveal(string path, RevealKind kind);

    /// <summary>Opens <paramref name="path"/> in whatever app the system associates with it.</summary>
    /// <param name="path">The file to open.</param>
    /// <exception cref="IOException">No app would open the file.</exception>
    void OpenWithDefaultApp(string path);

    /// <summary>
    /// Opens <paramref name="path"/>, a file a link named, in whatever app the system associates with it, with no verb and no
    /// arguments.
    /// </summary>
    /// <param name="path">
    /// A fully qualified local path of a viewable type (<see cref="Yamv.Core.Links.LinkPolicy.ViewableExtensions"/>); not a
    /// share or device path, and on Windows no name in it ends in a dot or a space.
    /// </param>
    /// <exception cref="ArgumentException">The path is not one a link may open.</exception>
    /// <exception cref="IOException">No app would open the file.</exception>
    void OpenViewable(string path);

    /// <summary>
    /// Opens <paramref name="path"/>, a file or folder a link named and the user agreed to open, in whatever app the system
    /// associates with it, with no verb and no arguments; a folder on Windows opens in Explorer, past the shell.
    /// </summary>
    /// <param name="path">
    /// A fully qualified local path of any type; not a share or device path, and on Windows no name in it ends in a dot or a space.
    /// </param>
    /// <param name="isDirectory">Whether <paramref name="path"/> is a folder, as the link policy resolved it.</param>
    /// <exception cref="ArgumentException">The path is not one a link may open.</exception>
    /// <exception cref="IOException">No app would open the file, or on Windows it has no extension and a program of its name sits beside it.</exception>
    void OpenConfirmed(string path, bool isDirectory);

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    /// <param name="owner">The window whose clipboard is used.</param>
    /// <param name="text">The text to copy.</param>
    Task CopyTextAsync(TopLevel owner, string text);
}

/// <summary>The desktops YAMV's file actions tell apart.</summary>
internal enum FilePlatform
{
    /// <summary>Windows: Explorer, and the shell opening a file by its association.</summary>
    Windows,

    /// <summary>macOS: Finder, and <c>/usr/bin/open</c>.</summary>
    MacOS,

    /// <summary>Anything else, where neither reveal nor open is known.</summary>
    Other,
}

/// <summary>What a process said when it was run and waited for.</summary>
/// <param name="TimedOut">Whether it was still running when the wait ended.</param>
/// <param name="ExitCode">The code it exited with; it means nothing when <paramref name="TimedOut"/> is true.</param>
/// <param name="StandardError">What it wrote to its error stream.</param>
internal sealed record ProcessOutcome(bool TimedOut, int ExitCode, string StandardError);

/// <summary>
/// What a test replaces: the platform the actions run on, the way a process is run and waited for, whether a file exists, and
/// the Windows folder.
/// </summary>
/// <param name="Platform">The desktop the actions run on.</param>
/// <param name="Run">Starts a process, waits up to the timeout for it, and reports what it said.</param>
/// <param name="FileExists">Whether a file exists at a path.</param>
/// <param name="WindowsFolder">The folder Windows is installed in, which holds Explorer; empty elsewhere.</param>
internal sealed record FileActionSeams(
    FilePlatform Platform,
    Func<ProcessStartInfo, TimeSpan, ProcessOutcome> Run,
    Func<string, bool> FileExists,
    string WindowsFolder
)
{
    /// <summary>This machine's desktop, disk and Windows folder, and a real process run and waited for.</summary>
    public static FileActionSeams Default { get; } =
        new(ThisPlatform, RunProcess, File.Exists, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static FilePlatform ThisPlatform =>
        OperatingSystem.IsWindows() ? FilePlatform.Windows
        : OperatingSystem.IsMacOS() ? FilePlatform.MacOS
        : FilePlatform.Other;

    /// <summary>Starts the process, waits up to <paramref name="timeout"/> for it, and reads its error stream when it exits.</summary>
    /// <param name="start">The process to run.</param>
    /// <param name="timeout">How long to wait before leaving it running.</param>
    /// <returns>Its code and error stream, or that it outlived the wait.</returns>
    private static ProcessOutcome RunProcess(ProcessStartInfo start, TimeSpan timeout)
    {
        start.UseShellExecute = false;
        start.RedirectStandardError = true;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        if (!process.WaitForExit(timeout))
        {
            return new ProcessOutcome(TimedOut: true, ExitCode: 0, StandardError: string.Empty);
        }

        return new ProcessOutcome(TimedOut: false, ExitCode: process.ExitCode, StandardError: process.StandardError.ReadToEnd());
    }
}
