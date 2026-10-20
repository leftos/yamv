namespace Yamv.Core.Launch;

/// <summary>What a launch of YAMV asks it to do, parsed from the command line.</summary>
public abstract record LaunchRequest;

/// <summary>No file arguments: open the main window with the documents from last time.</summary>
public sealed record MainLaunch : LaunchRequest;

/// <summary>Open a viewer window that shows just these files.</summary>
/// <param name="Files">The full paths to show, in the order they were given.</param>
public sealed record ViewerLaunch(IReadOnlyList<string> Files) : LaunchRequest;

/// <summary>Add YAMV to the "Open with" list for <c>.md</c> files.</summary>
public sealed record RegisterLaunch : LaunchRequest;

/// <summary>Remove what <see cref="RegisterLaunch"/> added.</summary>
public sealed record UnregisterLaunch : LaunchRequest;

/// <summary>Print the usage text and exit.</summary>
public sealed record HelpLaunch : LaunchRequest;

/// <summary>What to tell the user before exiting with code 2.</summary>
/// <param name="Message">The text to print.</param>
public sealed record InvalidLaunch(string Message) : LaunchRequest;
