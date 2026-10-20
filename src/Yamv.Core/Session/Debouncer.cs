namespace Yamv.Core.Session;

/// <summary>
/// Runs an action once the delay has passed without another <see cref="Schedule"/>. The action runs on a thread-pool
/// thread, or on the thread that calls <see cref="Flush"/>, and never twice at once unless the action itself calls
/// <see cref="Flush"/>.
/// </summary>
/// <remarks>
/// <see cref="Flush"/> waits for a run already in progress before it runs the pending action. The action must not wait
/// synchronously on the thread that calls <see cref="Flush"/> or <see cref="Dispose"/>, or the two wait on each other.
/// <c>Yamv.Core.Documents.DocumentWindowState</c> is single-threaded, so its window snapshots the state it saves on its
/// own thread before scheduling a save. An exception the action throws goes to the <c>onError</c> handler and never
/// reaches the timer's thread.
/// </remarks>
public sealed class Debouncer : IDisposable
{
    private readonly Lock gate = new();
    private readonly Lock runGate = new();
    private readonly TimeSpan delay;
    private readonly TimeProvider time;
    private readonly Action action;
    private readonly Action<Exception> onError;
    private ITimer? timer;
    private bool pending;
    private bool disposed;

    /// <summary>Creates a debouncer that runs <paramref name="action"/> on <paramref name="time"/>'s clock.</summary>
    /// <param name="delay">
    /// How long the debouncer waits for another <see cref="Schedule"/> before running the action; it must be greater than
    /// zero, so that a timer which fires synchronously cannot run the action inside <see cref="Schedule"/>.
    /// </param>
    /// <param name="time">The clock and timer source; the app passes <see cref="TimeProvider.System"/>, a test a fake.</param>
    /// <param name="action">What to run once the delay has passed.</param>
    /// <param name="onError">Where an exception the action throws is reported; it is never thrown on the timer's thread.</param>
    public Debouncer(TimeSpan delay, TimeProvider time, Action action, Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        this.delay = delay;
        this.time = time;
        this.action = action;
        this.onError = onError;
    }

    /// <summary>Starts the delay again, so the action runs only after the delay passes with no further call.</summary>
    public void Schedule()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = true;
            timer ??= time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs a pending action at once, here, and leaves nothing for the timer to run; does nothing when none is pending.</summary>
    public void Flush()
    {
        lock (runGate)
        {
            lock (gate)
            {
                if (disposed || !pending)
                {
                    return;
                }

                pending = false;
            }

            RunAction();
        }
    }

    /// <summary>Cancels a pending action, stops the timer and waits for an action already running to finish.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending = false;
            timer?.Dispose();
            timer = null;
        }

        lock (runGate)
        {
            // Wait for a save already in progress; nothing runs after this.
        }
    }

    private void OnTimer(object? state)
    {
        lock (runGate)
        {
            lock (gate)
            {
                if (disposed || !pending)
                {
                    return;
                }

                pending = false;
            }

            RunAction();
        }
    }

    private void RunAction()
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            onError(exception);
        }
    }
}
