using System.Runtime.Versioning;
using System.Security;
using Avalonia;
using Yamv.App.Platform;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;
using Yamv.Core.Launch;
using Yamv.Core.Registration;

namespace Yamv.App;

internal static class Program
{
    private const int MaxLoggedMessageLength = 200;

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable(App.HarnessVariable) == "1")
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        LaunchRequest request = LaunchOptions.Parse(args, Environment.CurrentDirectory, Directory.Exists);
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
            StartApplication start => Start(start.Files, args),
            _ => throw new InvalidOperationException($"Unknown startup action: {action}."),
        };

    /// <summary>
    /// Hands the launch's files to the app and starts Avalonia for a viewer launch. No files is a main launch: it takes the
    /// main-instance lock and runs as the main instance, or, when another process holds the lock, asks that one to come forward.
    /// </summary>
    private static int Start(IReadOnlyList<string> files, string[] args)
    {
        App.ViewerFiles = files;
        if (files.Count > 0)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        var log = new FileAppLog(AppPaths.LogFile);
        App.ProcessLog = log;
        using var coordinator = new InstanceCoordinator(AppPaths.MainLockFile, log);
        if (!coordinator.TryAcquireMainLock())
        {
            SecondLaunchOutcome outcome = RunSecondLaunch(coordinator, log);
            if (outcome != SecondLaunchOutcome.BecameMain)
            {
                return outcome == SecondLaunchOutcome.Activated ? 0 : 1;
            }
        }

        return RunMain(coordinator, log, args);
    }

    /// <summary>Starts the instance server at once, then Avalonia; the coordinator holds the lock until the caller disposes it.</summary>
    private static int RunMain(InstanceCoordinator coordinator, IAppLog log, string[] args)
    {
        var gate = new ActivationGate();
        coordinator.StartServer(message => OnInstanceMessage(message, gate, log));
        App.Activation = gate;
        App.StopAnswering = coordinator.StopAnswering;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void OnInstanceMessage(string message, ActivationGate gate, IAppLog log)
    {
        if (message == InstanceMessages.Activate)
        {
            gate.Request();
            return;
        }

        log.LogWarning($"Instance server: ignoring an unknown message '{ForLog(message)}'.");
    }

    /// <summary>At most the first 200 characters of <paramref name="message"/>, with line breaks escaped so it stays on one log line.</summary>
    private static string ForLog(string message) =>
        message[..Math.Min(message.Length, MaxLoggedMessageLength)]
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Asks the main instance that holds the lock to come forward; blocks, since no sync context exists before Avalonia.</summary>
    private static SecondLaunchOutcome RunSecondLaunch(InstanceCoordinator coordinator, IAppLog log)
    {
        var actions = new SecondLaunchActions(
            ReadPid: () => InstanceCoordinator.TryReadMainPid(AppPaths.MainLockFile, log),
            AllowForeground: pid => AllowForeground(pid, log),
            Signal: timeout => InstanceCoordinator.SignalAsync(AppPaths.MainLockFile, InstanceMessages.Activate, timeout, log),
            BringForward: pid => BringForward(pid, log),
            RetryLock: coordinator.TryAcquireMainLockQuietly,
            Delay: delay => Task.Delay(delay)
        );
        return new SecondLaunch(actions, log).RunAsync().GetAwaiter().GetResult();
    }

    private static void AllowForeground(int pid, IAppLog log)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsForeground.AllowForeground(pid, log);
        }
    }

    private static bool BringForward(int pid, IAppLog log)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsForeground.BringForward(pid, log);
        }

        log.LogInfo("Second launch: bringing the main window forward is not built for macOS yet.");
        return false;
    }

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
