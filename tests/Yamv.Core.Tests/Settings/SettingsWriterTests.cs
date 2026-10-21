using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Diagnostics;
using Yamv.Core.Settings;
using Yamv.Core.Tests.Session;

namespace Yamv.Core.Tests.Settings;

public sealed class SettingsWriterTests : IDisposable
{
    /// <summary>A hang guard: a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-settings-writer-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAppLog _log = new();

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Request_DoesNoIoOnTheCallersThread()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, "{}");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int pauses = 0;
        var scheduler = new ManualScheduler();
        using var writer = new SettingsWriter(
            CreateStore(_ =>
            {
                pauses++;
                held.Dispose();
            }),
            _log,
            scheduler,
            HangGuard
        );

        try
        {
            writer.Request("ink", AppSettings.DarkAppearance);

            Assert.Equal(0, pauses);
            Assert.Empty(_log.Entries);
            Assert.Equal(1, scheduler.QueuedCount);

            scheduler.RunOne();
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, pauses);
        Assert.Equal(new AppSettings("ink", AppSettings.DarkAppearance), ReadFile());
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.True(writer.WhenIdle().IsCompleted);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Dispose_GivesUpAfterItsWait_LogsAWarningAndTheWriteStillRuns()
    {
        var scheduler = new ManualScheduler();
        var writer = new SettingsWriter(CreateStore(_ => { }), _log, scheduler, TimeSpan.Zero);
        writer.Request("ink", AppSettings.DarkAppearance);

        writer.Dispose();

        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.Contains(SettingsFile, warning.Message, StringComparison.Ordinal);
        Assert.Equal(1, scheduler.QueuedCount);

        scheduler.RunOne();

        Assert.Equal(new AppSettings("ink", AppSettings.DarkAppearance), ReadFile());
        Assert.True(writer.WhenIdle().IsCompleted);
    }

    [Fact]
    public void Dispose_WritesThePairRequestedDuringTheRunningWrite()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, "{}");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var entered = new ManualResetEventSlim();
        SettingsWriter? writer = null;
        SettingsStore store = CreateStore(_ =>
        {
            if (entered.IsSet)
            {
                return;
            }

            entered.Set();
            WaitUntil(() => writer!.IsDisposed);
            held.Dispose();
        });
        writer = new SettingsWriter(store, _log, new ThreadPerTaskScheduler(), HangGuard);

        try
        {
            writer.Request("ink", AppSettings.DarkAppearance);
            Assert.True(entered.Wait(HangGuard, TestContext.Current.CancellationToken), "The write never reached the held file.");
            writer.Request("paper", AppSettings.LightAppearance);

            writer.Dispose();

            Assert.Equal(new AppSettings("paper", AppSettings.LightAppearance), ReadFile());
        }
        finally
        {
            held.Dispose();
            writer.Dispose();
        }

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Request_LogThrowsOnTheFailedWrite_TheWriterStillTakesTheNextRequest()
    {
        var scheduler = new ManualScheduler();
        using var writer = new SettingsWriter(CreateStore(_ => { }), new ThrowingWarningLog(), scheduler, HangGuard);

        writer.Request("", AppSettings.DarkAppearance);
        scheduler.RunOne();

        Assert.True(writer.WhenIdle().IsCompleted);

        writer.Request("ink", AppSettings.DarkAppearance);
        Assert.Equal(1, scheduler.QueuedCount);
        scheduler.RunOne();

        Assert.Equal(new AppSettings("ink", AppSettings.DarkAppearance), ReadFile());
    }

    [Fact]
    public void Request_BurstWhileAWriteIsBlocked_WritesTheLastPairInAtMostTwoWrites()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, "{}");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scheduler = new ManualScheduler();
        SettingsWriter? writer = null;
        bool burstSent = false;
        SettingsStore store = CreateStore(_ =>
        {
            if (burstSent)
            {
                return;
            }

            burstSent = true;
            writer!.Request("paper", AppSettings.LightAppearance);
            writer.Request("studio", AppSettings.DarkAppearance);
            writer.Request("my-theme", AppSettings.LightAppearance);
            held.Dispose();
        });
        writer = new SettingsWriter(store, _log, scheduler, HangGuard);

        try
        {
            writer.Request("ink", AppSettings.DarkAppearance);
            scheduler.RunOne();
            Assert.True(burstSent);
            Assert.Equal(1, scheduler.QueuedCount);
            scheduler.RunOne();
        }
        finally
        {
            held.Dispose();
            writer.Dispose();
        }

        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Equal(2, scheduler.ExecutedCount);
        Assert.Equal(new AppSettings("my-theme", AppSettings.LightAppearance), ReadFile());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Request_WriteThrows_IsLoggedAndTheNextRequestWrites()
    {
        var scheduler = new ManualScheduler();
        using var writer = new SettingsWriter(CreateStore(_ => { }), _log, scheduler, HangGuard);

        writer.Request("", AppSettings.DarkAppearance);
        scheduler.RunOne();

        LogEntry warning = Assert.Single(_log.Entries);
        Assert.Equal("warning", warning.Level);
        Assert.IsType<ArgumentException>(warning.Exception);
        Assert.Contains(SettingsFile, warning.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsFile));

        writer.Request("ink", AppSettings.DarkAppearance);
        scheduler.RunOne();

        Assert.Equal(new AppSettings("ink", AppSettings.DarkAppearance), ReadFile());
        Assert.Single(_log.Entries);
    }

    [Fact]
    public void Dispose_WaitsForTheRunningWrite()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, "{}");
        var held = new FileStream(SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var entered = new ManualResetEventSlim();
        SettingsWriter? writer = null;
        SettingsStore store = CreateStore(_ =>
        {
            if (entered.IsSet)
            {
                return;
            }

            entered.Set();
            WaitUntil(() => writer!.IsDisposed);
            held.Dispose();
        });
        writer = new SettingsWriter(store, _log, new ThreadPerTaskScheduler(), HangGuard);

        try
        {
            writer.Request("ink", AppSettings.DarkAppearance);
            Assert.True(entered.Wait(HangGuard, TestContext.Current.CancellationToken), "The write never reached the held file.");

            writer.Dispose();

            Assert.Equal(new AppSettings("ink", AppSettings.DarkAppearance), ReadFile());
            Assert.True(writer.WhenIdle().IsCompleted);
        }
        finally
        {
            held.Dispose();
            writer.Dispose();
        }

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Request_AfterDispose_IsIgnored()
    {
        var scheduler = new ManualScheduler();
        var writer = new SettingsWriter(CreateStore(_ => { }), _log, scheduler, HangGuard);
        writer.Dispose();

        writer.Request("ink", AppSettings.DarkAppearance);

        Assert.Equal(0, scheduler.QueuedCount);
        Assert.True(writer.WhenIdle().IsCompleted);
        Assert.False(File.Exists(SettingsFile));
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

    private SettingsStore CreateStore(Action<TimeSpan> pause) => new(SettingsFile, _log, pause, new FakeTimeProvider());

    private AppSettings ReadFile() => new SettingsStore(SettingsFile, new RecordingAppLog()).Load();

    /// <summary>Holds every queued task until the test runs it, so a test decides when a write happens and on which thread.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly Queue<Task> _queued = new();

        public int QueuedCount => _queued.Count;

        public int ExecutedCount { get; private set; }

        public void RunOne()
        {
            Task task = _queued.Dequeue();
            ExecutedCount++;
            Assert.True(TryExecuteTask(task));
        }

        protected override void QueueTask(Task task) => _queued.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _queued];
    }

    /// <summary>A log whose warnings throw, standing for a log that fails while the writer reports a failed write.</summary>
    private sealed class ThrowingWarningLog : IAppLog
    {
        public void LogInfo(string message) { }

        public void LogWarning(string message, Exception? exception = null) => throw new InvalidOperationException("The log failed.");

        public void LogError(string message, Exception? exception = null) { }
    }

    /// <summary>Runs each task on a new thread of its own, so a write can block while the test thread goes on.</summary>
    private sealed class ThreadPerTaskScheduler : TaskScheduler
    {
        protected override void QueueTask(Task task) => new Thread(() => TryExecuteTask(task)) { IsBackground = true }.Start();

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
