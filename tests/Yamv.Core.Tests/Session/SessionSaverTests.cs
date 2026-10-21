using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class SessionSaverTests : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-saver-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingAppLog _log = new();

    public SessionSaverTests()
    {
        Directory.CreateDirectory(_folder);
        SessionFile = Path.Combine(_folder, "session.json");
        Store = new SessionStore(SessionFile, _log);
    }

    private string SessionFile { get; }

    private SessionStore Store { get; }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Schedule_SavesOnceTheDelayHasPassed_AndNotBefore()
    {
        using var saver = new SessionSaver(Store, _time, _log);

        saver.Schedule(State(width: 300));
        _time.Advance(Delay - TimeSpan.FromMilliseconds(1));
        Assert.False(File.Exists(SessionFile));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(300, Store.Load().SidebarWidth);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Schedule_ThreeTimesWithinTheDelay_SavesOnceWithTheLastState()
    {
        using var saver = new SessionSaver(Store, _time, _log);

        saver.Schedule(State(width: 300));
        _time.Advance(TimeSpan.FromMilliseconds(200));
        saver.Schedule(State(width: 310));
        _time.Advance(TimeSpan.FromMilliseconds(200));
        saver.Schedule(State(width: 320));
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(File.Exists(SessionFile));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(320, Store.Load().SidebarWidth);
        File.Delete(SessionFile);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(SessionFile), "a second save followed the first");
    }

    [Fact]
    public void MarkDirty_Alone_SavesNothingUntilFlush_WhichSavesTheStateGiven()
    {
        using var saver = new SessionSaver(Store, _time, _log);

        saver.MarkDirty();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(SessionFile));

        saver.Flush(State(width: 333));
        Assert.Equal(333, Store.Load().SidebarWidth);
    }

    [Fact]
    public void Flush_AfterAScheduledSave_StillSavesAChangeMarkedBeforeIt()
    {
        using var saver = new SessionSaver(Store, _time, _log);
        saver.Schedule(State(width: 300));
        saver.MarkDirty();
        _time.Advance(Delay);
        File.Delete(SessionFile);

        saver.Flush(State(width: 400));

        Assert.Equal(400, Store.Load().SidebarWidth);
    }

    [Fact]
    public void Flush_WithNothingPendingOrDirty_WritesNothing()
    {
        using var saver = new SessionSaver(Store, _time, _log);

        saver.Flush(State(width: 300));

        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public void Flush_AfterAScheduledSaveRan_WritesNothingMore()
    {
        using var saver = new SessionSaver(Store, _time, _log);
        saver.Schedule(State(width: 300));
        _time.Advance(Delay);
        File.Delete(SessionFile);

        saver.Flush(State(width: 400));

        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public void Flush_WithASavePending_SavesTheStateGivenAtOnceAndLeavesNothingPending()
    {
        using var saver = new SessionSaver(Store, _time, _log);
        saver.Schedule(State(width: 300));

        saver.Flush(State(width: 410));
        Assert.Equal(410, Store.Load().SidebarWidth);
        File.Delete(SessionFile);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public void SaveThatThrows_LogsOneError_AndALaterFlushRetries()
    {
        Directory.CreateDirectory(SessionFile);
        using var saver = new SessionSaver(Store, _time, _log);

        saver.Schedule(State(width: 300));
        _time.Advance(Delay);

        LogEntry error = Assert.Single(_log.Entries);
        Assert.Equal("error", error.Level);
        Assert.Equal($"Session: could not save {SessionFile}.", error.Message);
        Assert.NotNull(error.Exception);

        Directory.Delete(SessionFile);
        saver.Flush(State(width: 350));

        Assert.Equal(350, Store.Load().SidebarWidth);
        Assert.Single(_log.Entries);
    }

    [Fact]
    public void Dispose_WithASavePending_CancelsIt()
    {
        var saver = new SessionSaver(Store, _time, _log);
        saver.Schedule(State(width: 300));

        saver.Dispose();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(SessionFile));
    }

    private static SessionState State(double width) => new() { SidebarWidth = width };
}
