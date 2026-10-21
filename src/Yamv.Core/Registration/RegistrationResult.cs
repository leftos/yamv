namespace Yamv.Core.Registration;

/// <summary>What a registration or unregistration did, and the line to print for it.</summary>
/// <param name="Succeeded">Whether the work ran without a refusal or a registry error.</param>
/// <param name="Changed">Whether any registry key or value was written or removed.</param>
/// <param name="Message">The line to print to the user.</param>
public sealed record RegistrationResult(bool Succeeded, bool Changed, string Message)
{
    /// <summary>The process exit code that matches this result: 0 on success, 1 on failure.</summary>
    public int ExitCode => Succeeded ? 0 : 1;
}
