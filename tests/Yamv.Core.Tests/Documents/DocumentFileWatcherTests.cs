using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Documents;
using Yamv.Core.Tests.Session;
using Yamv.Core.Tests.Theming;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Documents;

public sealed class DocumentFileWatcherTests : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "yamv-docs");
    private static readonly string Docs = Path.Combine(Root, "docs");
    private static readonly string Notes = Path.Combine(Root, "notes");
    private static readonly string Doc = Path.Combine(Docs, "readme.md");
    private static readonly string Second = Path.Combine(Docs, "second.md");
    private static readonly string Other = Path.Combine(Notes, "other.md");
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider time = new();
    private readonly RecordingAppLog log = new();
    private readonly List<string> order = [];
    private readonly FakeFolderArm arm;
    private readonly TestProbe probe = new();
    private readonly TestWork work = new();
    private readonly Dictionary<string, FileStamp?> stamps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> stampReads = new(StringComparer.Ordinal);
    private readonly List<string> reported = [];
    private Action? onStamp;
    private int changes;

    public DocumentFileWatcherTests()
    {
        arm = new FakeFolderArm(order);
    }

    public void Dispose() => probe.Dispose();

    [Fact]
    public void AddingAFolderChecksOnlyThatFolder()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Assert.Equal(1, probe.Calls(Docs));

        watcher.SetPaths([Doc, Other]);

        Assert.Equal(1, probe.Calls(Docs));
        Assert.Equal(1, probe.Calls(Notes));

        time.Advance(Tick);
        Assert.Equal(2, probe.Calls(Docs));
        Assert.Equal(2, probe.Calls(Notes));
    }

    [Fact]
    public async Task RequestsDuringARunningPassRunOneMorePass()
    {
        using DocumentFileWatcher watcher = Create();
        work.Hold = true;
        probe.Block(Docs);
        watcher.SetPaths([Doc]);
        Task pass = StartPass(work.Take());
        try
        {
            probe.WaitEntered();
            time.Advance(Tick);
            Assert.Equal(0, work.Count);
            time.Advance(Tick);
            Assert.Equal(0, work.Count);
        }
        finally
        {
            probe.Release();
        }

        await pass.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.Calls(Docs));
        Assert.Equal(1, arm.Attempts);
        Assert.Equal(0, work.Count);

        time.Advance(Tick);
        Assert.Equal(1, work.Count);
    }

    [Fact]
    public async Task AHungFolderDoesNotDelayAnotherFolder()
    {
        using DocumentFileWatcher watcher = Create();
        work.Hold = true;
        probe.Block(Docs);
        watcher.SetPaths([Doc, Other]);
        Assert.Equal(2, work.Count);

        Task first = StartPass(work.Take());
        Task second = StartPass(work.Take());
        try
        {
            probe.WaitEntered();
            await Task.WhenAny(first, second).WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            Assert.Equal(1, arm.Attempts);
            Assert.True(arm.ByFolder.ContainsKey(Notes));

            time.Advance(Tick);
            Assert.Equal(1, work.Count);
        }
        finally
        {
            probe.Release();
        }

        await Task.WhenAll(first, second).WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        work.RunAll();
        Assert.Equal(2, arm.Attempts);
    }

    [Fact]
    public async Task AHungFolderHoldsOneThreadAcrossTicksAndReopen()
    {
        using DocumentFileWatcher watcher = Create();
        work.Hold = true;
        probe.Block(Docs);
        watcher.SetPaths([Doc]);
        Task pass = StartPass(work.Take());
        try
        {
            probe.WaitEntered();
            time.Advance(Tick);
            Assert.Equal(0, work.Count);
            watcher.SetPaths([]);
            Assert.Equal(0, work.Count);
            watcher.SetPaths([Doc]);
            Assert.Equal(0, work.Count);
            time.Advance(Tick);
            Assert.Equal(0, work.Count);
        }
        finally
        {
            probe.Release();
        }

        await pass.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(0, work.Count);
        Assert.Equal(2, probe.Calls(Docs));
        Assert.Equal(1, arm.Attempts);
        Assert.True(arm.Current.Started);
    }

    [Fact]
    public void SetPathsOnlyPostsWork()
    {
        using DocumentFileWatcher watcher = Create();
        work.Hold = true;

        watcher.SetPaths([Doc]);

        Assert.Equal(1, work.Count);
        Assert.Equal(0, probe.Calls(Docs));
        Assert.Equal(0, arm.Attempts);

        work.RunAll();
        FakeFolderEvents events = arm.ByFolder[Docs];

        watcher.SetPaths([]);

        Assert.Equal(1, work.Count);
        Assert.Equal(0, events.DisposeCount);

        work.RunAll();
        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void APassNeverRunsUnderTheLock()
    {
        using DocumentFileWatcher watcher = Create();
        bool? free = null;
        void CheckFromAnotherThread() =>
            probe.OnProbe = _ =>
            {
                probe.OnProbe = null;
                var other = new Thread(() => watcher.SetPaths([Doc])) { IsBackground = true };
                other.Start();
                free = other.Join(HangGuard);
            };

        CheckFromAnotherThread();
        watcher.SetPaths([Doc]);
        Assert.True(free, "SetPaths ran the pass under its lock");

        free = null;
        CheckFromAnotherThread();
        time.Advance(Tick);
        Assert.True(free, "the tick ran the pass under its lock");
    }

    [Fact]
    public void AnErrorRaisedInsideStartAfterAHealthyWatchPolls()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        time.Advance(Tick);

        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Debounce);
        Assert.Equal(1, changes);

        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Single(log.Entries, entry => entry.Message == $"The folder {Docs} cannot report changes; checking its documents every 2 s.");
    }

    [Fact]
    public void AnEarlyPassDoesNotMakeANewWatchHealthy()
    {
        using DocumentFileWatcher watcher = Create();
        time.Advance(TimeSpan.FromSeconds(1));
        watcher.SetPaths([Doc]);
        time.Advance(TimeSpan.FromMilliseconds(500));
        watcher.SetPaths([Doc, Other]);
        time.Advance(TimeSpan.FromMilliseconds(500));

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Error, null, null, new IOException("no change notifications")));
        time.Advance(Debounce);

        Assert.Equal(0, changes);
        Assert.Single(log.Entries, entry => entry.Message == $"The folder {Docs} cannot report changes; checking its documents every 2 s.");
    }

    [Fact]
    public void AnErrorEventReadsNoStampsOnItsThread()
    {
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        work.Hold = true;

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Error, null, null, new IOException("no change notifications")));

        Assert.Equal(0, StampReads(Doc));
        Assert.Equal(1, work.Count);

        work.RunAll();
        Assert.Equal(1, StampReads(Doc));

        work.Hold = false;
        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void ADocumentAddedToAPolledFolderIsBaselinedAtOnce()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        stamps[Second] = new FileStamp(20, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        work.Hold = true;

        watcher.SetPaths([Doc, Second]);

        Assert.Equal(1, work.Count);
        work.RunAll();
        Assert.Equal(1, StampReads(Second));

        work.Hold = false;
        stamps[Second] = new FileStamp(21, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Second], reported);
    }

    [Fact]
    public void AnArmFailureBaselinesTheFolderAtOnce()
    {
        arm.FailNext(new IOException("cannot watch"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void ASourceRetiredDuringAFailedPassIsDisposed()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        FakeFolderEvents events = arm.ByFolder[Docs];
        probe.ThrowNext = new InvalidOperationException("the probe failed");
        probe.OnProbe = _ =>
        {
            probe.OnProbe = null;
            watcher.SetPaths([]);
        };
        work.Hold = true;

        time.Advance(Tick);
        work.RunAll();

        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public async Task DisposeDuringAHungArmReturnsAndTheLateSourceIsDisposed()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int outwaited = 0;
        DocumentFileWatcher watcher = Create();
        work.Hold = true;
        arm.OnAttempt = _ =>
        {
            entered.Set();
            if (!release.Wait(HangGuard))
            {
                Volatile.Write(ref outwaited, 1);
            }
        };
        watcher.SetPaths([Doc]);
        Task pass = StartPass(work.Take());
        try
        {
            Assert.True(entered.Wait(HangGuard, TestContext.Current.CancellationToken), "the pass never armed the folder");
            watcher.Dispose();
            Assert.Equal(0, Volatile.Read(ref outwaited));
        }
        finally
        {
            release.Set();
        }

        await pass.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        FakeFolderEvents events = Assert.Single(arm.Armed);
        Assert.Equal(1, events.DisposeCount);
        Assert.False(events.Started);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void InPlaceWriteReportsTheDocumentAfterTheDebounce()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, changes);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void RenameSaveReportsTheDocumentByItsNewName()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        FakeFolderEvents events = arm.ByFolder[Docs];
        events.Raise(new FolderEvent(FolderEventKind.Created, "write.tmp", null, null));
        events.Raise(new FolderEvent(FolderEventKind.Changed, "write.tmp", null, null));
        events.Raise(new FolderEvent(FolderEventKind.Renamed, "readme.md", "write.tmp", null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void RenameAwayReportsTheDocumentByItsOldName()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Renamed, "readme.md~", "readme.md", null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void EventForAFileThatIsNotOpenReportsNothing()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        FakeFolderEvents events = arm.ByFolder[Docs];
        events.Raise(new FolderEvent(FolderEventKind.Changed, "unknown.md", null, null));
        events.Raise(new FolderEvent(FolderEventKind.Renamed, "unknown.md", "unknown~.md", null));
        events.Raise(new FolderEvent(FolderEventKind.Deleted, "notes.md", null, null));
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void BurstAcrossTwoDocumentsIsOneReport()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second]);
        Settle();

        FakeFolderEvents events = arm.ByFolder[Docs];
        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(TimeSpan.FromMilliseconds(100));
        events.Raise(new FolderEvent(FolderEventKind.Changed, "second.md", null, null));

        // A fixed window from the first event would have fired here; the second event pushed the deadline to 250 ms.
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, changes);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, changes);
        Assert.Equal([Doc, Second], [.. reported.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void ContinuousWriterIsReportedAtLeastOncePerSecond()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        FakeFolderEvents events = arm.ByFolder[Docs];
        for (int i = 0; i < 30; i++)
        {
            events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
            time.Advance(TimeSpan.FromMilliseconds(100));

            if (i == 9)
            {
                Assert.Equal(1, changes);
            }

            if (i == 19)
            {
                Assert.Equal(2, changes);
            }
        }

        Assert.Equal(3, changes);
        Assert.Equal(3, reported.Count);
        Assert.All(reported, path => Assert.Equal(Doc, path));
    }

    [Fact]
    public void TwoDocumentsInOneFolderArmOneSource()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second]);
        Settle();

        Assert.Equal(1, arm.Attempts);

        watcher.SetPaths([Doc, Second, Other]);
        Settle();
        Assert.Equal(2, arm.Attempts);

        if (AppPaths.PathComparer == StringComparer.Ordinal)
        {
            return;
        }

        watcher.SetPaths([Doc, Doc.ToUpperInvariant()]);
        Settle();
        Assert.Equal(2, arm.Attempts);

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Changed, "README.MD", null, null));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void RemovingTheLastDocumentOfAFolderDisposesItsSource()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        FakeFolderEvents events = arm.ByFolder[Docs];

        watcher.SetPaths([Other]);

        Assert.Equal(1, events.DisposeCount);
        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ErrorEventReportsEveryOpenDocumentInThatFolderAndRearms()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second, Other]);
        Settle();
        time.Advance(Tick);
        FakeFolderEvents events = arm.ByFolder[Docs];

        events.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc, Second], [.. reported.Order(StringComparer.Ordinal)]);
        Assert.Contains(log.Entries, entry => entry.Level == "warning" && entry.Exception is InternalBufferOverflowException);

        time.Advance(Tick);

        Assert.Equal(3, arm.Attempts);
        Assert.Equal(1, events.DisposeCount);
        Assert.Contains(log.Entries, entry => entry.Message == $"Watching the folder {Docs} again.");
    }

    [Fact]
    public void ReplacedFolderIsRearmedAndReportsItsDocuments()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        FakeFolderEvents first = arm.ByFolder[Docs];

        probe.State = FolderState.Present("replaced");
        time.Advance(Tick);

        Assert.Equal(2, arm.Attempts);
        Assert.Equal(1, first.DisposeCount);

        time.Advance(Debounce);
        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void MissingFolderIsArmedWhenItAppears()
    {
        probe.State = FolderState.Missing;
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        Assert.Equal(0, arm.Attempts);

        time.Advance(Tick);
        Assert.Equal(0, arm.Attempts);
        Assert.Equal(0, changes);

        probe.State = FolderState.Present("arrived");
        time.Advance(Tick);
        Assert.Equal(1, arm.Attempts);

        time.Advance(Debounce);
        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void NoReportAfterDispose()
    {
        DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        FakeFolderEvents events = arm.ByFolder[Docs];

        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        watcher.Dispose();

        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        events.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
        Assert.Equal(1, events.DisposeCount);
        Assert.DoesNotContain(log.Entries, entry => entry.Level != "info");
    }

    [Fact]
    public async Task DisposeWaitsForARunningHandler()
    {
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var steps = new ConcurrentQueue<string>();
        var watcher = new DocumentFileWatcher(time, log, Seams());
        watcher.Changed += (_, _) =>
        {
            inside.Set();
            release.Wait();
            steps.Enqueue("changed returned");
        };

        try
        {
            watcher.SetPaths([Doc]);
            Settle();
            arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
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
    public void ThrowingHandlerIsLoggedAndTheWatcherKeepsWorking()
    {
        int calls = 0;
        using var watcher = new DocumentFileWatcher(time, log, Seams());
        watcher.Changed += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("the subscriber failed");
        };
        watcher.SetPaths([Doc]);
        Settle();
        FakeFolderEvents events = arm.ByFolder[Docs];

        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce);
        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce);

        Assert.Equal(2, calls);
        LogEntry[] errors = [.. log.Entries.Where(entry => entry.Level == "error")];
        Assert.Equal(2, errors.Length);
        Assert.All(errors, entry => Assert.IsType<InvalidOperationException>(entry.Exception));
    }

    [Fact]
    public void AnArmForAFolderRemovedWhileArmingDisposesItsSource()
    {
        DocumentFileWatcher? watcher = null;
        watcher = Create();
        arm.OnAttempt = _ => watcher!.SetPaths([]);

        watcher.SetPaths([Doc]);
        Settle();
        watcher.Dispose();

        FakeFolderEvents events = Assert.Single(arm.Armed);
        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void AnErrorOnTheFirstTickAfterAnArmPolls()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Error, null, null, new IOException("no change notifications")));
        time.Advance(Debounce);
        Assert.Equal(0, changes);

        for (int i = 0; i < 3; i++)
        {
            time.Advance(Tick);
            arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Error, null, null, new IOException("no change notifications")));
            time.Advance(Debounce);
        }

        Assert.Equal(0, changes);
        Assert.Single(log.Entries, entry => entry.Message.Contains("cannot report changes", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.StartsWith("The document watcher of", StringComparison.Ordinal));
    }

    [Fact]
    public void AStartThatAlwaysThrowsPolls()
    {
        arm.FailNextStart(new IOException("cannot start"));
        arm.FailNextStart(new IOException("cannot start"));
        arm.FailNextStart(new IOException("cannot start"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Tick);
        time.Advance(Tick);

        Assert.Equal(3, arm.Attempts);
        Assert.Single(log.Entries, entry => entry.Message.Contains("cannot report changes", StringComparison.Ordinal));

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnArmThatAlwaysThrowsPolls()
    {
        arm.FailNext(new IOException("cannot watch"));
        arm.FailNext(new IOException("cannot watch"));
        arm.FailNext(new IOException("cannot watch"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Tick);
        time.Advance(Tick);

        Assert.Equal(3, arm.Attempts);
        Assert.Single(log.Entries, entry => entry.Message.Contains("cannot report changes", StringComparison.Ordinal));

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void AChangeBeforeTheFirstPollIsReported()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void ADocumentClosedDuringAStampReadIsNotReported()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        stamps[Second] = new FileStamp(20, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second]);
        Settle();
        time.Advance(Debounce);
        reported.Clear();
        changes = 0;

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        stamps[Second] = new FileStamp(21, Epoch.AddMinutes(2));
        onStamp = () => watcher.SetPaths([Second]);
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Second], reported);
    }

    [Fact]
    public async Task SetPathsDoesNotWaitForAnArm()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int outwaited = 0;

        // The real clock and the real thread pool, which is what the app hands the watcher.
        using var watcher = new DocumentFileWatcher(TimeProvider.System, log, Seams(PostToPool));
        arm.OnAttempt = _ =>
        {
            entered.Set();
            if (!release.Wait(HangGuard))
            {
                Volatile.Write(ref outwaited, 1);
            }
        };

        try
        {
            await Task.Run(() => watcher.SetPaths([Doc]), TestContext.Current.CancellationToken)
                .WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref outwaited));
            Assert.True(entered.Wait(HangGuard, TestContext.Current.CancellationToken), "the pass never armed the folder");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RepeatedStartErrorsPollWithoutReporting()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Debounce);
        Assert.Equal(0, changes);

        for (int i = 0; i < 2; i++)
        {
            time.Advance(Tick);
            time.Advance(Debounce);
        }

        Assert.Equal(0, changes);
        Assert.Single(log.Entries, entry => entry.Message == $"The folder {Docs} cannot report changes; checking its documents every 2 s.");
    }

    [Fact]
    public void PollingReportsAChangedStampOnly()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        stamps[Second] = new FileStamp(20, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second]);
        Settle();
        time.Advance(Debounce);
        reported.Clear();
        changes = 0;

        time.Advance(Tick);
        time.Advance(Debounce);
        Assert.Equal(0, changes);

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void PollingStopsWhenTheWatchRecovers()
    {
        arm.ErrorOnNextStart(new IOException("no change notifications"));
        stamps[Doc] = new FileStamp(10, Epoch);
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Tick);
        time.Advance(Tick);
        Assert.Contains(log.Entries, entry => entry.Level == "info" && entry.Message == $"Watching the folder {Docs} again.");
        time.Advance(Debounce);
        Assert.Equal(1, changes);

        stamps[Doc] = new FileStamp(11, Epoch.AddMinutes(1));
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void RecoveryAfterAnErrorReportsEveryOpenDocument()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc, Second]);
        Settle();
        time.Advance(Tick);
        FakeFolderEvents events = arm.ByFolder[Docs];

        events.Raise(new FolderEvent(FolderEventKind.Error, null, null, new InternalBufferOverflowException()));
        time.Advance(Debounce);
        Assert.Equal(1, changes);

        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(2, changes);
        Assert.Equal([Doc, Doc, Second, Second], [.. reported.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void RecoveryAfterAnArmFailureReportsEveryOpenDocument()
    {
        arm.FailNext(new IOException("busy"));
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        Assert.Equal(0, changes);

        time.Advance(Tick);
        time.Advance(Tick);
        time.Advance(Debounce);

        Assert.Equal(1, changes);
        Assert.Equal([Doc], reported);
        Assert.Contains(log.Entries, entry => entry.Message == $"Watching the folder {Docs} again.");
    }

    [Fact]
    public void StartThrowingAfterDisposeIsSwallowed()
    {
        DocumentFileWatcher? watcher = null;
        var events = new DisposingFolderEvents(() => watcher!.Dispose());
        watcher = new DocumentFileWatcher(time, log, new DocumentFileWatcher.Seams(_ => events, probe, StampOf, work.Post));

        watcher.SetPaths([Doc]);
        Settle();

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ClosingADocumentBeforeTheDebounceDropsItFromTheReport()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));

        watcher.SetPaths([]);
        time.Advance(Tick + Debounce);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ANonQualifiedPathThrows()
    {
        using DocumentFileWatcher watcher = Create();

        Assert.Throws<ArgumentException>(() => watcher.SetPaths([Path.Combine("relative", "readme.md")]));
        Assert.Equal(0, arm.Attempts);
    }

    [Fact]
    public void ARootPathThrows()
    {
        using DocumentFileWatcher watcher = Create();

        Assert.Throws<ArgumentException>(() => watcher.SetPaths([Path.GetPathRoot(Doc)!]));
        Assert.Equal(0, arm.Attempts);
    }

    [Fact]
    public void APathIsNormalisedBeforeItIsWatched()
    {
        using DocumentFileWatcher watcher = Create();

        watcher.SetPaths([Path.Combine(Docs, ".", "readme.md")]);
        Settle();
        arm.ByFolder[Docs].Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce);

        Assert.Equal([Doc], reported);
    }

    [Fact]
    public void ArmFailuresWarnOnceAndTheRecoveryLogsOnce()
    {
        arm.FailNext(new IOException("first"));
        arm.FailNext(new IOException("second"));
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Tick);
        Assert.Single(log.Entries, entry => entry.Level == "warning");

        time.Advance(Tick);
        time.Advance(Tick);

        Assert.Equal(3, arm.Attempts);
        Assert.Single(log.Entries, entry => entry.Level == "warning");
        Assert.Equal($"Watching the folder {Docs} again.", log.Entries.Single(entry => entry.Level == "info").Message);
    }

    [Fact]
    public void AStartFailureIsLoggedAndRetried()
    {
        arm.FailNextStart(new IOException("cannot start"));
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        Assert.Equal(1, arm.Current.DisposeCount);
        Assert.Equal($"The folder {Docs} cannot report changes; checking its documents every 2 s.", log.Entries[0].Message);

        time.Advance(Tick);

        Assert.Equal(2, arm.Attempts);
        Assert.True(arm.Current.Started);
    }

    [Fact]
    public void AThrowingProbeIsLoggedAndTheNextTickWatches()
    {
        probe.ThrowNext = new InvalidOperationException("the probe failed");
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();

        Assert.Equal(0, arm.Attempts);
        Assert.Single(log.Entries, entry => entry.Level == "error");

        time.Advance(Tick);

        Assert.Equal(1, arm.Attempts);
        Assert.Contains(log.Entries, entry => entry.Message == $"Checking the folder {Docs} works again.");
    }

    [Fact]
    public void TheMissingFolderLogIsNotRepeated()
    {
        probe.State = FolderState.Missing;
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        time.Advance(Tick);

        Assert.Single(log.Entries, entry => entry.Message == $"The folder {Docs} does not exist; it is watched once it does.");
    }

    [Fact]
    public void APresentFolderGoingMissingDisposesItsSource()
    {
        using DocumentFileWatcher watcher = Create();
        watcher.SetPaths([Doc]);
        Settle();
        FakeFolderEvents events = arm.ByFolder[Docs];

        probe.State = FolderState.Missing;
        time.Advance(Tick);

        Assert.Equal(1, events.DisposeCount);
        events.Raise(new FolderEvent(FolderEventKind.Changed, "readme.md", null, null));
        time.Advance(Debounce);
        Assert.Equal(0, changes);
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

    private DocumentFileWatcher Create()
    {
        var watcher = new DocumentFileWatcher(time, log, Seams());
        watcher.Changed += (_, e) =>
        {
            changes++;
            reported.AddRange(e.Paths);
        };
        return watcher;
    }

    private DocumentFileWatcher.Seams Seams(Action<Action>? post = null) => new(arm.Arm, probe, StampOf, post ?? work.Post);

    private static void PostToPool(Action pass) => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), pass, preferLocal: false);

    /// <summary>Runs a held pass on a thread of its own, as the thread pool would.</summary>
    private static Task StartPass(Action pass) =>
        Task.Factory.StartNew(pass, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Advances the clock 1 ms. The passes <see cref="DocumentFileWatcher.SetPaths"/> asks for have already run inline unless
    /// <see cref="TestWork.Hold"/> is set, so this only moves the clock; it keeps the older tests' timelines as they were written.
    /// </summary>
    private void Settle() => time.Advance(TimeSpan.FromMilliseconds(1));

    private int StampReads(string path) => stampReads.TryGetValue(path, out int count) ? count : 0;

    private FileStamp? StampOf(string path)
    {
        stampReads.AddOrUpdate(path, 1, static (_, count) => count + 1);
        onStamp?.Invoke();
        onStamp = null;
        return stamps.TryGetValue(path, out FileStamp? stamp) ? stamp : null;
    }

    /// <summary>A source whose <c>Start</c> runs an action and then throws as a source disposed by it does.</summary>
    private sealed class DisposingFolderEvents(Action onStart) : IFolderEvents
    {
        public event Action<FolderEvent>? Raised
        {
            add { }
            remove { }
        }

        public void Start()
        {
            onStart();
            throw new ObjectDisposedException(nameof(DisposingFolderEvents));
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The watcher's way onto other threads: runs each pass inline once the watcher has posted it, or holds it for the test to
    /// run while <see cref="Hold"/> is set.
    /// </summary>
    private sealed class TestWork
    {
        private readonly ConcurrentQueue<Action> held = new();

        public bool Hold { get; set; }

        public int Count => held.Count;

        public void Post(Action pass)
        {
            if (Hold)
            {
                held.Enqueue(pass);
                return;
            }

            pass();
        }

        public Action Take() => held.TryDequeue(out Action? pass) ? pass : throw new InvalidOperationException("no pass was posted");

        public void RunAll()
        {
            while (held.TryDequeue(out Action? pass))
            {
                pass();
            }
        }
    }

    /// <summary>A probe that counts its calls per folder and can hold one folder's probes until the test releases them.</summary>
    private sealed class TestProbe : IFolderProbe, IDisposable
    {
        private readonly ConcurrentDictionary<string, int> calls = new(StringComparer.Ordinal);
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim released = new(false);
        private volatile string? blocked;

        public FolderState State { get; set; } = FolderState.Present("first");

        public Exception? ThrowNext { get; set; }

        /// <summary>Runs inside each probe, on the probing thread, with the folder probed.</summary>
        public Action<string>? OnProbe { get; set; }

        public int Calls(string folder) => calls.TryGetValue(folder, out int count) ? count : 0;

        /// <summary>Holds every probe of <paramref name="folder"/> until <see cref="Release"/>.</summary>
        public void Block(string folder) => blocked = folder;

        public void WaitEntered()
        {
            if (!entered.Wait(HangGuard))
            {
                throw new TimeoutException("the held folder was never probed");
            }
        }

        public void Release() => released.Set();

        public FolderState Probe(string folder)
        {
            calls.AddOrUpdate(folder, 1, static (_, count) => count + 1);
            OnProbe?.Invoke(folder);
            if (ThrowNext is { } failure)
            {
                ThrowNext = null;
                throw failure;
            }

            if (string.Equals(folder, blocked, StringComparison.Ordinal))
            {
                entered.Set();
                if (!released.Wait(HangGuard))
                {
                    throw new TimeoutException($"the probe of {folder} was never released");
                }
            }

            return State;
        }

        public void Dispose()
        {
            entered.Dispose();
            released.Dispose();
        }
    }
}
