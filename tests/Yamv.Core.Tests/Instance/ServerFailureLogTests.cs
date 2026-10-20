using System.Collections.Concurrent;
using Yamv.Core.Diagnostics;
using Yamv.Core.Instance;

namespace Yamv.Core.Tests.Instance;

public sealed class ServerFailureLogTests
{
    private readonly RecordingLog _log = new();
    private readonly ServerFailureLog _failures;

    public ServerFailureLogTests()
    {
        _failures = new ServerFailureLog(_log, "The server under test");
    }

    private static IOException FailureA => new("disk gone");

    private static InvalidOperationException FailureB => new("other failure");

    [Fact]
    public void Record_SameFailureThreeTimesThenAnother_LogsTwoLinesTheSecondCountingTheRepeats()
    {
        _failures.Record(FailureA);
        _failures.Record(FailureA);
        _failures.Record(FailureA);
        _failures.Record(FailureB);

        Assert.Collection(
            _log.Errors,
            first => Assert.IsType<IOException>(first.Exception),
            second =>
            {
                Assert.IsType<InvalidOperationException>(second.Exception);
                Assert.Contains("repeated 2", second.Message, StringComparison.Ordinal);
            }
        );
    }

    [Fact]
    public void Record_RepeatedFailures_DelayDoublesFrom100MsAndIsCappedAt30s()
    {
        double[] delays = [.. Enumerable.Range(0, 11).Select(_ => _failures.Record(FailureA).TotalMilliseconds)];

        double[] expected = [100, 200, 400, 800, 1_600, 3_200, 6_400, 12_800, 25_600, 30_000, 30_000];
        Assert.Equal(expected, delays);
    }

    [Fact]
    public void RecordSuccess_AfterSuppressedRepeats_LogsRecoveryAndResetsTheDelay()
    {
        _failures.Record(FailureA);
        _failures.Record(FailureA);
        _failures.Record(FailureA);

        _failures.RecordSuccess();

        string info = Assert.Single(_log.Infos);
        Assert.Contains("recovered after 2 repeat", info, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _failures.Record(FailureB));
    }

    [Fact]
    public void RecordSuccess_WithNoSuppressedRepeats_LogsNothing()
    {
        _failures.Record(FailureA);

        _failures.RecordSuccess();

        Assert.Empty(_log.Infos);
    }

    [Fact]
    public void Record_SameFailureAfterASuccess_IsLoggedAgain()
    {
        _failures.Record(FailureA);
        _failures.RecordSuccess();

        _failures.Record(FailureA);

        Assert.Equal(2, _log.Errors.Count);
    }

    [Fact]
    public void RecordConnectFailure_FirstInARowRetriesAtOnceAndLaterOnesBackOff()
    {
        TimeSpan? first = _failures.RecordConnectFailure(FailureA);
        TimeSpan? second = _failures.RecordConnectFailure(FailureA);
        TimeSpan? third = _failures.RecordConnectFailure(FailureA);
        _failures.RecordSuccess();
        TimeSpan? afterSuccess = _failures.RecordConnectFailure(FailureA);

        Assert.Null(first);
        Assert.Equal(TimeSpan.FromMilliseconds(100), second);
        Assert.Equal(TimeSpan.FromMilliseconds(200), third);
        Assert.Null(afterSuccess);
    }

    [Fact]
    public void RecordConnectFailure_LogsAWarningWithoutTheException()
    {
        _failures.RecordConnectFailure(FailureA);

        (string Message, Exception? Exception) warning = Assert.Single(_log.Warnings);
        Assert.Null(warning.Exception);
        Assert.Contains("disk gone", warning.Message, StringComparison.Ordinal);
        Assert.Empty(_log.Errors);
    }

    private sealed class RecordingLog : IAppLog
    {
        public ConcurrentQueue<string> Infos { get; } = new();

        public ConcurrentQueue<(string Message, Exception? Exception)> Warnings { get; } = new();

        public ConcurrentQueue<(string Message, Exception? Exception)> Errors { get; } = new();

        public void LogInfo(string message) => Infos.Enqueue(message);

        public void LogWarning(string message, Exception? exception = null) => Warnings.Enqueue((message, exception));

        public void LogError(string message, Exception? exception = null) => Errors.Enqueue((message, exception));
    }
}
