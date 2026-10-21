using Yamv.Core.Diagnostics;
using Yamv.Core.Documents;

namespace Yamv.App.Services;

/// <summary>The <see cref="IDocumentChanges"/> of a real window: a <see cref="DocumentFileWatcher"/> on the system clock.</summary>
public sealed class DocumentChanges : IDocumentChanges
{
    private readonly DocumentFileWatcher _watcher;

    /// <summary>Starts a watcher that follows no document until <see cref="SetPaths"/> says which.</summary>
    /// <param name="log">Where a folder that cannot be watched is reported.</param>
    public DocumentChanges(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _watcher = new DocumentFileWatcher(TimeProvider.System, log);
    }

    /// <inheritdoc/>
    public event EventHandler<DocumentsChangedEventArgs>? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }

    /// <inheritdoc/>
    public void SetPaths(IEnumerable<string> documentPaths) => _watcher.SetPaths(documentPaths);

    /// <inheritdoc/>
    public void Dispose() => _watcher.Dispose();
}
