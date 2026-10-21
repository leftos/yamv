using Yamv.Core.Diagnostics;

namespace Yamv.Core.Settings;

/// <summary>
/// The app-wide settings in <c>settings.json</c>, shared by every YAMV process. Safe to call from any thread.
/// </summary>
/// <remarks>
/// <para>
/// Every call reads the file again and parses it only when its bytes changed, so a change another process made shows at
/// the next call. A read never changes the file: a missing file, or one that is locked or does not parse (an editor
/// saving in place), keeps the settings read before.
/// </para>
/// <para>
/// Only <see cref="SetThemeId"/> and <see cref="SetAppearance"/> write. Each changes a fresh read of the file and replaces
/// the file in one move, so a reader never sees half a file. A change that cannot read or write the file is logged, leaves
/// the file as it was and returns <see langword="false"/>. A change over a file that does not parse first copies it to
/// <c>settings.json.bad</c>. A change that changes nothing writes nothing.
/// </para>
/// <para>
/// The contract is one store per file per process (the App holds one instance); separate processes coordinate through the
/// file alone. Calls are serialised by a lock that is held through the file IO and the retry pauses, up to about 300 ms
/// when the file is held, so a caller keeps a change off the UI thread.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private const int Retries = 5;
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TemporaryFileMaxAge = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly IAppLog log;
    private readonly Action<TimeSpan> pause;
    private SettingsState state = SettingsState.Empty;
    private byte[]? seenBytes;
    private bool seenBytesParsed = true;
    private bool readFailing;

    /// <summary>Creates a store for <paramref name="filePath"/> and deletes temporary files a crashed writer left behind.</summary>
    public SettingsStore(string filePath, IAppLog log)
        : this(filePath, log, Thread.Sleep, TimeProvider.System) { }

    /// <summary>Creates a store with the pause between retries and the clock injected, so tests control both.</summary>
    /// <param name="filePath">The settings file.</param>
    /// <param name="log">Where failures are reported.</param>
    /// <param name="pause">Waits between retries of a read or move the file system refused.</param>
    /// <param name="time">The clock that ages leftover temporary files.</param>
    internal SettingsStore(string filePath, IAppLog log, Action<TimeSpan> pause, TimeProvider time)
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

    private enum ReadOutcome
    {
        Missing,
        Locked,
        Parsed,
        Unparseable,
    }

    /// <summary>The JSON file the settings live in.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The settings the file holds: the defaults before any read, the last values read while the file is missing, and the
    /// settings read before while it cannot be read or does not parse. Never writes the file.
    /// </summary>
    public AppSettings Load()
    {
        lock (gate)
        {
            ReadForLookup();
            return state.Values;
        }
    }

    /// <summary>
    /// Sets the theme every window shows, applied to a fresh read of the file so another value changed meanwhile survives.
    /// A change over a file that does not parse first copies it to <c>settings.json.bad</c>.
    /// </summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    /// <exception cref="ArgumentException"><paramref name="themeId"/> is blank.</exception>
    public bool SetThemeId(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            throw new ArgumentException($"A theme id cannot be blank; it was '{themeId}'.", nameof(themeId));
        }

        return Change(values => values with { ThemeId = themeId });
    }

    /// <summary>
    /// Sets the light or dark preference, applied to a fresh read of the file so another value changed meanwhile survives.
    /// A change over a file that does not parse first copies it to <c>settings.json.bad</c>.
    /// </summary>
    /// <returns><see langword="false"/> when the file could not be read or written; the failure is logged.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="appearance"/> is not <see cref="AppSettings.SystemAppearance"/>, <see cref="AppSettings.LightAppearance"/>
    /// or <see cref="AppSettings.DarkAppearance"/>.
    /// </exception>
    public bool SetAppearance(string appearance)
    {
        if (!AppSettings.IsAppearance(appearance))
        {
            throw new ArgumentException(
                $"The appearance '{appearance}' is not \"{AppSettings.SystemAppearance}\", "
                    + $"\"{AppSettings.LightAppearance}\" or \"{AppSettings.DarkAppearance}\".",
                nameof(appearance)
            );
        }

        return Change(values => values with { Appearance = appearance });
    }

    private bool Change(Func<AppSettings, AppSettings> change)
    {
        lock (gate)
        {
            ReadOutcome outcome = ReadForChange(out Exception? failure);
            if (outcome == ReadOutcome.Locked)
            {
                log.LogError($"Cannot change the settings file {FilePath}: it cannot be read. It is left as it was.", failure);
                return false;
            }

            readFailing = false;
            AppSettings changed = change(state.Values);
            if (outcome != ReadOutcome.Missing && changed == state.Values)
            {
                return true;
            }

            string badCopy = FilePath + ".bad";
            if (outcome == ReadOutcome.Unparseable && !ReplaceAtomically(badCopy, seenBytes ?? []))
            {
                return false;
            }

            byte[] bytes = SettingsFile.Serialize(new SettingsState(changed, state.UnknownFields));
            if (!ReplaceAtomically(FilePath, bytes))
            {
                return false;
            }

            if (outcome == ReadOutcome.Unparseable)
            {
                log.LogWarning(
                    $"The settings file {FilePath} did not parse; saved it as {badCopy} and wrote the last good settings with this change."
                );
            }

            state = new SettingsState(changed, state.UnknownFields);
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
            log.LogError($"Cannot read the settings file {FilePath}; keeping the settings read before.", failure);
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

    /// <summary>Reads the file and, when its bytes changed, parses them into <see cref="state"/>.</summary>
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
            seenBytesParsed = SettingsFile.TryParse(bytes, FilePath, log, out SettingsState? parsed);
            state = parsed ?? state;
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

    /// <summary>Writes <paramref name="bytes"/> to a temporary file beside the settings file and moves it over <paramref name="target"/>.</summary>
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
            log.LogWarning($"Cannot look for leftover temporary settings files in {directory}.", exception);
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
            log.LogWarning($"Cannot delete the temporary settings file {path}.", exception);
        }
    }
}
