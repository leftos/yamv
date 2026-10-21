using Yamv.App.Tests.Fakes;
using Yamv.Core.Instance;

namespace Yamv.App.Tests;

/// <summary>
/// A real instance server on a lock path private to the test, its handler the test's: a launch's request reaches it over the
/// same pipe and wire a real second launch uses. Disposing it stops the server, releases the lock and deletes the folder.
/// </summary>
internal sealed class TestInstanceServer : IDisposable
{
    /// <summary>A hang guard: a passing run never waits it out.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "yamv-instance-" + Guid.NewGuid().ToString("N"));

    public TestInstanceServer(Action<InstanceRequest> onRequest)
    {
        Directory.CreateDirectory(_stateRoot);
        LockPath = Path.Combine(_stateRoot, "main.lock");
        // Longer than the hang guard, so a request the test holds is never abandoned by the clock, however busy the machine.
        Coordinator = new InstanceCoordinator(LockPath, Log) { ReplyBound = TimeSpan.FromMinutes(5) };
        Assert.True(Coordinator.TryAcquireMainLock());
        Coordinator.StartServer(onRequest);
    }

    public string LockPath { get; }

    /// <summary>The coordinator's log, and the log the test's launches write to.</summary>
    public RecordingAppLog Log { get; } = new();

    public InstanceCoordinator Coordinator { get; }

    /// <summary>The pid file the coordinator wrote beside its lock file.</summary>
    public string PidFile => Path.ChangeExtension(LockPath, ".pid");

    /// <summary>Sends <paramref name="message"/> as a launch does and returns how it ended.</summary>
    public Task<InstanceRequestOutcome> SendAsync(string message) =>
        InstanceCoordinator.RequestAsync(LockPath, message, HangGuard, Log, TestContext.Current.CancellationToken);

    /// <summary>Sends the <c>open</c> message for <paramref name="files"/> as a file launch does.</summary>
    public Task<InstanceRequestOutcome> SendOpenAsync(IReadOnlyList<string> files) => SendAsync(OpenMessageFor(files));

    /// <summary>The <c>open</c> message for <paramref name="files"/>.</summary>
    public static string OpenMessageFor(IReadOnlyList<string> files)
    {
        Assert.True(OpenMessage.TryEncode(files, out string message, out OpenMessageProblem problem), problem.ToString());
        return message;
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }
}
