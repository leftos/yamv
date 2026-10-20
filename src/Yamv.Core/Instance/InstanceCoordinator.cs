using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Instance;

/// <summary>
/// Keeps YAMV to one main instance per user: the main instance holds an exclusive lock on <paramref name="lockFilePath"/>
/// and listens on the per-user pipe <paramref name="pipeName"/>; a later no-arg launch fails to take the lock and signals it.
/// </summary>
/// <param name="lockFilePath">The lock file, normally <see cref="AppPaths.MainLockFile"/>. Its folder is created on demand.</param>
/// <param name="pipeName">The pipe name, normally <see cref="DefaultPipeName"/>; at most 16 characters so the macOS socket path fits.</param>
/// <param name="log">Where every caught exception is reported.</param>
public sealed class InstanceCoordinator(string lockFilePath, string pipeName, IAppLog log) : IDisposable
{
    private const int ConnectAttemptMilliseconds = 250;

    private const PipeOptions SharedPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static readonly TimeSpan ServerErrorBackoff = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private FileStream? _lockStream;
    private Task? _serverLoop;
    private bool _disposed;

    /// <summary>
    /// <c>yamv-</c> plus the first 8 lower-case hex characters of SHA-256 of the user name: unique per user, 13 characters long.
    /// </summary>
    public static string DefaultPipeName { get; } =
        "yamv-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..8];

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
    /// Starts accepting messages on a background task. <paramref name="onMessage"/> runs on a thread-pool thread, one message at a
    /// time; an exception it throws is logged and the server keeps serving.
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

            if (_serverLoop is not null)
            {
                throw new InvalidOperationException("The instance server is already running.");
            }

            CancellationToken token = _shutdown.Token;
            _serverLoop = Task.Run(() => RunServerLoopAsync(onMessage, token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the main instance listening on <paramref name="pipeName"/>, retrying the connect until
    /// <paramref name="timeout"/> so a main instance that is still starting gets it. True once written; false (logged as a warning)
    /// when no server answered in time or the pipe belongs to another user.
    /// </summary>
    /// <exception cref="ArgumentException">The message encodes to more than 64 KiB.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<bool> SignalAsync(
        string pipeName,
        string message,
        TimeSpan timeout,
        IAppLog log,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(log);
        byte[] frame = InstanceFrame.Encode(message);

        long start = Stopwatch.GetTimestamp();
        Exception? lastFailure = null;
        while (true)
        {
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                log.LogWarning($"No main instance answered on pipe '{pipeName}' within {timeout.TotalMilliseconds:0} ms.", lastFailure);
                return false;
            }

            NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, SharedPipeOptions);
            await using (client.ConfigureAwait(false))
            {
                int attemptMilliseconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, ConnectAttemptMilliseconds);
                try
                {
                    await client.ConnectAsync(attemptMilliseconds, cancellationToken).ConfigureAwait(false);
                    await client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    await client.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    log.LogWarning($"Pipe '{pipeName}' belongs to another user; not signalling it.", ex);
                    return false;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    lastFailure = ex;
                }
            }
        }
    }

    /// <summary>Stops the server, waits for its loop to end and releases the lock. Safe to call more than once.</summary>
    public void Dispose()
    {
        Task? serverLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            serverLoop = _serverLoop;
        }

        _shutdown.Cancel();
        serverLoop?.GetAwaiter().GetResult();
        _shutdown.Dispose();
        _lockStream?.Dispose();
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

    private async Task RunServerLoopAsync(Action<string> onMessage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneConnectionAsync(onMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError($"The instance server on pipe '{pipeName}' failed; it keeps accepting.", ex);
                if (!await BackOffAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    private async Task ServeOneConnectionAsync(Action<string> onMessage, CancellationToken cancellationToken)
    {
        NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            SharedPipeOptions
        );
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            string message;
            try
            {
                message = await InstanceFrame.ReadAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or IOException)
            {
                log.LogWarning($"Dropped a malformed instance message on pipe '{pipeName}'.", ex);
                return;
            }

            InvokeHandler(onMessage, message);
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

    /// <summary>Waits briefly after a server failure so a persistent one does not spin. False when shutdown cut the wait short.</summary>
    private static async Task<bool> BackOffAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ServerErrorBackoff, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
