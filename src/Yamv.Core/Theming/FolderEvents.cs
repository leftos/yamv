namespace Yamv.Core.Theming;

/// <summary>What a raw folder event reports.</summary>
internal enum FolderEventKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    Error,
}

/// <summary>One raw event from a watched folder: the entry names it carries, or the error that stopped the source.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Name">The entry's name, or its new name for a rename; null when the source does not know it.</param>
/// <param name="OldName">The entry's old name for a rename; null otherwise.</param>
/// <param name="Error">What stopped or overflowed the source, for <see cref="FolderEventKind.Error"/>.</param>
internal sealed record FolderEvent(FolderEventKind Kind, string? Name, string? OldName, Exception? Error);

/// <summary>A source of raw events for one folder's own entries, raising nothing until <see cref="Start"/>.</summary>
/// <remarks>An event already being delivered can still arrive after <see cref="IDisposable.Dispose"/> returns.</remarks>
internal interface IFolderEvents : IDisposable
{
    event Action<FolderEvent>? Raised;

    /// <summary>Starts raising events; an error starting can be raised as an <see cref="FolderEventKind.Error"/> event inside this call.</summary>
    void Start();
}

/// <summary>Reports whether a folder exists and which folder it is.</summary>
internal interface IFolderProbe
{
    FolderState Probe(string folder);
}

/// <summary>A folder's state: missing, or present with an identity that changes when the folder is replaced by another.</summary>
internal sealed record FolderState(bool Exists, string? Identity)
{
    public static FolderState Missing { get; } = new(false, null);

    public static FolderState Present(string identity) => new(true, identity);
}
