using System.Text.Json;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Session;

/// <summary>Reads and writes the state YAMV restores on the next launch.</summary>
/// <remarks>
/// <see cref="Load"/> never throws: a missing, unreadable or corrupt file yields <see cref="SessionState.Empty"/>,
/// so the app always starts. <see cref="Save"/> lets exceptions propagate and leaves it to the caller to report them.
/// </remarks>
public sealed class SessionStore(string filePath, IAppLog log)
{
    /// <summary>The JSON file the state lives in.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>
    /// Loads the stored state with out-of-range scroll positions clamped and entries for files that are not open dropped.
    /// </summary>
    public SessionState Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
            {
                return SessionState.Empty;
            }

            json = File.ReadAllText(FilePath);
        }
        catch (IOException exception)
        {
            log.LogError($"Cannot read the session file {FilePath}.", exception);
            return SessionState.Empty;
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogError($"Cannot read the session file {FilePath}.", exception);
            return SessionState.Empty;
        }

        SessionState? state;
        try
        {
            state =
                JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState)
                ?? throw new JsonException("The session file holds JSON null instead of a session object.");
        }
        catch (JsonException exception)
        {
            log.LogWarning($"The session file {FilePath} is unreadable; moving it to {FilePath}.bad.", exception);
            MoveCorruptFileAside();
            return SessionState.Empty;
        }

        return Normalize(state);
    }

    /// <summary>Writes <paramref name="state"/>, creating its directory and leaving no partial file behind.</summary>
    public void Save(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SessionState normalized = Normalize(state);
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(normalized, SessionJsonContext.Default.SessionState);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, overwrite: true);
    }

    private static SessionState Normalize(SessionState state)
    {
        var openFiles = new HashSet<string>(state.OpenFiles ?? [], AppPaths.PathComparer);
        var scrollPositions = new Dictionary<string, double>(AppPaths.PathComparer);
        foreach ((string path, double position) in state.ScrollPositions ?? new Dictionary<string, double>())
        {
            if (openFiles.Contains(path))
            {
                scrollPositions[path] = Math.Clamp(position, 0, 1);
            }
        }

        string? selectedFile = state.SelectedFile is not null && openFiles.Contains(state.SelectedFile) ? state.SelectedFile : null;
        return state with { ScrollPositions = scrollPositions, SelectedFile = selectedFile };
    }

    private void MoveCorruptFileAside()
    {
        string destination = FilePath + ".bad";
        try
        {
            File.Move(FilePath, destination, overwrite: true);
        }
        catch (IOException exception)
        {
            log.LogError($"Cannot move the corrupt session file {FilePath} to {destination}.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogError($"Cannot move the corrupt session file {FilePath} to {destination}.", exception);
        }
    }
}
