using System.Runtime.Versioning;
using System.Security;
using Avalonia;
using Yamv.App.Platform;
using Yamv.Core.Launch;
using Yamv.Core.Registration;

namespace Yamv.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable(App.HarnessVariable) == "1")
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        LaunchRequest request = LaunchOptions.Parse(args, Environment.CurrentDirectory, File.Exists);
        return CarryOut(StartupPlan.Decide(request, OperatingSystem.IsWindows()), args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .WithInterFont().LogToTrace();

    private static int CarryOut(StartupAction action, string[] args) =>
        action switch
        {
            PrintAndExit print => Finish(print.Text, print.Stream, print.ExitCode, notifyShell: false),
            RegisterStartup => RunRegistration(register: true),
            UnregisterStartup => RunRegistration(register: false),
            StartApplication => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args),
            _ => throw new InvalidOperationException($"Unknown startup action: {action}."),
        };

    /// <summary>Writes the text to the console, notifies the shell when asked, and returns the exit code.</summary>
    private static int Finish(string message, StartupOutput stream, int exitCode, bool notifyShell)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsConsole.Attach();
        }

        TextWriter writer = stream == StartupOutput.StandardError ? Console.Error : Console.Out;
        writer.WriteLine(message);
        if (notifyShell && OperatingSystem.IsWindows())
        {
            ShellNotify.AssociationsChanged();
        }

        return exitCode;
    }

    /// <summary>Registers or unregisters YAMV's file association, then prints what happened and exits.</summary>
    private static int RunRegistration(bool register)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Registering file associations is a Windows-only action.");
        }

        RegistrationOutcome outcome = AssociationOutcome(register);
        return Finish(outcome.Message, outcome.Stream, outcome.ExitCode, outcome.NotifyShell);
    }

    [SupportedOSPlatform("windows")]
    private static RegistrationOutcome AssociationOutcome(bool register)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return StartupPlan.FromUnknownPath(register);
        }

        try
        {
            using var association = WindowsFileAssociation.ForCurrentUser(exePath);
            return StartupPlan.FromResult(register ? association.Register() : association.Unregister());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return StartupPlan.FromException(exception, register);
        }
    }
}
