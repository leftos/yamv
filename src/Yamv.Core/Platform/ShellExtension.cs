namespace Yamv.Core.Platform;

/// <summary>
/// How the Windows shell reads a file name's extension, which decides whether it runs a program of the same name beside the
/// file: it finds no extension when the final name has no dot, or when an ASCII space follows its last dot.
/// </summary>
public static class ShellExtension
{
    /// <summary>Whether the shell reads <paramref name="path"/> as having no extension.</summary>
    /// <param name="path">The path whose final segment is read; any folders before it are ignored.</param>
    public static bool IsMissing(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 || name.AsSpan(dot + 1).Contains(' ');
    }
}
