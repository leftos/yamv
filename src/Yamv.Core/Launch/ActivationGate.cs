namespace Yamv.Core.Launch;

/// <summary>
/// Holds an activate request until the main window can receive it: the instance coordinator's message
/// handler calls <see cref="Request"/> from a thread-pool thread, and the app calls <see cref="MarkReady"/>
/// once the main window is first shown.
/// </summary>
/// <remarks>
/// <see cref="Request"/> may be called from any thread; <see cref="MarkReady"/> is for the UI thread.
/// Requests that arrive before the gate is ready are coalesced into a single <see cref="Activate"/> at
/// <see cref="MarkReady"/>. A request that arrives while the gate is ready raises <see cref="Activate"/> at
/// once, on the requesting thread, so its handler must post to the UI thread and re-check the window's state
/// there. The event is raised outside the gate's lock, and the gate never becomes unready again.
/// </remarks>
public sealed class ActivationGate
{
    private readonly Lock _gate = new();
    private bool _ready;
    private bool _pending;

    /// <summary>Raised when the main window should be brought to the front.</summary>
    public event EventHandler? Activate;

    /// <summary>Asks for the main window to be activated, from any thread.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (!_ready)
            {
                _pending = true;
                return;
            }
        }

        Raise();
    }

    /// <summary>Marks the main window as existing, delivering at most one activate that waited for it.</summary>
    public void MarkReady()
    {
        bool raise;
        lock (_gate)
        {
            raise = _pending;
            _pending = false;
            _ready = true;
        }

        if (raise)
        {
            Raise();
        }
    }

    private void Raise() => Activate?.Invoke(this, EventArgs.Empty);
}
