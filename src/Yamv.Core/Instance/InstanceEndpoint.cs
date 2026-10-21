using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Instance;

/// <summary>The platform rules <see cref="InstanceEndpoint.Resolve"/> follows.</summary>
internal enum EndpointPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>What <see cref="InstanceEndpoint.Resolve"/> reads from the system, so tests can supply every platform's answers.</summary>
/// <param name="Platform">Which platform's rules apply.</param>
/// <param name="UserSid">The current account's SID on Windows; null or a throw when it cannot be read.</param>
/// <param name="DarwinCacheDir">The per-user Darwin cache folder on macOS; null when it is unavailable.</param>
/// <param name="SocketPathFits">True when a Unix socket path fits the platform's <c>sun_path</c>.</param>
internal sealed record EndpointEnvironment(
    EndpointPlatform Platform,
    Func<string?> UserSid,
    Func<string?> DarwinCacheDir,
    Func<string, bool> SocketPathFits
)
{
    /// <summary>The answers of the running process.</summary>
    public static EndpointEnvironment ForCurrentProcess() =>
        new(CurrentPlatform(), InstanceEndpoint.CurrentUserSid, InstanceEndpoint.DarwinUserCacheDir, InstanceEndpoint.SocketPathFits);

    private static EndpointPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return EndpointPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? EndpointPlatform.MacOS : EndpointPlatform.Linux;
    }
}

/// <summary>
/// Derives the instance pipe from the main-instance lock file's path, so the lock and the pipe always share one scope: on Windows
/// a pipe name hashed from the account SID and the lock path, on macOS and Linux the socket <c>main.sock</c> beside the lock.
/// </summary>
internal static partial class InstanceEndpoint
{
    private const string Prefix = "yamv-";
    private const string SocketFileName = "main.sock";

    /// <summary><c>_CS_DARWIN_USER_CACHE_DIR</c> from the macOS SDK's <c>unistd.h</c>.</summary>
    private const int CsDarwinUserCacheDir = 65538;

    /// <summary>
    /// The pipe name or socket path for the lock at <paramref name="lockFilePath"/>. Windows: <c>yamv-</c> plus 16 hex characters of
    /// SHA-256 over the SID and the full lock path. macOS and Linux: <c>main.sock</c> beside the lock when the path fits; otherwise,
    /// on macOS, a socket named from the lock path in the user cache folder, and failing that (or on Linux) a name the runtime
    /// puts under <c>$TMPDIR</c>. Every fallback is logged.
    /// </summary>
    public static string Resolve(string lockFilePath, EndpointEnvironment environment, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);
        string fullLockPath = Path.GetFullPath(lockFilePath);
        return environment.Platform == EndpointPlatform.Windows
            ? WindowsPipeName(fullLockPath, environment.UserSid, log)
            : UnixSocketPath(fullLockPath, environment, log);
    }

    /// <summary>The current account's SID, or null off Windows or when the process token has none.</summary>
    internal static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    /// <summary>The per-user Darwin cache folder from <c>confstr</c>, or null off macOS or when it is unavailable.</summary>
    internal static string? DarwinUserCacheDir()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        byte[] buffer = new byte[1024];
        nuint length = Confstr(CsDarwinUserCacheDir, buffer, (nuint)buffer.Length);

        // The length counts the terminating NUL; zero means the name is unknown, more than the buffer means it was cut short.
        if (length <= 1 || length > (nuint)buffer.Length)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
    }

    /// <summary>True when <paramref name="path"/> fits a Unix socket address: its UTF-8 bytes and a NUL within <c>sun_path</c>.</summary>
    internal static bool SocketPathFits(string path)
    {
        try
        {
            _ = new UnixDomainSocketEndPoint(path);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // The runtime's own length check is the answer; the caller logs the fallback it takes.
            return false;
        }
    }

    private static string WindowsPipeName(string fullLockPath, Func<string?> userSid, IAppLog log)
    {
        string? sid = null;
        Exception? failure = null;
        try
        {
            sid = userSid();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        if (string.IsNullOrEmpty(sid))
        {
            log.LogWarning(
                $"Could not read the account SID, so the instance pipe name is derived from the user name '{Environment.UserName}'; "
                    + "another account with the same name could block second launches from reaching this one.",
                failure
            );
            sid = Environment.UserName;
        }

        return Prefix + Hex(sid + "\n" + fullLockPath, 16);
    }

    private static string UnixSocketPath(string fullLockPath, EndpointEnvironment environment, IAppLog log)
    {
        string besideLock = Path.Combine(Path.GetDirectoryName(fullLockPath) ?? fullLockPath, SocketFileName);
        if (environment.SocketPathFits(besideLock))
        {
            return besideLock;
        }

        if (environment.Platform == EndpointPlatform.MacOS)
        {
            return DarwinCacheSocketPath(fullLockPath, besideLock, environment, log);
        }

        string relative = RelativeName(fullLockPath);
        log.LogWarning(
            $"The instance socket path {besideLock} is too long for this platform, so the instance pipe is '{relative}' in the temp "
                + "folder; a launch with a different TMPDIR cannot reach it."
        );
        return relative;
    }

    private static string DarwinCacheSocketPath(string fullLockPath, string besideLock, EndpointEnvironment environment, IAppLog log)
    {
        string? cacheDir = null;
        Exception? failure = null;
        try
        {
            cacheDir = environment.DarwinCacheDir();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            failure = ex;
        }

        if (!string.IsNullOrEmpty(cacheDir))
        {
            string cachePath = Path.Combine(cacheDir, Prefix + Hex(fullLockPath, 16) + ".sock");
            if (environment.SocketPathFits(cachePath))
            {
                log.LogWarning(
                    $"The instance socket path {besideLock} is too long for macOS, so the instance socket is {cachePath} in the user cache folder."
                );
                return cachePath;
            }
        }

        string relative = RelativeName(fullLockPath);
        log.LogError(
            $"The instance socket path {besideLock} is too long for macOS and the user cache folder ({cacheDir ?? "unavailable"}) gives "
                + $"no path that fits, so the instance pipe is '{relative}' in TMPDIR; a launch with a different TMPDIR cannot reach it.",
            failure
        );
        return relative;
    }

    /// <summary>A pipe name the runtime turns into a socket under <c>$TMPDIR</c>.</summary>
    private static string RelativeName(string fullLockPath) => Prefix + Hex(fullLockPath, 8);

    private static string Hex(string text, int length) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    [LibraryImport("libc", EntryPoint = "confstr")]
    private static partial nuint Confstr(int name, byte[] buffer, nuint length);
}
