using System.Security;
using System.Security.Cryptography;
using System.Text;
using Yamv.Core.Instance;
using Yamv.Core.Tests.Session;

namespace Yamv.Core.Tests.Instance;

public sealed class InstanceEndpointTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    /// <summary>A lock path private to the case; nothing here creates it.</summary>
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "yt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
    private readonly string _lockPath;
    private readonly RecordingAppLog _log = new();

    public InstanceEndpointTests()
    {
        _lockPath = Path.Combine(_stateRoot, "main.lock");
    }

    private IEnumerable<LogEntry> Warnings => _log.Entries.Where(entry => entry.Level == "warning");

    private IEnumerable<LogEntry> Errors => _log.Entries.Where(entry => entry.Level == "error");

    [Fact]
    public void Endpoint_IsTheSameForOneLockPathAndDiffersForAnother()
    {
        using InstanceCoordinator first = new(_lockPath, _log);
        using InstanceCoordinator again = new(_lockPath, _log);
        // In a folder of its own: on Unix the endpoint is the main.sock beside the lock, so only another folder gives another one.
        using InstanceCoordinator other = new(Path.Combine(_stateRoot, "b", "main.lock"), _log);

        Assert.Equal(first.Endpoint, again.Endpoint);
        Assert.NotEqual(first.Endpoint, other.Endpoint);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Endpoint_OnUnix_IsOneSocketForTwoLockFilesInOneFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only a Unix endpoint is the folder's main.sock, whatever the lock file's name.");
        using InstanceCoordinator main = new(_lockPath, _log);
        using InstanceCoordinator other = new(Path.Combine(_stateRoot, "other.lock"), _log);

        Assert.Equal(main.Endpoint, other.Endpoint);
        Assert.Equal(Path.Combine(Path.GetFullPath(_stateRoot), "main.sock"), main.Endpoint);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Resolve_Windows_IsPrefixAndSixteenHexOfSidAndFullLockPath()
    {
        string endpoint = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid), _log);

        Assert.Matches("^yamv-[0-9a-f]{16}$", endpoint);
        Assert.Equal("yamv-" + Hex(Sid + "\n" + Path.GetFullPath(_lockPath), 16), endpoint);
        Assert.DoesNotContain(Sid, endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(endpoint, InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid), _log));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Resolve_Windows_DiffersForTwoLockPaths()
    {
        string first = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid), _log);
        string second = InstanceEndpoint.Resolve(_lockPath + "2", EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid), _log);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Resolve_Windows_DiffersForTwoSidsUnderOneUserName()
    {
        string first = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid), _log);
        string second = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => Sid + "2"), _log);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Resolve_Windows_SidThrows_FallsBackToUserNameWithAWarning()
    {
        string endpoint = InstanceEndpoint.Resolve(
            _lockPath,
            EnvironmentFor(EndpointPlatform.Windows, sid: () => throw new SecurityException("no token under test")),
            _log
        );

        Assert.Equal(UserNameFallback(), endpoint);
        LogEntry warning = Assert.Single(Warnings);
        Assert.IsType<SecurityException>(warning.Exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_Windows_NoSid_FallsBackToUserNameWithAWarning(string? sid)
    {
        string endpoint = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Windows, sid: () => sid), _log);

        Assert.Equal(UserNameFallback(), endpoint);
        LogEntry warning = Assert.Single(Warnings);
        Assert.Null(warning.Exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_Unix_Fits_IsMainSockBesideTheLock(bool macOS)
    {
        EndpointPlatform platform = macOS ? EndpointPlatform.MacOS : EndpointPlatform.Linux;

        string endpoint = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(platform), _log);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!, "main.sock"), endpoint);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Resolve_MacOSTooLong_IsInTheUserCacheFolderWithOneWarningNamingBothPaths()
    {
        string cacheDir = Path.Combine(_stateRoot, "cache");
        string besideLock = Path.Combine(_stateRoot, "main.sock");

        string endpoint = InstanceEndpoint.Resolve(
            _lockPath,
            EnvironmentFor(EndpointPlatform.MacOS, cacheDir: () => cacheDir, fits: path => path != besideLock),
            _log
        );

        Assert.Equal(Path.Combine(cacheDir, "yamv-" + Hex(Path.GetFullPath(_lockPath), 16) + ".sock"), endpoint);
        LogEntry warning = Assert.Single(Warnings);
        Assert.Contains(besideLock, warning.Message, StringComparison.Ordinal);
        Assert.Contains(endpoint, warning.Message, StringComparison.Ordinal);
        Assert.Empty(Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_MacOSTooLongAndNoCachePathFits_IsTheRelativeNameWithAnError(bool cacheAvailable)
    {
        string cacheDir = Path.Combine(_stateRoot, "cache");

        string endpoint = InstanceEndpoint.Resolve(
            _lockPath,
            EnvironmentFor(EndpointPlatform.MacOS, cacheDir: () => cacheAvailable ? cacheDir : null, fits: _ => false),
            _log
        );

        Assert.Equal(RelativeName(), endpoint);
        Assert.Single(Errors);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Resolve_MacOSTooLongAndCacheLookupThrows_IsTheRelativeNameWithTheErrorItCaused()
    {
        string endpoint = InstanceEndpoint.Resolve(
            _lockPath,
            EnvironmentFor(EndpointPlatform.MacOS, cacheDir: () => throw new DllNotFoundException("libc under test"), fits: _ => false),
            _log
        );

        Assert.Equal(RelativeName(), endpoint);
        LogEntry error = Assert.Single(Errors);
        Assert.IsType<DllNotFoundException>(error.Exception);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Resolve_LinuxTooLong_IsTheRelativeNameWithAWarning()
    {
        string endpoint = InstanceEndpoint.Resolve(_lockPath, EnvironmentFor(EndpointPlatform.Linux, fits: _ => false), _log);

        Assert.Equal(RelativeName(), endpoint);
        Assert.Single(Warnings);
        Assert.Empty(Errors);
    }

    private static EndpointEnvironment EnvironmentFor(
        EndpointPlatform platform,
        Func<string?>? sid = null,
        Func<string?>? cacheDir = null,
        Func<string, bool>? fits = null
    ) => new(platform, sid ?? (() => null), cacheDir ?? (() => null), fits ?? (_ => true));

    private static string Hex(string text, int length) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    private string UserNameFallback() => "yamv-" + Hex(Environment.UserName + "\n" + Path.GetFullPath(_lockPath), 16);

    private string RelativeName() => "yamv-" + Hex(Path.GetFullPath(_lockPath), 8);
}
