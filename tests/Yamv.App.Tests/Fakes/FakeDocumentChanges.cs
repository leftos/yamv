using Yamv.App.Services;
using Yamv.Core.Documents;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IDocumentChanges"/> that records what it was told to follow and raises reports on demand.</summary>
public sealed class FakeDocumentChanges : IDocumentChanges
{
    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<string>> _setPathsCalls = [];
    private EventHandler<DocumentsChangedEventArgs>? _changed;

    public event EventHandler<DocumentsChangedEventArgs>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    /// <summary>Every <see cref="SetPaths"/> call's paths, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<string>> SetPathsCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _setPathsCalls];
            }
        }
    }

    /// <summary>When set, every <see cref="SetPaths"/> call throws it after recording its paths.</summary>
    public Exception? ThrowOnSetPaths { get; set; }

    /// <summary>True once <see cref="Dispose"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>True while anything is subscribed to <see cref="Changed"/>.</summary>
    public bool HasSubscribers
    {
        get
        {
            lock (_gate)
            {
                return _changed is not null;
            }
        }
    }

    public void SetPaths(IEnumerable<string> documentPaths)
    {
        lock (_gate)
        {
            _setPathsCalls.Add([.. documentPaths]);
        }

        if (ThrowOnSetPaths is { } exception)
        {
            throw exception;
        }
    }

    /// <summary>Raises <see cref="Changed"/> on the calling thread as the watcher would, naming <paramref name="paths"/>.</summary>
    public void Raise(params string[] paths)
    {
        EventHandler<DocumentsChangedEventArgs>? changed;
        lock (_gate)
        {
            changed = _changed;
        }

        changed?.Invoke(this, new DocumentsChangedEventArgs(new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)));
    }

    public void Dispose() => Disposed = true;
}
