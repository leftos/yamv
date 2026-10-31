using Avalonia.Threading;

namespace Yamv.App.ViewModels;

/// <summary>What the drop overlay shows.</summary>
public enum DropOverlayState
{
    /// <summary>No drag is over the window: the overlay is closed.</summary>
    Hidden,

    /// <summary>The page saw a drag, and the files are not known yet: the overlay is up and draws nothing.</summary>
    Undecided,

    /// <summary>The drag holds a document: "Drop to open".</summary>
    Open,

    /// <summary>The drag holds no document: the grey refusal.</summary>
    Refused,
}

/// <summary>
/// Follows a file drag over a window. The page's signals and the window's drag events do not pair up: the page's
/// <c>dragleave</c> comes before the overlay's own enter, a cancel may bring no leave at all, and a move from the overlay to the
/// title bar is a leave and an enter. So every leave only starts a grace, which an enter stops and whose end hides the overlay.
/// Every member is called on the UI thread.
/// </summary>
/// <param name="time">The clock of the grace and of <see cref="DragSignalledRecently"/>.</param>
public sealed class DropOverlayViewModel(TimeProvider time) : ViewModelBase, IDisposable
{
    /// <summary>How long a drag may go unseen before the overlay hides.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    /// <summary>How long after the page's last signal a new-window file link still counts as a drop.</summary>
    internal static readonly TimeSpan FallbackWindow = TimeSpan.FromSeconds(2);

    private ITimer? _graceTimer;
    private int _graceRun;
    private DateTimeOffset? _lastSignal;

    /// <summary>What the overlay shows now.</summary>
    public DropOverlayState State
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Whether the page signalled a file drag within <see cref="FallbackWindow"/>.</summary>
    public bool DragSignalledRecently => _lastSignal is { } signal && time.GetUtcNow() - signal <= FallbackWindow;

    /// <summary>The page saw files being dragged over it.</summary>
    public void PageDragOver()
    {
        if (State == DropOverlayState.Hidden)
        {
            State = DropOverlayState.Undecided;
        }

        _lastSignal = time.GetUtcNow();
        StartGrace();
    }

    /// <summary>The page lost the drag, which may only have moved to the overlay.</summary>
    public void PageDragLeft()
    {
        _lastSignal = null;
        StartGrace();
    }

    /// <summary>The window or the overlay got the drag and read its files.</summary>
    /// <param name="anyDocument">Whether the drag holds a document.</param>
    public void Entered(bool anyDocument)
    {
        StopGrace();
        State = anyDocument ? DropOverlayState.Open : DropOverlayState.Refused;
    }

    /// <summary>The window or the overlay lost the drag, which may only have moved between them.</summary>
    public void Left() => StartGrace();

    /// <summary>The drag ended here: the overlay hides at once.</summary>
    public void Ended()
    {
        _lastSignal = null;
        StopGrace();
        State = DropOverlayState.Hidden;
    }

    public void Dispose() => StopGrace();

    private void StartGrace()
    {
        StopGrace();
        int run = _graceRun;
        _graceTimer = time.CreateTimer(_ => Dispatcher.UIThread.Post(() => OnGraceEnded(run)), null, Grace, Timeout.InfiniteTimeSpan);
    }

    private void StopGrace()
    {
        _graceRun++;
        _graceTimer?.Dispose();
        _graceTimer = null;
    }

    /// <summary>A timer that fired just as it was stopped has a stale run, and does nothing.</summary>
    private void OnGraceEnded(int run)
    {
        if (run == _graceRun)
        {
            State = DropOverlayState.Hidden;
        }
    }
}
