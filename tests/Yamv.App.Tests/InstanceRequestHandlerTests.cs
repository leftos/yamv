using System.Collections.Concurrent;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Instance;
using Yamv.Core.Launch;

namespace Yamv.App.Tests;

/// <summary>The instance server's handler, reached through a real coordinator on a private lock path.</summary>
public sealed class InstanceRequestHandlerTests : IDisposable
{
    private readonly RecordingAppLog _log = new();
    private int _activated;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly ConcurrentQueue<(IReadOnlyList<string> Files, InstanceRequest Request)> _opened = new();
    private readonly TestInstanceServer _server;
    private Action<Action> _post;

    public InstanceRequestHandlerTests()
    {
        _post = _posted.Enqueue;
        var handler = new InstanceRequestHandler(
            job => _post(job),
            () => Interlocked.Increment(ref _activated),
            (files, request) =>
            {
                _opened.Enqueue((files, request));
                return _take(request);
            },
            () => TimeSpan.FromMilliseconds(_uptimeMs),
            _log
        );
        _server = new TestInstanceServer(handler.Handle);
    }

    /// <summary>What the fake app does with a request on the UI thread: true when it took it. By default it leaves it alone.</summary>
    private Func<InstanceRequest, bool> _take = _ => false;

    private int _uptimeMs = 1234;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ReadinessLine_IsWrittenOnceForTheFirstOpenRequestTheAppTakes()
    {
        _post = job => job();
        _take = request =>
        {
            request.Complete(InstanceReply.Closing);
            return false;
        };
        Assert.Equal(InstanceRequestOutcome.Closing, await _server.SendOpenAsync([Path.Combine(Path.GetTempPath(), "yamv-closing.md")]));
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("first open request", StringComparison.Ordinal));

        // Each take sets the uptime its own readiness line would read: the handler runs one request at a time, so the second
        // take comes after the first request's line, whenever the launch hears its reply.
        int taken = 0;
        _take = request =>
        {
            taken++;
            _uptimeMs = taken == 1 ? 2345 : 3456;
            Assert.True(request.TryBegin());
            request.Complete(InstanceReply.Opened);
            return true;
        };
        Assert.Equal(InstanceRequestOutcome.Confirmed, await _server.SendOpenAsync([Path.Combine(Path.GetTempPath(), "yamv-first.md")]));
        Assert.Equal(InstanceRequestOutcome.Confirmed, await _server.SendOpenAsync([Path.Combine(Path.GetTempPath(), "yamv-second.md")]));

        Assert.Equal(3, _opened.Count);
        LogEntry line = Assert.Single(_log.Entries, entry => entry.Message.Contains("first open request", StringComparison.Ordinal));
        Assert.Equal("Instance server: the UI thread took its first open request 2345 ms after the process started.", line.Message);
    }

    [Fact]
    public async Task Open_WithMalformedPayload_IsRefused()
    {
        InstanceRequestOutcome outcome = await _server.SendAsync("open [\"not a full path\"]");

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
        Assert.Empty(_posted);
        Assert.Contains(
            _log.Entries,
            entry => entry.Message.StartsWith("Instance server: refusing a malformed open message", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task UnknownMessageWithReply_IsRefused()
    {
        InstanceRequestOutcome outcome = await _server.SendAsync("frobnicate");

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
        Assert.Empty(_posted);
        Assert.Contains(_log.Entries, entry => entry.Message == "Instance server: refusing an unknown message 'frobnicate'.");
    }

    [Fact]
    public async Task Activate_NeedsNoReplyAndPostsOneJob()
    {
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post = job =>
        {
            _posted.Enqueue(job);
            posted.TrySetResult();
        };

        bool delivered = await InstanceCoordinator.SignalAsync(
            _server.LockPath,
            InstanceMessages.Activate,
            TestInstanceServer.HangGuard,
            _log,
            TestContext.Current.CancellationToken
        );

        Assert.True(delivered);
        await posted.Task.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken);

        // The activate runs only when the UI thread runs its job, never on the server's pool thread.
        Assert.Equal(0, Volatile.Read(ref _activated));
        Assert.True(_posted.TryDequeue(out Action? job));
        job!();
        Assert.Equal(1, Volatile.Read(ref _activated));
        Assert.Empty(_posted);
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.StartsWith("Instance server: refusing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Open_PostsToTheUiThreadAndNeverBlocksThePoolThread()
    {
        string file = Path.Combine(Path.GetTempPath(), "yamv-open-" + Guid.NewGuid().ToString("N") + ".md");
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post = job =>
        {
            _posted.Enqueue(job);
            posted.TrySetResult();
        };

        Task<InstanceRequestOutcome> open = _server.SendOpenAsync([file]);
        await posted.Task.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken);

        // The UI job has not run, yet the handler already answers the next request: it returned without waiting on that job.
        Assert.Equal(InstanceRequestOutcome.Refused, await _server.SendAsync("frobnicate"));
        Assert.Empty(_opened);

        Assert.True(_posted.TryDequeue(out Action? job));
        job!();
        (IReadOnlyList<string> files, InstanceRequest request) = Assert.Single(_opened);
        Assert.Equal([file], files);
        Assert.True(request.TryBegin());
        request.Complete(InstanceReply.Opened);
        Assert.Equal(InstanceRequestOutcome.Confirmed, await open.WaitAsync(TestInstanceServer.HangGuard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_WhenThePostThrows_IsRefused()
    {
        _post = _ => throw new InvalidOperationException("The dispatcher has shut down.");

        InstanceRequestOutcome outcome = await _server.SendOpenAsync([Path.Combine(Path.GetTempPath(), "yamv-open.md")]);

        Assert.Equal(InstanceRequestOutcome.Refused, outcome);
        Assert.Empty(_opened);
        LogEntry error = Assert.Single(_log.Entries, entry => entry.Level == "error");
        Assert.Equal("Instance server: could not hand an open of 1 file(s) to the UI thread; refusing it.", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }
}
