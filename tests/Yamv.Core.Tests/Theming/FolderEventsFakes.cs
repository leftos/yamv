using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

/// <summary>A folder-event source the test raises events on by hand; it keeps raising them after it is disposed.</summary>
internal sealed class FakeFolderEvents : IFolderEvents
{
    private int disposeCount;

    public event Action<FolderEvent>? Raised;

    public int DisposeCount => Volatile.Read(ref disposeCount);

    public bool Started { get; private set; }

    /// <summary>An error <see cref="Start"/> raises as an Error event before it returns, as a watcher that fails to start does.</summary>
    public Exception? ErrorOnStart { get; set; }

    /// <summary>An exception <see cref="Start"/> throws.</summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>Runs first inside <see cref="Start"/>, as a source whose start runs the watcher's dispose before it throws.</summary>
    public Action? OnStart { get; set; }

    public void Raise(FolderEvent folderEvent) => Raised?.Invoke(folderEvent);

    public void Start()
    {
        Started = true;
        OnStart?.Invoke();
        if (ErrorOnStart is { } error)
        {
            Raise(new FolderEvent(FolderEventKind.Error, null, null, error));
        }

        if (ThrowOnStart is { } failure)
        {
            throw failure;
        }
    }

    public void Dispose() => Interlocked.Increment(ref disposeCount);
}

/// <summary>The arm function: records each attempt in <paramref name="order"/> and fails the attempts it was told to.</summary>
internal sealed class FakeFolderArm(List<string> order)
{
    private readonly Queue<Exception> failures = new();
    private readonly Queue<Exception> startErrors = new();
    private readonly Queue<Exception> startFailures = new();

    public List<FakeFolderEvents> Armed { get; } = [];

    /// <summary>The source most recently armed for each folder, by the folder the arm function was given.</summary>
    public Dictionary<string, FakeFolderEvents> ByFolder { get; } = new(StringComparer.Ordinal);

    public int Attempts { get; private set; }

    public FakeFolderEvents Current => Armed[^1];

    /// <summary>Runs inside each attempt, with the attempt's number, before the attempt returns its source.</summary>
    public Action<int>? OnAttempt { get; set; }

    public void FailNext(Exception exception) => failures.Enqueue(exception);

    /// <summary>The next source built raises <paramref name="exception"/> as an Error event inside its <c>Start</c>.</summary>
    public void ErrorOnNextStart(Exception exception) => startErrors.Enqueue(exception);

    /// <summary>The next source built throws <paramref name="exception"/> from its <c>Start</c>.</summary>
    public void FailNextStart(Exception exception) => startFailures.Enqueue(exception);

    public IFolderEvents Arm(string folder)
    {
        Attempts++;
        if (failures.TryDequeue(out Exception? failure))
        {
            throw failure;
        }

        var events = new FakeFolderEvents
        {
            ErrorOnStart = startErrors.TryDequeue(out Exception? error) ? error : null,
            ThrowOnStart = startFailures.TryDequeue(out Exception? startFailure) ? startFailure : null,
        };
        Armed.Add(events);
        ByFolder[folder] = events;
        order.Add("arm");
        OnAttempt?.Invoke(Attempts);
        return events;
    }
}

/// <summary>Passes on a real source's events, releasing a semaphore after each one is handled.</summary>
internal sealed class SignallingFolderEvents : IFolderEvents
{
    private readonly IFolderEvents inner;
    private readonly SemaphoreSlim signal;

    public SignallingFolderEvents(IFolderEvents inner, SemaphoreSlim signal)
    {
        this.inner = inner;
        this.signal = signal;
        inner.Raised += OnRaised;
    }

    public event Action<FolderEvent>? Raised;

    public void Start() => inner.Start();

    public void Dispose() => inner.Dispose();

    private void OnRaised(FolderEvent folderEvent)
    {
        Raised?.Invoke(folderEvent);
        signal.Release();
    }
}

/// <summary>A probe reporting whatever state the test sets, or throwing once when told to.</summary>
internal sealed class FakeFolderProbe : IFolderProbe
{
    public FolderState State { get; set; } = FolderState.Present("first");

    public Exception? ThrowNext { get; set; }

    public FolderState Probe(string folder)
    {
        if (ThrowNext is { } failure)
        {
            ThrowNext = null;
            throw failure;
        }

        return State;
    }
}
