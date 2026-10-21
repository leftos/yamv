using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Theming;

namespace Yamv.App.Tests.Services;

public sealed class ThemeFolderSyncTests
{
    private static readonly TimeSpan RaiseBound = TimeSpan.FromMinutes(1);

    private readonly RecordingAppLog _log = new();
    private readonly FakeChangeSource _source = new();

    [Fact]
    public Task Changed_on_a_pool_thread_reloads_on_the_ui_thread() =>
        Headless.Run(() =>
        {
            List<bool> onUiThread = [];
            using ThemeFolderSync sync = CreateSync(() => onUiThread.Add(Dispatcher.UIThread.CheckAccess()));

            Assert.True(RaiseOnPoolThread(_source.Raise), "The change was not raised within the bound.");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([true], onUiThread);
        });

    [Fact]
    public Task Handler_never_blocks_the_raising_thread_on_the_ui_thread() =>
        Headless.Run(() =>
        {
            int reloads = 0;
            using ThemeFolderSync sync = CreateSync(() => reloads++);

            // The UI thread is busy running this test while the pool thread raises: a handler that waited on it would never return.
            Assert.True(RaiseOnPoolThread(_source.Raise), "The handler blocked the raising thread on the UI thread.");
            Assert.Equal(0, reloads);

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, reloads);
        });

    [Fact]
    public Task Dispose_before_the_posted_reload_runs_does_nothing() =>
        Headless.Run(() =>
        {
            int reloads = 0;
            ThemeFolderSync sync = CreateSync(() => reloads++);
            Assert.True(RaiseOnPoolThread(_source.Raise), "The change was not raised within the bound.");

            sync.Dispose();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, reloads);
            Assert.Equal(1, _source.DisposeCount);
        });

    [Fact]
    public Task Dispose_twice_is_safe() =>
        Headless.Run(() =>
        {
            var disposed = new ObjectDisposedException("watcher");
            _source.ThrowOnDispose = disposed;
            ThemeFolderSync sync = CreateSync(() => { });

            sync.Dispose();
            sync.Dispose();

            Assert.Equal(1, _source.DisposeCount);
            LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
            Assert.Same(disposed, error.Exception);
        });

    [Fact]
    public Task Reload_failure_is_logged_not_thrown_on_the_ui_thread() =>
        Headless.Run(() =>
        {
            var failure = new IOException("The themes folder cannot be read.");
            using ThemeFolderSync sync = CreateSync(() => throw failure);
            Assert.True(RaiseOnPoolThread(_source.Raise), "The change was not raised within the bound.");

            Dispatcher.UIThread.RunJobs();

            LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
            Assert.Same(failure, error.Exception);
        });

    [Fact]
    public Task Saving_a_theme_file_in_the_real_folder_reloads_the_bridge() =>
        Headless.RunAsync(async () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "yamv-sync-themes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "mine" + ThemeCatalog.FileSuffix);
            WriteTheme(file, "#112233");
            var bridge = new ThemeBridge(new ThemeCatalog(folder, _log), Application.Current!, _log);
            try
            {
                bridge.Apply("mine", "light");
                var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.ThemesReloaded += (_, _) =>
                {
                    if (Accent(bridge) == Color.Parse("#445566"))
                    {
                        reloaded.TrySetResult();
                    }
                };
                using var sync = new ThemeFolderSync(bridge, folder, TimeProvider.System, _log);

                WriteTheme(file, "#445566");

                await reloaded.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
                Assert.Equal("mine", bridge.Current!.Id);
            }
            finally
            {
                bridge.Dispose();
                Directory.Delete(folder, recursive: true);
            }
        });

    private static Color Accent(ThemeBridge bridge) => Color.FromUInt32(ChromePalette.From(bridge.Current!).Colors["accent"]);

    private static void WriteTheme(string file, string lightAccent) =>
        File.WriteAllText(file, "{ \"name\": \"Mine\", \"light\": { \"tokens\": { \"accent\": \"" + lightAccent + "\" } }, \"dark\": {} }");

    /// <summary>Runs <paramref name="raise"/> on a thread-pool thread and says whether it returned within <see cref="RaiseBound"/>.</summary>
    private static bool RaiseOnPoolThread(Action raise)
    {
        using var returned = new ManualResetEventSlim();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            raise();
            returned.Set();
        });
        return returned.Wait(RaiseBound, TestContext.Current.CancellationToken);
    }

    private ThemeFolderSync CreateSync(Action reload) =>
        new(
            reload,
            _log,
            onChanged =>
            {
                _source.OnChanged = onChanged;
                return new StopHandle(_source);
            }
        );

    /// <summary>Disposing it stops the <see cref="FakeChangeSource"/>, as disposing the folder watcher stops it.</summary>
    private sealed class StopHandle(FakeChangeSource source) : IDisposable
    {
        public void Dispose() => source.Stop();
    }

    /// <summary>Stands in for the folder watcher: the test raises its change signal, and it counts and can fail its stops.</summary>
    private sealed class FakeChangeSource
    {
        public Action? OnChanged { get; set; }

        public Exception? ThrowOnDispose { get; set; }

        public int DisposeCount { get; private set; }

        public void Raise() => OnChanged!.Invoke();

        public void Stop()
        {
            DisposeCount++;
            if (ThrowOnDispose is { } exception)
            {
                throw exception;
            }
        }
    }
}
