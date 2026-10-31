namespace Yamv.Core.Launch;

/// <summary>What a failed file launch tells the user, in the product's own words.</summary>
public static class LaunchFailureText
{
    private const string Single = "YAMV is not responding; the file was not opened.";

    /// <summary>The box's text for a launch that named <paramref name="fileCount"/> files and opened none of them.</summary>
    /// <param name="fileCount">The files the launch named; one or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileCount"/> is less than one.</exception>
    public static string For(int fileCount) =>
        fileCount switch
        {
            1 => Single,
            > 1 => $"YAMV is not responding; {fileCount} files were not opened.",
            _ => throw new ArgumentOutOfRangeException(nameof(fileCount), fileCount, "A launch names at least one file."),
        };
}
