using System.Diagnostics;
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
    /// Takes the main-instance lock for every launch. The launch that gets it runs as the app, its files (if any) opening as
    /// the first viewer window. One that does not sends its files to the running app, or with no files asks it to come forward;
    /// it runs as the app after all when the app exits meanwhile and leaves it the lock.
    /// </summary>
    private static int Start(IReadOnlyList<string> files, string[] args)
    {
        App.ViewerFiles = files;
        var log = new FileAppLog(AppPaths.LogFile);
        App.ProcessLog = log;
        using var coordinator = new InstanceCoordinator(AppPaths.MainLockFile, log);
        if (!coordinator.TryAcquireMainLock())
        {
            var launch = new SecondLaunch(SecondLaunchActionsFor(coordinator, log), log);

            // Blocking: no sync context exists before Avalonia.
            int? exitCode = RunSecondLaunch(
                files,
                () => launch.RunAsync().GetAwaiter().GetResult(),
                () => launch.OpenAsync(files, SecondLaunch.OpenDeadline, SecondLaunch.OpenOverallCap).GetAwaiter().GetResult(),
                log
            );
            if (exitCode is { } code)
            {
                return code;
            }
        }

        return RunMain(coordinator, log, args);
    }

    /// <summary>
    /// What a launch that did not get the lock does: with no files it asks the app to come forward (<paramref name="activate"/>),
    /// with files it sends them (<paramref name="open"/>). Returns the exit code, or null when the app had exited and this launch
    /// now holds the lock, so it runs as the app.
    /// </summary>
    internal static int? RunSecondLaunch(IReadOnlyList<string> files, Func<SecondLaunchOutcome> activate, Func<SecondLaunchResult> open, IAppLog log)
    {
        SecondLaunchResult result = files.Count == 0 ? new SecondLaunchResult(activate(), null) : open();
        switch (result.Outcome)
        {
            case SecondLaunchOutcome.Activated or SecondLaunchOutcome.Opened:
                return 0;
            case SecondLaunchOutcome.BecameMain:
                return null;
            default:
                if (result.Failure is { } failure)
                {
                    log.LogWarning($"Second launch: the {files.Count} file(s) were not opened ({failure}); exiting 1.");
                }

                return 1;
        }
    }

    /// <summary>
    /// Starts the instance server at once, then Avalonia; the coordinator holds the lock until the caller disposes it. Every
    /// request reaches the UI thread through the ready latch, which the app opens once its framework is initialized and its start
    /// window shown, so no pool thread posts to the dispatcher before then.
    /// </summary>
    private static int RunMain(InstanceCoordinator coordinator, IAppLog log, string[] args)
    {
        var latch = new UiReadyLatch();
        var handler = new InstanceRequestHandler(
            latch.Post,
            () => ActivateInApp(log),
            (files, request) => OpenInApp(files, request, log),
            SinceProcessStart,
            log
        );
        coordinator.StartServer(handler.Handle);
        App.ReadyLatch = latch;
        App.Lifetime = new AppLifetimePolicy(OperatingSystem.IsMacOS() ? LastWindowBehavior.KeepRunning : LastWindowBehavior.Quit);
        App.StopAnswering = () => StopAnsweringInBackground(coordinator, log);
        return RunAsApp(() => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args), coordinator.StopAnswering, log);
    }

    /// <summary>
    /// Runs the app's UI loop, then finishes stopping on this thread once the loop is over: the pid file is deleted and the server
    /// stopped before this returns and the caller releases the lock, even when the app began the stop in the background and the
    /// pool has not run it yet, or the loop threw. A throw from the stop is logged; when the loop threw too, the loop's exception
    /// is the one that propagates.
    /// </summary>
    internal static int RunAsApp(Func<int> runLoop, Action finishStopping, IAppLog log)
    {
        int exitCode;
        try
        {
            exitCode = runLoop();
        }
        catch
        {
            FinishStoppingLogged(finishStopping, log, rethrow: false);
            throw;
        }

        FinishStoppingLogged(finishStopping, log, rethrow: true);
        return exitCode;
    }

    /// <summary>Runs the stop, logging a throw; rethrows it only when no exception from the UI loop is already on its way out.</summary>
    private static void FinishStoppingLogged(Action finishStopping, IAppLog log, bool rethrow)
    {
        try
        {
            finishStopping();
        }
        // Logged so it is never lost; when the loop threw, the loop's exception is the one the caller sees.
        catch (Exception exception) when (!rethrow)
        {
            log.LogError("Instance server: finishing the stop failed after the UI loop failed; the loop's exception is kept.", exception);
        }
        catch (Exception exception)
        {
            log.LogError("Instance server: finishing the stop failed.", exception);
            throw;
        }
    }

    /// <summary>
    /// Begins <see cref="InstanceCoordinator.StopAnswering"/> on a pool thread and returns at once, so its waits on the server never
    /// run on the UI thread. <see cref="RunAsApp"/> finishes the stop once the UI loop is over, before the lock is released.
    /// </summary>
    internal static void StopAnsweringInBackground(InstanceCoordinator coordinator, IAppLog log)
    {
        _ = Task.Run(coordinator.StopAnswering)
            .ContinueWith(
                stop => log.LogError("Instance server: stopping it failed; the lock is still released at exit.", stop.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );
    }

    /// <summary>Hands an open request to the running app on the UI thread; refuses it when no app is there to take it.</summary>
    private static bool OpenInApp(IReadOnlyList<string> files, InstanceRequest request, IAppLog log)
    {
        if (Application.Current is App app)
        {
            return app.OpenViewer(files, request);
        }

        log.LogError($"Instance server: no app to open {files.Count} file(s) in; refusing the request.");
        request.Complete(InstanceReply.Refused);
        return false;
    }

    /// <summary>Hands an activate to the running app on the UI thread; logs it when no app is there to take it.</summary>
    private static void ActivateInApp(IAppLog log)
    {
        if (Application.Current is App app)
        {
            app.ActivateRequested();
            return;
        }

        log.LogError("Instance server: no app to bring forward; dropping an activate.");
    }

    /// <summary>The platform actions a launch that did not get the lock runs through.</summary>
    private static SecondLaunchActions SecondLaunchActionsFor(InstanceCoordinator coordinator, IAppLog log) =>
        new(
            ReadPid: () => InstanceCoordinator.TryReadMainPid(AppPaths.MainLockFile, log),
            AllowForeground: pid => AllowForeground(pid, log),
            Signal: timeout => InstanceCoordinator.SignalAsync(AppPaths.MainLockFile, InstanceMessages.Activate, timeout, log),
            Request: (message, budget) => InstanceCoordinator.RequestAsync(AppPaths.MainLockFile, message, budget, log),
            BringForward: pid => BringForward(pid, log),
            RetryLock: coordinator.TryAcquireMainLockQuietly,
            Delay: delay => Task.Delay(delay),
            Time: TimeProvider.System
        );

    /// <summary>How long ago this process started: what the readiness line measures from.</summary>
    private static TimeSpan SinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.UtcNow - process.StartTime.ToUniversalTime();
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
