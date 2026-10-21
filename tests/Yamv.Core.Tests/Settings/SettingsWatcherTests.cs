using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;
using Yamv.Core.Settings;
using Yamv.Core.Tests.Session;
using Yamv.Core.Tests.Theming;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Settings;

public sealed class SettingsWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);

    /// <summary>A hang guard: a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private static readonly AppSettings Saved = new("ink", AppSettings.DarkAppearance);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-settings-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeFolderArm _arm = new([]);
    private readonly ManualScheduler _scheduler = new();
    private readonly ConcurrentQueue<AppSettings> _reports = new();
    private readonly SettingsStore _store;
    private readonly SettingsWriter _writer;

    public SettingsWatcherTests()
    {
        Directory.CreateDirectory(_directory);
        WriteFile("""{ "themeId": "ink", "appearance": "dark" }""");
        _store = new SettingsStore(SettingsFile, _log);
        _writer = new SettingsWriter(_store, _log, _scheduler, HangGuard);
        Assert.Equal(Saved, _store.Load());
    }

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private FakeFolderEvents Events => _arm.ByFolder[Path.GetFullPath(_directory)];

    public void Dispose()
    {
        _writer.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Changed_AfterTheFileChangesOnDisk_ReportsTheNewSettings()
    {
        using SettingsWatcher watcher = Create();

        WriteFile("""{ "themeId": "paper", "appearance": "light" }""");
        RaiseOnSettingsFile();

        Assert.Equal([new AppSettings("paper", AppSettings.LightAppearance)], _reports);
    }

    [Fact]
    public async Task Changed_WhileTheWriterIsBusy_ReportsNothing_AndReportsTheFinalStateOnceIdle()
    {
        using SettingsWatcher watcher = Create();
        _writer.Request("paper", AppSettings.LightAppearance);

        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");
        RaiseOnSettingsFile();
        Task waiting = watcher.Waiting;
        RaiseOnSettingsFile();

        Assert.Same(waiting, watcher.Waiting);
        Assert.Empty(_reports);

        _scheduler.RunOne();
        await waiting.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal([new AppSettings("paper", AppSettings.LightAppearance)], _reports);
    }

    [Fact]
    public void Changed_ForTheStoresOwnWrite_ReportsValuesEqualToThePick()
    {
        using SettingsWatcher watcher = Create();
        _writer.Request("paper", AppSettings.LightAppearance);
        _scheduler.RunOne();

        RaiseOnSettingsFile();

        Assert.Equal([new AppSettings("paper", AppSettings.LightAppearance)], _reports);
    }

    [Fact]
    public void Changed_ForTheStoresOwnWriteArrivingAsARename_ReportsValuesEqualToThePick()
    {
        using SettingsWatcher watcher = Create();
        _writer.Request("paper", AppSettings.LightAppearance);
        _scheduler.RunOne();

        Events.Raise(new FolderEvent(FolderEventKind.Renamed, "settings.json", $"settings.json.{Guid.NewGuid():N}.tmp", null));
        _time.Advance(Debounce);

        Assert.Equal([new AppSettings("paper", AppSettings.LightAppearance)], _reports);
    }

    [Fact]
    public async Task Changed_WhenTheReadAfterTheWriterGoesIdleThrows_LogsIt()
    {
        var store = new SettingsStore(SettingsFile, new ThrowingAppLog(), _ => { }, _time);
        using var writer = new SettingsWriter(store, _log, _scheduler, HangGuard);
        using SettingsWatcher watcher = Create(store, writer);
        using var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        writer.Request("paper", AppSettings.LightAppearance);
        RaiseOnSettingsFile();
        Task waiting = watcher.Waiting;

        _scheduler.RunOne();
        await waiting.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Empty(_reports);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public void Changed_AfterTheFileIsDeleted_ReportsNothingNew()
    {
        using SettingsWatcher watcher = Create();

        File.Delete(SettingsFile);
        Events.Raise(new FolderEvent(FolderEventKind.Deleted, "settings.json", null, null));
        _time.Advance(Debounce);

        Assert.Equal([Saved], _reports);
    }

    [Fact]
    public void Changed_ForAnUnparseableFile_ReportsNothingNew()
    {
        using SettingsWatcher watcher = Create();

        WriteFile("{ \"themeId\": ");
        RaiseOnSettingsFile();

        Assert.Equal([Saved], _reports);
    }

    [Fact]
    public void Changed_ForAnEventOnAnotherFileInTheFolder_ReportsNothing()
    {
        using SettingsWatcher watcher = Create();
        string temporary = $"settings.json.{Guid.NewGuid():N}.tmp";

        Events.Raise(new FolderEvent(FolderEventKind.Changed, "session.json", null, null));
        Events.Raise(new FolderEvent(FolderEventKind.Changed, "widths.json", null, null));
        Events.Raise(new FolderEvent(FolderEventKind.Created, temporary, null, null));
        Events.Raise(new FolderEvent(FolderEventKind.Changed, temporary, null, null));
        Events.Raise(new FolderEvent(FolderEventKind.Deleted, temporary, null, null));
        Events.Raise(new FolderEvent(FolderEventKind.Changed, "settings.json.bad", null, null));
        _time.Advance(MaxWait);

        Assert.Empty(_reports);
    }

    [Fact]
    public async Task Dispose_StopsReports_AndWaitsForARunningHandler()
    {
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var steps = new ConcurrentQueue<string>();
        SettingsWatcher watcher = Create();
        watcher.Changed += (_, _) =>
        {
            inside.Set();
            release.Wait(HangGuard);
            steps.Enqueue("changed returned");
        };

        try
        {
            Events.Raise(new FolderEvent(FolderEventKind.Changed, "settings.json", null, null));
            var timerRun = Task.Run(() => _time.Advance(Debounce), TestContext.Current.CancellationToken);
            Assert.True(inside.Wait(HangGuard, TestContext.Current.CancellationToken), "Changed was never raised");

            var disposer = new Thread(() =>
            {
                watcher.Dispose();
                steps.Enqueue("dispose returned");
            })
            {
                IsBackground = true,
            };
            disposer.Start();
            WaitUntil(() => (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0 || !disposer.IsAlive);
            release.Set();
            Assert.True(disposer.Join(HangGuard), "Dispose never returned");
            await timerRun.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            watcher.Dispose();
        }

        Assert.Equal(["changed returned", "dispose returned"], steps);

        WriteFile("""{ "themeId": "paper", "appearance": "light" }""");
        RaiseOnSettingsFile();

        Assert.Single(_reports);
    }

    [Fact]
    public async Task Dispose_WhileWaitingForTheWriter_ReportsNothingAfter()
    {
        SettingsWatcher watcher = Create();
        _writer.Request("paper", AppSettings.LightAppearance);
        RaiseOnSettingsFile();
        Task waiting = watcher.Waiting;
        Assert.False(waiting.IsCompleted);

        watcher.Dispose();
        _scheduler.RunOne();
        await waiting.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Empty(_reports);
        Assert.Equal(new AppSettings("paper", AppSettings.LightAppearance), _store.Load());
    }

    [Fact]
    public async Task Dispose_WhileAHandlerRunsAfterTheWriterGoesIdle_WaitsForIt_AndReportsNothingAfter()
    {
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var steps = new ConcurrentQueue<string>();
        SettingsWatcher watcher = Create();
        watcher.Changed += (_, _) =>
        {
            inside.Set();
            release.Wait(HangGuard);
            steps.Enqueue("changed returned");
        };

        try
        {
            _writer.Request("paper", AppSettings.LightAppearance);
            RaiseOnSettingsFile();
            Task waiting = watcher.Waiting;
            _scheduler.RunOne();
            Assert.True(inside.Wait(HangGuard, TestContext.Current.CancellationToken), "Changed was never raised");

            var disposer = new Thread(() =>
            {
                watcher.Dispose();
                steps.Enqueue("dispose returned");
            })
            {
                IsBackground = true,
            };
            disposer.Start();
            WaitUntil(() => (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0 || !disposer.IsAlive);
            release.Set();
            Assert.True(disposer.Join(HangGuard), "Dispose never returned");
            await waiting.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            watcher.Dispose();
        }

        Assert.Equal(["changed returned", "dispose returned"], steps);

        WriteFile("""{ "themeId": "my-theme", "appearance": "dark" }""");
        RaiseOnSettingsFile();

        Assert.Equal([new AppSettings("paper", AppSettings.LightAppearance)], _reports);
    }

    /// <summary>Waits with a sleeping poll until <paramref name="condition"/> holds, failing at the hang guard.</summary>
    private static void WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + HangGuard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition never held.");
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// A watcher over fake folder events whose passes run inline, so its folder is armed before this returns, recording each
    /// report.
    /// </summary>
    private SettingsWatcher Create() => Create(_store, _writer);

    /// <inheritdoc cref="Create()"/>
    private SettingsWatcher Create(SettingsStore store, SettingsWriter writer)
    {
        var seams = new DocumentFileWatcher.Seams(_arm.Arm, new FakeFolderProbe(), _ => null, pass => pass());
        var watcher = new SettingsWatcher(store, writer, _log, new DocumentFileWatcher(_time, _log, seams));
        watcher.Changed += (_, settings) => _reports.Enqueue(settings);
        return watcher;
    }

    /// <summary>Raises a change of <c>settings.json</c> and runs the clock past the debounce, so a report due runs now.</summary>
    private void RaiseOnSettingsFile()
    {
        Events.Raise(new FolderEvent(FolderEventKind.Changed, "settings.json", null, null));
        _time.Advance(Debounce);
    }

    private void WriteFile(string json) => File.WriteAllText(SettingsFile, json);

    /// <summary>A log whose error reports throw, so a store that cannot read its file throws from <see cref="SettingsStore.Load"/>.</summary>
    private sealed class ThrowingAppLog : IAppLog
    {
        public void LogInfo(string message) { }

        public void LogWarning(string message, Exception? exception = null) { }

        public void LogError(string message, Exception? exception = null) => throw new InvalidOperationException(message, exception);
    }

    /// <summary>Holds every queued task until the test runs it, so a test decides when a write happens.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly Queue<Task> _queued = new();

        public void RunOne() => Assert.True(TryExecuteTask(_queued.Dequeue()));

        protected override void QueueTask(Task task) => _queued.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _queued];
    }
}
