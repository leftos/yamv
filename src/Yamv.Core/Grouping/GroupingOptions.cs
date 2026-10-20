namespace Yamv.Core.Grouping;

/// <summary>What grouping needs to know about the machine it runs on.</summary>
/// <param name="HomeDirectory">
/// The user's home folder, shown as <c>~</c> in a group name; null on Windows, where paths are shown whole.
/// </param>
/// <param name="PathComparison">How two file paths are compared on this platform's file system.</param>
public sealed record GroupingOptions(string? HomeDirectory, StringComparison PathComparison)
{
    /// <summary>The options for the platform YAMV is running on.</summary>
    public static GroupingOptions ForCurrentPlatform { get; } =
        new(OperatingSystem.IsWindows() ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.PathComparison);
}
