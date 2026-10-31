using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class LaunchErrorGateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-launch-error-" + Guid.NewGuid().ToString("N"));

    private string LockPath => Path.Combine(_folder, "launch-error.lock");

    [Fact]
    public void TryTake_WhileTheFirstHolderHasIt_ASecondTakerIsHeldByAnother()
    {
        using IDisposable first = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(LockPath)).Holder;

        Assert.IsType<LaunchErrorBoxHeld>(LaunchErrorGate.TryTake(LockPath));
    }

    [Fact]
    public void TryTake_AfterTheHolderIsDisposed_TheNextTakerTakesIt()
    {
        LaunchErrorBoxTaken first = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(LockPath));
        Assert.IsType<LaunchErrorBoxHeld>(LaunchErrorGate.TryTake(LockPath));

        first.Holder.Dispose();

        using IDisposable later = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(LockPath)).Holder;
    }

    [Fact]
    public void TryTake_AMissingFolder_IsCreated()
    {
        Assert.False(Directory.Exists(_folder));

        using IDisposable holder = Assert.IsType<LaunchErrorBoxTaken>(LaunchErrorGate.TryTake(LockPath)).Holder;

        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public void TryTake_AReadOnlyLockFile_ReportsCannotOpen()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(LockPath, "read only");
        File.SetAttributes(LockPath, FileAttributes.ReadOnly);

        LaunchErrorGateResult result;
        try
        {
            result = LaunchErrorGate.TryTake(LockPath);
        }
        finally
        {
            File.SetAttributes(LockPath, FileAttributes.Normal);
        }

        LaunchErrorBoxCannotOpen cannotOpen = Assert.IsType<LaunchErrorBoxCannotOpen>(result);
        Assert.IsType<UnauthorizedAccessException>(cannotOpen.Failure);
    }

    [Fact]
    public void TryTake_AFileWhereTheLockFilesFolderGoes_ReportsCannotOpenNotHeld()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "blocker"), "a file where the folder goes");

        LaunchErrorGateResult result = LaunchErrorGate.TryTake(Path.Combine(_folder, "blocker", "launch-error.lock"));

        LaunchErrorBoxCannotOpen cannotOpen = Assert.IsType<LaunchErrorBoxCannotOpen>(result);
        Assert.IsType<IOException>(cannotOpen.Failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
