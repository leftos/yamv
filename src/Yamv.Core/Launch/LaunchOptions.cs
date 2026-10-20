namespace Yamv.Core.Launch;

/// <summary>Parses YAMV's command line into the request the app then carries out.</summary>
public static class LaunchOptions
{
    /// <summary>The text shown for <c>--help</c> and appended to an unknown option.</summary>
    public const string Usage = """
        Usage:
          yamv                 Open the main window with the documents from last time.
          yamv <file>...       Open a viewer window with just these files.
          yamv --register      Add YAMV to "Open with" for .md files (Windows).
          yamv --unregister    Remove what --register added.
        """;

    private const string ProcessSerialNumberPrefix = "-psn_";

    /// <summary>
    /// Turns <paramref name="args"/> into what this launch should do. File arguments are resolved against
    /// <paramref name="currentDirectory"/>, de-duplicated in first-seen order, and checked with <paramref name="fileExists"/>.
    /// </summary>
    /// <param name="args">The command-line arguments, without the program name.</param>
    /// <param name="currentDirectory">The folder a relative file argument is resolved against.</param>
    /// <param name="fileExists">Reports whether a file argument exists.</param>
    public static LaunchRequest Parse(IReadOnlyList<string> args, string currentDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        List<string> arguments = [.. args.Where(argument => !argument.StartsWith(ProcessSerialNumberPrefix, StringComparison.Ordinal))];
        if (arguments.Count == 0)
        {
            return new MainLaunch();
        }

        string? option = arguments.FirstOrDefault(IsOption);
        if (option is not null)
        {
            if (arguments.Count > 1)
            {
                return new InvalidLaunch($"{option} cannot be combined with other arguments");
            }

            return option switch
            {
                "--register" => new RegisterLaunch(),
                "--unregister" => new UnregisterLaunch(),
                _ => new HelpLaunch(),
            };
        }

        string? unknown = arguments.FirstOrDefault(argument => argument.StartsWith('-'));
        if (unknown is not null)
        {
            return new InvalidLaunch($"Unknown option: {unknown}\n\n{Usage}");
        }

        List<string> files = [];
        HashSet<string> seen = new(StringComparer.FromComparison(AppPaths.PathComparison));
        List<string> missing = [];
        foreach (string argument in arguments)
        {
            string fullPath = Path.GetFullPath(argument, currentDirectory);
            if (!seen.Add(fullPath))
            {
                continue;
            }

            files.Add(fullPath);
            if (!fileExists(fullPath))
            {
                missing.Add(fullPath);
            }
        }

        if (missing.Count > 0)
        {
            return new InvalidLaunch(string.Join(Environment.NewLine, missing.Select(path => $"File not found: {path}")));
        }

        return new ViewerLaunch(files);
    }

    private static bool IsOption(string argument) => argument is "--register" or "--unregister" or "--help" or "-h" or "/?";
}
