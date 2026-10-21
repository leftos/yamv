using Yamv.Core.Documents;

namespace Yamv.App.Services;

/// <summary>
/// Reports which of a window's open documents may have changed on disk. A window owns one, tells it which documents it holds,
/// and disposes it when the window goes.
/// </summary>
public interface IDocumentChanges : IDisposable
{
    /// <summary>
    /// Raised on a thread-pool thread with the open documents that may have changed. A subscriber posts its work to its UI
    /// thread and never waits on it there, since <see cref="IDisposable.Dispose"/> waits for a report already running.
    /// </summary>
    event EventHandler<DocumentsChangedEventArgs>? Changed;

    /// <summary>Replaces the documents followed; it does no work on their folders on the calling thread.</summary>
    /// <param name="documentPaths">The full paths of the window's open documents.</param>
    /// <exception cref="ArgumentException">A path is not a full path, or has no folder of its own.</exception>
    void SetPaths(IEnumerable<string> documentPaths);
}
