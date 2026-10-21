using Yamv.Core.Diagnostics;

namespace Yamv.Core.Widths;

/// <summary>
/// The per-folder content widths in <c>widths.json</c>, shared by every YAMV instance. Safe to call from any thread.
/// </summary>
/// <remarks>
/// <para>
/// Every call reads the file again and parses it only when its bytes changed, so a change another instance made shows
/// at the next call. A read never changes the file: a missing file is no widths, and a file that is locked or does not
/// parse (an editor saving in place) keeps the widths read before.
/// </para>
/// <para>
/// Only <see cref="Set"/>, <see cref="SetDefault"/>, <see cref="Remove"/> and <see cref="ClearAtOrBelow"/> write. Each
/// one changes a fresh read of the file and replaces the file in one move, so a reader never sees half a file. A
/// change that cannot read or write the file is logged, leaves the file as it was and returns <see langword="false"/>.
/// A change over a file that does not parse first copies it to <c>widths.json.bad</c>. A change that changes nothing
/// writes nothing.
/// </para>
/// <para>
/// The contract is one store per file per process (the App holds one instance); separate processes coordinate through
/// the file alone. Calls are serialised by a lock that is held through the file IO and the retry pauses, up to about
/// 300 ms when the file is held, so a caller keeps a call that may change the file off the UI thread.
/// </para>
/// </remarks>
public sealed class WidthStore
{
    private const int Retries = 5;
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TemporaryFileMaxAge = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly IAppLog log;
    private readonly Action<TimeSpan> pause;
    private WidthsState widths = WidthsState.Empty;
    private byte[]? seenBytes;
    private bool seenBytesParsed = true;
    private bool readFailing;

    /// <summary>Creates a store for <paramref name="filePath"/> and deletes temporary files a crashed writer left behind.</summary>
    public WidthStore(string filePath, IAppLog log)
        : this(filePath, log, Thread.Sleep, TimeProvider.System) { }

    /// <summary>Creates a store with the pause between retries and the clock injected, so tests control both.</summary>
    /// <param name="filePath">The widths file.</param>
    /// <param name="log">Where failures are reported.</param>
    /// <param name="pause">Waits between retries of a read or move the file system refused.</param>
    /// <param name="time">The clock that ages leftover temporary files.</param>
    internal WidthStore(string filePath, IAppLog log, Action<TimeSpan> pause, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(time);
        FilePath = Path.GetFullPath(filePath);
        this.log = log;
        this.pause = pause;
        DeleteOldTemporaryFiles(time.GetUtcNow());
    }

    /// <summary>The JSON file the widths live in.</summary>
    public string FilePath { get; }

    /// <summary>The app default: the width of a document no folder at or above has a width for.</summary>
    public ContentWidth Default
    {
        get
        {
            lock (gate)
            {
                ReadForLookup();
                return widths.Default;
            }
        }
    }

    /// <summary>
    /// The width for <paramref name="documentPath"/>: that of the nearest folder at or above the document's own folder
    /// that has one, else the app default, with where it came from.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is blank or not a full path.</exception>
    public (ContentWidth Width, WidthSource Source) Resolve(string documentPath)
    {
        string? folder = Path.GetDirectoryName(WidthKeys.Normalize(documentPath, nameof(documentPath)));
        return ResolveFrom(folder);
    }

    /// <summary>
    /// The width a document in <paramref name="folder"/> uses: that of the nearest folder at or above it that has one, else
    /// the app default, with where it came from.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank or not a full path.</exception>
    public (ContentWidth Width, WidthSource Source) ResolveFolder(string folder) => ResolveFrom(WidthKeys.Normalize(folder, nameof(folder)));

    private (ContentWidth Width, WidthSource Source) ResolveFrom(string? folder)
    {
        lock (gate)
        {
            ReadForLookup();
            for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
            {
                if (widths.Folders.TryGetValue(current, out FolderWidth stored))
                {
                    return (stored.Width, new WidthSource(stored.Folder));
                }
            }

            return (widths.Default, WidthSource.AppDefault);
        }
    }

    /// <summary>The width <paramref name="folder"/> holds itself, or <see langword="null"/> when it inherits one.</summary>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank or not a full path.</exception>
    public ContentWidth? Get(string folder)
    {
        string key = WidthKeys.Normalize(folder, nameof(folder));
        lock (gate)
        {
            ReadForLookup();
            return widths.Folders.TryGetValue(key, out FolderWidth stored) ? stored.Width : null;
        }
    }

    /// <summary>Gives <paramref name="folder"/> its own width.</summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank or not a full path.</exception>
    public bool Set(string folder, ContentWidth width)
    {
        string key = WidthKeys.Normalize(folder, nameof(folder));
        return Change(state => state.WithFolder(key, width));
    }

    /// <summary>Sets the app default.</summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    public bool SetDefault(ContentWidth width) => Change(state => state.WithDefault(width));

    /// <summary>Removes <paramref name="folder"/>'s own width, so it inherits again.</summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank or not a full path.</exception>
    public bool Remove(string folder)
    {
        string key = WidthKeys.Normalize(folder, nameof(folder));
        return Change(state => state.Without(stored => AppPaths.PathComparer.Equals(stored, key)));
    }

    /// <summary>Removes the width of <paramref name="groupRoot"/> and of every folder inside it.</summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    /// <exception cref="ArgumentException"><paramref name="groupRoot"/> is blank or not a full path.</exception>
    public bool ClearAtOrBelow(string groupRoot)
    {
        string root = WidthKeys.Normalize(groupRoot, nameof(groupRoot));
        return Change(state => state.Without(stored => WidthKeys.IsAtOrBelow(stored, root)));
    }

    private enum ReadOutcome
    {
        Missing,
        Locked,
        Parsed,
        Unparseable,
    }

    private bool Change(Func<WidthsState, WidthsState?> change)
    {
        lock (gate)
        {
            ReadOutcome outcome = ReadForChange(out Exception? failure);
            if (outcome == ReadOutcome.Locked)
            {
                log.LogError($"Cannot change the widths file {FilePath}: it cannot be read. It is left as it was.", failure);
                return false;
            }

            readFailing = false;
            WidthsState? changed = change(widths);
            if (changed is null)
            {
                return true;
            }

            string badCopy = FilePath + ".bad";
            if (outcome == ReadOutcome.Unparseable && !ReplaceAtomically(badCopy, seenBytes ?? []))
            {
                return false;
            }

            byte[] bytes = WidthsFile.Serialize(changed);
            if (!ReplaceAtomically(FilePath, bytes))
            {
                return false;
            }

            if (outcome == ReadOutcome.Unparseable)
            {
                log.LogWarning($"The widths file {FilePath} did not parse; saved it as {badCopy} and wrote the last good widths with this change.");
            }

            widths = changed;
            seenBytes = bytes;
            seenBytesParsed = true;
            return true;
        }
    }

    private void ReadForLookup()
    {
        if (Read(out Exception? failure) != ReadOutcome.Locked)
        {
            readFailing = false;
            return;
        }

        if (!readFailing)
        {
            log.LogError($"Cannot read the widths file {FilePath}; keeping the widths read before.", failure);
            readFailing = true;
        }
    }

    private ReadOutcome ReadForChange(out Exception? failure)
    {
        for (int attempt = 0; ; attempt++)
        {
            ReadOutcome outcome = Read(out failure);
            if (outcome != ReadOutcome.Locked || attempt == Retries)
            {
                return outcome;
            }

            pause(RetryPause);
        }
    }

    /// <summary>Reads the file and, when its bytes changed, parses them into <see cref="widths"/>.</summary>
    private ReadOutcome Read(out Exception? failure)
    {
        failure = null;
        byte[] bytes;
        try
        {
            bytes = ReadAllBytesShared();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            widths = WidthsState.Empty;
            seenBytes = null;
            seenBytesParsed = true;
            return ReadOutcome.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception;
            return ReadOutcome.Locked;
        }

        if (seenBytes is null || !bytes.AsSpan().SequenceEqual(seenBytes))
        {
            seenBytes = bytes;
            seenBytesParsed = WidthsFile.TryParse(bytes, FilePath, log, out WidthsState? parsed);
            widths = parsed ?? widths;
        }

        return seenBytesParsed ? ReadOutcome.Parsed : ReadOutcome.Unparseable;
    }

    private byte[] ReadAllBytesShared()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Writes <paramref name="bytes"/> to a temporary file beside the widths file and moves it over <paramref name="target"/>.</summary>
    private bool ReplaceAtomically(string target, byte[] bytes)
    {
        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetries(temporary, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogError($"Cannot write {target}; it is left as it was.", exception);
            return false;
        }
        finally
        {
            DeleteFile(temporary);
        }
    }

    private void MoveWithRetries(string source, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < Retries)
            {
                pause(RetryPause);
            }
        }
    }

    private void DeleteOldTemporaryFiles(DateTimeOffset now)
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            string pattern = Path.GetFileName(FilePath) + ".*.tmp";
            foreach (string path in Directory.EnumerateFiles(directory, pattern, new EnumerationOptions()))
            {
                if (now - File.GetLastWriteTimeUtc(path) > TemporaryFileMaxAge)
                {
                    DeleteFile(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Cannot look for leftover temporary widths files in {directory}.", exception);
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Cannot delete the temporary widths file {path}.", exception);
        }
    }
}
