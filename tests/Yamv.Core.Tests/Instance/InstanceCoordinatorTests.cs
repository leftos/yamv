using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Tests.Instance;

public sealed class InstanceCoordinatorTests : IDisposable
{
    /// <summary>
    /// A hang guard: a passing run never waits it out. Defaults to 300 s; the environment variable <c>YAMV_TEST_HANG_GUARD_SECONDS</c>
    /// (read by these tests only, a positive whole number of seconds) shortens it while iterating on a change that may hang, so a
    /// red round costs seconds.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("YAMV_TEST_HANG_GUARD_SECONDS"), out int seconds) && seconds > 0 ? seconds : 300
    );

    private static readonly string TestsRoot = Path.Combine(Path.GetTempPath(), "yamv-tests");

    private readonly string _stateRoot = Path.Combine(TestsRoot, Guid.NewGuid().ToString("N"));
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

        if (Directory.Exists(TestsRoot) && !Directory.EnumerateFileSystemEntries(TestsRoot).Any())
        {
            Directory.Delete(TestsRoot);
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
        Assert.False(signal.IsCompleted);
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

        await SendRawAsync(Header(1_000_000));

        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.IsType<InvalidDataException>(warning.Exception);
    }

    [Fact]
    public async Task Server_TruncatedFrame_IsLoggedAndALaterFrameStillArrives()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        await SendRawAsync([.. Header(10), (byte)'a', (byte)'b', (byte)'c']);

        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.IsType<EndOfStreamException>(warning.Exception);
    }

    [Fact]
    public async Task Server_HandlerThrows_IsLoggedAndALaterMessageStillArrives()
    {
        const string Poison = "poison";
        TaskCompletionSource<string> received = NewSource<string>();
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log);
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(message =>
        {
            if (message == Poison)
            {
                throw new InvalidOperationException("handler failure under test");
            }

            received.TrySetResult(message);
        });

        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, Poison, HangGuard, _log, Token));
        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) error = Assert.Single(_log.Errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task Server_SecondClientConnectsWhileAFrameIsMidRead_BothArrive()
    {
        ConcurrentQueue<string> arrived = new();
        TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log) { FrameReadDeadline = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(message =>
        {
            arrived.Enqueue(message);
            if (arrived.Count == 2)
            {
                bothArrived.TrySetResult();
            }
        });

        NamedPipeClientStream rawA = NewRawClient();
        await using (rawA)
        {
            await rawA.ConnectAsync(HangGuard, Token);
            // The header declares 5 bytes and 2 follow, so the server reads these and waits for 3 more.
            byte[] headerAndTwoBytes = [.. Header(5), (byte)'f', (byte)'i'];
            await rawA.WriteAsync(headerAndTwoBytes, Token);
            await rawA.FlushAsync(Token);

            NamedPipeClientStream rawB = NewRawClient();
            await using (rawB)
            {
                // Connects only if a listener exists while A's frame is still being read.
                await rawB.ConnectAsync(HangGuard, Token);
                Task writeB = rawB.WriteAsync(InstanceFrame.Encode("second"), Token).AsTask();

                await rawA.WriteAsync("rst"u8.ToArray(), Token);
                await rawA.FlushAsync(Token);
                await writeB.WaitAsync(HangGuard, Token);
                await bothArrived.Task.WaitAsync(HangGuard, Token);
            }
        }

        string[] expected = ["first", "second"];
        Assert.Equal(expected, arrived);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task SignalAsync_ServerBusyReading_ReturnsFalseWithinTimeout()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "On Unix the kernel send buffer takes the frame at once, so the write never waits on a busy server."
        );
        TaskCompletionSource<string> received = NewSource<string>();
        using InstanceCoordinator main = new(_lockPath, _pipeName, _log) { FrameReadDeadline = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(message => received.TrySetResult(message));

        NamedPipeClientStream rawA = NewRawClient();
        await using (rawA)
        {
            await rawA.ConnectAsync(HangGuard, Token);
            // A Windows write completes once the server has read it: the server is now mid-read, waiting for 3 more bytes.
            byte[] headerAndTwoBytes = [.. Header(5), (byte)'a', (byte)'b'];
            await rawA.WriteAsync(headerAndTwoBytes, Token);
            await rawA.FlushAsync(Token);

            bool sent = await InstanceCoordinator.SignalAsync(_pipeName, "unread", TimeSpan.FromSeconds(1), _log, Token);

            Assert.False(sent);
            Assert.Contains(_log.Warnings, warning => warning.Message.Contains("did not read", StringComparison.Ordinal));
        }

        // The listener the timed-out client connected to has been served and replaced, so a later signal still arrives.
        Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, InstanceMessages.Activate, HangGuard, _log, Token));
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_InfiniteTimeout_IsDelivered()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        bool sent = await InstanceCoordinator
            .SignalAsync(_pipeName, InstanceMessages.Activate, Timeout.InfiniteTimeSpan, _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.True(sent);
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-5d)]
    [InlineData(2_147_483_648d)]
    public async Task SignalAsync_TimeoutNotPositiveOrOverIntMaxMilliseconds_Throws(double milliseconds)
    {
        Task<bool> signal = InstanceCoordinator.SignalAsync(
            _pipeName,
            InstanceMessages.Activate,
            TimeSpan.FromMilliseconds(milliseconds),
            _log,
            Token
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => signal.WaitAsync(HangGuard, Token));
    }

    [Theory]
    [InlineData("yt/ab")]
    [InlineData("yt.ab")]
    public async Task SignalAsync_NameWithACharacterOutsideLettersDigitsDashUnderscore_Throws(string name)
    {
        Task<bool> signal = InstanceCoordinator.SignalAsync(name, InstanceMessages.Activate, HangGuard, _log, Token);

        await Assert.ThrowsAsync<ArgumentException>(() => signal.WaitAsync(HangGuard, Token));
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
    public async Task Dispose_WithMessagesQueuedBehindABlockedHandler_LogsTheDropCount()
    {
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource threeQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceCoordinator main = new(_lockPath, _pipeName, _log);
        Assert.True(main.TryAcquireMainLock());
        main.FrameQueued = total =>
        {
            if (total == 3)
            {
                threeQueued.TrySetResult();
            }
        };
        main.StartServer(message =>
        {
            if (message == "first")
            {
                firstEntered.TrySetResult();
                release.Task.Wait(HangGuard, Token);
            }
        });

        try
        {
            Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, "first", HangGuard, _log, Token));
            await firstEntered.Task.WaitAsync(HangGuard, Token);
            Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, "second", HangGuard, _log, Token));
            Assert.True(await InstanceCoordinator.SignalAsync(_pipeName, "third", HangGuard, _log, Token));

            // On Unix a signal returns once its frame is in the socket buffer; wait until the server has queued all three.
            await threeQueued.Task.WaitAsync(HangGuard, Token);
        }
        finally
        {
            try
            {
                // The handler stays blocked through Dispose, so the two queued messages are still queued when it counts them.
                main.Dispose();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        Assert.Contains(_log.Warnings, warning => warning.Message.StartsWith("Dropped 2 instance message(s)", StringComparison.Ordinal));
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

    private static byte[] Header(int declaredLength)
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        return header;
    }

    private NamedPipeClientStream NewRawClient() => new(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Connects to the server, writes <paramref name="bytes"/> as they are and closes.</summary>
    private async Task SendRawAsync(byte[] bytes)
    {
        NamedPipeClientStream raw = NewRawClient();
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            await raw.WriteAsync(bytes, Token);
            await raw.FlushAsync(Token);
        }
    }

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
