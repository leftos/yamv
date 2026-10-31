using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class LaunchArbiterTests
{
    private static readonly string[] First = ["/notes/a.md"];

    [Fact]
    public void Start_with_files_opens_a_viewer()
    {
        LaunchArbiter arbiter = new();

        OpenViewerWindow viewer = Assert.IsType<OpenViewerWindow>(arbiter.Start(First));

        Assert.Equal(First, viewer.Files);
        Assert.False(arbiter.MainWindowOpen);
        Assert.IsType<OpenMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void Start_without_files_opens_the_main_window()
    {
        LaunchArbiter arbiter = new();

        Assert.IsType<OpenMainWindow>(arbiter.Start([]));

        Assert.True(arbiter.MainWindowOpen);
        Assert.IsType<ActivateMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void Start_never_starts_a_grace_timer()
    {
        LaunchArbiter withFiles = new();
        LaunchArbiter withoutFiles = new();

        Assert.IsNotType<StartGraceTimer>(withFiles.Start(First));
        Assert.IsNotType<StartGraceTimer>(withoutFiles.Start([]));

        Assert.IsType<None>(withFiles.Start(First));
        Assert.IsType<None>(withoutFiles.FrameworkReady());
        Assert.IsType<None>(withoutFiles.GraceElapsed());
    }

    [Fact]
    public void MainWindowOpen_follows_open_and_close()
    {
        LaunchArbiter arbiter = new();
        Assert.False(arbiter.MainWindowOpen);

        arbiter.Start([]);
        Assert.True(arbiter.MainWindowOpen);

        arbiter.MainWindowClosed();
        Assert.False(arbiter.MainWindowOpen);

        Assert.IsType<OpenMainWindow>(arbiter.ActivateRequested());
        Assert.True(arbiter.MainWindowOpen);

        arbiter.MainWindowClosed();
        arbiter.MainWindowOpened();
        Assert.True(arbiter.MainWindowOpen);
    }

    [Fact]
    public void FrameworkReady_FreshArbiter_ReturnsStartGraceTimer()
    {
        LaunchArbiter arbiter = new();

        StartGraceTimer timer = Assert.IsType<StartGraceTimer>(arbiter.FrameworkReady());

        Assert.Equal(LaunchArbiter.GracePeriod, timer.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(300), LaunchArbiter.GracePeriod);
    }

    [Fact]
    public void FrameworkReady_ThenFilesOpenedInGrace_OpensViewerAndNoMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();

        OpenViewerWindow viewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(First));
        Assert.Equal(First, viewer.Files);
        Assert.IsType<None>(arbiter.GraceElapsed());
    }

    [Fact]
    public void FrameworkReady_Twice_ReturnsNoneAndKeepsOneGrace()
    {
        LaunchArbiter arbiter = new();
        Assert.IsType<StartGraceTimer>(arbiter.FrameworkReady());

        Assert.IsType<None>(arbiter.FrameworkReady());
        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
        Assert.IsType<ActivateMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    [Fact]
    public void GraceElapsed_AfterFrameworkReady_OpensMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();

        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
    }

    [Fact]
    public void GraceElapsed_AfterEmptyFilesOpenedInGrace_OpensMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        Assert.IsType<None>(arbiter.FilesOpened([]));

        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
    }

    [Fact]
    public void GraceElapsed_FilesAndActivateInGrace_OpensMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        arbiter.FilesOpened(First);

        Assert.IsType<HoldUntilGraceEnds>(arbiter.ActivateRequested());
        Assert.IsType<None>(arbiter.HeldActivatesBegun(1));
        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
    }

    [Fact]
    public void GraceElapsed_FilesAndAbandonedActivateInGrace_OpensNoMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        arbiter.FilesOpened(First);

        Assert.IsType<HoldUntilGraceEnds>(arbiter.ActivateRequested());
        Assert.IsType<None>(arbiter.HeldActivatesBegun(0));
        Assert.IsType<None>(arbiter.GraceElapsed());
        Assert.False(arbiter.MainWindowOpen);
    }

    [Fact]
    public void HeldActivatesBegun_OutsideTheGrace_AsksForNothing()
    {
        LaunchArbiter arbiter = new();
        arbiter.FilesOpened(First);
        Assert.IsType<None>(arbiter.HeldActivatesBegun(1));

        arbiter.FrameworkReady();
        Assert.IsType<None>(arbiter.GraceElapsed());
        Assert.Throws<ArgumentOutOfRangeException>(() => arbiter.HeldActivatesBegun(-1));
    }

    [Fact]
    public void GraceElapsed_FilesAndReopenInGrace_OpensMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        arbiter.FilesOpened(First);

        Assert.IsType<None>(arbiter.Reopen(anyWindowOpen: false));
        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
    }

    [Fact]
    public void GraceElapsed_BeforeFrameworkReady_ReturnsNone()
    {
        LaunchArbiter arbiter = new();

        Assert.IsType<None>(arbiter.GraceElapsed());
    }

    [Fact]
    public void FilesOpened_AfterGrace_OpensViewerWindowWithEveryFile()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        string[] files = ["/notes/a.md", "/notes/b.md"];

        OpenViewerWindow viewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(files));

        Assert.Equal(files, viewer.Files);
    }

    [Fact]
    public void FilesOpened_AfterMainWindowClosed_OpensViewerWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();

        OpenViewerWindow viewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(First));

        Assert.Equal(First, viewer.Files);
    }

    [Fact]
    public void FilesOpened_TwiceInGrace_OpensTwoViewerWindowsAndNoMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        string[] second = ["/notes/b.md"];

        OpenViewerWindow firstViewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(First));
        OpenViewerWindow secondViewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(second));

        Assert.Equal(First, firstViewer.Files);
        Assert.Equal(second, secondViewer.Files);
        Assert.IsType<None>(arbiter.GraceElapsed());
    }

    [Fact]
    public void FilesOpened_EmptyList_ReturnsNone()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();

        Assert.IsType<None>(arbiter.FilesOpened([]));
    }

    [Fact]
    public void FilesOpened_BeforeFrameworkReady_OpensViewerAndNoMainWindow()
    {
        LaunchArbiter arbiter = new();

        OpenViewerWindow viewer = Assert.IsType<OpenViewerWindow>(arbiter.FilesOpened(First));
        Assert.Equal(First, viewer.Files);

        Assert.IsType<StartGraceTimer>(arbiter.FrameworkReady());
        Assert.IsType<None>(arbiter.GraceElapsed());
    }

    [Fact]
    public void Reopen_NoWindowOpen_OpensMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();

        Assert.IsType<OpenMainWindow>(arbiter.Reopen(anyWindowOpen: false));
    }

    [Fact]
    public void Reopen_NoWindowOpenWhileMainBelievedOpen_OpensMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();

        Assert.IsType<OpenMainWindow>(arbiter.Reopen(anyWindowOpen: false));
    }

    [Fact]
    public void Reopen_MainWindowOpen_ActivatesMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();

        Assert.IsType<ActivateMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    [Fact]
    public void Reopen_OnlyViewerOpen_OpensMainWindow()
    {
        LaunchArbiter arbiter = RunningWithViewerOnly();

        Assert.IsType<OpenMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    [Fact]
    public void Reopen_ClosedThenOpened_OpensThenActivates()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();

        Assert.IsType<OpenMainWindow>(arbiter.Reopen(anyWindowOpen: true));
        Assert.IsType<ActivateMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    [Fact]
    public void Reopen_ClosedThenMainWindowOpened_ActivatesMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();
        Assert.IsType<None>(arbiter.MainWindowOpened());

        Assert.IsType<ActivateMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    [Fact]
    public void Reopen_BeforeFrameworkReady_ReturnsNone()
    {
        LaunchArbiter arbiter = new();

        Assert.IsType<None>(arbiter.Reopen(anyWindowOpen: false));
    }

    [Fact]
    public void ActivateRequested_MainWindowOpen_ActivatesMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();

        Assert.IsType<ActivateMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void ActivateRequested_AfterMainWindowClosed_OpensMainWindow()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();

        Assert.IsType<OpenMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void ActivateRequested_ClosedThenOpened_OpensThenActivates()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();
        arbiter.MainWindowClosed();

        Assert.IsType<OpenMainWindow>(arbiter.ActivateRequested());
        Assert.IsType<ActivateMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void ActivateRequested_BeforeGraceElapsed_HoldsThenOpensMainWindowOnce()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();

        Assert.IsType<HoldUntilGraceEnds>(arbiter.ActivateRequested());
        Assert.IsType<HoldUntilGraceEnds>(arbiter.ActivateRequested());
        Assert.IsType<OpenMainWindow>(arbiter.GraceElapsed());
        Assert.IsType<None>(arbiter.GraceElapsed());
        Assert.IsType<ActivateMainWindow>(arbiter.ActivateRequested());
    }

    [Fact]
    public void InGrace_RunsFromFrameworkReadyUntilGraceElapsed()
    {
        LaunchArbiter arbiter = new();
        Assert.False(arbiter.InGrace);

        arbiter.FrameworkReady();
        Assert.True(arbiter.InGrace);

        arbiter.GraceElapsed();
        Assert.False(arbiter.InGrace);

        LaunchArbiter started = new();
        started.Start([]);
        Assert.False(started.InGrace);
    }

    [Fact]
    public void ActivateRequested_BeforeFrameworkReady_ReturnsNone()
    {
        LaunchArbiter arbiter = new();

        Assert.IsType<None>(arbiter.ActivateRequested());
    }

    [Fact]
    public void MainWindowOpened_Twice_ReturnsNoneAndReopenActivates()
    {
        LaunchArbiter arbiter = RunningWithMainWindow();

        Assert.IsType<None>(arbiter.MainWindowOpened());
        Assert.IsType<None>(arbiter.MainWindowOpened());
        Assert.IsType<ActivateMainWindow>(arbiter.Reopen(anyWindowOpen: true));
    }

    private static LaunchArbiter RunningWithMainWindow()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        arbiter.GraceElapsed();
        return arbiter;
    }

    private static LaunchArbiter RunningWithViewerOnly()
    {
        LaunchArbiter arbiter = new();
        arbiter.FrameworkReady();
        arbiter.FilesOpened(First);
        arbiter.GraceElapsed();
        return arbiter;
    }
}
