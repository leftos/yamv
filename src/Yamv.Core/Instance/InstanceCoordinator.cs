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
/// and listens on the pipe <see cref="Endpoint"/> derived from that path; a later no-arg launch fails to take the lock and signals
/// it. On Unix the lock is an advisory <c>flock</c>, which .NET turns off silently when <c>System.IO.DisableFileLocking</c> is set
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

    /// <summary>Messages read from clients, waiting for the handler loop: the accept loop never waits on a busy handler.</summary>
    /// <remarks>Default options: the single-reader unbounded channel cannot report <c>Count</c>, which Dispose needs.</remarks>
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

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

    /// <summary>Frames queued since the server started; touched only by the accept loop.</summary>
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
    /// How long <see cref="Dispose"/> waits for the accept loop, and then for a running handler, before it goes on; 5 s each.
    /// Lets tests turn a wrong wait into a hang their guard catches instead of a race with these bounds.
    /// </summary>
    internal TimeSpan DisposeWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Called on the accept loop before each listener is created, with whether it is created as the server's first instance; set
    /// it before <see cref="StartServer"/>. An exception it throws fails that creation, so tests can stage a failed listener.
    /// </summary>
    internal Action<bool>? ListenerCreating { get; set; }

    /// <summary>
    /// Called before each connect attempt <see cref="SignalAsync"/> makes, with the connect timeout in milliseconds, in the async
    /// flow that set it. Lets tests check how a signal paces its attempts without measuring CPU time.
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
    /// Starts accepting messages on background tasks. A listener is always waiting: the next one is created as soon as a client
    /// connects, before that client's frame is read. Each frame is read at once and queued; <paramref name="onMessage"/> drains the
    /// queue on a thread-pool thread, one message at a time, and an exception it throws is logged while the server keeps serving.
    /// <see cref="Dispose"/> waits for a running <paramref name="onMessage"/>, so it must post work to the thread that disposes this
    /// coordinator, never block on it. Calling <see cref="Dispose"/> from inside <paramref name="onMessage"/> returns at once: as the
    /// first call it releases the lock without waiting on the handler it runs in, and as a later call it leaves the release to the
    /// first.
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

            if (_stoppedAnswering)
            {
                throw new InvalidOperationException("The instance server cannot start after StopAnswering.");
            }

            CancellationToken token = _shutdown.Token;
            _acceptLoop = Task.Run(() => RunServerLoopAsync(token), CancellationToken.None);
            _handlerLoop = Task.Run(() => RunHandlerLoopAsync(onMessage, token), CancellationToken.None);
            LogIfFaulted(_acceptLoop, "accept loop", "it accepts no more messages");
            LogIfFaulted(_handlerLoop, "handler loop", "queued messages are no longer handled");
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the main instance holding the lock at <paramref name="lockFilePath"/>, on the pipe
    /// derived from that path (see <see cref="Endpoint"/>), retrying the connect every 50 ms so a main instance that is still
    /// starting gets it; <paramref name="timeout"/> bounds the whole wait, the write included, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a bound.
    /// On Unix a file at the endpoint that is not a socket is retried the same way, since the main instance's server replaces
    /// whatever it finds there when it starts; one that is never replaced makes the signal wait out its timeout.
    /// Returns true once the frame was delivered: on Windows the server has read it; on Unix it is in the connected socket's send
    /// buffer, so a main instance that exits before reading it loses it. Either way it may not have been handled yet. False (logged
    /// as a warning) when no server answered in time, the server did not read the frame in time, the pipe belongs to another user,
    /// or opening or connecting the pipe failed in a way a retry cannot fix.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="lockFilePath"/> is empty or not a valid path, or the message encodes to more than 64 KiB.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is neither <see cref="Timeout.InfiniteTimeSpan"/> nor positive and at most <see cref="int.MaxValue"/> ms.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<bool> SignalAsync(
        string lockFilePath,
        string message,
        TimeSpan timeout,
        IAppLog log,
        CancellationToken cancellationToken = default
    )
    {
        ValidateTimeout(timeout);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(log);
        byte[] frame = InstanceFrame.Encode(message);
        string pipeName = InstanceEndpoint.Resolve(lockFilePath, EndpointEnvironment.ForCurrentProcess(), log);

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
                await PauseBeforeRetryAsync(Remaining(timeout, start), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SocketException)
            {
                log.LogWarning(DescribeFinalConnectFailure(pipeName, ex), ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Stops answering second launches while keeping the lock: deletes the pid file first, then stops the server as
    /// <see cref="Dispose"/> does, waiting the same bounded way. A launch from then on fails its signal, finds no pid and retries
    /// the lock for a bounded time, so it becomes the main instance once this one has released the lock. The main instance calls
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
    /// Stops the server, unless <see cref="StopAnswering"/> already began to: waits up to 5 s for the accept loop, logs how many
    /// queued messages were dropped, waits up to 5 s for a running handler (logging a warning when either wait runs out) and
    /// releases the lock. After <see cref="StopAnswering"/>, finished or still running on another thread, it waits for both loops
    /// the same bounded way before releasing the lock. Safe to call more than once and from several threads: a later call returns
    /// once the first has released the lock. A call from inside this coordinator's message handler returns at once: as the first
    /// call it skips the wait on its own handler, and as a later one the first call is waiting on that handler.
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
            DisposeShutdownIfDone(stopped);
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
    /// The pause before the next connect attempt: <see cref="ConnectRetryDelay"/>, or what is left of the timeout when that is
    /// shorter, rounded up to whole milliseconds and at least 1 ms, so every retry waits on a timer and a launch with no server to
    /// reach never spins. <see cref="Task.Delay(TimeSpan)"/> counts whole milliseconds, so a shorter pause would not wait at all.
    /// </summary>
    internal static TimeSpan ConnectRetryPause(TimeSpan remaining)
    {
        if (remaining == Timeout.InfiniteTimeSpan || remaining >= ConnectRetryDelay)
        {
            return ConnectRetryDelay;
        }

        return TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)));
    }

    private static Task PauseBeforeRetryAsync(TimeSpan remaining, CancellationToken cancellationToken) =>
        Task.Delay(ConnectRetryPause(remaining), cancellationToken);

    /// <summary>
    /// Makes one connect attempt and writes <paramref name="frame"/> within <paramref name="remaining"/>. False (logged) when the
    /// pipe cannot be opened at all; an <see cref="OperationCanceledException"/> while <paramref name="cancellationToken"/> is not
    /// cancelled means the write ran out of time.
    /// </summary>
    private static async Task<bool> SendOnceAsync(string pipeName, byte[] frame, TimeSpan remaining, IAppLog log, CancellationToken cancellationToken)
    {
        long attemptStart = Stopwatch.GetTimestamp();
        NamedPipeClientStream? client = await ConnectClientAsync(pipeName, log, cancellationToken).ConfigureAwait(false);
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
    /// Opens the pipe and makes one connect attempt that does not wait for a server to appear: a timeout of 0 tries once, where a
    /// longer one makes the runtime retry in a spin while the pipe does not exist, on Windows as on Unix. Null (logged) when the
    /// pipe cannot be opened at all: on Unix, a socket path longer than the platform allows; .NET reports both that and a reserved
    /// name as <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static async Task<NamedPipeClientStream?> ConnectClientAsync(string pipeName, IAppLog log, CancellationToken cancellationToken)
    {
        NamedPipeClientStream? client = null;
        bool connected = false;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, SharedPipeOptions);
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
            : $"Pipe '{pipeName}' belongs to another user; not signalling it.";

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
        await using (connected.ConfigureAwait(false))
        {
            // On Unix every instance shares one listening socket, closed with the last instance. The next listener must exist
            // before this one is read or disposed, or the clients already queued in the socket's backlog are dropped.
            _listener = CreateListener(firstInstance: false);
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
            _listener = CreateListener(firstInstance: false);
        }
        finally
        {
            await failed.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A server instance with the default zero-size input buffer: a Windows client's write then completes only once the server has
    /// read it, so a client cannot close before its frame is read and lose it. On Windows the first instance (the server holds no
    /// other) is created with <see cref="PipeOptions.FirstPipeInstance"/>, so a name another process already holds fails loudly
    /// instead of sharing it. Never on Unix: there the flag skips the unlink of a crashed main's stale socket file.
    /// </summary>
    private NamedPipeServerStream CreateListener(bool firstInstance)
    {
        ListenerCreating?.Invoke(firstInstance);
        PipeOptions options = firstInstance && OperatingSystem.IsWindows() ? SharedPipeOptions | PipeOptions.FirstPipeInstance : SharedPipeOptions;
        return new(Endpoint, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

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
                $"Dropped a malformed instance message on pipe '{Endpoint}': no complete frame within {FrameReadDeadline.TotalSeconds:0} s.",
                ex
            );
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or IOException)
        {
            log.LogWarning($"Dropped a malformed instance message on pipe '{Endpoint}'.", ex);
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
        InstanceCoordinator? outer = t_handlerRunningFor;
        t_handlerRunningFor = this;
        try
        {
            onMessage(message);
        }
        catch (Exception ex)
        {
            log.LogError($"The handler for instance message '{message}' threw; the server keeps serving.", ex);
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
