using Yamv.Core.Diagnostics;

namespace Yamv.Core.Instance;

/// <summary>
/// Logs the failures of a retrying loop without flooding the log, and paces its retries. A failure is logged when its exception
/// type or message differs from the last one logged; repeats are counted and the count is reported in the next line logged, or
/// in the recovery line a success logs. The retry delay doubles from 100 ms up to 30 s and drops back to 100 ms after a success.
/// </summary>
/// <param name="log">Where the failures go.</param>
/// <param name="subject">What fails, as the start of a sentence, for example "The instance server on pipe 'yamv-1234abcd'".</param>
internal sealed class ServerFailureLog(IAppLog log, string subject)
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private string? _lastLoggedKey;
    private int _suppressedRepeats;
    private TimeSpan _nextDelay = InitialDelay;
    private int _consecutiveConnectFailures;

    /// <summary>
    /// Records a failure of the loop itself, logged as an error with its stack trace unless it repeats the last one, and returns
    /// the delay before the retry.
    /// </summary>
    public TimeSpan Record(Exception exception)
    {
        if (IsNewFailure(exception, out string repeats))
        {
            log.LogError($"{subject} failed; it keeps accepting.{repeats}", exception);
        }

        return NextDelay();
    }

    /// <summary>
    /// Records a client lost before it was accepted (for example one that closed before the server took the connection), logged as
    /// a warning without a stack trace unless it repeats the last failure. Null when the loop should retry at once: the first such
    /// failure since the last success; from the second in a row, the delay before the retry, as <see cref="Record"/> paces it.
    /// </summary>
    public TimeSpan? RecordConnectFailure(Exception exception)
    {
        if (IsNewFailure(exception, out string repeats))
        {
            log.LogWarning($"{subject} lost a client before accepting it; it keeps accepting.{repeats} {Describe(exception)}");
        }

        _consecutiveConnectFailures++;
        return _consecutiveConnectFailures == 1 ? null : NextDelay();
    }

    /// <summary>
    /// Notes a success: logs the recovery when repeats went unlogged, forgets the last failure so a recurrence is logged again, and
    /// sets the next failure's delay back to the initial one.
    /// </summary>
    public void RecordSuccess()
    {
        if (_suppressedRepeats > 0)
        {
            log.LogInfo($"{subject} recovered after {_suppressedRepeats} repeat(s) of its last failure that were not logged.");
        }

        _lastLoggedKey = null;
        _suppressedRepeats = 0;
        _consecutiveConnectFailures = 0;
        _nextDelay = InitialDelay;
    }

    private static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";

    /// <summary>True when <paramref name="exception"/> differs from the last failure logged; a repeat is only counted.</summary>
    private bool IsNewFailure(Exception exception, out string repeats)
    {
        string key = Describe(exception);
        if (key == _lastLoggedKey)
        {
            _suppressedRepeats++;
            repeats = "";
            return false;
        }

        repeats = _suppressedRepeats > 0 ? $" The previous failure repeated {_suppressedRepeats} more time(s) without being logged." : "";
        _lastLoggedKey = key;
        _suppressedRepeats = 0;
        return true;
    }

    private TimeSpan NextDelay()
    {
        TimeSpan delay = _nextDelay;
        _nextDelay = TimeSpan.FromTicks(Math.Min(_nextDelay.Ticks * 2, MaxDelay.Ticks));
        return delay;
    }
}
