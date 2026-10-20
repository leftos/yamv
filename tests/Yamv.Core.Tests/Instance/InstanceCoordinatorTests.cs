using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Tests.Instance;

public sealed class InstanceCoordinatorTests : IDisposable
{
    /// <summary>A hang guard: a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "yamv-tests", Guid.NewGuid().ToString("N"));
    private readonly string _lockPath;
    private readonly string _pipeName = "yt" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private readonly RecordingLog _log = new();

    public InstanceCoordinatorTests()
    {
        _lockPath = Path.Combine(_stateRoot, "nested", "main.lock");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    [Fact]
    public void TryAcquireMainLock_SecondCoordinatorOnSamePath_FailsUntilFirstIsDisposed()
    {
        using InstanceCoordinator second = new(_lockPath, _pipeName, _log);
        using (InstanceCoordinator first = new(_lockPath, _pipeName, _log))
        {
            Assert.True(first.TryAcquireMainLock());
            Assert.False(second.TryAcquireMainLock());
        }

        Assert.True(second.TryAcquireMainLock());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task SignalAsync_ServerRunning_DeliversActivate()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        bool sent = await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token);

        Assert.True(sent);
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_SentBeforeServerStarts_IsDelivered()
    {
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log);
        Assert.True(main.TryAcquireMainLock());
        TaskCompletionSource<string> received = NewSource<string>();

        Task<bool> signal = InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        main.StartServer(message => received.TrySetResult(message));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        Assert.True(await signal.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_NoServer_ReturnsFalseAndLogsOneWarning()
    {
        bool sent = await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, TimeSpan.FromMilliseconds(300), _log, Token);

        Assert.False(sent);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public async Task Server_OversizeFrame_IsLoggedAndALaterFrameStillArrives()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        NamedPipeClientStream raw = new(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, 1_000_000);
            await raw.WriteAsync(header, Token);
            await raw.FlushAsync(Token);
        }

        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.IsType<InvalidDataException>(warning.Exception);
    }

    [Fact]
    public async Task SignalAsync_ThreeConcurrentSignals_AllArrive()
    {
        string[] messages = ["one", "two", "three"];
        ConcurrentQueue<string> arrived = new();
        TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log);
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(message =>
        {
            arrived.Enqueue(message);
            if (arrived.Count == messages.Length)
            {
                allArrived.TrySetResult();
            }
        });

        bool[] sent = await Task.WhenAll(messages.Select(message => InstanceCoordinator.SignalAsync(_pipeName, message, HangGuard, _log, Token)));
        await allArrived.Task.WaitAsync(HangGuard, Token);

        Assert.All(sent, Assert.True);
        Assert.Equal(messages.Order(), arrived.Order());
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        InstanceCoordinator main = StartMain(out _);

        main.Dispose();
        main.Dispose();
    }

    [Fact]
    public void StartServer_WithoutLock_Throws()
    {
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log);

        Assert.Throws<InvalidOperationException>(() => main.StartServer(_ => { }));
    }

    [Fact]
    public void DefaultPipeName_IsShortAndPrefixed()
    {
        string name = InstanceCoordinator.DefaultPipeName;

        Assert.StartsWith("yamv-", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 16, $"'{name}' is {name.Length} characters; the macOS socket path needs at most 16.");
    }

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private InstanceCoordinator StartMain(out TaskCompletionSource<string> received)
    {
        InstanceCoordinator main = new(_lockPath, _pipeName, _log);
        Assert.True(main.TryAcquireMainLock());
        TaskCompletionSource<string> source = NewSource<string>();
        main.StartServer(message => source.TrySetResult(message));
        received = source;
        return main;
    }

    private sealed class RecordingLog : IAppLog
    {
        private readonly ConcurrentQueue<(string Message, Exception? Exception)> _warnings = new();
        private readonly ConcurrentQueue<(string Message, Exception? Exception)> _errors = new();

        public IReadOnlyCollection<(string Message, Exception? Exception)> Warnings => _warnings;

        public IReadOnlyCollection<(string Message, Exception? Exception)> Errors => _errors;

        public void LogInfo(string message) { }

        public void LogWarning(string message, Exception? exception = null) => _warnings.Enqueue((message, exception));

        public void LogError(string message, Exception? exception = null) => _errors.Enqueue((message, exception));
    }
}
