namespace Yamv.Core.Launch;

/// <summary>The launch state machine's answer to one input: what the app should do next.</summary>
public abstract record LaunchAction;

/// <summary>Show the main window.</summary>
public sealed record OpenMainWindow : LaunchAction;

/// <summary>Open a viewer window showing these files.</summary>
/// <param name="Files">The full paths that were opened, in the order they arrived.</param>
public sealed record OpenViewerWindow(IReadOnlyList<string> Files) : LaunchAction;

/// <summary>Bring the already open main window to the front.</summary>
public sealed record ActivateMainWindow : LaunchAction;

/// <summary>Start the grace timer; its end is reported back through <see cref="LaunchArbiter.GraceElapsed"/>.</summary>
/// <param name="Delay">How long to wait before reporting.</param>
public sealed record StartGraceTimer(TimeSpan Delay) : LaunchAction;

/// <summary>Nothing to do for this input.</summary>
public sealed record None : LaunchAction;

/// <summary>
/// The launch state machine: turns framework, file-open and Dock/activate events into the windows to open. <see cref="Start"/>
/// opens the start window at once; for the macOS grace period it keeps no timer of its own: <see cref="FrameworkReady"/> asks
/// the caller to start one, and the caller reports its end back through <see cref="GraceElapsed"/>. Inputs that arrive in an
/// order the machine does not cover return <see cref="None"/> and never throw.
/// </summary>
/// <remarks>
/// The arbiter is driven from one thread, the UI thread. The main window counts as open from the moment
/// <see cref="OpenMainWindow"/> is returned, so no input asks for a second one while one is open.
/// </remarks>
public sealed class LaunchArbiter
{
    /// <summary>How long main-window creation waits after framework initialization for launch-time file events.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(300);

    private bool _frameworkReady;
    private bool _inGrace;
    private bool _filesSeen;
    private bool _mainRequested;
    private bool _mainOpen;

    /// <summary>
    /// Whether the arbiter counts the main window as open: from <see cref="OpenMainWindow"/> or <see cref="MainWindowOpened"/>
    /// until <see cref="MainWindowClosed"/>.
    /// </summary>
    public bool MainWindowOpen => _mainOpen;

    /// <summary>
    /// Reports that the UI framework initialized for a start with no grace period: a viewer window over <paramref name="files"/>
    /// when the launch named any, else the main window. A second call, or one after <see cref="FrameworkReady"/>, returns <see cref="None"/>.
    /// </summary>
    /// <param name="files">The full paths the launch named, possibly none.</param>
    public LaunchAction Start(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (_frameworkReady)
        {
            return new None();
        }

        _frameworkReady = true;
        if (files.Count > 0)
        {
            _filesSeen = true;
            return new OpenViewerWindow(files);
        }

        return OpenMain();
    }

    /// <summary>Reports that the UI framework initialized; the caller starts the grace timer.</summary>
    public LaunchAction FrameworkReady()
    {
        if (_frameworkReady)
        {
            return new None();
        }

        _frameworkReady = true;
        _inGrace = true;
        return new StartGraceTimer(GracePeriod);
    }

    /// <summary>Reports that the grace period ended, opening the main window unless a file event replaced it.</summary>
    public LaunchAction GraceElapsed()
    {
        if (!_inGrace)
        {
            return new None();
        }

        _inGrace = false;
        if (_filesSeen && !_mainRequested)
        {
            return new None();
        }

        return _mainOpen ? new None() : OpenMain();
    }

    /// <summary>Reports one Finder open, which opens a viewer window with that event's files.</summary>
    /// <param name="paths">The full paths of the files the event carried.</param>
    public LaunchAction FilesOpened(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return new None();
        }

        _filesSeen = true;
        return new OpenViewerWindow(paths);
    }

    /// <summary>Reports a Dock click, which reopens the main window when it is closed.</summary>
    /// <param name="anyWindowOpen">Whether any window exists, minimized included; false corrects a stale belief that the main window is open.</param>
    public LaunchAction Reopen(bool anyWindowOpen)
    {
        if (_inGrace)
        {
            _mainRequested = true;
            return new None();
        }

        if (!_frameworkReady)
        {
            return new None();
        }

        return anyWindowOpen && _mainOpen ? new ActivateMainWindow() : OpenMain();
    }

    /// <summary>Reports an activate request, which activates the main window or opens it when it was closed.</summary>
    public LaunchAction ActivateRequested()
    {
        if (_inGrace)
        {
            _mainRequested = true;
            return new None();
        }

        if (!_frameworkReady)
        {
            return new None();
        }

        return _mainOpen ? new ActivateMainWindow() : OpenMain();
    }

    /// <summary>Reports that the main window is shown.</summary>
    public LaunchAction MainWindowOpened()
    {
        _mainOpen = true;
        return new None();
    }

    /// <summary>Reports that the main window closed; a later activate or Dock click opens it again.</summary>
    public LaunchAction MainWindowClosed()
    {
        _mainOpen = false;
        return new None();
    }

    private OpenMainWindow OpenMain()
    {
        _mainOpen = true;
        return new OpenMainWindow();
    }
}
