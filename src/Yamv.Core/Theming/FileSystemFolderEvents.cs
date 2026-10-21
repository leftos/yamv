using System.Globalization;

namespace Yamv.Core.Theming;

/// <summary>A <see cref="FileSystemWatcher"/> on one folder's own entries, raising each of its events as a <see cref="FolderEvent"/>.</summary>
/// <remarks>
/// Events arrive on a thread-pool thread on Windows, and on the run-loop thread every watcher in the process shares on macOS.
/// The constructor throws when the folder does not exist, and <see cref="Start"/> when it cannot be watched.
/// </remarks>
internal sealed class FileSystemFolderEvents : IFolderEvents
{
    private readonly FileSystemWatcher watcher;

    public FileSystemFolderEvents(string folder)
    {
        watcher = new FileSystemWatcher(folder, "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        watcher.Created += OnEntryEvent;
        watcher.Changed += OnEntryEvent;
        watcher.Deleted += OnEntryEvent;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
    }

    public event Action<FolderEvent>? Raised;

    public void Start() => watcher.EnableRaisingEvents = true;

    public void Dispose() => watcher.Dispose();

    private static FolderEventKind KindOf(WatcherChangeTypes type) =>
        type switch
        {
            WatcherChangeTypes.Created => FolderEventKind.Created,
            WatcherChangeTypes.Deleted => FolderEventKind.Deleted,
            _ => FolderEventKind.Changed,
        };

    private void OnEntryEvent(object sender, FileSystemEventArgs e) => Raised?.Invoke(new FolderEvent(KindOf(e.ChangeType), e.Name, null, null));

    private void OnRenamed(object sender, RenamedEventArgs e) => Raised?.Invoke(new FolderEvent(FolderEventKind.Renamed, e.Name, e.OldName, null));

    private void OnError(object sender, ErrorEventArgs e) => Raised?.Invoke(new FolderEvent(FolderEventKind.Error, null, null, e.GetException()));
}

/// <summary>Probes a real folder: its identity is the path and creation time of the folder, or of the final target of any link it is.</summary>
/// <remarks>
/// A folder deleted and recreated at once gets a new creation time: NTFS name tunnelling keeps the creation time of a file
/// recreated under a deleted file's name, but not of a folder.
/// </remarks>
internal sealed class FileSystemFolderProbe : IFolderProbe
{
    public FolderState Probe(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
        {
            return FolderState.Missing;
        }

        FileSystemInfo target = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        return FolderState.Present(string.Create(CultureInfo.InvariantCulture, $"{target.CreationTimeUtc.Ticks}|{target.FullName}"));
    }
}
