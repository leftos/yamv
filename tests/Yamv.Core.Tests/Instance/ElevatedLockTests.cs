using System.Security.Cryptography;
using Yamv.Core.Instance;
using Yamv.Core.Tests.Session;

namespace Yamv.Core.Tests.Instance;

/// <summary>The elevated instance's lock beside the main one: a lock, a pid file and an endpoint of its own.</summary>
public sealed class ElevatedLockTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "yt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
    private readonly RecordingAppLog _log = new();

    [Fact]
    public void ElevatedLockFile_IsMainAdminLock_AndTheMainLockKeepsItsName()
    {
        Assert.Equal("main.admin.lock", Path.GetFileName(AppPaths.ElevatedLockFile));
        Assert.Equal("main.lock", Path.GetFileName(AppPaths.MainLockFile));
    }

    [Fact]
    public void MainAndElevatedLocks_BothTakeTheirLock_EachWithItsOwnPidFileAndPipe()
    {
        string mainLock = Path.Combine(_stateRoot, "main.lock");
        string elevatedLock = Path.Combine(_stateRoot, "main.admin.lock");
        using InstanceCoordinator main = new(mainLock, _log);
        using InstanceCoordinator elevated = new(elevatedLock, _log);

        Assert.True(main.TryAcquireMainLock());
        Assert.True(elevated.TryAcquireMainLock());

        Assert.True(File.Exists(Path.Combine(_stateRoot, "main.pid")));
        Assert.True(File.Exists(Path.Combine(_stateRoot, "main.admin.pid")));
        var windows = new EndpointEnvironment(EndpointPlatform.Windows, () => Sid, () => null, _ => true);
        Assert.NotEqual(InstanceEndpoint.Resolve(mainLock, windows, _log), InstanceEndpoint.Resolve(elevatedLock, windows, _log));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }
}
