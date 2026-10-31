using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.App;

/// <summary>
/// Answers the instance server's requests on its pool thread, one at a time, and never blocks that thread on the UI thread:
/// <c>activate</c> is posted to the UI thread to bring the app forward, a well-formed <c>open</c> is posted to the UI thread to open
/// a viewer window, and every other request is refused.
/// </summary>
/// <param name="postToUi">
/// Queues a job on the UI thread and returns at once (the app's ready latch); a throw refuses the request it carried.
/// </param>
/// <param name="activate">
/// Brings the app forward for the request and answers it, on the UI thread; true when a window came forward.
/// </param>
/// <param name="openViewer">
/// Opens the files or brings forward a window holding them, on the UI thread; true when the app took the request.
/// </param>
/// <param name="sinceProcessStart">How long ago the process started, for the one line that says when the UI took its first request.</param>
/// <param name="log">The process's log.</param>
internal sealed class InstanceRequestHandler(
    Action<Action> postToUi,
    Func<InstanceRequest, bool> activate,
    Func<IReadOnlyList<string>, InstanceRequest, bool> openViewer,
    Func<TimeSpan> sinceProcessStart,
    IAppLog log
)
{
    private const int MaxLoggedMessageLength = 200;

    private int _firstRequestTaken;

    /// <summary>Handles one request the server read; returns as soon as the request is answered or queued for the UI thread.</summary>
    public void Handle(InstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message == InstanceMessages.Activate)
        {
            PostToUi(() => activate(request), "an activate", request);
            return;
        }

        if (OpenMessage.TryDecode(request.Message, out IReadOnlyList<string> files))
        {
            PostToUi(() => openViewer(files, request), $"an open of {files.Count} file(s)", request);
            return;
        }

        string kind = request.Message.StartsWith(InstanceMessages.Open, StringComparison.Ordinal) ? "a malformed open" : "an unknown";
        log.LogWarning($"Instance server: refusing {kind} message '{ForLog(request.Message)}'.");
        request.Complete(InstanceReply.Refused);
    }

    /// <summary>
    /// Posts <paramref name="job"/>, which says whether the app took <paramref name="request"/>; <paramref name="what"/> names it for
    /// the log.
    /// </summary>
    private void PostToUi(Func<bool> job, string what, InstanceRequest request)
    {
        try
        {
            postToUi(() =>
            {
                if (job())
                {
                    LogReadinessOnce();
                }
            });
        }
        // Whatever stopped the post, the request will never be carried out, so its launcher hears refused rather than waiting.
        catch (Exception exception)
        {
            log.LogError($"Instance server: could not hand {what} to the UI thread; refusing it.", exception);
            request.Complete(InstanceReply.Refused);
        }
    }

    /// <summary>
    /// The cold-start readiness measure: once per process, how long after the process started the UI thread took its first request
    /// (one the app carried out or began, not one it answered closing or found abandoned), which is what a launcher's request
    /// deadline waits for.
    /// </summary>
    private void LogReadinessOnce()
    {
        if (Interlocked.Exchange(ref _firstRequestTaken, 1) == 0)
        {
            log.LogInfo(
                $"Instance server: the UI thread took its first request {sinceProcessStart().TotalMilliseconds:0} ms after the process started."
            );
        }
    }

    /// <summary>At most the first 200 characters of <paramref name="message"/>, with line breaks escaped so it stays on one log line.</summary>
    private static string ForLog(string message) =>
        message[..Math.Min(message.Length, MaxLoggedMessageLength)]
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
