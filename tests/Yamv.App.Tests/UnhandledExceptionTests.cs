using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.Core;

namespace Yamv.App.Tests;

/// <summary>
/// What the app does with an exception that escapes one of its own handlers: it is logged and the UI loop keeps running, and a
/// caught one says so in a toast in the most recently active document window. The headless session builds a fresh
/// <see cref="App"/> for each test, so each one installs the handlers itself, hands the app a recording log and opens its own data
/// folder.
/// </summary>
[Collection(nameof(EnvironmentVariables))]
public sealed class UnhandledExceptionTests
{
    private const string EscapedMessage = "Unhandled: an exception escaped a UI handler";
    private const string UnobservedTaskMessage = "Unhandled: a task's exception was never observed.";
    private const string NoHandlerCaughtMessage = "Unhandled: an exception no handler caught; the app ends.";
    private const string WarnedText = "Something went wrong; see the log";

    [Fact]
    public Task A_throw_in_a_dispatcher_job_is_logged_and_the_loop_keeps_running() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: true);
            var boom = new InvalidOperationException("boom");
            bool ran = false;
            Dispatcher.UIThread.Post(() => throw boom);
            Dispatcher.UIThread.Post(() => ran = true);

            Dispatcher.UIThread.RunJobs();

            LogEntry entry = Assert.Single(Escaped(start.Log));
            Assert.Same(boom, entry.Exception);
            Assert.True(ran);
        });

    [Fact]
    public Task A_throw_in_an_async_void_handler_is_logged() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: true);

            Dispatcher.UIThread.Post(ThrowsAfterYield);
            Dispatcher.UIThread.RunJobs();

            LogEntry entry = Assert.Single(Escaped(start.Log));
            Assert.Equal("async void boom", Assert.IsType<InvalidOperationException>(entry.Exception).Message);

            static async void ThrowsAfterYield()
            {
                await Task.Yield();
                throw new InvalidOperationException("async void boom");
            }
        });

    [Fact]
    public Task The_toast_goes_to_the_most_recently_active_window() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: true);
            Window main = Assert.IsAssignableFrom<Window>(start.StartWindow);
            start.App.ShowNewViewer([MissingFile()]);
            Window viewer = start.App.ViewerWindows[^1];
            viewer.Activate();
            Dispatcher.UIThread.RunJobs();

            ThrowInAJob();
            ThrowInAJob();

            ToastItem toast = Assert.Single(ToastsOf(viewer).Items);
            Assert.Equal((ToastKind.Warning, WarnedText), (toast.Kind, toast.Text));
            Assert.Equal(2, toast.Count);
            Assert.Empty(ToastsOf(main).Items);

            main.Activate();
            Dispatcher.UIThread.RunJobs();
            ThrowInAJob();

            Assert.Single(ToastsOf(main).Items);
            Assert.Equal(2, Assert.Single(ToastsOf(viewer).Items).Count);
        });

    [Fact]
    public Task With_no_window_open_a_throw_is_only_logged() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: false);
            Assert.Null(start.App.MainDocumentWindow);
            Assert.Empty(start.App.ViewerWindows);

            Exception? escaped = Record.Exception(ThrowInAJob);

            Assert.Null(escaped);
            Assert.Single(Escaped(start.Log));
        });

    [Fact]
    public Task After_release_the_handler_is_gone() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: true);
            ThrowInAJob();
            Assert.Single(Escaped(start.Log));

            start.Release();
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));

            Assert.Throws<InvalidOperationException>(() => Dispatcher.UIThread.RunJobs());
            Assert.Single(Escaped(start.Log));
        });

    [Fact]
    public Task An_unobserved_task_exception_is_logged_and_observed() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: false);
            var boom = new InvalidOperationException("task boom");
            var args = new UnobservedTaskExceptionEventArgs(new AggregateException(boom));

            start.App.OnUnobservedTaskException(this, args);

            LogEntry entry = Assert.Single(ErrorsAbout(start.Log, UnobservedTaskMessage, boom));
            Assert.Same(boom, Assert.Single(Assert.IsType<AggregateException>(entry.Exception).InnerExceptions));
            Assert.True(args.Observed);
        });

    [Fact]
    public Task An_unobserved_task_exception_already_handled_on_the_UI_thread_is_not_logged_again() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: true);
            var boom = new InvalidOperationException("boom");
            Dispatcher.UIThread.Post(() => throw boom);
            Dispatcher.UIThread.RunJobs();
            var args = new UnobservedTaskExceptionEventArgs(new AggregateException(boom));

            start.App.OnUnobservedTaskException(this, args);

            Assert.Single(Escaped(start.Log));
            Assert.Empty(ErrorsAbout(start.Log, UnobservedTaskMessage, boom));
            Assert.True(args.Observed);
        });

    [Fact]
    public Task An_exception_no_handler_caught_is_logged() =>
        Headless.Run(() =>
        {
            using var start = new Start(openWindow: false);
            var boom = new InvalidOperationException("main thread boom");

            start.App.OnAppDomainUnhandledException(this, new UnhandledExceptionEventArgs(boom, isTerminating: true));

            Assert.Same(boom, Assert.Single(Errors(start.Log, NoHandlerCaughtMessage)).Exception);
        });

    /// <summary>Posts a job that throws, then runs the jobs, which today ends the loop with the exception.</summary>
    private static void ThrowInAJob()
    {
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The log's error entries whose message starts with <paramref name="prefix"/>, in order.</summary>
    private static List<LogEntry> Errors(RecordingAppLog log, string prefix) =>
        [.. log.Entries.Where(entry => entry.Level == "error" && entry.Message.StartsWith(prefix, StringComparison.Ordinal))];

    /// <summary>The log's entries for an exception that escaped a handler, in order.</summary>
    private static List<LogEntry> Escaped(RecordingAppLog log) => Errors(log, EscapedMessage);

    /// <summary>
    /// The log's error entries for <paramref name="prefix"/> that carry it, directly or inside the aggregate a dispatcher or task
    /// failure arrives in. A test filters to its own exception, since the real unobserved-task handler is installed while it runs
    /// and can log a faulted dispatcher task an earlier test left behind.
    /// </summary>
    private static List<LogEntry> ErrorsAbout(RecordingAppLog log, string prefix, Exception exception) =>
        [.. Errors(log, prefix).Where(entry => Carries(entry.Exception, exception))];

    /// <summary>Whether <paramref name="logged"/> is <paramref name="exception"/> itself, or an aggregate that holds it.</summary>
    private static bool Carries(Exception? logged, Exception exception)
    {
        if (logged is null)
        {
            return false;
        }

        return ReferenceEquals(logged, exception)
            || (logged is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => ReferenceEquals(inner, exception)));
    }

    private static ToastCenter ToastsOf(Window window) =>
        Assert.IsType<ToastCenter>(Assert.IsType<DocumentWindowViewModel>(window.DataContext).Toasts);

    private static string MissingFile() => Path.Combine(Path.GetTempPath(), "yamv-gone-" + Guid.NewGuid().ToString("N") + ".md");

    /// <summary>
    /// One start over a fresh data folder, with a recording log the app takes as its process log; disposing it closes the
    /// window, releases the app and restores the statics and environment the test changed.
    /// </summary>
    private sealed class Start : IDisposable
    {
        private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "yamv-unhandled-" + Guid.NewGuid().ToString("N"));
        private readonly string? _dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        private readonly string? _harnessBefore = Environment.GetEnvironmentVariable(App.HarnessVariable);

        public Start(bool openWindow)
        {
            App = Assert.IsType<App>(Application.Current);
            try
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataFolder);
                Environment.SetEnvironmentVariable(App.HarnessVariable, null);
                App.ViewerFiles = [];
                App.ProcessLog = Log;
                App.ResetLog();
                App.InstallUnhandledExceptionHandlers();
                StartWindow = openWindow ? App.CreateStartWindow() : null;
            }
            catch
            {
                try
                {
                    App.ReleaseMainWindow();
                }
                finally
                {
                    Restore();
                }

                throw;
            }
        }

        public App App { get; }

        public Window? StartWindow { get; }

        public RecordingAppLog Log { get; } = new();

        /// <summary>Closes the open window and releases the app, as the exit does.</summary>
        public void Release()
        {
            try
            {
                CloseWindows();
            }
            finally
            {
                App.ReleaseMainWindow();
            }
        }

        public void Dispose()
        {
            try
            {
                Release();
            }
            finally
            {
                Restore();
            }
        }

        private void CloseWindows()
        {
            foreach (Window viewer in App.ViewerWindows.Where(viewer => viewer.IsVisible).ToList())
            {
                viewer.Close();
            }

            if (App.MainDocumentWindow is { IsVisible: true } main)
            {
                main.Close();
            }
        }

        private void Restore()
        {
            App.ResetLog();
            App.ViewerFiles = [];
            App.ProcessLog = null;
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataBefore);
            Environment.SetEnvironmentVariable(App.HarnessVariable, _harnessBefore);
            if (Directory.Exists(_dataFolder))
            {
                Directory.Delete(_dataFolder, recursive: true);
            }
        }
    }
}
