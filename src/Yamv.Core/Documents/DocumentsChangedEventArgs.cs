namespace Yamv.Core.Documents;

/// <summary>
/// The open documents one report names as possibly changed: an event named one of them, a watcher error or the start of a
/// working watch followed a blind period, their folder appeared or was replaced, or a poll found a different length or last
/// write time.
/// </summary>
/// <param name="paths">The canonical full paths of the open documents that may have changed.</param>
public sealed class DocumentsChangedEventArgs(IReadOnlySet<string> paths) : EventArgs
{
    /// <summary>The canonical full paths of the open documents that may have changed, compared as the platform compares file paths.</summary>
    public IReadOnlySet<string> Paths { get; } = paths;
}
