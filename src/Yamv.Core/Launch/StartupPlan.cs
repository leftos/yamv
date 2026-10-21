using Yamv.Core.Registration;

namespace Yamv.Core.Launch;

/// <summary>Turns a parsed <see cref="LaunchRequest"/> into what the process does before and beside any window.</summary>
public static class StartupPlan
{
    private const string MacRegistrationMessage =
        "--register and --unregister are for Windows; on macOS, Finder's Open With lists YAMV from its app bundle.";

    /// <summary>What this launch does.</summary>
    /// <param name="request">What the command line asked for.</param>
    /// <param name="isWindows">Whether the process runs on Windows.</param>
    /// <returns>The action that carries the request out.</returns>
    public static StartupAction Decide(LaunchRequest request, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request switch
        {
            HelpLaunch => new PrintAndExit(LaunchOptions.Usage, StartupOutput.StandardOutput, ExitCode: 0),
            InvalidLaunch invalid => new PrintAndExit(invalid.Message, StartupOutput.StandardError, ExitCode: 2),
            RegisterLaunch when isWindows => new RegisterStartup(),
            UnregisterLaunch when isWindows => new UnregisterStartup(),
            RegisterLaunch or UnregisterLaunch => new PrintAndExit(MacRegistrationMessage, StartupOutput.StandardError, ExitCode: 2),
            MainLaunch => new StartApplication([]),
            ViewerLaunch viewer => new StartApplication(viewer.Files),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown launch request."),
        };
    }

    /// <summary>The line to print and the code to exit with for a registration or unregistration that ran.</summary>
    /// <param name="result">What registering or unregistering did.</param>
    /// <returns>The message, its stream, the exit code, and whether to notify the shell.</returns>
    public static RegistrationOutcome FromResult(RegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RegistrationOutcome(
            result.Message,
            result.Succeeded ? StartupOutput.StandardOutput : StartupOutput.StandardError,
            result.ExitCode,
            NotifyShell: result.Succeeded && result.Changed
        );
    }

    /// <summary>The line to print and the code to exit with for a registration or unregistration that threw.</summary>
    /// <param name="exception">The registry exception.</param>
    /// <param name="register">Whether registering or unregistering threw it.</param>
    /// <returns>The message, stderr, exit code 1, and no shell notification.</returns>
    public static RegistrationOutcome FromException(Exception exception, bool register)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return Failure(register, exception.Message);
    }

    /// <summary>The line to print and the code to exit with when the OS does not report YAMV's own path.</summary>
    /// <param name="register">Whether registering or unregistering asked for it.</param>
    /// <returns>The message, stderr, exit code 1, and no shell notification.</returns>
    public static RegistrationOutcome FromUnknownPath(bool register) => Failure(register, "the path of yamv.exe is unknown.");

    private static RegistrationOutcome Failure(bool register, string detail)
    {
        string verb = register ? "register" : "unregister";
        return new RegistrationOutcome($"Could not {verb}: {detail}", StartupOutput.StandardError, ExitCode: 1, NotifyShell: false);
    }
}

/// <summary>What a launch does, chosen before any window exists.</summary>
public abstract record StartupAction;

/// <summary>Write <paramref name="Text"/> to <paramref name="Stream"/> and exit with <paramref name="ExitCode"/>.</summary>
/// <param name="Text">The line to print.</param>
/// <param name="Stream">The console stream the line goes to.</param>
/// <param name="ExitCode">The process exit code.</param>
public sealed record PrintAndExit(string Text, StartupOutput Stream, int ExitCode) : StartupAction;

/// <summary>Add YAMV to the "Open with" list, notify the shell when something changed, and exit.</summary>
public sealed record RegisterStartup : StartupAction;

/// <summary>Remove YAMV's "Open with" entries, notify the shell when something was removed, and exit.</summary>
public sealed record UnregisterStartup : StartupAction;

/// <summary>Start the app, as YAMV always did.</summary>
/// <param name="Files">The files a viewer window opens; empty for a main launch.</param>
public sealed record StartApplication(IReadOnlyList<string> Files) : StartupAction;

/// <summary>The console stream a startup message goes to.</summary>
public enum StartupOutput
{
    /// <summary>Standard output, which the user sees for a result they asked for.</summary>
    StandardOutput,

    /// <summary>Standard error, which the user sees for a refusal or an error.</summary>
    StandardError,
}

/// <summary>What <c>--register</c> or <c>--unregister</c> prints, exits with, and whether the shell is told to refresh.</summary>
/// <param name="Message">The line to print.</param>
/// <param name="Stream">The console stream the line goes to.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="NotifyShell">Whether Explorer should be told that the associations changed.</param>
public sealed record RegistrationOutcome(string Message, StartupOutput Stream, int ExitCode, bool NotifyShell);
