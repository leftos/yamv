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
    private const double DefaultSidebarWidth = 280;
    private const string DefaultThemeId = "studio";
    private const string DefaultAppearance = "system";

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
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    private static SessionState Normalize(SessionState state)
    {
        List<string> openFiles = FilterPaths(state.OpenFiles ?? []);
        var open = new HashSet<string>(openFiles, AppPaths.PathComparer);

        return state with
        {
            OpenFiles = openFiles,
            CollapsedGroups = FilterPaths(state.CollapsedGroups ?? []),
            ScrollPositions = NormalizeScrollPositions(state.ScrollPositions ?? new Dictionary<string, double>(), open),
            SelectedFile = state.SelectedFile is not null && open.Contains(state.SelectedFile) ? state.SelectedFile : null,
            Window = NormalizeWindow(state.Window),
            SidebarWidth = NormalizeSidebarWidth(state.SidebarWidth),
            ThemeId = string.IsNullOrWhiteSpace(state.ThemeId) ? DefaultThemeId : state.ThemeId,
            Appearance = NormalizeAppearance(state.Appearance),
        };
    }

    /// <summary>Drops null and blank entries, which a JSON array can hold even though the type says otherwise.</summary>
    private static List<string> FilterPaths(IReadOnlyList<string> paths)
    {
        List<string> result = [];
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static Dictionary<string, double> NormalizeScrollPositions(IReadOnlyDictionary<string, double> positions, HashSet<string> openFiles)
    {
        var scrollPositions = new Dictionary<string, double>(AppPaths.PathComparer);
        foreach ((string path, double position) in positions)
        {
            if (openFiles.Contains(path))
            {
                scrollPositions[path] = double.IsFinite(position) ? Math.Clamp(position, 0, 1) : 0;
            }
        }

        return scrollPositions;
    }

    private static WindowBounds? NormalizeWindow(WindowBounds? window)
    {
        if (window is not { } bounds || !IsUsableSize(bounds))
        {
            return null;
        }

        return double.IsFinite(bounds.Scaling) && bounds.Scaling > 0 ? bounds : bounds with { Scaling = 1 };
    }

    private static bool IsUsableSize(WindowBounds bounds) =>
        double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width > 0
        && bounds.Height > 0;

    private static double NormalizeSidebarWidth(double width) => double.IsFinite(width) && width > 0 ? width : DefaultSidebarWidth;

    private static string NormalizeAppearance(string appearance) => appearance is "system" or "light" or "dark" ? appearance : DefaultAppearance;

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
