namespace Yamv.Core.Widths;

/// <summary>
/// The one spelling rule for folder keys: a full path, with trailing separators trimmed except at a root. Keys are
/// compared with <see cref="AppPaths.PathComparer"/>.
/// </summary>
internal static class WidthKeys
{
    /// <summary>The key for <paramref name="folder"/>, or <see langword="null"/> when it is blank or not a full path.</summary>
    public static string? TryNormalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (ArgumentException)
        {
            // A path with a character the platform rejects names no folder; the caller reports it.
            return null;
        }
    }

    /// <summary>The key for <paramref name="path"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank or not a full path.</exception>
    public static string Normalize(string path, string parameterName) =>
        TryNormalize(path) ?? throw new ArgumentException($"'{path}' is not a full path.", parameterName);

    /// <summary>Whether <paramref name="key"/> is <paramref name="root"/> or a folder inside it.</summary>
    public static bool IsAtOrBelow(string key, string root)
    {
        if (string.Equals(key, root, AppPaths.PathComparison))
        {
            return true;
        }

        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return key.StartsWith(prefix, AppPaths.PathComparison);
    }
}
