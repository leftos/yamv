using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Instance;

/// <summary>
/// Keeps YAMV to one main instance per user: the main instance holds an exclusive lock on <paramref name="lockFilePath"/>
/// and listens on the per-user pipe <paramref name="pipeName"/>; a later no-arg launch fails to take the lock and signals it.
/// On Unix the lock is an advisory <c>flock</c>, which .NET turns off silently when <c>System.IO.DisableFileLocking</c> is set
/// or the file system does not support it (NFS, SMB).
/// </summary>
/// <param name="lockFilePath">The lock file, normally <see cref="AppPaths.MainLockFile"/>. Its folder is created on demand.</param>
/// <param name="pipeName">
/// The pipe name, normally <see cref="DefaultPipeName"/>; at most <see cref="MaxPipeNameLength"/> characters so the macOS socket path fits.
/// </param>
/// <param name="log">Where every caught exception is reported.</param>
/// <exception cref="ArgumentException">
/// <paramref name="pipeName"/> is empty, longer than <see cref="MaxPipeNameLength"/>, or holds a character other than an ASCII
/// letter, digit, <c>-</c> or <c>_</c>.
/// </exception>
public sealed class InstanceCoordinator(string lockFilePath, string pipeName, IAppLog log) : IDisposable
{
    /// <summary>The longest pipe name accepted: on macOS the socket path <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c> is limited to about 104 bytes.</summary>
    public const int MaxPipeNameLength = 16;

    private const int ConnectAttemptMilliseconds = 250;

    private const PipeOptions SharedPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    private readonly string _pipeName = ValidatePipeName(pipeName, nameof(pipeName));
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Messages read from clients, waiting for the handler loop: the accept loop never waits on a busy handler.</summary>
    /// <remarks>Default options: the single-reader unbounded channel cannot report <c>Count</c>, which Dispose needs.</remarks>
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    private FileStream? _lockStream;

    /// <summary>The loop that accepts clients and queues their frames, once <see cref="StartServer"/> has run.</summary>
    private Task? _acceptLoop;

    /// <summary>The loop that runs the message handler on the queued frames, once <see cref="StartServer"/> has run.</summary>
    private Task? _handlerLoop;
    private bool _disposed;

    /// <summary>The server instance waiting for the next client; touched only by the accept loop.</summary>
    private NamedPipeServerStream? _listener;

    /// <summary>Frames queued since the server started; touched only by the accept loop.</summary>
    private int _framesQueued;

    /// <summary>
    /// <c>yamv-</c> plus the first 8 lower-case hex characters of SHA-256 of the user name: unique per user, 13 characters long.
    /// </summary>
    public static string DefaultPipeName { get; } =
        "yamv-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..8];

    /// <summary>
    /// Called on the accept loop after each frame is queued, with the number queued since the server started; set it before
    /// <see cref="StartServer"/>. Lets tests wait for frames whose Unix client reported delivery before the server read them.
    /// </summary>
    internal Action<int>? FrameQueued { get; set; }

    /// <summary>
    /// How long the server waits for a connected client's whole frame before dropping it; 30 s. Set it before
    /// <see cref="StartServer"/>. Lets tests hold a client mid-frame through a stall on a loaded machine.
    /// </summary>
    internal TimeSpan FrameReadDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes the main-instance lock, held until <see cref="Dispose"/> or the process ends. True when this process now holds it
    /// (or already did); false when another process holds it or the lock file cannot be opened.
    /// </summary>
    public bool TryAcquireMainLock()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lockStream is not null)
            {
                return true;
            }

            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(lockFilePath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException ex) when (IsShareViolation(ex))
            {
                log.LogInfo($"Another process holds the main-instance lock {lockFilePath}, so this one is not the main instance: {ex.Message}");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError($"Could not open the main-instance lock {lockFilePath}; check that its folder is writable.", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Starts accepting messages on background tasks. A listener is always waiting: the next one is created as soon as a client
    /// connects, before that client's frame is read. Each frame is read at once and queued; <paramref name="onMessage"/> drains the
    /// queue on a thread-pool thread, one message at a time, and an exception it throws is logged while the server keeps serving.
    /// <see cref="Dispose"/> waits for a running <paramref name="onMessage"/>, so it must post work to the thread that disposes this
    /// coordinator, never block on it. Calling <see cref="Dispose"/> from inside <paramref name="onMessage"/> waits out its own 5 s bound.
    /// </summary>
    /// <exception cref="InvalidOperationException">This coordinator does not hold the lock, or the server is already running.</exception>
    public void StartServer(Action<string> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
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

            CancellationToken token = _shutdown.Token;
            _acceptLoop = Task.Run(() => RunServerLoopAsync(token), CancellationToken.None);
            _handlerLoop = Task.Run(() => RunHandlerLoopAsync(onMessage, token), CancellationToken.None);
            LogIfFaulted(_acceptLoop, "accept loop", "it accepts no more messages");
            LogIfFaulted(_handlerLoop, "handler loop", "queued messages are no longer handled");
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the main instance listening on <paramref name="pipeName"/>, retrying the connect so a
    /// main instance that is still starting gets it; <paramref name="timeout"/> bounds the whole wait, the write included, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a bound.
    /// Returns true once the frame was delivered: on Windows the server has read it; on Unix it is in the connected socket's send
    /// buffer, so a main instance that exits before reading it loses it. Either way it may not have been handled yet. False (logged
    /// as a warning) when no server answered in time, the server did not read the frame in time, the pipe belongs to another user,
    /// or opening or connecting the pipe failed in a way a retry cannot fix.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="pipeName"/> is empty, longer than <see cref="MaxPipeNameLength"/> or holds a character other than an ASCII
    /// letter, digit, <c>-</c> or <c>_</c>; or the message encodes to more than 64 KiB.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is neither <see cref="Timeout.InfiniteTimeSpan"/> nor positive and at most <see cref="int.MaxValue"/> ms.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<bool> SignalAsync(
        string pipeName,
        string message,
        TimeSpan timeout,
        IAppLog log,
        CancellationToken cancellationToken = default
    )
    {
        ValidatePipeName(pipeName, nameof(pipeName));
        ValidateTimeout(timeout);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(log);
        byte[] frame = InstanceFrame.Encode(message);

        long start = Stopwatch.GetTimestamp();
        Exception? lastFailure = null;
        while (true)
        {
            TimeSpan remaining = Remaining(timeout, start);
            if (remaining <= TimeSpan.Zero && remaining != Timeout.InfiniteTimeSpan)
            {
                log.LogWarning($"No main instance answered on pipe '{pipeName}' within {timeout.TotalMilliseconds:0} ms.", lastFailure);
                return false;
            }

            try
            {
                return await SendOnceAsync(pipeName, frame, remaining, log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(
                    $"The main instance on pipe '{pipeName}' did not read the message within {timeout.TotalMilliseconds:0} ms; "
                        + "it may be busy reading another client.",
                    ex
                );
                return false;
            }
            catch (Exception ex) when (IsRetryableConnectFailure(ex))
            {
                lastFailure = ex;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SocketException)
            {
                log.LogWarning(DescribeFinalConnectFailure(pipeName, ex), ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the server: waits up to 5 s for the accept loop, logs how many queued messages were dropped, waits up to 5 s for a
    /// running handler (logging a warning when either wait runs out) and releases the lock. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        Task? acceptLoop;
        Task? handlerLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            acceptLoop = _acceptLoop;
            handlerLoop = _handlerLoop;
        }

        try
        {
            // The queue is never completed: a frame read just before the cancel is still queued, so the count below includes it.
            _shutdown.Cancel();

            // The accept loop never runs user code, so it stops on its own; once it has, no frame joins the queue being counted.
            bool acceptStopped =
                acceptLoop is null
                || WaitForLoop(
                    acceptLoop,
                    $"The instance server's accept loop on pipe '{_pipeName}' did not stop within {DisposeWait.TotalSeconds:0} s; the dropped-message count may be short."
                );
            LogDroppedMessages();
            bool handlerStopped =
                handlerLoop is null
                || WaitForLoop(
                    handlerLoop,
                    $"The instance server on pipe '{_pipeName}' did not stop within {DisposeWait.TotalSeconds:0} s; "
                        + "a message handler is probably blocked. Releasing the lock anyway."
                );
            if (acceptStopped && handlerStopped)
            {
                _shutdown.Dispose();
            }
        }
        finally
        {
            _lockStream?.Dispose();
        }
    }

    private static string ValidatePipeName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, paramName);
        if (name.Length > MaxPipeNameLength)
        {
            throw new ArgumentException(
                $"A pipe name is at most {MaxPipeNameLength} characters so the macOS socket path fits; '{name}' has {name.Length}.",
                paramName
            );
        }

        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException(
                $"A pipe name holds only ASCII letters, digits, '-' and '_', so it is a valid file name on every platform; '{name}' does not.",
                paramName
            );
        }

        return name;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"The signal timeout is Timeout.InfiniteTimeSpan or positive and at most {int.MaxValue} ms."
            );
        }
    }

    /// <summary>What is left of <paramref name="timeout"/> since <paramref name="start"/>; an infinite timeout stays infinite.</summary>
    private static TimeSpan Remaining(TimeSpan timeout, long start) =>
        timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : timeout - Stopwatch.GetElapsedTime(start);

    /// <summary>
    /// Connects within <paramref name="remaining"/> (one attempt of at most 250 ms) and writes <paramref name="frame"/> in what is
    /// left of it. False (logged) when the pipe cannot be opened at all; an <see cref="OperationCanceledException"/> while
    /// <paramref name="cancellationToken"/> is not cancelled means the write ran out of time.
    /// </summary>
    private static async Task<bool> SendOnceAsync(string pipeName, byte[] frame, TimeSpan remaining, IAppLog log, CancellationToken cancellationToken)
    {
        long attemptStart = Stopwatch.GetTimestamp();
        int attemptMilliseconds =
            remaining == Timeout.InfiniteTimeSpan
                ? ConnectAttemptMilliseconds
                : (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, ConnectAttemptMilliseconds);
        NamedPipeClientStream? client = await ConnectClientAsync(pipeName, attemptMilliseconds, log, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return false;
        }

        await using (client.ConfigureAwait(false))
        {
            // On Windows the write waits until the server reads it, which a server busy with another client may not do in time.
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TimeSpan writeBudget = Remaining(remaining, attemptStart);
            writeDeadline.CancelAfter(writeBudget > TimeSpan.Zero || writeBudget == Timeout.InfiniteTimeSpan ? writeBudget : TimeSpan.Zero);
            await client.WriteAsync(frame, writeDeadline.Token).ConfigureAwait(false);
            await client.FlushAsync(writeDeadline.Token).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Opens the pipe and makes one connect attempt. Null (logged) when the pipe cannot be opened at all: on Unix, a socket path
    /// longer than the platform allows; .NET reports both that and a reserved name as <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static async Task<NamedPipeClientStream?> ConnectClientAsync(
        string pipeName,
        int attemptMilliseconds,
        IAppLog log,
        CancellationToken cancellationToken
    )
    {
        NamedPipeClientStream? client = null;
        bool connected = false;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, SharedPipeOptions);
            await client.ConnectAsync(attemptMilliseconds, cancellationToken).ConfigureAwait(false);
            connected = true;
            return client;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            log.LogWarning($"Could not open pipe '{pipeName}'{DescribeSocketPath(pipeName)}; a retry cannot fix this.", ex);
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

    private static bool IsRetryableSocketError(SocketException exception) =>
        exception.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.TryAgain;

    private static string DescribeFinalConnectFailure(string pipeName, Exception exception) =>
        exception is SocketException socketException
            ? $"Could not connect to pipe '{pipeName}' ({socketException.SocketErrorCode}); a retry cannot fix this."
            : $"Pipe '{pipeName}' belongs to another user; not signalling it.";

    /// <summary>On Unix, the length of the socket path .NET builds for the pipe, which the platform limits to about 104 bytes.</summary>
    private static string DescribeSocketPath(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return "";
        }

        string socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        return $" (its socket path {socketPath} is {socketPath.Length} characters; a shorter TMPDIR fixes this)";
    }

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
            log.LogWarning($"Dropped {dropped} instance message(s) still queued on pipe '{_pipeName}' at shutdown.");
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
                log.LogError($"The instance server's {loopName} on pipe '{_pipeName}' stopped with an error; {consequence}.", faulted.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        ServerFailureLog failures = new(log, $"The instance server on pipe '{_pipeName}'");
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
        _listener ??= CreateListener();
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
        await using (connected.ConfigureAwait(false))
        {
            // On Unix every instance shares one listening socket, closed with the last instance. The next listener must exist
            // before this one is read or disposed, or the clients already queued in the socket's backlog are dropped.
            _listener = CreateListener();
            await ReadIntoQueueAsync(connected, cancellationToken).ConfigureAwait(false);
        }

        failures.RecordSuccess();
        return null;
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
            _listener = CreateListener();
        }
        finally
        {
            await failed.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A server instance with the default zero-size input buffer: a Windows client's write then completes only once the server has
    /// read it, so a client cannot close before its frame is read and lose it.
    /// </summary>
    private NamedPipeServerStream CreateListener() =>
        new(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, SharedPipeOptions);

    private async Task DisposeListenerAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }

    /// <summary>Reads one frame and queues it for the handler loop, so reading never waits on a busy handler.</summary>
    private async Task ReadIntoQueueAsync(NamedPipeServerStream connected, CancellationToken cancellationToken)
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
                $"Dropped a malformed instance message on pipe '{_pipeName}': no complete frame within {FrameReadDeadline.TotalSeconds:0} s.",
                ex
            );
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or IOException)
        {
            log.LogWarning($"Dropped a malformed instance message on pipe '{_pipeName}'.", ex);
            return;
        }

        bool queued = _queue.Writer.TryWrite(message);
        Debug.Assert(queued, "An unbounded channel that is never completed accepts every write.");
        _framesQueued++;
        FrameQueued?.Invoke(_framesQueued);
    }

    /// <summary>Runs <paramref name="onMessage"/> for each queued message, one at a time, until shutdown.</summary>
    private async Task RunHandlerLoopAsync(Action<string> onMessage, CancellationToken cancellationToken)
    {
        ChannelReader<string> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!cancellationToken.IsCancellationRequested && reader.TryRead(out string? message))
                {
                    InvokeHandler(onMessage, message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is the loop's normal end; Dispose reports any messages still queued.
            return;
        }
    }

    private void InvokeHandler(Action<string> onMessage, string message)
    {
        try
        {
            onMessage(message);
        }
        catch (Exception ex)
        {
            log.LogError($"The handler for instance message '{message}' threw; the server keeps serving.", ex);
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
