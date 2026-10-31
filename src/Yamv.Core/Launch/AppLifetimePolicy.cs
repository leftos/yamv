namespace Yamv.Core.Launch;

/// <summary>What the app does when its last document window goes.</summary>
public enum LastWindowBehavior
{
    /// <summary>The app stops answering launches and quits (Windows).</summary>
    Quit,

    /// <summary>The app keeps running with no window and stops answering only when it exits (macOS).</summary>
    KeepRunning,
}

/// <summary>What the app does after a document window went.</summary>
public enum LastWindowStep
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Stop answering launches, then quit the app.</summary>
    StopAndQuit,
}

/// <summary>
/// The app's last-window rule for one start: whether windows may still open, what a document window going leaves the app to do,
/// and whether the app's exit still has to stop answering. The app stops at most once, by whichever comes first. Driven from the
/// UI thread.
/// </summary>
/// <param name="behavior">What the last document window going does.</param>
public sealed class AppLifetimePolicy(LastWindowBehavior behavior)
{
    private bool _stopping;

    /// <summary>False once the app began stopping: from then on no window is built.</summary>
    public bool MayOpenWindows => !_stopping;

    /// <summary>Reports that a document window went, leaving <paramref name="remaining"/> open.</summary>
    /// <param name="remaining">The document windows still open.</param>
    /// <returns>
    /// <see cref="LastWindowStep.StopAndQuit"/> the first time none remain under <see cref="LastWindowBehavior.Quit"/>; otherwise
    /// <see cref="LastWindowStep.None"/>.
    /// </returns>
    public LastWindowStep DocumentWindowGone(int remaining)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        if (behavior != LastWindowBehavior.Quit || remaining > 0 || _stopping)
        {
            return LastWindowStep.None;
        }

        _stopping = true;
        return LastWindowStep.StopAndQuit;
    }

    /// <summary>Reports that the app is exiting.</summary>
    /// <returns>True when the app had not begun stopping, so the exit runs the stop; false after that.</returns>
    public bool Exiting()
    {
        if (_stopping)
        {
            return false;
        }

        _stopping = true;
        return true;
    }
}
