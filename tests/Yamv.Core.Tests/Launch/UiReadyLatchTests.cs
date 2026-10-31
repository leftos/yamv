using System.Collections.Concurrent;
using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class UiReadyLatchTests
{
    /// <summary>A hang guard: a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public void Jobs_posted_before_open_run_once_in_order_when_it_opens()
    {
        UiReadyLatch latch = new();
        List<string> ran = [];
        latch.Post(() => ran.Add("first"));
        latch.Post(() => ran.Add("second"));
        latch.Post(() => ran.Add("third"));
        Assert.Empty(ran);

        latch.Open(job => job());

        Assert.Equal(["first", "second", "third"], ran);
    }

    [Fact]
    public void Jobs_posted_after_open_go_straight_through()
    {
        UiReadyLatch latch = new();
        List<Action> posted = [];
        latch.Open(posted.Add);
        List<string> ran = [];

        latch.Post(() => ran.Add("after"));

        Action job = Assert.Single(posted);
        job();
        Assert.Equal(["after"], ran);
    }

    [Fact]
    public void Post_never_blocks_the_caller()
    {
        UiReadyLatch latch = new();
        var ran = new ConcurrentQueue<string>();
        using var otherPosted = new ManualResetEventSlim();
        latch.Post(() => ran.Enqueue("queued"));

        // The poster waits for another thread's post while the latch is releasing: that post must complete, never wait on Open.
        bool otherFinished = false;
        latch.Open(job =>
        {
            if (!otherFinished)
            {
                var other = new Thread(() =>
                {
                    latch.Post(() => ran.Enqueue("during open"));
                    otherPosted.Set();
                })
                {
                    IsBackground = true,
                };
                other.Start();
                otherFinished = otherPosted.Wait(HangGuard);
                Assert.True(otherFinished, "a post made while the latch was opening did not return");
            }

            job();
        });

        Assert.Equal(["queued", "during open"], ran);
    }

    [Fact]
    public void Open_twice_releases_nothing_twice()
    {
        UiReadyLatch latch = new();
        List<string> ran = [];
        latch.Post(() => ran.Add("queued"));
        latch.Open(job => job());

        int secondPosts = 0;
        latch.Open(job =>
        {
            secondPosts++;
            job();
        });

        Assert.Equal(["queued"], ran);
        Assert.Equal(0, secondPosts);
        latch.Post(() => ran.Add("after"));
        Assert.Equal(["queued", "after"], ran);
    }
}
