using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Yamv.Core.Diagnostics;
using Yamv.Core.Platform;

namespace Yamv.App.Services;

/// <summary>The file types the document pickers offer: markdown and text first, then all files.</summary>
public static class DocumentFileTypes
{
    /// <summary>The picker filters, in the order the picker lists them.</summary>
    public static IReadOnlyList<FilePickerFileType> Filters { get; } =
    [
        new("Markdown and text")
        {
            Patterns = ["*.md", "*.markdown", "*.mdx", "*.txt"],
            AppleUniformTypeIdentifiers = ["net.daringfireball.markdown", "public.plain-text"],
        },
        new("All files") { Patterns = ["*"], AppleUniformTypeIdentifiers = ["public.item"] },
    ];
}

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
/// The platform's file actions: the window's storage provider for the pickers, Explorer or Finder for reveal, and the window's
/// clipboard. Failures it can meet (no local path, no clipboard, a file manager that will not start) are logged as warnings.
/// </summary>
/// <param name="log">Where warnings go.</param>
/// <param name="startProcess">Starts a process; <see cref="StartProcess"/> outside tests.</param>
internal sealed class AvaloniaFileActions(IAppLog log, Action<ProcessStartInfo> startProcess) : IFileActions
{
    /// <summary>Starts the process and lets it run on its own, without waiting for it or reading its exit code.</summary>
    public static void StartProcess(ProcessStartInfo start) => Process.Start(start)?.Dispose();

    public async Task<IReadOnlyList<string>> PickDocumentsAsync(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var options = new FilePickerOpenOptions
        {
            Title = "Open files",
            AllowMultiple = true,
            FileTypeFilter = DocumentFileTypes.Filters,
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
            FileTypeFilter = DocumentFileTypes.Filters,
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
        if (RevealCommand.For(path, kind, OperatingSystem.IsMacOS()) is not { } command)
        {
            log.LogWarning($"Reveal: no folder of {path} exists.");
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
        }
    }

    public async Task CopyTextAsync(TopLevel owner, string text)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Clipboard is not { } clipboard)
        {
            log.LogWarning("Copy path: the window has no clipboard.");
            return;
        }

        await clipboard.SetTextAsync(text);
    }

    /// <summary>The local paths of the picked files, in pick order; a file with no local path is logged and left out.</summary>
    private List<string> LocalPaths(IReadOnlyList<IStorageFile> files)
    {
        List<string> paths = [];
        foreach (IStorageFile file in files)
        {
            using (file)
            {
                if (file.TryGetLocalPath() is string path)
                {
                    paths.Add(path);
                }
                else
                {
                    log.LogWarning($"Pick: {file.Name} has no local path and is not opened.");
                }
            }
        }

        return paths;
    }
}

/// <summary>The file work a document window hands to the platform: the pickers, revealing a path and copying text.</summary>
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

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    /// <param name="owner">The window whose clipboard is used.</param>
    /// <param name="text">The text to copy.</param>
    Task CopyTextAsync(TopLevel owner, string text);
}
