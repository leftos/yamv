using System.Security;
using Yamv.Core.Launch;
using Yamv.Core.Registration;

namespace Yamv.Core.Tests.Launch;

public sealed class StartupPlanTests
{
    private const string MacSentence = "--register and --unregister are for Windows; on macOS, Finder's Open With lists YAMV from its app bundle.";

    private const string FailureMessage = "access denied";

    [Fact]
    public void Decide_Help_PrintsUsageToStdoutAndExitsZero()
    {
        PrintAndExit print = Assert.IsType<PrintAndExit>(StartupPlan.Decide(new HelpLaunch(), isWindows: true));

        Assert.Equal(LaunchOptions.Usage, print.Text);
        Assert.Equal(StartupOutput.StandardOutput, print.Stream);
        Assert.Equal(0, print.ExitCode);
    }

    [Fact]
    public void Decide_Invalid_PrintsItsMessageToStderrAndExitsTwo()
    {
        PrintAndExit print = Assert.IsType<PrintAndExit>(StartupPlan.Decide(new InvalidLaunch("Unknown option: --bogus"), isWindows: true));

        Assert.Equal("Unknown option: --bogus", print.Text);
        Assert.Equal(StartupOutput.StandardError, print.Stream);
        Assert.Equal(2, print.ExitCode);
    }

    [Fact]
    public void Decide_RegisterOnWindows_ReturnsRegisterStartup() =>
        Assert.IsType<RegisterStartup>(StartupPlan.Decide(new RegisterLaunch(), isWindows: true));

    [Fact]
    public void Decide_UnregisterOnWindows_ReturnsUnregisterStartup() =>
        Assert.IsType<UnregisterStartup>(StartupPlan.Decide(new UnregisterLaunch(), isWindows: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_RegistrationOffWindows_PrintsTheMacSentenceToStderrAndExitsTwo(bool register)
    {
        LaunchRequest request = register ? new RegisterLaunch() : new UnregisterLaunch();

        PrintAndExit print = Assert.IsType<PrintAndExit>(StartupPlan.Decide(request, isWindows: false));

        Assert.Equal(MacSentence, print.Text);
        Assert.Equal(StartupOutput.StandardError, print.Stream);
        Assert.Equal(2, print.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_Main_StartsTheAppWithNoFiles(bool isWindows)
    {
        StartApplication start = Assert.IsType<StartApplication>(StartupPlan.Decide(new MainLaunch(), isWindows));

        Assert.Empty(start.Files);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_ViewerWithFiles_StartsTheAppWithThoseFiles(bool isWindows)
    {
        StartApplication start = Assert.IsType<StartApplication>(StartupPlan.Decide(new ViewerLaunch([@"C:\work\a.md"]), isWindows));

        Assert.Equal([@"C:\work\a.md"], start.Files);
    }

    [Fact]
    public void FromResult_SucceededWithChange_PrintsToStdoutNotifiesAndExitsZero()
    {
        RegistrationOutcome outcome = StartupPlan.FromResult(new RegistrationResult(Succeeded: true, Changed: true, Message: "Registered YAMV."));

        Assert.Equal("Registered YAMV.", outcome.Message);
        Assert.Equal(StartupOutput.StandardOutput, outcome.Stream);
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.NotifyShell);
    }

    [Fact]
    public void FromResult_SucceededWithoutChange_PrintsToStdoutAndDoesNotNotify()
    {
        RegistrationOutcome outcome = StartupPlan.FromResult(new RegistrationResult(Succeeded: true, Changed: false, Message: "Nothing to remove."));

        Assert.Equal("Nothing to remove.", outcome.Message);
        Assert.Equal(StartupOutput.StandardOutput, outcome.Stream);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.NotifyShell);
    }

    [Fact]
    public void FromResult_Failed_PrintsToStderrAndExitsOne()
    {
        RegistrationOutcome outcome = StartupPlan.FromResult(
            new RegistrationResult(Succeeded: false, Changed: false, Message: "Not registering: not yamv.exe.")
        );

        Assert.Equal("Not registering: not yamv.exe.", outcome.Message);
        Assert.Equal(StartupOutput.StandardError, outcome.Stream);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(outcome.NotifyShell);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("security")]
    public void FromException_Register_PrintsCouldNotRegisterToStderrAndExitsOne(string kind)
    {
        RegistrationOutcome outcome = StartupPlan.FromException(MakeException(kind), register: true);

        Assert.Equal($"Could not register: {FailureMessage}", outcome.Message);
        Assert.Equal(StartupOutput.StandardError, outcome.Stream);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(outcome.NotifyShell);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("security")]
    public void FromException_Unregister_PrintsCouldNotUnregisterToStderrAndExitsOne(string kind)
    {
        RegistrationOutcome outcome = StartupPlan.FromException(MakeException(kind), register: false);

        Assert.Equal($"Could not unregister: {FailureMessage}", outcome.Message);
        Assert.Equal(StartupOutput.StandardError, outcome.Stream);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(outcome.NotifyShell);
    }

    [Theory]
    [InlineData(true, "Could not register: the path of yamv.exe is unknown.")]
    [InlineData(false, "Could not unregister: the path of yamv.exe is unknown.")]
    public void FromUnknownPath_PrintsCouldNotToStderrAndExitsOne(bool register, string expected)
    {
        RegistrationOutcome outcome = StartupPlan.FromUnknownPath(register);

        Assert.Equal(expected, outcome.Message);
        Assert.Equal(StartupOutput.StandardError, outcome.Stream);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(outcome.NotifyShell);
    }

    private static Exception MakeException(string kind) =>
        kind switch
        {
            "access" => new UnauthorizedAccessException(FailureMessage),
            "io" => new IOException(FailureMessage),
            "security" => new SecurityException(FailureMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown exception kind."),
        };
}
