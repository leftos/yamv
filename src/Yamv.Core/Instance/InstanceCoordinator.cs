using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Instance;

/// <summary>
/// Keeps YAMV to one main instance per user: the main instance holds an exclusive lock on <paramref name="lockFilePath"/>
/// and listens on the pipe <see cref="Endpoint"/> derived from that path; a later launch fails to take the lock and sends it a
/// request. On Unix the lock is an advisory <c>flock</c>, which .NET turns off silently when <c>System.IO.DisableFileLocking</c> is set
/// or the file system does not support it (NFS, SMB). The main instance also writes its pid and start time to a pid file beside the
/// lock, named after the lock file with the extension <c>.pid</c> (a second launch cannot read the locked file itself), and deletes it
/// before releasing the lock.
/// </summary>
/// <param name="lockFilePath">The lock file, normally <see cref="AppPaths.MainLockFile"/>. Its folder is created on demand.</param>
/// <param name="log">Where every caught exception is reported.</param>
/// <exception cref="ArgumentException"><paramref name="lockFilePath"/> is empty or not a valid path.</exception>
public sealed class InstanceCoordinator(string lockFilePath, IAppLog log) : IDisposable
{
    /// <summary>The pause between connect attempts while no server answers.</summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>A connect timeout of 0 makes one attempt; a longer one makes the runtime retry in a spin while no server exists.</summary>
    private const int ConnectAttemptTimeoutMilliseconds = 0;

    private const PipeOptions SharedPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const int PidMoveAttempts = 3;
    private static readonly TimeSpan PidMoveRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>The longest pid file read; a longer one is malformed. A valid one is at most 31 bytes.</summary>
    private const int MaxPidFileLength = 64;

    /// <summary>The coordinator whose message handler is running on this thread, if any.</summary>
    [ThreadStatic]
    private static InstanceCoordinator? t_handlerRunningFor;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Requests read from clients, waiting for the handler loop: no connection ever waits on a busy handler.</summary>
    /// <remarks>Default options: the single-reader unbounded channel cannot report <c>Count</c>, which Dispose needs.</remarks>
    private readonly Channel<InstanceRequest> _queue = Channel.CreateUnbounded<InstanceRequest>();

    /// <summary>Requests that expect a reply and whose connection is still open; guarded by <see cref="_gate"/>.</summary>
    private readonly HashSet<InstanceRequest> _openRequests = [];

    /// <summary>The per-connection tasks still running; guarded by <see cref="_gate"/>.</summary>
    private readonly HashSet<Task> _connections = [];

    /// <summary>True once the server began stopping: a request read from then on is abandoned as soon as it is read.</summary>
    private bool _abandoning;

    /// <summary>Completed once the first <see cref="Dispose"/> has released the lock; later callers wait on it.</summary>
    private readonly TaskCompletionSource _lockReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FileStream? _lockStream;

    /// <summary>True once this instance has written the pid file, so <see cref="Dispose"/> deletes only a file it wrote.</summary>
    private bool _pidFileWritten;

    /// <summary>The loop that accepts clients and queues their frames, once <see cref="StartServer"/> has run.</summary>
    private Task? _acceptLoop;

    /// <summary>The loop that runs the message handler on the queued frames, once <see cref="StartServer"/> has run.</summary>
    private Task? _handlerLoop;
    private bool _disposed;

    /// <summary>True once <see cref="StopAnswering"/> or <see cref="Dispose"/> has begun stopping the server.</summary>
    private bool _stoppedAnswering;

    /// <summary>True once the server's shutdown token has been cancelled, so disposing it can no longer race the cancel.</summary>
    private bool _shutdownCancelled;

    /// <summary>The server instance waiting for the next client; touched only by the accept loop.</summary>
    private NamedPipeServerStream? _listener;

    /// <summary>Frames queued since the server started; incremented by the connection tasks.</summary>
    private int _framesQueued;

    /// <summary>
    /// True once the server's first listener exists; touched only by the accept loop. Only that first one asks for
    /// <see cref="PipeOptions.FirstPipeInstance"/>: a later listener created while no other exists is still this server's own.
    /// </summary>
    private bool _firstListenerCreated;

    /// <summary>The lock file's full path, computed once, the pid file beside it and the <see cref="Endpoint"/> derived from it.</summary>
    private readonly (string FullLockPath, string PidPath, string Endpoint) _paths = ResolvePaths(lockFilePath, log);

    /// <summary>
    /// The pipe this coordinator's server listens on, derived from the lock file's path so a launch that shares the lock reaches it:
    /// on Windows <c>yamv-</c> plus a hash of the account SID and the full lock path; on macOS and Linux the socket path
    /// <c>main.sock</c> beside the lock, or a logged fallback when that path is too long for the platform.
    /// </summary>
    public string Endpoint => _paths.Endpoint;

    /// <summary>
    /// Called on a connection task after each frame is queued, with the number queued since the server started; set it before
    /// <see cref="StartServer"/>. Lets tests wait for frames whose Unix client reported delivery before the server read them.
    /// </summary>
    internal Action<int>? FrameQueued { get; set; }

    /// <summary>
    /// How long a connection waits for its request's reply before abandoning the request and closing without one; 10 s by
    /// default. A launcher's request deadline (<see cref="Launch.SecondLaunch.RequestDeadline"/>) stays below it, or a request the
    /// app has begun may close with no reply and be sent twice.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive or is longer than <see cref="int.MaxValue"/> ms.</exception>
    public TimeSpan ReplyBound
    {
        get;
        init
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A reply bound is positive and at most int.MaxValue ms.");
            }

            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long writing a reply may take (on Windows it completes only once the launcher reads it), and how long a stopping server
    /// still waits for a reply the app has begun; 2 s. Set it before <see cref="StartServer"/>.
    /// </summary>
    internal TimeSpan ReplyWriteBound { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Called on the connection task just before it writes a reply. Lets tests see which thread writes it.</summary>
    internal Action? ReplyWriting { get; set; }

    /// <summary>
    /// Called by the stop that cancels the server, just before the cancel. Lets tests check that every open request was
    /// abandoned before the connections learn of the stop.
    /// </summary>
    internal Action? ShutdownCancelling { get; set; }

    /// <summary>The connection tasks still running. Lets tests check that every server stream is closed before the lock is released.</summary>
    internal int OpenConnectionCount
    {
        get
        {
            lock (_gate)
            {
                // An ended task may still be in the set until its removal continuation runs.
                return _connections.Count(connection => !connection.IsCompleted);
            }
        }
    }

    /// <summary>
    /// How long the server waits for a connected client's whole frame before dropping it; 30 s. Set it before
    /// <see cref="StartServer"/>. Lets tests hold a client mid-frame through a stall on a loaded machine.
    /// </summary>
    internal TimeSpan FrameReadDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="Dispose"/> waits for the accept loop, then for a running handler, then for the open connections, before
    /// it goes on; 5 s each.
    /// Lets tests turn a wrong wait into a hang their guard catches instead of a race with these bounds.
    /// </summary>
    internal TimeSpan DisposeWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Called on the accept loop before each listener is created, with whether it is created as the server's first instance; set
    /// it before <see cref="StartServer"/>. An exception it throws fails that creation, so tests can stage a failed listener.
    /// </summary>
    internal Action<bool>? ListenerCreating { get; set; }

    /// <summary>
    /// Called before each connect attempt a request makes, with the connect timeout in milliseconds, in the async flow that set
    /// it. Lets tests check how a request paces its attempts without measuring CPU time.
    /// </summary>
    internal static AsyncLocal<Action<int>?> ConnectAttempted { get; } = new();

    /// <summary>
    /// Called before the pid file's text reads this process's start time, in the async flow that set it. An exception it throws
    /// stands for a start time the runtime cannot read, so tests can stage a failed pid-file write.
    /// </summary>
    internal static AsyncLocal<Action?> PidTextReading { get; } = new();

    /// <summary>
    /// Called by the first <see cref="Dispose"/> once it has deleted the pid file and before it closes the lock. Lets tests check
    /// that the pid file is gone while the lock is still held.
    /// </summary>
    internal Action? LockReleasing { get; set; }

    /// <summary>
    /// Takes the main-instance lock, held until <see cref="Dispose"/> or the process ends. True when this process now holds it
    /// (or already did); false when another process holds it (logged as info) or the lock file cannot be opened (logged as an
    /// error). On first taking it, records this process's pid and start time in the pid file beside the lock (see
    /// <see cref="TryReadMainPid"/>); a failed write is a warning.
    /// </summary>
    public bool TryAcquireMainLock() => AcquireMainLock(logHeldByAnother: true) == MainLockAttempt.Acquired;

    /// <summary>
    /// Takes the main-instance lock as <see cref="TryAcquireMainLock"/> does, for a launch that retries it while another process
    /// holds it: a lock held by another process is not logged, so the retries stay quiet. A lock file that cannot be opened is
    /// still logged as an error, and reported as <see cref="MainLockAttempt.CannotOpen"/> so the caller stops retrying.
    /// </summary>
    public MainLockAttempt TryAcquireMainLockQuietly() => AcquireMainLock(logHeldByAnother: false);

    private MainLockAttempt AcquireMainLock(bool logHeldByAnother)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lockStream is not null)
            {
                return MainLockAttempt.Acquired;
            }

            try
            {
                string? directory = Path.GetDirectoryName(_paths.FullLockPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _lockStream = new FileStream(_paths.FullLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsShareViolation(ex))
            {
                if (logHeldByAnother)
                {
                    log.LogInfo(
                        $"Another process holds the main-instance lock {_paths.FullLockPath}, so this one is not the main instance: {ex.Message}"
                    );
                }

                return MainLockAttempt.HeldByAnother;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError($"Could not open the main-instance lock {_paths.FullLockPath}; check that its folder is writable.", ex);
                return MainLockAttempt.CannotOpen;
            }

            // Outside the catches above: the lock is held now, so no pid-file failure may turn this into a failure.
            WritePidFile();
            return MainLockAttempt.Acquired;
        }
    }

    /// <summary>
    /// Reads the pid the main instance holding the lock at <paramref name="lockFilePath"/> recorded beside it, for a second launch.
    /// Returns it only when that process is running and started at the recorded time, so a stale file left by a crash or a pid
    /// the system has since reused gives null. A missing file gives null silently; every other null is logged as info.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="lockFilePath"/> is empty or not a valid path.</exception>
    public static int? TryReadMainPid(string lockFilePath, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string pidPath = PidPathFor(Path.GetFullPath(lockFilePath));
        string? text = ReadPidFile(pidPath, log);
        if (text is null)
        {
            return null;
        }

        if (!TryParsePidFile(text, out int pid, out long startTicks))
        {
            log.LogInfo($"Ignoring the main-instance pid file {pidPath}: it does not hold a pid line and a start-time line.");
            return null;
        }

        return IsRecordedProcessRunning(pidPath, pid, startTicks, log) ? pid : null;
    }

    /// <summary>
    /// Starts accepting requests on background tasks. A listener is always waiting: the next one is created as soon as a client
    /// connects, and each connection then runs on a task of its own, so no client waits on another's frame or reply. Each frame
    /// is read and queued as an <see cref="InstanceRequest"/>; <paramref name="onRequest"/> drains the queue on a thread-pool
    /// thread, one request at a time, and an exception it throws is logged (a request not yet begun is then answered
    /// <c>refused</c>) while the server keeps serving.
    /// A request keeps its connection open until it is completed, its launcher hangs up (it is then
    /// abandoned), <see cref="ReplyBound"/> passes (abandoned, closed without a reply) or the server stops (abandoned and answered
    /// <c>closing</c>); the reply is written within <see cref="ReplyWriteBound"/>.
    /// <see cref="Dispose"/> waits for a running <paramref name="onRequest"/>, so it must post work to the thread that disposes this
    /// coordinator, never block on it. Calling <see cref="Dispose"/> from inside <paramref name="onRequest"/> never waits on the
    /// handler it runs in: as the first call it waits only for the open connections (up to <see cref="DisposeWait"/>; a request
    /// the app has begun keeps its connection up to <see cref="ReplyWriteBound"/> for its reply) and then releases the lock, and
    /// as a later call it returns at once and leaves the release to the first.
    /// </summary>
    /// <exception cref="InvalidOperationException">This coordinator does not hold the lock, or the server is already running.</exception>
    public void StartServer(Action<InstanceRequest> onRequest)
    {
        ArgumentNullException.ThrowIfNull(onRequest);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lockStream is null)
            {
                throw new InvalidOperationException(
                    "StartServer needs the main-instance lock: call TryAcquireMainLock first and start the server only when it returns true."
                );
            }

            if (_acceptLoop is not null)
            {
                throw new InvalidOperationException("The instance server is already running.");
            }

            if (_stoppedAnswering)
            {
                throw new InvalidOperationException("The instance server cannot start after StopAnswering.");
            }

            CancellationToken token = _shutdown.Token;
            _acceptLoop = Task.Run(() => RunServerLoopAsync(token), CancellationToken.None);
            _handlerLoop = Task.Run(() => RunHandlerLoopAsync(onRequest, token), CancellationToken.None);
            LogIfFaulted(_acceptLoop, "accept loop", "it accepts no more messages");
            LogIfFaulted(_handlerLoop, "handler loop", "queued messages are no longer handled");
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the main instance holding the lock at <paramref name="lockFilePath"/> and waits for its
    /// one reply on the same connection. <paramref name="budget"/> starts at the first connect attempt and covers the connects
    /// (retried every 50 ms while no server answers), the write and the reply; <see cref="Timeout.InfiniteTimeSpan"/> waits
    /// without a bound. The client is disposed before this returns, so the server has seen the hang-up before the caller reports
    /// anything. Every outcome but <see cref="InstanceRequestOutcome.Confirmed"/> is logged.
    /// </summary>
    /// <returns>
    /// <see cref="InstanceRequestOutcome.Confirmed"/>, <see cref="InstanceRequestOutcome.Refused"/> (or an unknown reply) and
    /// <see cref="InstanceRequestOutcome.Closing"/> are the server's answer, a connection that ended before any reply counting as
    /// closing; <see cref="InstanceRequestOutcome.NoReply"/> when the budget ran out after connecting;
    /// <see cref="InstanceRequestOutcome.NoServer"/> when it ran out before; <see cref="InstanceRequestOutcome.Denied"/> when the
    /// pipe belongs to another user or a connect failed in a way a retry cannot fix.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="lockFilePath"/> is empty or not a valid path, or the message encodes to more than 64 KiB.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="budget"/> is neither <see cref="Timeout.InfiniteTimeSpan"/> nor positive and at most <see cref="int.MaxValue"/> ms.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<InstanceRequestOutcome> RequestAsync(
        string lockFilePath,
        string message,
        TimeSpan budget,
        IAppLog log,
        CancellationToken cancellationToken = default
    ) => RequestAsync(lockFilePath, message, new RequestBudget(budget, TimeProvider.System), log, cancellationToken);

    /// <summary>A request's budget and the clock it runs on.</summary>
    internal readonly record struct RequestBudget(TimeSpan Length, TimeProvider Time);

    /// <summary>What one request's connect and exchange share: the pipe, the budget, the log, and both tokens.</summary>
    private readonly record struct RequestContext(
        string PipeName,
        RequestBudget Budget,
        IAppLog Log,
        CancellationToken Deadline,
        CancellationToken Caller
    );

    /// <summary><see cref="RequestAsync(string, string, TimeSpan, IAppLog, CancellationToken)"/> on the clock of <paramref name="budget"/>.</summary>
    internal static async Task<InstanceRequestOutcome> RequestAsync(
        string lockFilePath,
        string message,
        RequestBudget budget,
        IAppLog log,
        CancellationToken cancellationToken
    )
    {
        ValidateTimeout(budget.Length, nameof(budget), "request budget");
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(log);
        byte[] frame = InstanceFrame.Encode(message);
        string pipeName = InstanceEndpoint.Resolve(lockFilePath, EndpointEnvironment.ForCurrentProcess(), log);

        using var budgetSource = new CancellationTokenSource(budget.Length, budget.Time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);
        RequestContext context = new(pipeName, budget, log, deadline.Token, cancellationToken);
        (NamedPipeClientStream? client, InstanceRequestOutcome failure) = await ConnectWithinBudgetAsync(context).ConfigureAwait(false);
        if (client is null)
        {
            return failure;
        }

        await using (client.ConfigureAwait(false))
        {
            return await ExchangeAsync(client, frame, context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects both ways, retrying every 50 ms on the budget's clock until the deadline. The client, or null with the outcome to
    /// report.
    /// </summary>
    private static async Task<(NamedPipeClientStream? Client, InstanceRequestOutcome Failure)> ConnectWithinBudgetAsync(RequestContext context)
    {
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                NamedPipeClientStream? client = await ConnectClientAsync(context.PipeName, context.Log, context.Deadline).ConfigureAwait(false);
                return (client, InstanceRequestOutcome.NoServer);
            }
            catch (Exception ex) when (IsRetryableConnectFailure(ex))
            {
                lastFailure = ex;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SocketException)
            {
                context.Log.LogWarning(DescribeFinalConnectFailure(context.PipeName, ex), ex);
                return (null, InstanceRequestOutcome.Denied);
            }
            catch (OperationCanceledException) when (!context.Caller.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(ConnectRetryDelay, context.Budget.Time, context.Deadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.Caller.IsCancellationRequested)
            {
                break;
            }
        }

        context.Log.LogWarning(
            $"No main instance answered on pipe '{context.PipeName}' within {context.Budget.Length.TotalMilliseconds:0} ms.",
            lastFailure
        );
        return (null, InstanceRequestOutcome.NoServer);
    }

    /// <summary>Writes <paramref name="frame"/> on the connected <paramref name="client"/> and reads the one reply.</summary>
    private static async Task<InstanceRequestOutcome> ExchangeAsync(NamedPipeClientStream client, byte[] frame, RequestContext context)
    {
        (string pipeName, IAppLog log, CancellationToken deadline) = (context.PipeName, context.Log, context.Deadline);
        TimeSpan budget = context.Budget.Length;
        try
        {
            await client.WriteAsync(frame, deadline).ConfigureAwait(false);
            await client.FlushAsync(deadline).ConfigureAwait(false);
            string text = await InstanceFrame.ReadAsync(client, InstanceFrame.MaxReplyPayloadBytes, deadline).ConfigureAwait(false);
            return ToOutcome(InstanceReplies.Parse(text), text, pipeName, log);
        }
        catch (OperationCanceledException ex) when (!context.Caller.IsCancellationRequested)
        {
            log.LogWarning($"The main instance on pipe '{pipeName}' did not answer within {budget.TotalMilliseconds:0} ms; hanging up.", ex);
            return InstanceRequestOutcome.NoReply;
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            log.LogWarning($"The main instance on pipe '{pipeName}' sent a reply that is not one; counting it as refused.", ex);
            return InstanceRequestOutcome.Refused;
        }
        catch (IOException ex)
        {
            log.LogInfo($"The main instance on pipe '{pipeName}' closed the connection before replying; it is closing: {ex.Message}");
            return InstanceRequestOutcome.Closing;
        }
    }

    private static InstanceRequestOutcome ToOutcome(InstanceReply reply, string text, string pipeName, IAppLog log)
    {
        switch (reply)
        {
            case InstanceReply.Opened:
                return InstanceRequestOutcome.Confirmed;
            case InstanceReply.Closing:
                log.LogInfo($"The main instance on pipe '{pipeName}' is closing and did not take the request.");
                return InstanceRequestOutcome.Closing;
            default:
                log.LogWarning($"The main instance on pipe '{pipeName}' refused the request (reply '{text}').");
                return InstanceRequestOutcome.Refused;
        }
    }

    /// <summary>
    /// Stops answering second launches while keeping the lock: deletes the pid file first, then stops the server as
    /// <see cref="Dispose"/> does, waiting the same bounded way. A launch from then on finds no server and no pid and retries the
    /// lock for a bounded time, so it becomes the main instance once this one has released the lock. The main instance calls
    /// this as it starts to shut down, before it saves its session; <see cref="Dispose"/> then only waits for the server and
    /// releases the lock. Safe to call more than once, before <see cref="StartServer"/> or without it, and after
    /// <see cref="Dispose"/>, where it does nothing.
    /// </summary>
    public void StopAnswering()
    {
        Task? acceptLoop;
        Task? handlerLoop;
        lock (_gate)
        {
            if (_stoppedAnswering)
            {
                return;
            }

            _stoppedAnswering = true;

            // First, so a stop that waits on a stuck handler never leaves a pid a second launch could bring forward.
            DeleteOwnPidFile();
            acceptLoop = _acceptLoop;
            handlerLoop = _handlerLoop;
        }

        _ = StopServer(acceptLoop, handlerLoop);
    }

    /// <summary>
    /// Stops the server, unless <see cref="StopAnswering"/> already began to: answers <c>closing</c> to every open request nobody
    /// has begun, waits up to 5 s for the accept loop, logs how many queued messages were dropped, waits up to 5 s for a running
    /// handler and up to 5 s for the open connections (logging a warning when a wait runs out) and releases the lock. After
    /// <see cref="StopAnswering"/>, finished or still running on another thread, it waits for both loops and the connections the
    /// same bounded way before releasing the lock. Safe to call more than once and from several threads: a later call returns
    /// once the first has released the lock. A call from inside this coordinator's message handler never waits on that handler:
    /// as the first call it still waits for the open connections before releasing the lock, and as a later one it returns at once,
    /// since the first call is waiting on that handler.
    /// </summary>
    public void Dispose()
    {
        Task? acceptLoop;
        Task? handlerLoop;
        bool alreadyDisposing;
        bool stopStartedElsewhere;
        lock (_gate)
        {
            alreadyDisposing = _disposed;
            _disposed = true;
            stopStartedElsewhere = _stoppedAnswering;
            _stoppedAnswering = true;
            acceptLoop = _acceptLoop;
            handlerLoop = _handlerLoop;
        }

        if (alreadyDisposing)
        {
            WaitForFirstDispose();
            return;
        }

        try
        {
            bool stopped = stopStartedElsewhere ? WaitForLoops(acceptLoop, handlerLoop) : StopServer(acceptLoop, handlerLoop);

            // Every server stream is closed before the lock is: on Unix the last one's close deletes the socket file, which must not
            // happen after the next main instance has made its own.
            bool connectionsEnded = WaitForConnections();
            DisposeShutdownIfDone(stopped && connectionsEnded);
        }
        finally
        {
            ReleaseLock();
        }
    }

    /// <summary>True on the thread running this coordinator's message handler, inside the handler.</summary>
    private bool IsOnOwnHandlerThread => ReferenceEquals(t_handlerRunningFor, this);

    /// <summary>
    /// Waits until the first <see cref="Dispose"/> has released the lock. A caller inside this coordinator's handler returns at
    /// once: the first caller is waiting on that handler.
    /// </summary>
    private void WaitForFirstDispose()
    {
        if (!IsOnOwnHandlerThread)
        {
            _lockReleased.Task.Wait();
        }
    }

    /// <summary>
    /// Cancels both loops, waits for the accept loop, logs the dropped-message count and waits for the handler loop, unless the
    /// caller is that handler: the loop it runs in ends once it returns. True when both loops have ended.
    /// </summary>
    private bool StopServer(Task? acceptLoop, Task? handlerLoop)
    {
        // Before any wait: no launcher waits on a request this instance will never carry out.
        AbandonOpenRequests();
        ShutdownCancelling?.Invoke();

        // The queue is never completed: a frame read just before the cancel is still queued, so the count below includes it.
        _shutdown.Cancel();
        lock (_gate)
        {
            _shutdownCancelled = true;
        }

        // The accept loop never runs user code, so it stops on its own; once it has, no frame joins the queue being counted.
        bool acceptStopped = WaitForAcceptLoop(acceptLoop);
        LogDroppedMessages();
        return WaitForHandlerLoop(handlerLoop) && acceptStopped;
    }

    /// <summary>Waits for both loops that another call is stopping, the same bounded way. True when both have ended.</summary>
    private bool WaitForLoops(Task? acceptLoop, Task? handlerLoop)
    {
        bool acceptStopped = WaitForAcceptLoop(acceptLoop);
        return WaitForHandlerLoop(handlerLoop) && acceptStopped;
    }

    /// <summary>
    /// Answers <c>closing</c> to every open request nobody has begun, and makes every request read from now on abandoned as soon
    /// as it is read. A request the app has begun keeps its connection for up to <see cref="ReplyWriteBound"/>, for its reply.
    /// </summary>
    private void AbandonOpenRequests()
    {
        InstanceRequest[] open;
        lock (_gate)
        {
            _abandoning = true;
            open = [.. _openRequests];
        }

        foreach (InstanceRequest request in open)
        {
            request.TryAbandon(InstanceReply.Closing);
        }
    }

    /// <summary>Waits up to <see cref="DisposeWait"/> for every connection task to end. True when they all have.</summary>
    private bool WaitForConnections()
    {
        Task[] running;
        lock (_gate)
        {
            running = [.. _connections];
        }

        try
        {
            if (Task.WaitAll(running, DisposeWait))
            {
                return true;
            }
        }
        catch (AggregateException)
        {
            // Every connection ended; the fault continuation set up when each started has logged its error.
            return true;
        }

        log.LogWarning(
            $"{running.Count(task => !task.IsCompleted)} instance connection(s) on pipe '{Endpoint}' did not end within "
                + $"{DisposeWait.TotalSeconds:0} s; releasing the lock without them."
        );
        return false;
    }

    private bool WaitForAcceptLoop(Task? acceptLoop) =>
        acceptLoop is null
        || WaitForLoop(
            acceptLoop,
            $"The instance server's accept loop on pipe '{Endpoint}' did not stop within {DisposeWait.TotalSeconds:0} s; "
                + "the dropped-message count may be short."
        );

    /// <summary>Waits for the handler loop; false at once on the handler's own thread, whose loop ends once it returns.</summary>
    private bool WaitForHandlerLoop(Task? handlerLoop) =>
        !IsOnOwnHandlerThread
        && (
            handlerLoop is null
            || WaitForLoop(
                handlerLoop,
                $"The instance server on pipe '{Endpoint}' did not stop within {DisposeWait.TotalSeconds:0} s; "
                    + "a message handler is probably blocked. Going on without it."
            )
        );

    /// <summary>
    /// Disposes the shutdown token once both loops have ended and its cancel has run, so a <see cref="StopAnswering"/> still on
    /// its way to the cancel never meets a disposed token.
    /// </summary>
    private void DisposeShutdownIfDone(bool loopsStopped)
    {
        bool cancelled;
        lock (_gate)
        {
            cancelled = _shutdownCancelled;
        }

        if (loopsStopped && cancelled)
        {
            _shutdown.Dispose();
        }
    }

    /// <summary>
    /// Deletes the pid file this instance wrote while it still holds the lock, releases the lock, then lets every later
    /// <see cref="Dispose"/> caller return.
    /// </summary>
    private void ReleaseLock()
    {
        try
        {
            lock (_gate)
            {
                DeleteOwnPidFile();
            }

            LockReleasing?.Invoke();
        }
        finally
        {
            try
            {
                _lockStream?.Dispose();
            }
            finally
            {
                _lockReleased.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Records this process's pid and start time beside the lock, through a temporary file replaced into place so a reader never
    /// sees half a file. A failure is logged and leaves the main instance running.
    /// </summary>
    private void WritePidFile()
    {
        string pidPath = _paths.PidPath;
        string tempPath = $"{pidPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, CurrentProcessPidText(), Utf8NoBom);
            MovePidFileWithRetry(tempPath, pidPath);
            _pidFileWritten = true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            log.LogWarning($"Could not write the main-instance pid file {pidPath}; a second launch cannot bring this window forward.", ex);
            DeleteFileLoggingFailure(tempPath, $"Could not delete the temporary pid file {tempPath}.");
        }
    }

    /// <summary>
    /// Replaces <paramref name="pidPath"/> with <paramref name="tempPath"/>, retrying a destination a reader or a virus scanner
    /// briefly holds; the last attempt's failure is thrown.
    /// </summary>
    private void MovePidFileWithRetry(string tempPath, string pidPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, pidPath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < PidMoveAttempts)
            {
                log.LogInfo($"Cannot replace the main-instance pid file {pidPath} (attempt {attempt} of {PidMoveAttempts}); retrying: {ex.Message}");
                Thread.Sleep(PidMoveRetryDelay);
            }
        }
    }

    private void DeleteOwnPidFile()
    {
        if (_pidFileWritten)
        {
            _pidFileWritten = false;
            DeleteFileLoggingFailure(
                _paths.PidPath,
                $"Could not delete the main-instance pid file {_paths.PidPath}; a second launch ignores it once this process has exited."
            );
        }
    }

    private void DeleteFileLoggingFailure(string path, string warning)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(warning, ex);
        }
    }

    /// <summary>The pid file's text: the pid and the process start time in UTC ticks, one per line, each line ending in LF.</summary>
    private static string CurrentProcessPidText()
    {
        PidTextReading.Value?.Invoke();
        using var current = Process.GetCurrentProcess();
        long startTicks = current.StartTime.ToUniversalTime().Ticks;
        return string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}\n{startTicks}\n");
    }

    /// <summary>The pid file beside the lock: the lock's file name with the extension <c>.pid</c>.</summary>
    private static string PidPathFor(string fullLockPath) => Path.ChangeExtension(fullLockPath, ".pid");

    /// <summary>
    /// The pid file's text, at most one character past <see cref="MaxPidFileLength"/>, or null (logged as info unless the file is
    /// missing) when it cannot be read. The file is opened sharing write and delete, so a reader never blocks the main instance's
    /// replace or delete.
    /// </summary>
    private static string? ReadPidFile(string pidPath, IAppLog log)
    {
        try
        {
            using FileStream stream = new(pidPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Utf8NoBom);
            char[] buffer = new char[MaxPidFileLength + 1];
            int read = reader.ReadBlock(buffer);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No pid file: no main instance is running, or one is starting or shutting down.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogInfo($"Could not read the main-instance pid file {pidPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> is at most <see cref="MaxPidFileLength"/> characters and exactly a decimal pid line and a
    /// decimal ticks line, with one trailing LF allowed.
    /// </summary>
    private static bool TryParsePidFile(string text, out int pid, out long startTicks)
    {
        pid = 0;
        startTicks = 0;
        if (text.Length > MaxPidFileLength)
        {
            return false;
        }

        string body = text.EndsWith('\n') ? text[..^1] : text;
        string[] lines = body.Split('\n');
        return lines.Length == 2
            && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks);
    }

    /// <summary>True when process <paramref name="pid"/> is running and started at <paramref name="startTicks"/>; false is logged as info.</summary>
    private static bool IsRecordedProcessRunning(string pidPath, int pid, long startTicks, IAppLog log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                log.LogInfo($"The main-instance pid file {pidPath} names process {pid}, which has exited; the file is stale.");
                return false;
            }

            if (process.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                log.LogInfo($"The main-instance pid file {pidPath} names process {pid}, which started at another time; the pid was reused.");
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            log.LogInfo($"The main-instance pid file {pidPath} names process {pid}, which is not running; the file is stale.");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            log.LogInfo($"Could not inspect process {pid} named in the main-instance pid file {pidPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The lock file's full path, the pid file beside it and the endpoint derived from the lock.</summary>
    private static (string FullLockPath, string PidPath, string Endpoint) ResolvePaths(string lockFilePath, IAppLog log)
    {
        string fullLockPath = Path.GetFullPath(lockFilePath);
        return (fullLockPath, PidPathFor(fullLockPath), InstanceEndpoint.Resolve(fullLockPath, EndpointEnvironment.ForCurrentProcess(), log));
    }

    /// <summary>
    /// Throws unless <paramref name="value"/> is <see cref="Timeout.InfiniteTimeSpan"/> or positive and at most
    /// <see cref="int.MaxValue"/> ms; the exception names the caller's own parameter and describes it.
    /// </summary>
    private static void ValidateTimeout(TimeSpan value, string name, string description)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"The {description} is Timeout.InfiniteTimeSpan or positive and at most {int.MaxValue} ms."
            );
        }
    }

    /// <summary>
    /// Opens the pipe both ways and makes one connect attempt that does not wait for a server to appear: a timeout of 0 tries once,
    /// where a longer one makes the runtime retry in a spin while the pipe does not exist, on Windows as on Unix. Null (logged) when the
    /// pipe cannot be opened at all: on Unix, a socket path longer than the platform allows; .NET reports both that and a reserved
    /// name as <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static async Task<NamedPipeClientStream?> ConnectClientAsync(string pipeName, IAppLog log, CancellationToken cancellationToken)
    {
        NamedPipeClientStream? client = null;
        bool connected = false;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, SharedPipeOptions);
            int attemptTimeoutMilliseconds = ConnectAttemptTimeoutMilliseconds;
            ConnectAttempted.Value?.Invoke(attemptTimeoutMilliseconds);
            await client.ConnectAsync(attemptTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            connected = true;
            return client;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            log.LogWarning($"Could not open pipe '{pipeName}'; a retry cannot fix this.", ex);
            return null;
        }
        finally
        {
            if (!connected && client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableConnectFailure(Exception exception) =>
        exception is TimeoutException or IOException || exception is SocketException socketException && IsRetryableSocketError(socketException);

    internal static bool IsRetryableSocketError(SocketException exception) =>
        exception.SocketErrorCode
            is SocketError.ConnectionRefused
                or SocketError.AddressNotAvailable
                or SocketError.TryAgain
                or SocketError.NotSocket;

    private static string DescribeFinalConnectFailure(string pipeName, Exception exception) =>
        exception is SocketException socketException
            ? $"Could not connect to pipe '{pipeName}' ({socketException.SocketErrorCode}); a retry cannot fix this."
            : $"Pipe '{pipeName}' belongs to another user; not sending to it.";

    private static bool IsShareViolation(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            const int ErrorSharingViolation = 32;
            const int ErrorLockViolation = 33;
            int win32Error = exception.HResult & 0xFFFF;
            return win32Error is ErrorSharingViolation or ErrorLockViolation;
        }

        // On Unix, .NET raises a failed flock(LOCK_EX | LOCK_NB) as an IOException whose HResult is the raw errno EWOULDBLOCK.
        int eWouldBlock = OperatingSystem.IsLinux() ? 11 : 35;
        return exception.HResult == eWouldBlock;
    }

    private void LogDroppedMessages()
    {
        int dropped = _queue.Reader.Count;
        if (dropped > 0)
        {
            log.LogWarning($"Dropped {dropped} instance message(s) still queued on pipe '{Endpoint}' at shutdown.");
        }
    }

    /// <summary>
    /// True when <paramref name="loop"/> ended within <see cref="DisposeWait"/>; a loop still running is logged with
    /// <paramref name="notStoppedWarning"/> and left to finish.
    /// </summary>
    private bool WaitForLoop(Task loop, string notStoppedWarning)
    {
        try
        {
            if (loop.Wait(DisposeWait))
            {
                return true;
            }

            log.LogWarning(notStoppedWarning);
            return false;
        }
        catch (AggregateException)
        {
            // The loop ended; the fault continuation set up in StartServer has logged its error.
            return true;
        }
    }

    private void LogIfFaulted(Task loop, string loopName, string consequence) =>
        _ = loop.ContinueWith(
            faulted =>
                log.LogError($"The instance server's {loopName} on pipe '{Endpoint}' stopped with an error; {consequence}.", faulted.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        ServerFailureLog failures = new(log, $"The instance server on pipe '{Endpoint}'");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan? retryDelay;
                try
                {
                    retryDelay = await ServeOneClientAsync(failures, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ArgumentOutOfRangeException ex) when (_listener is null)
                {
                    // Only a listener's creation runs while no listener is waiting. The runtime rejects the endpoint itself (on Unix,
                    // a socket path too long for the platform): a retry cannot fix it.
                    log.LogError(
                        $"The instance server cannot listen on '{Endpoint}' and has stopped; "
                            + "second launches will bring its window forward without it.",
                        ex
                    );
                    return;
                }
                catch (Exception ex)
                {
                    // Creating a listener or reading a client failed. A listener that exists is kept: disposing it could close the
                    // shared Unix socket and drop the clients in its backlog.
                    retryDelay = failures.Record(ex);
                }

                if (retryDelay is { } delay && !await BackOffAsync(delay, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            await DisposeListenerAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Accepts one client and reads its frame. Returns the delay before the next pass, or null to go on at once.</summary>
    private async Task<TimeSpan?> ServeOneClientAsync(ServerFailureLog failures, CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            try
            {
                _listener = CreateListener(firstInstance: !_firstListenerCreated);
                _firstListenerCreated = true;
            }
            catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows() && !_firstListenerCreated)
            {
                // The first listener's FirstPipeInstance flag fails when any other process already holds the name.
                return failures.RecordKnownCause(
                    ex,
                    $"The instance pipe {Endpoint} is held by another account or a previous YAMV that is still exiting; "
                        + "second launches will bring its window forward without it until it is free."
                );
            }
        }

        NamedPipeServerStream connected = _listener;
        try
        {
            await connected.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // For example a client that closed before the server took the connection (ERROR_NO_DATA on Windows).
            TimeSpan? delay = failures.RecordConnectFailure(ex);
            await ReplaceListenerAsync(connected).ConfigureAwait(false);
            return delay;
        }

        _listener = null;
        try
        {
            // On Unix every instance shares one listening socket, closed with the last instance. The next listener must exist
            // before this one is read or disposed, or the clients already queued in the socket's backlog are dropped.
            _listener = CreateListener(firstInstance: false);
        }
        catch
        {
            await connected.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        StartConnection(connected, cancellationToken);
        failures.RecordSuccess();
        return null;
    }

    /// <summary>
    /// Serves <paramref name="connected"/> on a task of its own, tracked until it ends so <see cref="Dispose"/> can wait for it,
    /// so the accept loop goes straight back to accepting.
    /// </summary>
    private void StartConnection(NamedPipeServerStream connected, CancellationToken cancellationToken)
    {
        var connection = Task.Run(() => ServeConnectionAsync(connected, cancellationToken), CancellationToken.None);
        lock (_gate)
        {
            _connections.Add(connection);
        }

        // Registered after the add, so the removal never runs first even when the task has already ended.
        _ = connection.ContinueWith(
            ended =>
            {
                lock (_gate)
                {
                    _connections.Remove(ended);
                }

                if (ended.IsFaulted)
                {
                    log.LogError($"An instance connection on pipe '{Endpoint}' stopped with an error.", ended.Exception);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>Reads the client's request, queues it, and writes its reply.</summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream connected, CancellationToken cancellationToken)
    {
        await using (connected.ConfigureAwait(false))
        {
            InstanceRequest? request = await ReadIntoQueueAsync(connected, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            try
            {
                await AnswerAsync(connected, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _openRequests.Remove(request);
                }
            }
        }
    }

    /// <summary>
    /// Waits for the request's reply while watching for the launcher's hang-up, then writes the reply, if there is one to write.
    /// </summary>
    private async Task AnswerAsync(NamedPipeServerStream connected, InstanceRequest request, CancellationToken cancellationToken)
    {
        using var stopWatching = new CancellationTokenSource();
        Task hangUp = WatchForHangUpAsync(connected, stopWatching.Token);
        try
        {
            InstanceReply? reply = await WaitForReplyAsync(request, hangUp, cancellationToken).ConfigureAwait(false);
            if (reply is { } answer)
            {
                await WriteReplyAsync(connected, answer).ConfigureAwait(false);
            }
        }
        finally
        {
            await stopWatching.CancelAsync().ConfigureAwait(false);
            await hangUp.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Completes when the launcher hangs up: a pending one-byte read ends with 0 bytes on both platforms (or an error once the
    /// pipe is broken). A launcher that sends more after its frame is misbehaving and is treated as gone.
    /// </summary>
    private static async Task WatchForHangUpAsync(NamedPipeServerStream connected, CancellationToken cancellationToken)
    {
        byte[] probe = new byte[1];
        try
        {
            _ = await connected.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A broken pipe is a hang-up too.
        }
    }

    /// <summary>
    /// The reply to write, or null when there is none: the launcher hung up (the request is abandoned), the reply bound passed
    /// (abandoned, closed without a reply) or the server stopped while the app had begun the request and did not answer within
    /// <see cref="ReplyWriteBound"/>.
    /// </summary>
    private async Task<InstanceReply?> WaitForReplyAsync(InstanceRequest request, Task hangUp, CancellationToken cancellationToken)
    {
        using var boundSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bound = Task.Delay(ReplyBound, boundSource.Token);
        Task first = await Task.WhenAny(request.Reply, hangUp, bound).ConfigureAwait(false);
        boundSource.Cancel();
        if (first == request.Reply)
        {
            return await request.Reply.ConfigureAwait(false);
        }

        if (first == hangUp)
        {
            if (request.TryAbandon(InstanceReply.Closing))
            {
                log.LogInfo($"A launcher on pipe '{Endpoint}' hung up before its request was taken; abandoned it.");
            }

            return null;
        }

        return cancellationToken.IsCancellationRequested ? await ReplyWhileStoppingAsync(request).ConfigureAwait(false) : AbandonAtBound(request);
    }

    /// <summary>
    /// The reply of a request the server is stopping under: <c>closing</c> when nobody has begun it, else the app's own reply if
    /// it comes within <see cref="ReplyWriteBound"/>.
    /// </summary>
    private async Task<InstanceReply?> ReplyWhileStoppingAsync(InstanceRequest request)
    {
        request.TryAbandon(InstanceReply.Closing);
        try
        {
            return await request.Reply.WaitAsync(ReplyWriteBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.LogWarning(
                $"A request on pipe '{Endpoint}' that the app had begun got no reply within {ReplyWriteBound.TotalSeconds:0} s of "
                    + "stopping; closing its connection without one."
            );
            return null;
        }
    }

    private InstanceReply? AbandonAtBound(InstanceRequest request)
    {
        string state = request.TryAbandon(InstanceReply.Closing) ? "abandoned it" : "the app had begun it";
        log.LogWarning(
            $"A request on pipe '{Endpoint}' got no reply within {ReplyBound.TotalSeconds:0} s; {state} and closed its connection without a reply."
        );
        return null;
    }

    /// <summary>
    /// Writes <paramref name="reply"/> within <see cref="ReplyWriteBound"/>, never on the thread that completed the request: a
    /// Windows write completes only once the launcher reads it. A launcher that is gone or never reads is logged.
    /// </summary>
    private async Task WriteReplyAsync(NamedPipeServerStream connected, InstanceReply reply)
    {
        ReplyWriting?.Invoke();
        using var writeDeadline = new CancellationTokenSource(ReplyWriteBound);
        try
        {
            await connected.WriteAsync(InstanceFrame.Encode(InstanceReplies.ToText(reply)), writeDeadline.Token).ConfigureAwait(false);
            await connected.FlushAsync(writeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            log.LogWarning(
                $"A launcher on pipe '{Endpoint}' did not read its reply '{InstanceReplies.ToText(reply)}' within "
                    + $"{ReplyWriteBound.TotalMilliseconds:0} ms; closing its connection.",
                ex
            );
        }
        catch (IOException ex)
        {
            log.LogInfo($"A launcher on pipe '{Endpoint}' hung up before its reply '{InstanceReplies.ToText(reply)}' was written: {ex.Message}");
        }
    }

    /// <summary>
    /// Swaps a listener whose wait failed for a fresh one, created before the failed one is disposed so the shared Unix socket and
    /// its backlog stay open.
    /// </summary>
    private async Task ReplaceListenerAsync(NamedPipeServerStream failed)
    {
        _listener = null;
        try
        {
            _listener = CreateListener(firstInstance: false);
        }
        finally
        {
            await failed.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A duplex server instance with the default zero-size buffers: a Windows client's write then completes only once the server has
    /// read it, so a client cannot close before its frame is read and lose it, and a reply's write completes only once the client
    /// has read it. On Windows the first instance (the server holds no
    /// other) is created with <see cref="PipeOptions.FirstPipeInstance"/>, so a name another process already holds fails loudly
    /// instead of sharing it. Never on Unix: there the flag skips the unlink of a crashed main's stale socket file.
    /// </summary>
    private NamedPipeServerStream CreateListener(bool firstInstance)
    {
        ListenerCreating?.Invoke(firstInstance);
        PipeOptions options = firstInstance && OperatingSystem.IsWindows() ? SharedPipeOptions | PipeOptions.FirstPipeInstance : SharedPipeOptions;
        return new(Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

    private async Task DisposeListenerAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }

    /// <summary>
    /// Reads one frame and queues it for the handler loop, so reading never waits on a busy handler. The queued request, or null
    /// when the frame was malformed or not read in time (logged).
    /// </summary>
    private async Task<InstanceRequest?> ReadIntoQueueAsync(NamedPipeServerStream connected, CancellationToken cancellationToken)
    {
        string message;
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readDeadline.CancelAfter(FrameReadDeadline);
        try
        {
            message = await InstanceFrame.ReadAsync(connected, readDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(
                $"Dropped a malformed instance message on pipe '{Endpoint}': no complete frame within {FrameReadDeadline.TotalSeconds:0} s.",
                ex
            );
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or IOException)
        {
            log.LogWarning($"Dropped a malformed instance message on pipe '{Endpoint}'.", ex);
            return null;
        }

        InstanceRequest request = new(message);
        TrackRequest(request);
        bool queued = _queue.Writer.TryWrite(request);
        Debug.Assert(queued, "An unbounded channel that is never completed accepts every write.");
        FrameQueued?.Invoke(Interlocked.Increment(ref _framesQueued));
        return request;
    }

    /// <summary>Records a request, abandoning it at once when the server has begun stopping.</summary>
    private void TrackRequest(InstanceRequest request)
    {
        bool stopping;
        lock (_gate)
        {
            _openRequests.Add(request);
            stopping = _abandoning;
        }

        if (stopping)
        {
            request.TryAbandon(InstanceReply.Closing);
        }
    }

    /// <summary>Runs <paramref name="onRequest"/> for each queued request, one at a time, until shutdown.</summary>
    private async Task RunHandlerLoopAsync(Action<InstanceRequest> onRequest, CancellationToken cancellationToken)
    {
        ChannelReader<InstanceRequest> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!cancellationToken.IsCancellationRequested && reader.TryRead(out InstanceRequest? request))
                {
                    InvokeHandler(onRequest, request);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is the loop's normal end; Dispose reports any messages still queued.
            return;
        }
    }

    private void InvokeHandler(Action<InstanceRequest> onRequest, InstanceRequest request)
    {
        InstanceCoordinator? outer = t_handlerRunningFor;
        t_handlerRunningFor = this;
        try
        {
            onRequest(request);
        }
        catch (Exception ex)
        {
            log.LogError($"The handler for instance message '{request.Message}' threw; the server keeps serving.", ex);

            // A request nobody has begun will not be: its launcher hears so at once instead of at its deadline.
            request.TryAbandon(InstanceReply.Refused);
        }
        finally
        {
            t_handlerRunningFor = outer;
        }
    }

    /// <summary>Waits <paramref name="delay"/> before the loop retries. False when shutdown cut the wait short.</summary>
    private static async Task<bool> BackOffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>What one try for the main-instance lock found.</summary>
public enum MainLockAttempt
{
    /// <summary>This process now holds the lock.</summary>
    Acquired,

    /// <summary>Another process holds the lock; it may release it, so a retry can succeed.</summary>
    HeldByAnother,

    /// <summary>The lock file cannot be opened at all; a retry cannot fix that.</summary>
    CannotOpen,
}
