namespace Yamv.Core;

/// <summary>The per-user folders YAMV keeps its state in.</summary>
public static class AppPaths
{
    /// <summary>The environment variable that, when set and not empty, names the data directory on every platform.</summary>
    public const string DataDirectoryVariable = "YAMV_DATA_DIR";

    /// <summary>
    /// The full path of <c>YAMV_DATA_DIR</c> when that variable is set and not empty, so a dev run or a test never touches
    /// the user's own state. Otherwise <c>%APPDATA%\YAMV</c> on Windows, <c>~/Library/Application Support/YAMV</c> on macOS,
    /// <c>$XDG_CONFIG_HOME/yamv</c> (or <c>~/.config/yamv</c>) elsewhere. Not created by this call.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrEmpty(overridden))
            {
                return Path.GetFullPath(overridden);
            }

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

    /// <summary>The per-folder content widths, beside <see cref="SessionFile"/>.</summary>
    public static string WidthsFile => Path.Combine(DataDirectory, "widths.json");

    /// <summary>The app-wide settings shared by every window, beside <see cref="WidthsFile"/>.</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string MainLockFile => Path.Combine(DataDirectory, "main.lock");

    /// <summary>The lock a failed launch holds while its not-responding box is up, so a burst of them shows one box.</summary>
    public static string LaunchErrorLockFile => Path.Combine(DataDirectory, "launch-error.lock");

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
