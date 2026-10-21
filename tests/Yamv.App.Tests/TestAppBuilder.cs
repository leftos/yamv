using Avalonia;
using Avalonia.Headless;
using Yamv.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Yamv.App.Tests;

/// <summary>
/// Builds the real <see cref="App"/> on the headless platform, so tests run without a window on the desktop. Popups that do
/// not ask for the overlay layer are top levels of their own, as on the desktop: the content-width popover is one.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true, OverlayPopups = false });
}

/// <summary>
/// Runs test bodies on the headless Avalonia UI thread of the assembly's one session. Avalonia.Headless.XUnit 12.1.4 fails
/// discovery under xunit.v3 4.0.2 (<c>MissingMethodException</c> on <c>Xunit.v3.TestIntrospectionHelper.GetTestCaseDetails</c>
/// from <c>AvaloniaFactDiscoverer.CreateTestCase</c>), so plain facts dispatch through this instead.
/// </summary>
public static class Headless
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);

    /// <summary>Runs <paramref name="body"/> on the UI thread.</summary>
    public static Task Run(Action body) => Session.Dispatch(body, TestContext.Current.CancellationToken);

    /// <summary>Runs <paramref name="body"/> on the UI thread, its awaits resuming there.</summary>
    public static Task RunAsync(Func<Task> body) =>
        Session.Dispatch(
            async () =>
            {
                await body();
                return true;
            },
            TestContext.Current.CancellationToken
        );
}
