namespace Yamv.Core;

/// <summary>The per-user folders YAMV keeps its state in.</summary>
public static class AppPaths
{
    /// <summary>
    /// <c>%APPDATA%\YAMV</c> on Windows, <c>~/Library/Application Support/YAMV</c> on macOS,
    /// <c>$XDG_CONFIG_HOME/yamv</c> (or <c>~/.config/yamv</c>) elsewhere. Not created by this call.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YAMV");
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(home, "Library", "Application Support", "YAMV");
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "yamv");
        }
    }

    public static string SessionFile => Path.Combine(DataDirectory, "session.json");

    public static string MainLockFile => Path.Combine(DataDirectory, "main.lock");

    public static string LogFile => Path.Combine(DataDirectory, "logs", "yamv.log");

    /// <summary>The folder the user's own theme files live in.</summary>
    public static string ThemesDirectory => Path.Combine(DataDirectory, "themes");

    /// <summary>
    /// The path comparison of the platform's default file system: case-insensitive on Windows and macOS.
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
