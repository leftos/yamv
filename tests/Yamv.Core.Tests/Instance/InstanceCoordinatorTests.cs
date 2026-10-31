using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;
using ThreadState = System.Threading.ThreadState;

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

    /// <summary>A folder of its own per case, kept short so the <c>main.sock</c> beside its lock fits macOS's 104-byte limit.</summary>
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "yt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
    private readonly string _lockPath;
    private readonly RecordingLog _log = new();

    public InstanceCoordinatorTests()
    {
        _lockPath = Path.Combine(_stateRoot, "main.lock");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string PidPath => Path.Combine(_stateRoot, "main.pid");

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    [Fact]
    public void Default_reply_bound_exceeds_the_open_deadline()
    {
        using InstanceCoordinator coordinator = new(_lockPath, _log);

        Assert.True(
            coordinator.ReplyBound > Yamv.Core.Launch.SecondLaunch.OpenDeadline,
            $"the default reply bound {coordinator.ReplyBound} must exceed the launcher's open deadline {Yamv.Core.Launch.SecondLaunch.OpenDeadline}"
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReplyBound_NotPositive_Throws(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InstanceCoordinator(_lockPath, _log) { ReplyBound = TimeSpan.FromMilliseconds(milliseconds) }
        );
    }

    [Fact]
    public void TryAcquireMainLock_SecondCoordinatorOnSamePath_FailsUntilFirstIsDisposed()
    {
        using InstanceCoordinator second = new(_lockPath, _log);
        using (InstanceCoordinator first = new(_lockPath, _log))
        {
            Assert.True(first.TryAcquireMainLock());
            Assert.False(second.TryAcquireMainLock());
        }

        Assert.True(second.TryAcquireMainLock());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void TryAcquireMainLock_Acquired_WritesPidFileThatReadsBackAsThisProcess()
    {
        using InstanceCoordinator main = new(_lockPath, _log);

        Assert.True(main.TryAcquireMainLock());

        using var current = Process.GetCurrentProcess();
        string expected = $"{Environment.ProcessId}\n{current.StartTime.ToUniversalTime().Ticks}\n";
        Assert.Equal(expected, File.ReadAllText(PidPath));
        Assert.Equal(Environment.ProcessId, InstanceCoordinator.TryReadMainPid(_lockPath, _log));
        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Infos);
    }

    [Fact]
    public async Task StopAnswering_StopsServerAndDeletesPidFileButKeepsTheLockUntilDispose()
    {
        using InstanceCoordinator other = new(_lockPath, _log);
        InstanceCoordinator main = StartMain(out _);
        try
        {
            Assert.True(File.Exists(PidPath));

            main.StopAnswering();

            Assert.False(File.Exists(PidPath));
            Assert.Null(InstanceCoordinator.TryReadMainPid(_lockPath, _log));
            bool delivered = await InstanceCoordinator
                .SignalAsync(_lockPath, InstanceMessages.Activate, TimeSpan.FromMilliseconds(200), _log, Token)
                .WaitAsync(HangGuard, Token);
            Assert.False(delivered);
            Assert.False(other.TryAcquireMainLock());

            main.StopAnswering();
            main.Dispose();

            Assert.True(other.TryAcquireMainLock());
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task StopAnswering_WaitingOnABlockedHandler_HasAlreadyDeletedThePidFile()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceCoordinator main = await StartMainWithBlockedHandlerAsync(_ => release.Task.Wait(HangGuard, Token));
        TaskCompletionSource countedQueue = WhenWarningLogged("Dropped 1 instance message(s)");
        Thread stopping = new(main.StopAnswering) { IsBackground = true };
        try
        {
            stopping.Start();
            // StopAnswering logs the drop count after the accept loop stops and before it waits on the blocked handler.
            await countedQueue.Task.WaitAsync(HangGuard, Token);

            Assert.True(stopping.IsAlive, "StopAnswering returned while the handler was still blocked.");
            Assert.Null(InstanceCoordinator.TryReadMainPid(_lockPath, _log));
            Assert.False(File.Exists(PidPath));
        }
        finally
        {
            release.TrySetResult();
            Assert.True(stopping.Join(HangGuard), "StopAnswering did not return after the handler was released.");
            main.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_WhileStopAnsweringWaitsOnABlockedHandler_WaitsForTheHandler()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool handlerEnded = false;
        bool? handlerEndedWhenDisposeReturned = null;
        InstanceCoordinator main = await StartMainWithBlockedHandlerAsync(_ =>
        {
            release.Task.Wait(HangGuard, Token);
            Volatile.Write(ref handlerEnded, true);
        });
        TaskCompletionSource countedQueue = WhenWarningLogged("Dropped 1 instance message(s)");
        Thread stopping = new(main.StopAnswering) { IsBackground = true };
        Thread disposing = new(() =>
        {
            main.Dispose();
            handlerEndedWhenDisposeReturned = Volatile.Read(ref handlerEnded);
        })
        {
            IsBackground = true,
        };
        try
        {
            stopping.Start();
            await countedQueue.Task.WaitAsync(HangGuard, Token);
            disposing.Start();
            await WaitUntilBlockedOrEndedAsync(disposing);

            Assert.True(disposing.IsAlive, "Dispose returned while StopAnswering's handler was still blocked.");
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(disposing.Join(HangGuard), "Dispose did not return after the handler was released.");
        Assert.True(stopping.Join(HangGuard), "StopAnswering did not return after the handler was released.");
        Assert.True(handlerEndedWhenDisposeReturned);
    }

    [Fact]
    public void StopAnswering_ServerNeverStarted_DeletesPidFileAndKeepsTheLock()
    {
        using InstanceCoordinator other = new(_lockPath, _log);
        InstanceCoordinator main = new(_lockPath, _log);
        try
        {
            Assert.True(main.TryAcquireMainLock());

            main.StopAnswering();
            main.StopAnswering();

            Assert.False(File.Exists(PidPath));
            Assert.False(other.TryAcquireMainLock());
            main.Dispose();
            Assert.True(other.TryAcquireMainLock());
            Assert.Empty(_log.Errors);
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public void TryAcquireMainLockQuietly_HeldByAnother_ReturnsHeldAndLogsNothing()
    {
        using InstanceCoordinator main = new(_lockPath, _log);
        using InstanceCoordinator waiting = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());

        Assert.Equal(MainLockAttempt.HeldByAnother, waiting.TryAcquireMainLockQuietly());

        Assert.Empty(_log.Infos);
        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Errors);
        main.Dispose();
        Assert.Equal(MainLockAttempt.Acquired, waiting.TryAcquireMainLockQuietly());
    }

    [Fact]
    public void TryAcquireMainLockQuietly_LockFileCannotBeOpened_ReturnsCannotOpenAndLogsAnError()
    {
        Directory.CreateDirectory(_lockPath);
        using InstanceCoordinator main = new(_lockPath, _log);

        Assert.Equal(MainLockAttempt.CannotOpen, main.TryAcquireMainLockQuietly());

        Assert.Single(_log.Errors);
    }

    [Fact]
    public void TryAcquireMainLock_AlreadyHeld_DoesNotRewritePidFile()
    {
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        Assert.True(File.Exists(PidPath));
        File.WriteAllText(PidPath, "marker");

        Assert.True(main.TryAcquireMainLock());

        Assert.Equal("marker", File.ReadAllText(PidPath));
    }

    [Fact]
    public void Dispose_DeletesOwnPidFileButLeavesOneItNeverWrote()
    {
        InstanceCoordinator first = new(_lockPath, _log);
        try
        {
            Assert.True(first.TryAcquireMainLock());
            Assert.True(File.Exists(PidPath));
            using (InstanceCoordinator second = new(_lockPath, _log))
            {
                Assert.False(second.TryAcquireMainLock());
            }

            Assert.True(File.Exists(PidPath));
            first.Dispose();
            Assert.False(File.Exists(PidPath));

            File.WriteAllText(PidPath, "left alone");
            new InstanceCoordinator(_lockPath, _log).Dispose();
            Assert.Equal("left alone", File.ReadAllText(PidPath));
            Assert.Empty(_log.Warnings);
        }
        finally
        {
            // Releases the lock before the fixture deletes its folder when an assertion failed; a second Dispose is safe.
            first.Dispose();
        }
    }

    [Fact]
    public void Dispose_DeletesPidFileWhileStillHoldingTheLock()
    {
        bool? pidFileExistedAtRelease = null;
        bool? otherCouldAcquireAtRelease = null;
        using InstanceCoordinator other = new(_lockPath, _log);
        InstanceCoordinator main = new(_lockPath, _log)
        {
            LockReleasing = () =>
            {
                pidFileExistedAtRelease = File.Exists(PidPath);
                otherCouldAcquireAtRelease = other.TryAcquireMainLock();
            },
        };
        try
        {
            Assert.True(main.TryAcquireMainLock());
        }
        finally
        {
            main.Dispose();
        }

        Assert.False(pidFileExistedAtRelease);
        Assert.False(otherCouldAcquireAtRelease);
    }

    [Fact]
    public void TryAcquireMainLock_ReaderHoldsOldPidFileOpen_NextMainStillWritesItsOwn()
    {
        InstanceCoordinator first = new(_lockPath, _log);
        try
        {
            Assert.True(first.TryAcquireMainLock());
            using FileStream reader = new(PidPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            first.Dispose();

            using InstanceCoordinator second = new(_lockPath, _log);
            Assert.True(second.TryAcquireMainLock());

            Assert.Equal(Environment.ProcessId, InstanceCoordinator.TryReadMainPid(_lockPath, _log));
            Assert.Empty(_log.Warnings);
        }
        finally
        {
            first.Dispose();
        }
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void TryAcquireMainLock_StartTimeUnreadable_StillAcquiresAndWarns(Type exceptionType)
    {
        using InstanceCoordinator main = new(_lockPath, _log);
        InstanceCoordinator.PidTextReading.Value = () => throw (Exception)Activator.CreateInstance(exceptionType)!;
        try
        {
            Assert.True(main.TryAcquireMainLock());
        }
        finally
        {
            InstanceCoordinator.PidTextReading.Value = null;
        }

        (_, Exception? logged) = Assert.Single(_log.Warnings);
        Assert.IsType(exceptionType, logged);
        Assert.False(File.Exists(PidPath));
        Assert.Empty(Directory.GetFiles(_stateRoot, "*.tmp"));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void TryReadMainPid_LongerThan64Bytes_ReturnsNullAndLogsInfo()
    {
        using var current = Process.GetCurrentProcess();
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture).PadLeft(32, '0');
        string ticks = current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture).PadLeft(31, '0');

        // Leading zeros keep it a valid pid file in every way but its length: 65 bytes.
        AssertReadIsNullWithOneInfo($"{pid}\n{ticks}\n");
    }

    [Fact]
    public void TryReadMainPid_NoFile_ReturnsNullSilently()
    {
        Assert.Null(InstanceCoordinator.TryReadMainPid(_lockPath, _log));
        Assert.Empty(_log.Infos);
    }

    [Theory]
    [InlineData("12345\n")]
    [InlineData("12345")]
    [InlineData("pid\nticks\n")]
    [InlineData("12345\n678\nextra\n")]
    [InlineData("")]
    public void TryReadMainPid_MalformedFile_ReturnsNullAndLogsInfo(string content) => AssertReadIsNullWithOneInfo(content);

    [Fact]
    public void TryReadMainPid_StartTimeOneTickOff_ReturnsNullAndLogsInfo()
    {
        using var current = Process.GetCurrentProcess();
        long ticks = current.StartTime.ToUniversalTime().Ticks;

        AssertReadIsNullWithOneInfo($"{Environment.ProcessId}\n{ticks + 1}\n");
    }

    [Fact]
    public void TryReadMainPid_ProcessNotRunning_ReturnsNullAndLogsInfo()
    {
        ProcessStartInfo startInfo = new("dotnet", "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        int exitedPid;
        using (Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet --version did not start."))
        {
            child.StandardOutput.ReadToEnd();
            Assert.True(child.WaitForExit(HangGuard));
            exitedPid = child.Id;
        }

        AssertReadIsNullWithOneInfo($"{exitedPid}\n{DateTime.UtcNow.Ticks}\n");
    }

    [Fact]
    public void TryAcquireMainLock_PidFileCannotBeWritten_StillAcquiresAndWarns()
    {
        Directory.CreateDirectory(PidPath);
        using InstanceCoordinator main = new(_lockPath, _log);

        Assert.True(main.TryAcquireMainLock());

        (string message, _) = Assert.Single(_log.Warnings);
        Assert.Contains(PidPath, message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_stateRoot, "*.tmp"));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task SignalAsync_ServerRunning_DeliversActivate()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        bool sent = await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token);

        Assert.True(sent);
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_SentBeforeServerStarts_IsDelivered()
    {
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        TaskCompletionSource<string> received = NewSource<string>();

        Task<bool> signal = InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.False(signal.IsCompleted);
        main.StartServer(request => received.TrySetResult(request.Message));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        Assert.True(await signal.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_NoServer_ReturnsFalseAndLogsOneWarning()
    {
        bool sent = await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, TimeSpan.FromMilliseconds(300), _log, Token);

        Assert.False(sent);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public async Task SignalAsync_NoServer_PacesConnectAttempts()
    {
        ConcurrentQueue<int> attemptTimeouts = new();
        InstanceCoordinator.ConnectAttempted.Value = attemptTimeout => attemptTimeouts.Enqueue(attemptTimeout);
        var timeout = TimeSpan.FromMilliseconds(400);
        long start = Stopwatch.GetTimestamp();

        bool sent = await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, timeout, _log, Token);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        Assert.False(sent);
        Assert.True(elapsed >= timeout, $"The signal gave up after {elapsed.TotalMilliseconds:0} ms, before its {timeout.TotalMilliseconds:0} ms.");
        // One attempt per 50 ms pause is about 8 in 400 ms; a busy machine only lengthens the pauses, so it makes fewer.
        Assert.InRange(attemptTimeouts.Count, 1, 15);
        // A connect timeout of 0 tries once; a longer one makes the runtime retry in a spin until it runs out.
        Assert.All(attemptTimeouts, attemptTimeout => Assert.Equal(0, attemptTimeout));
    }

    [Theory]
    [InlineData(-5d, 1d)]
    [InlineData(0d, 1d)]
    [InlineData(0.4d, 1d)]
    [InlineData(30.2d, 31d)]
    [InlineData(50d, 50d)]
    [InlineData(400d, 50d)]
    public void ConnectRetryPause_IsWholeMillisecondsAtLeastOneAndAtMostFifty(double remainingMilliseconds, double expectedMilliseconds)
    {
        TimeSpan pause = InstanceCoordinator.ConnectRetryPause(TimeSpan.FromMilliseconds(remainingMilliseconds));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), pause);
    }

    [Fact]
    public void ConnectRetryPause_InfiniteTimeout_IsFiftyMilliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(50), InstanceCoordinator.ConnectRetryPause(Timeout.InfiniteTimeSpan));

    [Fact]
    public async Task Server_OversizeFrame_IsLoggedAndALaterFrameStillArrives()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        await SendRawAsync(main, Header(1_000_000));

        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.IsType<InvalidDataException>(warning.Exception);
    }

    [Fact]
    public async Task Server_TruncatedFrame_IsLoggedAndALaterFrameStillArrives()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        await SendRawAsync(main, [.. Header(10), (byte)'a', (byte)'b', (byte)'c']);

        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.IsType<EndOfStreamException>(warning.Exception);
    }

    [Fact]
    public async Task Server_HandlerThrows_IsLoggedAndALaterMessageStillArrives()
    {
        const string Poison = "poison";
        TaskCompletionSource<string> received = NewSource<string>();
        TaskCompletionSource poisonLogged = WhenErrorLogged("The handler for instance message 'poison' threw");
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request =>
        {
            string message = request.Message;
            if (message == Poison)
            {
                throw new InvalidOperationException("handler failure under test");
            }

            received.TrySetResult(message);
        });

        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, Poison, HangGuard, _log, Token));
        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));

        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        // The two frames arrive on connections read on separate tasks, so the later message can reach its handler before
        // the poisoned one has thrown and been logged.
        await poisonLogged.Task.WaitAsync(HangGuard, Token);
        (string Message, Exception? Exception) error = Assert.Single(_log.Errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task Request_HandlerThrows_ClientGetsRefused()
    {
        using InstanceCoordinator main = StartRequestServer(_ => throw new InvalidOperationException("handler failure under test"));

        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", HangGuard, _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
        (string Message, Exception? Exception) error = Assert.Single(_log.Errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task Server_SecondClientConnectsWhileAFrameIsMidRead_BothArrive()
    {
        ConcurrentQueue<string> arrived = new();
        TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using InstanceCoordinator main = new(_lockPath, _log) { FrameReadDeadline = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request =>
        {
            string message = request.Message;
            arrived.Enqueue(message);
            if (arrived.Count == 2)
            {
                bothArrived.TrySetResult();
            }
        });

        NamedPipeClientStream rawA = NewRawClient(main);
        await using (rawA)
        {
            await rawA.ConnectAsync(HangGuard, Token);
            // The header declares 5 bytes and 2 follow, so the server reads these and waits for 3 more.
            byte[] headerAndTwoBytes = [.. Header(5), (byte)'f', (byte)'i'];
            await rawA.WriteAsync(headerAndTwoBytes, Token);
            await rawA.FlushAsync(Token);

            NamedPipeClientStream rawB = NewRawClient(main);
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

        // Each connection is read on a task of its own, so the two frames may reach the queue in either order.
        string[] expected = ["first", "second"];
        Assert.Equal(expected, arrived.Order());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task Server_ClientStalledMidFrame_DoesNotDelayAnotherClientsFrame()
    {
        TaskCompletionSource<string> received = NewSource<string>();
        using InstanceCoordinator main = new(_lockPath, _log) { FrameReadDeadline = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request => received.TrySetResult(request.Message));

        NamedPipeClientStream rawA = NewRawClient(main);
        await using (rawA)
        {
            await rawA.ConnectAsync(HangGuard, Token);
            // The header declares 5 bytes and 2 follow: the server reads them and waits for 3 more, which never come.
            byte[] headerAndTwoBytes = [.. Header(5), (byte)'a', (byte)'b'];
            await rawA.WriteAsync(headerAndTwoBytes, Token);
            await rawA.FlushAsync(Token);

            Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));

            // Handled while A is still connected mid-frame.
            Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
            Assert.True(rawA.IsConnected);
        }
    }

    [Fact]
    public async Task Server_RequestAwaitingReply_DoesNotDelayAnotherClientsFrame()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        using InstanceCoordinator main = StartRequestServer(request => requests.Writer.TryWrite(request));

        Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-a", HangGuard, _log, Token);
        InstanceRequest first = await NextAsync(requests);
        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));
        InstanceRequest second = await NextAsync(requests);

        Assert.Equal("open-a", first.Message);
        Assert.Equal(InstanceMessages.Activate, second.Message);
        Assert.False(waiting.IsCompleted);
        Assert.True(first.TryBegin());
        first.Complete(InstanceReply.Opened);
        Assert.Equal(InstanceRequestOutcome.Confirmed, await waiting.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task Request_HandlerCompletesOpened_ClientGetsConfirmed()
    {
        using InstanceCoordinator main = StartRequestServer(request =>
        {
            if (request.TryBegin())
            {
                request.Complete(InstanceReply.Opened);
            }
        });

        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", HangGuard, _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.Equal(InstanceRequestOutcome.Confirmed, outcome);
        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task Request_HandlerCompletesRefused_ClientGetsRefused()
    {
        using InstanceCoordinator main = StartRequestServer(request => request.Complete(InstanceReply.Refused));

        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", HangGuard, _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
    }

    [Fact]
    public async Task Request_UnknownReplyText_ClientGetsRefused()
    {
        using InstanceCoordinator unstarted = new(_lockPath, _log);
        NamedPipeServerStream raw = new(
            unstarted.Endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await using (raw)
        {
            Task<InstanceRequestOutcome> request = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            await raw.WaitForConnectionAsync(Token).WaitAsync(HangGuard, Token);
            Assert.Equal("open-x", await InstanceFrame.ReadAsync(raw, Token).WaitAsync(HangGuard, Token));
            await raw.WriteAsync(InstanceFrame.Encode("maybe"), Token).AsTask().WaitAsync(HangGuard, Token);

            Assert.Equal(InstanceRequestOutcome.Refused, await request.WaitAsync(HangGuard, Token));
        }
    }

    [Fact]
    public async Task Request_NoServer_ReturnsNoServerWithinBudget()
    {
        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", TimeSpan.FromMilliseconds(300), _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.Equal(InstanceRequestOutcome.NoServer, outcome);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public async Task SignalAsync_TimeoutOverIntMaxMilliseconds_ThrowsNamingTimeout()
    {
        ArgumentOutOfRangeException ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, AboveIntMax(), _log, Token)
        );

        Assert.Equal("timeout", ex.ParamName);
        Assert.StartsWith("The signal timeout is Timeout.InfiniteTimeSpan or positive and at most 2147483647 ms.", ex.Message);
    }

    [Fact]
    public async Task RequestAsync_BudgetOverIntMaxMilliseconds_ThrowsNamingBudget()
    {
        ArgumentOutOfRangeException ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            InstanceCoordinator.RequestAsync(_lockPath, "open-x", AboveIntMax(), _log, Token)
        );

        Assert.Equal("budget", ex.ParamName);
        Assert.StartsWith("The request budget is Timeout.InfiniteTimeSpan or positive and at most 2147483647 ms.", ex.Message);
    }

    /// <summary>One millisecond past the longest time a timer or a cancellation delay accepts.</summary>
    private static TimeSpan AboveIntMax() => TimeSpan.FromMilliseconds((double)int.MaxValue + 1);

    [Fact]
    public async Task Request_HandlerNeverCompletes_ClientGetsNoReplyAndServerSeesHangUp()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        using InstanceCoordinator main = StartRequestServer(request => requests.Writer.TryWrite(request));
        FakeTimeProvider time = new();

        // A round trip first, so a listener is known to exist before the fake clock, which never advances on its own, drives the
        // connect retries.
        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));
        Assert.Equal(InstanceMessages.Activate, (await NextAsync(requests)).Message);

        Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(
            _lockPath,
            "open-x",
            new InstanceCoordinator.RequestBudget(TimeSpan.FromSeconds(3), time),
            _log,
            Token
        );
        InstanceRequest request = await NextAsync(requests);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(InstanceRequestOutcome.NoReply, await waiting.WaitAsync(HangGuard, Token));
        // The launcher hung up before returning, so the server abandons the request and a late UI job cannot take it.
        Assert.Equal(InstanceReply.Closing, await request.Reply.WaitAsync(HangGuard, Token));
        Assert.False(request.TryBegin());
    }

    [Fact]
    public async Task Request_CompleteTwice_FirstWins()
    {
        using InstanceCoordinator main = StartRequestServer(request =>
        {
            Assert.True(request.TryBegin());
            request.Complete(InstanceReply.Refused);
            request.Complete(InstanceReply.Opened);
        });

        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", HangGuard, _log, Token)
            .WaitAsync(HangGuard, Token);

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
    }

    [Fact]
    public async Task Request_CompleteAfterAbandon_IsIgnored()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(request => requests.Writer.TryWrite(request));
            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            InstanceRequest request = await NextAsync(requests);

            main.StopAnswering();
            Assert.Equal(InstanceReply.Closing, await request.Reply.WaitAsync(HangGuard, Token));

            // A late answer must not turn the abandon reply into an opened: the launcher already gave up, and its request is
            // never carried out.
            request.Complete(InstanceReply.Opened);

            Assert.Equal(InstanceReply.Closing, await request.Reply.WaitAsync(HangGuard, Token));
            Assert.False(request.TryBegin());
            Assert.Equal(InstanceRequestOutcome.Closing, await waiting.WaitAsync(HangGuard, Token));
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task Request_TwoConcurrent_CompletedInReverseOrder_EachGetsItsOwnReply()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        using InstanceCoordinator main = StartRequestServer(request => requests.Writer.TryWrite(request));

        Task<InstanceRequestOutcome> a = InstanceCoordinator.RequestAsync(_lockPath, "open-a", HangGuard, _log, Token);
        Task<InstanceRequestOutcome> b = InstanceCoordinator.RequestAsync(_lockPath, "open-b", HangGuard, _log, Token);
        InstanceRequest[] both = [await NextAsync(requests), await NextAsync(requests)];
        InstanceRequest requestA = Assert.Single(both, request => request.Message == "open-a");
        InstanceRequest requestB = Assert.Single(both, request => request.Message == "open-b");

        Assert.True(requestB.TryBegin());
        requestB.Complete(InstanceReply.Opened);
        Assert.Equal(InstanceRequestOutcome.Confirmed, await b.WaitAsync(HangGuard, Token));
        requestA.Complete(InstanceReply.Refused);
        Assert.Equal(InstanceRequestOutcome.Refused, await a.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task Request_CompleteOnCallerThread_ReplyWriteRunsElsewhere()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        int writerThread = 0;
        using InstanceCoordinator main = new(_lockPath, _log)
        {
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
            ReplyWriting = () => Volatile.Write(ref writerThread, Environment.CurrentManagedThreadId),
        };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request => requests.Writer.TryWrite(request));

        Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
        InstanceRequest request = await NextAsync(requests);
        int completerThread = 0;
        Thread completer = new(() =>
        {
            completerThread = Environment.CurrentManagedThreadId;
            Assert.True(request.TryBegin());
            request.Complete(InstanceReply.Opened);
        })
        {
            IsBackground = true,
        };
        completer.Start();

        Assert.True(completer.Join(HangGuard));
        Assert.Equal(InstanceRequestOutcome.Confirmed, await waiting.WaitAsync(HangGuard, Token));
        Assert.NotEqual(0, Volatile.Read(ref writerThread));
        Assert.NotEqual(completerThread, Volatile.Read(ref writerThread));
    }

    [Fact]
    public async Task Request_ClientNeverReads_ConnectionEndsWithinServerBound()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "On Unix the kernel send buffer takes the reply at once, so the server's write never waits on the client."
        );
        TaskCompletionSource unread = WhenWarningLogged("A launcher on pipe");
        using InstanceCoordinator main = new(_lockPath, _log) { ReplyBound = HangGuard, ReplyWriteBound = TimeSpan.FromMilliseconds(1) };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(BeginAndConfirm);

        NamedPipeClientStream raw = NewRawClient(main);
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            await raw.WriteAsync(InstanceFrame.Encode("open-x"), Token);
            await raw.FlushAsync(Token);

            // The client never reads, so the reply's write runs out of its bound and the server closes the connection.
            await unread.Task.WaitAsync(HangGuard, Token);
        }

        Assert.Contains(_log.Warnings, warning => warning.Message.Contains("did not read its reply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_ServerReplyBoundPasses_RequestAbandonedAndClosedWithoutReply()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        using InstanceCoordinator main = new(_lockPath, _log) { ReplyBound = TimeSpan.FromMilliseconds(1), ReplyWriteBound = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request => requests.Writer.TryWrite(request));

        InstanceRequestOutcome outcome = await InstanceCoordinator
            .RequestAsync(_lockPath, "open-x", HangGuard, _log, Token)
            .WaitAsync(HangGuard, Token);
        InstanceRequest request = await NextAsync(requests);

        // No reply was written: the launcher read the end of the stream, which it counts as closing.
        Assert.Equal(InstanceRequestOutcome.Closing, outcome);
        Assert.Equal(InstanceReply.Closing, await request.Reply.WaitAsync(HangGuard, Token));
        Assert.False(request.TryBegin());
        Assert.Contains(_log.Warnings, warning => warning.Message.Contains("got no reply within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_BegunRequestCompletedAfterStop_StillWritesItsReply()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        InstanceCoordinator main = new(_lockPath, _log)
        {
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
            DisposeWait = HangGuard,
        };
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(request =>
            {
                Assert.True(request.TryBegin());
                requests.Writer.TryWrite(request);
            });

            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            InstanceRequest request = await NextAsync(requests);
            main.StopAnswering();
            request.Complete(InstanceReply.Opened);

            Assert.Equal(InstanceRequestOutcome.Confirmed, await waiting.WaitAsync(HangGuard, Token));
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task Server_PendingReadAndWriteOnOneStream_ReplyArrives()
    {
        using InstanceCoordinator main = StartRequestServer(BeginAndConfirm);

        NamedPipeClientStream raw = NewRawClient(main);
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            await raw.WriteAsync(InstanceFrame.Encode("open-x"), Token);
            await raw.FlushAsync(Token);

            // The server has a one-byte read pending on this stream, watching for a hang-up, while it writes the reply.
            string reply = await InstanceFrame.ReadAsync(raw, Token).WaitAsync(HangGuard, Token);

            Assert.Equal("opened", reply);
        }
    }

    [Fact]
    public async Task Signal_Activate_StillDeliveredWithoutAReply()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        using InstanceCoordinator main = StartRequestServer(request => requests.Writer.TryWrite(request));

        NamedPipeClientStream raw = NewRawClient(main);
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            await raw.WriteAsync(InstanceFrame.Encode(InstanceMessages.Activate), Token);
            await raw.FlushAsync(Token);
            InstanceRequest request = await NextAsync(requests);

            Assert.Equal(InstanceMessages.Activate, request.Message);
            Assert.False(request.ExpectsReply);
            // The server closes an activate connection once it is queued, without writing anything.
            Assert.Equal(0, await ReadOrZeroAsync(raw));
        }
    }

    [Fact]
    public async Task StopAnswering_WithPendingRequest_RepliesClosingBeforeWaitingOnTheHandler()
    {
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource twoQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
            FrameQueued = total =>
            {
                if (total == 2)
                {
                    twoQueued.TrySetResult();
                }
            },
        };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request =>
        {
            if (request.Message == "first")
            {
                firstEntered.TrySetResult();
                release.Task.Wait(HangGuard, Token);
            }
        });
        Thread stopping = new(main.StopAnswering) { IsBackground = true };
        try
        {
            Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, "first", HangGuard, _log, Token));
            await firstEntered.Task.WaitAsync(HangGuard, Token);
            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            await twoQueued.Task.WaitAsync(HangGuard, Token);

            stopping.Start();

            Assert.Equal(InstanceRequestOutcome.Closing, await waiting.WaitAsync(HangGuard, Token));
            Assert.True(stopping.IsAlive, "StopAnswering returned while the handler was still blocked.");
        }
        finally
        {
            release.TrySetResult();
            if (stopping.ThreadState != ThreadState.Unstarted)
            {
                Assert.True(stopping.Join(HangGuard), "StopAnswering did not return after the handler was released.");
            }

            main.Dispose();
        }
    }

    [Fact]
    public async Task StopAnswering_HandlerNeverCompletes_ReturnsWithinBound()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(request => requests.Writer.TryWrite(request));
            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            InstanceRequest request = await NextAsync(requests);

            Thread stopping = new(main.StopAnswering) { IsBackground = true };
            stopping.Start();

            Assert.True(stopping.Join(HangGuard), "StopAnswering waited on a request the app never completed.");
            Assert.Equal(InstanceRequestOutcome.Closing, await waiting.WaitAsync(HangGuard, Token));
            Assert.False(request.TryBegin());
            Assert.DoesNotContain(_log.Warnings, warning => warning.Message.Contains("did not stop", StringComparison.Ordinal));
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public void Request_CompleteOpenedBeforeBegin_ThrowsAndLeavesItPending()
    {
        InstanceRequest request = new("open-x", expectsReply: true);

        Assert.Throws<InvalidOperationException>(() => request.Complete(InstanceReply.Opened));

        Assert.False(request.Reply.IsCompleted);
        Assert.True(request.TryBegin());
    }

    [Fact]
    public async Task SignalAsync_InOnlyServer_StillReceivesActivate()
    {
        using InstanceCoordinator unstarted = new(_lockPath, _log);
        NamedPipeServerStream raw = new(
            unstarted.Endpoint,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await using (raw)
        {
            Task connected = raw.WaitForConnectionAsync(Token);
            Task<bool> signal = InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token);
            await Task.WhenAny(connected, signal).WaitAsync(HangGuard, Token);

            Assert.True(
                connected.IsCompletedSuccessfully,
                $"The signal ended before connecting. Warnings: {string.Join(" | ", _log.Warnings.Select(warning => warning.Message))}"
            );
            Assert.Equal(InstanceMessages.Activate, await InstanceFrame.ReadAsync(raw, Token).WaitAsync(HangGuard, Token));
            Assert.True(await signal.WaitAsync(HangGuard, Token));
        }
    }

    [Fact]
    public async Task StopAnswering_AbandonsOpenRequestsBeforeCancellingTheServer()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        InstanceRequest? pending = null;
        bool? begunAtCancel = null;
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
            ShutdownCancelling = () => begunAtCancel = pending?.TryBegin(),
        };
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(request => requests.Writer.TryWrite(request));
            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            pending = await NextAsync(requests);

            main.StopAnswering();

            // Abandoned synchronously, before the connections hear of the stop, so no UI job can take it from then on.
            Assert.False(begunAtCancel);
            Assert.False(pending.TryBegin());
            Assert.Equal(InstanceRequestOutcome.Closing, await waiting.WaitAsync(HangGuard, Token));
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task Request_ReplyOverTheReplyCap_IsRefusedUnread()
    {
        using InstanceCoordinator unstarted = new(_lockPath, _log);
        NamedPipeServerStream raw = new(
            unstarted.Endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await using (raw)
        {
            Task<InstanceRequestOutcome> request = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            await raw.WaitForConnectionAsync(Token).WaitAsync(HangGuard, Token);
            Assert.Equal("open-x", await InstanceFrame.ReadAsync(raw, Token).WaitAsync(HangGuard, Token));
            Task write = raw.WriteAsync(InstanceFrame.Encode("opened" + new string(' ', InstanceFrame.MaxReplyPayloadBytes)), Token).AsTask();

            Assert.Equal(InstanceRequestOutcome.Refused, await request.WaitAsync(HangGuard, Token));
            // Refused at the length, before the payload is read: the reply is not one, rather than an unknown one.
            Assert.Contains(_log.Warnings, warning => warning.Message.Contains("is not one", StringComparison.Ordinal));
            _ = write.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task Dispose_WaitsForConnectionTasksBeforeLockReleasing()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        int? openAtRelease = null;
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        main.LockReleasing = () => openAtRelease = main.OpenConnectionCount;
        Thread disposing = new(main.Dispose) { IsBackground = true };
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(request =>
            {
                // Begun and not yet answered: its connection stays open after the stop, waiting for the reply.
                Assert.True(request.TryBegin());
                requests.Writer.TryWrite(request);
            });
            Task<InstanceRequestOutcome> waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            InstanceRequest request = await NextAsync(requests);

            disposing.Start();
            await WaitUntilBlockedOrEndedAsync(disposing);

            // The connection holds until the test answers the request, so Dispose cannot have released the lock yet.
            Assert.True(disposing.IsAlive, "Dispose returned while a connection was still open.");
            Assert.Null(openAtRelease);
            request.Complete(InstanceReply.Opened);

            Assert.True(disposing.Join(HangGuard), "Dispose did not return after the last connection ended.");
            Assert.Equal(0, openAtRelease);
            Assert.Equal(InstanceRequestOutcome.Confirmed, await waiting.WaitAsync(HangGuard, Token));
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_ThenNewMain_EndpointStillReachable()
    {
        var requests = Channel.CreateUnbounded<InstanceRequest>();
        InstanceCoordinator first = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        Task<InstanceRequestOutcome> waiting;
        try
        {
            Assert.True(first.TryAcquireMainLock());
            first.StartServer(request => requests.Writer.TryWrite(request));
            waiting = InstanceCoordinator.RequestAsync(_lockPath, "open-x", HangGuard, _log, Token);
            await NextAsync(requests);
        }
        finally
        {
            first.Dispose();
        }

        Assert.Equal(InstanceRequestOutcome.Closing, await waiting.WaitAsync(HangGuard, Token));

        // A connection closed after the lock was released would, on Unix, delete the new main's socket file.
        using InstanceCoordinator second = StartMain(out TaskCompletionSource<string> received);
        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token));
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_InfiniteTimeout_IsDelivered()
    {
        using InstanceCoordinator main = StartMain(out TaskCompletionSource<string> received);

        bool sent = await InstanceCoordinator
            .SignalAsync(_lockPath, InstanceMessages.Activate, Timeout.InfiniteTimeSpan, _log, Token)
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
            _lockPath,
            InstanceMessages.Activate,
            TimeSpan.FromMilliseconds(milliseconds),
            _log,
            Token
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => signal.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task SignalAsync_ThreeConcurrentSignals_AllArrive()
    {
        string[] messages = ["one", "two", "three"];
        ConcurrentQueue<string> arrived = new();
        TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request =>
        {
            string message = request.Message;
            arrived.Enqueue(message);
            if (arrived.Count == messages.Length)
            {
                allArrived.TrySetResult();
            }
        });

        bool[] sent = await Task.WhenAll(messages.Select(message => InstanceCoordinator.SignalAsync(_lockPath, message, HangGuard, _log, Token)));
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

        // Dispose's bounds become the hang guard, and the handler is released once the count is logged, so Dispose returns as soon
        // as the handler does instead of waiting out a fixed bound.
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        _log.WarningLogged = message =>
        {
            if (message.StartsWith("Dropped ", StringComparison.Ordinal))
            {
                release.TrySetResult();
            }
        };
        Assert.True(main.TryAcquireMainLock());
        main.FrameQueued = total =>
        {
            if (total == 3)
            {
                threeQueued.TrySetResult();
            }
        };
        main.StartServer(request =>
        {
            string message = request.Message;
            if (message == "first")
            {
                firstEntered.TrySetResult();
                release.Task.Wait(HangGuard, Token);
            }
        });

        Task<InstanceRequestOutcome> second = Task.FromResult(InstanceRequestOutcome.NoServer);
        Task<InstanceRequestOutcome> third = second;
        try
        {
            Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, "first", HangGuard, _log, Token));
            await firstEntered.Task.WaitAsync(HangGuard, Token);
            second = InstanceCoordinator.RequestAsync(_lockPath, "second", HangGuard, _log, Token);
            third = InstanceCoordinator.RequestAsync(_lockPath, "third", HangGuard, _log, Token);

            // Wait until the server has queued all three.
            await threeQueued.Task.WaitAsync(HangGuard, Token);
        }
        finally
        {
            try
            {
                // The handler stays blocked until Dispose has counted, so the two queued messages are still queued then.
                main.Dispose();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        Assert.Contains(_log.Warnings, warning => warning.Message.StartsWith("Dropped 2 instance message(s)", StringComparison.Ordinal));

        // The dropped requests were abandoned, and each launcher heard so.
        Assert.Equal(InstanceRequestOutcome.Closing, await second.WaitAsync(HangGuard, Token));
        Assert.Equal(InstanceRequestOutcome.Closing, await third.WaitAsync(HangGuard, Token));
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        InstanceCoordinator main = StartMain(out _);

        main.Dispose();
        main.Dispose();
    }

    [Fact]
    public async Task Dispose_ConcurrentSecondCaller_ReturnsOnlyAfterLockReleased()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceCoordinator main = await StartMainWithBlockedHandlerAsync(_ => release.Task.Wait(HangGuard, Token));
        TaskCompletionSource firstCallerCountedQueue = WhenWarningLogged("Dropped 1 instance message(s)");
        using InstanceCoordinator probe = new(_lockPath, _log);
        bool? acquiredRightAfterSecondDispose = null;

        Thread first = new(main.Dispose) { IsBackground = true };
        Thread second = new(() =>
        {
            main.Dispose();
            acquiredRightAfterSecondDispose = probe.TryAcquireMainLock();
        })
        {
            IsBackground = true,
        };
        try
        {
            first.Start();
            // The first caller logs the drop count after the accept loop stops and before it waits on the blocked handler.
            await firstCallerCountedQueue.Task.WaitAsync(HangGuard, Token);
            second.Start();
            await WaitUntilBlockedOrEndedAsync(second);

            Assert.True(second.IsAlive, "The second Dispose returned while the first still held the lock.");
            Assert.Null(acquiredRightAfterSecondDispose);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(second.Join(HangGuard), "The second Dispose did not return after the first released the lock.");
        Assert.True(first.Join(HangGuard), "The first Dispose did not return after the handler was released.");
        Assert.True(acquiredRightAfterSecondDispose);
    }

    [Fact]
    public async Task Dispose_FromInsideHandler_ReturnsAtOnce()
    {
        TaskCompletionSource firstCallerCountedQueue = WhenWarningLogged("Dropped 1 instance message(s)");
        using InstanceCoordinator probe = new(_lockPath, _log);
        bool? acquiredRightAfterHandlerDispose = null;
        InstanceCoordinator? main = null;
        main = await StartMainWithBlockedHandlerAsync(_ =>
        {
            firstCallerCountedQueue.Task.Wait(HangGuard, Token);
            main!.Dispose();
            acquiredRightAfterHandlerDispose = probe.TryAcquireMainLock();
        });

        Thread first = new(main.Dispose) { IsBackground = true };
        first.Start();

        Assert.True(first.Join(HangGuard), "The first Dispose did not return after the handler returned.");
        // The handler's own Dispose returned while the first caller was still waiting on it, so the lock was still held.
        Assert.False(acquiredRightAfterHandlerDispose);
        Assert.DoesNotContain(_log.Warnings, warning => warning.Message.Contains("did not stop", StringComparison.Ordinal));
        Assert.True(probe.TryAcquireMainLock());
    }

    [Fact]
    public async Task StartServer_SquattedPipeName_LogsHeldByAnotherAccount_AndSignalFailsFast()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a Windows pipe name lives in a namespace another process can take first.");
        TaskCompletionSource heldLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _log.ErrorLogged = message =>
        {
            if (message.Contains("held by another account", StringComparison.Ordinal))
            {
                heldLogged.TrySetResult();
            }
        };
        TaskCompletionSource<string> received = NewSource<string>();
        using InstanceCoordinator main = new(_lockPath, _log);

        // The squatter takes the name first, without CurrentUserOnly. Its input buffer lets a client's write complete unread.
        using NamedPipeServerStream squatter = new(
            main.Endpoint,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 0
        );
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(request => received.TrySetResult(request.Message));
        await heldLogged.Task.WaitAsync(HangGuard, Token);

        Task squatterConnected = squatter.WaitForConnectionAsync(Token);
        bool sent = await InstanceCoordinator.SignalAsync(_lockPath, "squatted", HangGuard, _log, Token).WaitAsync(HangGuard, Token);
        await squatterConnected.WaitAsync(HangGuard, Token);
        string atSquatter = await InstanceFrame.ReadAsync(squatter, Token).WaitAsync(HangGuard, Token);

        // A same-account squatter passes the client's owner check, so the signal reaches it; it never waits out its timeout.
        Assert.True(sent);
        Assert.Equal("squatted", atSquatter);
        // Our server never listened, so its handler cannot have received anything.
        Assert.False(received.Task.IsCompleted);
        Assert.Single(_log.Errors, error => error.Message.Contains("held by another account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_FromInsideHandler_AsFirstCaller_ReleasesLockAtOnce()
    {
        TaskCompletionSource<bool> acquiredRightAfterHandlerDispose = NewSource<bool>();
        bool? pidFileExistedAfterHandlerDispose = null;
        using InstanceCoordinator probe = new(_lockPath, _log);
        InstanceCoordinator main = new(_lockPath, _log);
        try
        {
            Assert.True(main.TryAcquireMainLock());
            main.StartServer(_ =>
            {
                main.Dispose();
                pidFileExistedAfterHandlerDispose = File.Exists(PidPath);
                acquiredRightAfterHandlerDispose.TrySetResult(probe.TryAcquireMainLock());
            });

            Assert.True(
                await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token),
                $"Warnings: {string.Join(" | ", _log.Warnings.Select(warning => $"{warning.Message} [{warning.Exception?.Message}]"))}"
            );

            // The handler's Dispose did not wait on the handler it runs in, so the lock was free before the handler returned.
            Assert.True(await acquiredRightAfterHandlerDispose.Task.WaitAsync(HangGuard, Token));
            Assert.False(pidFileExistedAfterHandlerDispose);
            Assert.DoesNotContain(_log.Warnings, warning => warning.Message.Contains("probably blocked", StringComparison.Ordinal));
        }
        finally
        {
            // Releases the lock before the fixture deletes its folder when the handler never ran; a second Dispose is safe.
            main.Dispose();
        }
    }

    [Fact]
    public async Task StartServer_FollowOnListenerFails_NextListenerIsNotCreatedAsFirstInstance()
    {
        ConcurrentQueue<bool> creations = new();
        TaskCompletionSource<bool> createdAfterFailure = NewSource<bool>();
        bool failed = false;
        using InstanceCoordinator main = new(_lockPath, _log);
        main.ListenerCreating = firstInstance =>
        {
            creations.Enqueue(firstInstance);
            if (failed)
            {
                createdAfterFailure.TrySetResult(firstInstance);
            }
            else if (!firstInstance)
            {
                // The follow-on listener created once a client connects fails, so the next pass has no listener at all.
                failed = true;
                throw new IOException("listener creation failure under test");
            }
        };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(_ => { });

        // On Windows the frame is lost with its connection and the signal retries; on Unix it is in the socket buffer at once.
        Assert.True(
            await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token),
            $"Warnings: {string.Join(" | ", _log.Warnings.Select(warning => $"{warning.Message} [{warning.Exception?.Message}]"))}"
        );

        Assert.False(
            await createdAfterFailure.Task.WaitAsync(HangGuard, Token),
            $"Listener creations (first instance?): {string.Join(", ", creations)}"
        );
        Assert.DoesNotContain(_log.Errors, error => error.Message.Contains("held by another account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartServer_StaleSocketFileBesideTheLock_StillListens()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only a Unix endpoint is a file on disk.");
        TaskCompletionSource<string> received = NewSource<string>();
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());

        // A leftover that is not a socket (a crashed main leaves a socket; a regular file is the case the server must still replace):
        // the server must unlink it before binding, which it skips under FirstPipeInstance.
        File.WriteAllBytes(main.Endpoint, []);
        main.StartServer(request => received.TrySetResult(request.Message));

        Assert.True(
            await InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token),
            $"Warnings: {string.Join(" | ", _log.Warnings.Select(warning => $"{warning.Message} [{warning.Exception?.Message}]"))}"
        );
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task SignalAsync_NonSocketFileAtEndpointUntilServerStarts_RetriesAndIsDelivered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only a Unix endpoint is a file that can be something other than a socket.");
        using InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        File.WriteAllBytes(main.Endpoint, []);
        TaskCompletionSource secondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        InstanceCoordinator.ConnectAttempted.Value = _ =>
        {
            if (Interlocked.Increment(ref attempts) == 2)
            {
                secondAttempt.TrySetResult();
            }
        };
        TaskCompletionSource<string> received = NewSource<string>();

        // The server starts only once the signal has met the file and retried, or has given up on it.
        Task<bool> signal = InstanceCoordinator.SignalAsync(_lockPath, InstanceMessages.Activate, HangGuard, _log, Token);
        await Task.WhenAny(secondAttempt.Task, signal).WaitAsync(HangGuard, Token);
        main.StartServer(request => received.TrySetResult(request.Message));

        Assert.True(
            await signal.WaitAsync(HangGuard, Token),
            $"Connect attempts: {attempts}; "
                + $"warnings: {string.Join(" | ", _log.Warnings.Select(warning => $"{warning.Message} [{warning.Exception?.Message}]"))}"
        );
        Assert.Equal(InstanceMessages.Activate, await received.Task.WaitAsync(HangGuard, Token));
    }

    [Theory]
    [InlineData(SocketError.NotSocket, true)]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.AddressNotAvailable, true)]
    [InlineData(SocketError.TryAgain, true)]
    [InlineData(SocketError.AccessDenied, false)]
    public void IsRetryableSocketError_ClassifiesConnectFailures(SocketError error, bool expected) =>
        Assert.Equal(expected, InstanceCoordinator.IsRetryableSocketError(new SocketException((int)error)));

    [Fact]
    public void StartServer_WithoutLock_Throws()
    {
        using InstanceCoordinator main = new(_lockPath, _log);

        Assert.Throws<InvalidOperationException>(() => main.StartServer(_ => { }));
    }

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Header(int declaredLength)
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        return header;
    }

    private static NamedPipeClientStream NewRawClient(InstanceCoordinator main) =>
        new(".", main.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private InstanceCoordinator StartRequestServer(Action<InstanceRequest> onRequest)
    {
        // Both reply bounds become the hang guard, so no passing run has to beat them.
        InstanceCoordinator main = new(_lockPath, _log) { ReplyBound = HangGuard, ReplyWriteBound = HangGuard };
        Assert.True(main.TryAcquireMainLock());
        main.StartServer(onRequest);
        return main;
    }

    private static void BeginAndConfirm(InstanceRequest request)
    {
        Assert.True(request.TryBegin());
        request.Complete(InstanceReply.Opened);
    }

    private static async Task<InstanceRequest> NextAsync(Channel<InstanceRequest> requests) =>
        await requests.Reader.ReadAsync(Token).AsTask().WaitAsync(HangGuard, Token);

    /// <summary>Reads one byte; 0 when the server closed the connection, which on Windows may also surface as a broken pipe.</summary>
    private static async Task<int> ReadOrZeroAsync(NamedPipeClientStream raw)
    {
        try
        {
            return await raw.ReadAsync(new byte[1], Token).AsTask().WaitAsync(HangGuard, Token);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Connects to <paramref name="main"/>'s server, writes <paramref name="bytes"/> as they are and closes.</summary>
    private static async Task SendRawAsync(InstanceCoordinator main, byte[] bytes)
    {
        NamedPipeClientStream raw = NewRawClient(main);
        await using (raw)
        {
            await raw.ConnectAsync(HangGuard, Token);
            await raw.WriteAsync(bytes, Token);
            await raw.FlushAsync(Token);
        }
    }

    /// <summary>Writes <paramref name="content"/> as the pid file and checks that reading it gives null and one info line.</summary>
    private void AssertReadIsNullWithOneInfo(string content)
    {
        Directory.CreateDirectory(_stateRoot);
        File.WriteAllText(PidPath, content);

        Assert.Null(InstanceCoordinator.TryReadMainPid(_lockPath, _log));

        Assert.Single(_log.Infos);
        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Errors);
    }

    private InstanceCoordinator StartMain(out TaskCompletionSource<string> received)
    {
        InstanceCoordinator main = new(_lockPath, _log);
        Assert.True(main.TryAcquireMainLock());
        TaskCompletionSource<string> source = NewSource<string>();
        main.StartServer(request => source.TrySetResult(request.Message));
        received = source;
        return main;
    }

    /// <summary>
    /// Starts a main whose handler runs <paramref name="onFirst"/> for the message "first", and returns once that handler has
    /// started and a second message is queued behind it.
    /// </summary>
    private async Task<InstanceCoordinator> StartMainWithBlockedHandlerAsync(Action<string> onFirst)
    {
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource twoQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Dispose's own bounds become the hang guard, so a Dispose that waits wrongly hangs to the guard instead of racing 5 s.
        InstanceCoordinator main = new(_lockPath, _log)
        {
            DisposeWait = HangGuard,
            ReplyBound = HangGuard,
            ReplyWriteBound = HangGuard,
        };
        Assert.True(main.TryAcquireMainLock());
        main.FrameQueued = total =>
        {
            if (total == 2)
            {
                twoQueued.TrySetResult();
            }
        };
        main.StartServer(request =>
        {
            string message = request.Message;
            if (message == "first")
            {
                firstEntered.TrySetResult();
                onFirst(message);
            }
        });

        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, "first", HangGuard, _log, Token));
        await firstEntered.Task.WaitAsync(HangGuard, Token);
        Assert.True(await InstanceCoordinator.SignalAsync(_lockPath, "second", HangGuard, _log, Token));
        await twoQueued.Task.WaitAsync(HangGuard, Token);
        return main;
    }

    private TaskCompletionSource WhenWarningLogged(string prefix)
    {
        TaskCompletionSource logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _log.WarningLogged = message =>
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                logged.TrySetResult();
            }
        };
        return logged;
    }

    /// <summary>Completes when an error whose message starts with <paramref name="prefix"/> is logged.</summary>
    private TaskCompletionSource WhenErrorLogged(string prefix)
    {
        TaskCompletionSource logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _log.ErrorLogged = message =>
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                logged.TrySetResult();
            }
        };
        return logged;
    }

    /// <summary>Waits until <paramref name="thread"/> is blocked in a wait or has ended, polling with a sleep, never a spin.</summary>
    private static async Task WaitUntilBlockedOrEndedAsync(Thread thread)
    {
        long start = Stopwatch.GetTimestamp();
        while (thread.IsAlive && (thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < HangGuard, "The thread neither blocked nor ended.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), Token);
        }
    }

    private sealed class RecordingLog : IAppLog
    {
        private readonly ConcurrentQueue<(string Message, Exception? Exception)> _warnings = new();
        private readonly ConcurrentQueue<(string Message, Exception? Exception)> _errors = new();
        private readonly ConcurrentQueue<string> _infos = new();

        public IReadOnlyCollection<string> Infos => _infos;

        public IReadOnlyCollection<(string Message, Exception? Exception)> Warnings => _warnings;

        public IReadOnlyCollection<(string Message, Exception? Exception)> Errors => _errors;

        /// <summary>Called with each warning after it is recorded.</summary>
        public Action<string>? WarningLogged { get; set; }

        /// <summary>Called with each error after it is recorded.</summary>
        public Action<string>? ErrorLogged { get; set; }

        public void LogInfo(string message) => _infos.Enqueue(message);

        public void LogWarning(string message, Exception? exception = null)
        {
            _warnings.Enqueue((message, exception));
            WarningLogged?.Invoke(message);
        }

        public void LogError(string message, Exception? exception = null)
        {
            _errors.Enqueue((message, exception));
            ErrorLogged?.Invoke(message);
        }
    }
}
