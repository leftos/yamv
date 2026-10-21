using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;
using Yamv.Core.Launch;

namespace Yamv.App;

/// <summary>
/// Answers the instance server's requests on its pool thread, one at a time, and never blocks that thread on the UI thread:
/// <c>activate</c> goes through the activation gate, a well-formed <c>open</c> is posted to the UI thread to open a viewer
/// window, and every other request that expects a reply is refused.
/// </summary>
/// <param name="gate">The gate <c>activate</c> passes through.</param>
/// <param name="postToUi">Queues a job on the UI thread and returns at once; a throw refuses the request it carried.</param>
/// <param name="openViewer">
/// Opens a viewer window over the files for the request, on the UI thread; true when the app took the request.
/// </param>
/// <param name="sinceProcessStart">How long ago the process started, for the one line that says when the UI took its first request.</param>
/// <param name="log">The process's log.</param>
internal sealed class InstanceRequestHandler(
    ActivationGate gate,
    Action<Action> postToUi,
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
            gate.Request();
            return;
        }

        if (OpenMessage.TryDecode(request.Message, out IReadOnlyList<string> files))
        {
            Post(files, request);
            return;
        }

        string kind = request.Message.StartsWith(InstanceMessages.Open, StringComparison.Ordinal) ? "a malformed open" : "an unknown";
        log.LogWarning($"Instance server: refusing {kind} message '{ForLog(request.Message)}'.");
        if (request.ExpectsReply)
        {
            request.Complete(InstanceReply.Refused);
        }
    }

    private void Post(IReadOnlyList<string> files, InstanceRequest request)
    {
        try
        {
            postToUi(() =>
            {
                if (openViewer(files, request))
                {
                    LogReadinessOnce();
                }
            });
        }
        // Whatever stopped the post, the request will never be carried out, so its launcher hears refused rather than waiting.
        catch (Exception exception)
        {
            log.LogError($"Instance server: could not hand an open of {files.Count} file(s) to the UI thread; refusing it.", exception);
            request.Complete(InstanceReply.Refused);
        }
    }

    /// <summary>
    /// The cold-start readiness measure: once per process, how long after the process started the UI thread took its first open
    /// request (one the app began, not one it answered closing or found abandoned), which is what a launcher's open deadline
    /// waits for.
    /// </summary>
    private void LogReadinessOnce()
    {
        if (Interlocked.Exchange(ref _firstRequestTaken, 1) == 0)
        {
            log.LogInfo(
                $"Instance server: the UI thread took its first open request {sinceProcessStart().TotalMilliseconds:0} ms after the process started."
            );
        }
    }

    /// <summary>At most the first 200 characters of <paramref name="message"/>, with line breaks escaped so it stays on one log line.</summary>
    private static string ForLog(string message) =>
        message[..Math.Min(message.Length, MaxLoggedMessageLength)]
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
