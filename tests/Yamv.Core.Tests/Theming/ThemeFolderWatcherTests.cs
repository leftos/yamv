using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Tests.Session;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeFolderWatcherTests
{
    private const string ValidTheme = """{ "name": "Mine", "light": { "tokens": { "accent": "#123456" } } }""";
    private const string Folder = "themes";
    private const string ThemeFile = "mine" + ThemeCatalog.FileSuffix;
    private const string TempFile = ".0123456789abcdef0123456789abcdef.tmp";
    private const string OtherTempFile = ".fedcba9876543210fedcba9876543210.tmp";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private readonly FakeTimeProvider time = new();
    private readonly RecordingAppLog log = new();
    private readonly List<string> order = [];
    private readonly FakeFolderArm arm;
    private readonly FakeFolderProbe probe = new();
    private int changes;

    public ThemeFolderWatcherTests()
    {
        arm = new FakeFolderArm(order);
    }

    [Fact]
    public void Created_ThemeFile_RaisesChangedAfterDebounce()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Created, ThemeFile, null, null));
        time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, changes);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Renamed_TempToThemeFile_RaisesChanged()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Renamed, ThemeFile, TempFile, null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Renamed_OutWithNullName_RaisesChanged()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Renamed, null, TempFile, null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void FirstArm_RaisesOneChanged()
    {
        using ThemeFolderWatcher watcher = Create();
        Assert.Equal(1, arm.Attempts);

        time.Advance(Debounce);
        Assert.Equal(1, changes);

        time.Advance(Tick * 3);
        Assert.Equal(1, changes);
        Assert.Equal(1, arm.Attempts);
    }

    [Fact]
    public void TempOnlyEvents_RaiseNothing()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Created, TempFile, null, null));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Changed, TempFile, null, null));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Deleted, TempFile, null, null));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Renamed, OtherTempFile, TempFile, null));
        time.Advance(Debounce);
        Assert.Equal(0, changes);

        arm.Current.Raise(new FolderEvent(FolderEventKind.Changed, ThemeFile, null, null));
        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Burst_GapsUnderDebounce_RaisesOnce()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        for (int i = 0; i < 5; i++)
        {
            arm.Current.Raise(new FolderEvent(FolderEventKind.Changed, ThemeFile, null, null));
            time.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(0, changes);
        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DeleteThenCreate_WithinDebounce_RaisesOnce()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Deleted, ThemeFile, null, null));
        time.Advance(TimeSpan.FromMilliseconds(100));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Created, ThemeFile, null, null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("io")]
    [InlineData("access-denied")]
    public void Error_AnyException_RaisesChangedAndReArmsOnNextTick(string kind)
    {
        Exception error = kind switch
        {
            "overflow" => new InternalBufferOverflowException(),
            "io" => new IOException("FSW_BufferOverflow"),
            _ => new Win32Exception(5),
        };
        using ThemeFolderWatcher watcher = StartArmed();
        FakeFolderEvents first = arm.Current;

        first.Raise(new FolderEvent(FolderEventKind.Error, null, null, error));
        Assert.Equal(1, arm.Attempts);
        time.Advance(Debounce);
        Assert.Equal(1, changes);
        Assert.Equal(1, arm.Attempts);

        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, arm.Current.DisposeCount);
    }

    [Fact]
    public void FolderVanishes_DisarmsAndRaisesChanged()
    {
        using ThemeFolderWatcher watcher = StartArmed();

        probe.State = FolderState.Missing;
        time.Advance(Tick);
        Assert.Equal(1, arm.Current.DisposeCount);

        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FolderReturns_ArmsAndRaisesChanged()
    {
        probe.State = FolderState.Missing;
        using ThemeFolderWatcher watcher = Create();
        time.Advance(Tick + Debounce);
        Assert.Equal(0, arm.Attempts);
        Assert.Equal(0, changes);
        Assert.Single(log.Entries, entry => entry.Level == "info");

        probe.State = FolderState.Present("returned");
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(["arm", "changed"], order);
    }

    [Fact]
    public void FolderIdentityChanges_ReArms()
    {
        using ThemeFolderWatcher watcher = StartArmed();
        FakeFolderEvents first = arm.Current;

        probe.State = FolderState.Present("replaced");
        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, arm.Current.DisposeCount);

        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ArmFails_LogsOncePerStreakAndRetriesEachTick()
    {
        arm.FailNext(new ArgumentException("The directory name themes does not exist."));
        arm.FailNext(new FileNotFoundException("not found"));
        arm.FailNext(new DirectoryNotFoundException("not found"));
        arm.FailNext(new UnauthorizedAccessException("delete pending"));
        using ThemeFolderWatcher watcher = Create();
        time.Advance(Tick);
        time.Advance(Tick);
        time.Advance(Tick);
        Assert.Equal(4, arm.Attempts);
        Assert.Single(log.Entries, entry => entry.Level == "warning");

        time.Advance(Tick);
        Assert.Equal(5, arm.Attempts);
        arm.FailNext(new IOException("again"));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Tick);
        time.Advance(Tick);

        Assert.Equal(7, arm.Attempts);
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == "warning" && entry.Message.StartsWith("Cannot watch", StringComparison.Ordinal)));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == "error");
    }

    [Fact]
    public void ArmRecovers_LogsRecovery()
    {
        arm.FailNext(new IOException("busy"));
        using ThemeFolderWatcher watcher = Create();
        Assert.Single(log.Entries);
        Assert.Equal("warning", log.Entries[0].Level);

        time.Advance(Tick);

        Assert.Equal(2, arm.Attempts);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("info", log.Entries[1].Level);
        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SubscriberThrows_LoggedAndLaterChangesStillRaise()
    {
        int calls = 0;
        using var watcher = new ThemeFolderWatcher(Folder, time, log, arm.Arm, probe);
        watcher.Changed += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("the subscriber failed");
        };

        time.Advance(Debounce);
        arm.Current.Raise(new FolderEvent(FolderEventKind.Created, ThemeFile, null, null));
        time.Advance(Debounce);

        Assert.Equal(2, calls);
        LogEntry[] errors = [.. log.Entries.Where(entry => entry.Level == "error")];
        Assert.Equal(2, errors.Length);
        Assert.All(errors, entry => Assert.IsType<InvalidOperationException>(entry.Exception));
    }

    [Fact]
    public void ProbeThrows_LoggedAndNextTickRuns()
    {
        probe.ThrowNext = new InvalidOperationException("the probe failed");
        using ThemeFolderWatcher watcher = Create();
        Assert.Equal(0, arm.Attempts);
        LogEntry error = Assert.Single(log.Entries, entry => entry.Level == "error");
        Assert.IsType<InvalidOperationException>(error.Exception);

        time.Advance(Tick);

        Assert.Equal(1, arm.Attempts);
        Assert.Contains(log.Entries, entry => entry.Level == "info" && entry.Message.Contains("works again", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorDuringStart_RaisesChangedAndReArmsOnNextTick()
    {
        arm.ErrorOnNextStart(new IOException("FSEventStreamStart failed"));
        using ThemeFolderWatcher watcher = Create();
        FakeFolderEvents first = arm.Current;
        Assert.Equal(1, arm.Attempts);

        time.Advance(Debounce);
        Assert.Equal(1, changes);

        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        Assert.Equal(1, first.DisposeCount);
        Assert.True(arm.Current.Started);
    }

    [Fact]
    public void A_source_disposed_during_its_start_stops_quietly()
    {
        ThemeFolderWatcher? watcher = null;
        int attempts = 0;
        var held = new FakeFolderEvents { ThrowOnStart = new ObjectDisposedException("FileSystemWatcher"), OnStart = () => watcher?.Dispose() };
        try
        {
            watcher = new ThemeFolderWatcher(Folder, time, log, _ => ++attempts == 1 ? new FakeFolderEvents() : held, probe);
            probe.State = FolderState.Present("replaced");

            time.Advance(Tick);

            Assert.Equal(2, attempts);
            Assert.Equal(1, held.DisposeCount);
            Assert.Empty(log.Entries);

            time.Advance(Tick * 3);
            Assert.Equal(2, attempts);
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    [Fact]
    public void StartThrows_LoggedAsArmFailureAndRetried()
    {
        arm.FailNextStart(new IOException("cannot start"));
        using ThemeFolderWatcher watcher = Create();
        Assert.Equal(1, arm.Current.DisposeCount);
        LogEntry warning = Assert.Single(log.Entries, entry => entry.Level == "warning");
        Assert.StartsWith("Cannot watch", warning.Message, StringComparison.Ordinal);

        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        Assert.True(arm.Current.Started);
        time.Advance(Debounce);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void WatcherError_LogsWarningOncePerStreakAndRecovery()
    {
        using ThemeFolderWatcher watcher = StartArmed();
        var error = new InternalBufferOverflowException();

        arm.Current.Raise(new FolderEvent(FolderEventKind.Error, null, null, error));
        arm.Current.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        LogEntry warning = Assert.Single(log.Entries, entry => entry.Level == "warning");
        Assert.Same(error, warning.Exception);
        Assert.Equal($"The themes folder watcher of {Folder} failed; re-arming.", warning.Message);

        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        Assert.Equal("info", log.Entries[^1].Level);
        Assert.Equal($"Watching the themes folder {Folder} again.", log.Entries[^1].Message);
    }

    [Fact]
    public void FolderReturns_ArmFails_StillRaisesChanged()
    {
        probe.State = FolderState.Missing;
        using ThemeFolderWatcher watcher = Create();
        time.Advance(Tick);
        probe.State = FolderState.Present("returned");
        arm.FailNext(new IOException("busy"));

        time.Advance(Tick);
        Assert.Equal(1, arm.Attempts);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void FolderIdentityChanges_ArmFails_StillRaisesChanged()
    {
        using ThemeFolderWatcher watcher = StartArmed();
        probe.State = FolderState.Present("replaced");
        arm.FailNext(new IOException("busy"));

        time.Advance(Tick);
        Assert.Equal(2, arm.Attempts);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void DisposeDuringArm_DisposesNewSourceAndRaisesNothing()
    {
        ThemeFolderWatcher watcher = StartArmed();
        arm.OnAttempt = attempt =>
        {
            if (attempt == 2)
            {
                watcher.Dispose();
            }
        };
        probe.State = FolderState.Present("replaced");

        time.Advance(Tick + Debounce);

        Assert.Equal(2, arm.Attempts);
        Assert.Equal(1, arm.Current.DisposeCount);
        Assert.False(arm.Current.Started);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void EventAfterDispose_NoThrowNoChanged()
    {
        ThemeFolderWatcher watcher = StartArmed();
        FakeFolderEvents events = arm.Current;
        watcher.Dispose();

        events.Raise(new FolderEvent(FolderEventKind.Created, ThemeFile, null, null));
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
        Assert.Equal(1, arm.Attempts);
        Assert.DoesNotContain(log.Entries, entry => entry.Level != "info");
    }

    [Fact]
    public void ErrorAfterDispose_NoThrow()
    {
        ThemeFolderWatcher watcher = StartArmed();
        FakeFolderEvents events = arm.Current;
        watcher.Dispose();

        events.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
        Assert.Equal(1, arm.Attempts);
        Assert.DoesNotContain(log.Entries, entry => entry.Level != "info");
    }

    [Fact]
    public async Task Dispose_WhileChangedRuns_ReturnsAfterIt()
    {
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var steps = new ConcurrentQueue<string>();
        var watcher = new ThemeFolderWatcher(Folder, time, log, arm.Arm, probe);
        watcher.Changed += (_, _) =>
        {
            inside.Set();
            release.Wait();
            steps.Enqueue("changed returned");
        };

        try
        {
            var timerRun = Task.Run(() => time.Advance(Debounce), TestContext.Current.CancellationToken);
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
    }

    [Fact]
    public void A_tick_after_dispose_neither_probes_nor_logs()
    {
        var clock = new ManualTimeProvider();
        var failing = new FailingProbe();
        var watcher = new ThemeFolderWatcher(Folder, clock, log, arm.Arm, failing);
        ManualTimeProvider.ManualTimer tick = Assert.Single(clock.Timers);

        watcher.Dispose();
        tick.Fire();

        Assert.Equal(0, failing.Calls);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Dispose_Twice_NoOp()
    {
        ThemeFolderWatcher watcher = StartArmed();

        watcher.Dispose();
        watcher.Dispose();

        Assert.Equal(1, arm.Current.DisposeCount);
    }

    [Fact]
    public void ErrorFromReplacedWatcher_KeepsCurrentArmed()
    {
        using ThemeFolderWatcher watcher = StartArmed();
        FakeFolderEvents replaced = arm.Current;
        probe.State = FolderState.Present("replaced");
        time.Advance(Tick);
        time.Advance(Debounce);
        changes = 0;

        replaced.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        replaced.Raise(new FolderEvent(FolderEventKind.Created, ThemeFile, null, null));
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
        Assert.Equal(2, arm.Attempts);
        Assert.Equal(0, arm.Current.DisposeCount);
    }

    [Fact]
    public void RealFolder_CatalogSave_RaisesChanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "yamv-watcher-" + Guid.NewGuid().ToString("N"));
        string themes = Path.Combine(root, "themes");
        Directory.CreateDirectory(themes);

        // Not disposed: the file system watcher can still deliver an event, and release this, after it is disposed.
        var rawEvents = new SemaphoreSlim(0);
        try
        {
            using var watcher = new ThemeFolderWatcher(
                themes,
                time,
                log,
                path => new SignallingFolderEvents(new FileSystemFolderEvents(path), rawEvents),
                new FileSystemFolderProbe()
            );
            watcher.Changed += (_, _) => Interlocked.Increment(ref changes);
            time.Advance(Debounce);
            int beforeSave = Volatile.Read(ref changes);
            ThemeDefinition theme = ThemeParser.Parse(ValidTheme, "mine", ThemeSource.User, null).Theme!;

            new ThemeCatalog(themes, log).Save(theme);
            while (Volatile.Read(ref changes) == beforeSave)
            {
                Assert.True(rawEvents.Wait(HangGuard, TestContext.Current.CancellationToken), "the folder raised no event for the save");
                time.Advance(Debounce);
            }

            Assert.True(Volatile.Read(ref changes) > beforeSave);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)HangGuard.TotalMilliseconds;
        var spin = new SpinWait();
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("the condition never held");
            }

            spin.SpinOnce();
        }
    }

    private ThemeFolderWatcher Create()
    {
        var watcher = new ThemeFolderWatcher(Folder, time, log, arm.Arm, probe);
        watcher.Changed += (_, _) =>
        {
            changes++;
            order.Add("changed");
        };
        return watcher;
    }

    /// <summary>A watcher whose first arm has raised its Changed, with the count of changes reset.</summary>
    private ThemeFolderWatcher StartArmed()
    {
        ThemeFolderWatcher watcher = Create();
        time.Advance(Debounce);
        Assert.Equal(1, changes);
        changes = 0;
        return watcher;
    }

    /// <summary>
    /// A clock that runs nothing on its own: the test fires each timer by hand, a timer disposed before it fired included, as a
    /// real timer's already queued callback still runs after the timer is disposed.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }

        internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            /// <summary>Releases nothing: the test holds the callback and runs it when it asks to.</summary>
            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            /// <summary>Runs the callback, as a queued callback of a real timer does.</summary>
            public void Fire() => callback(state);
        }
    }

    /// <summary>A probe that counts its calls and fails, so a check that ran at all shows both as its call and as its log line.</summary>
    private sealed class FailingProbe : IFolderProbe
    {
        public int Calls { get; private set; }

        public FolderState Probe(string folder)
        {
            Calls++;
            throw new IOException($"the check ran for {folder} after the watcher was disposed");
        }
    }
}
