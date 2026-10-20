using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Yamv.Core.Session;

namespace Yamv.Core.Tests.Session;

public sealed class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Schedule_RepeatedWithinTheDelay_RunsTheActionOnce()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        using var debouncer = new Debouncer(Delay, time, () => runs++, Fail);

        debouncer.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(300));
        debouncer.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, runs);

        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Schedule_AfterTheActionRan_SchedulesAnotherRun()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        using var debouncer = new Debouncer(Delay, time, () => runs++, Fail);
        debouncer.Schedule();
        time.Advance(Delay);

        debouncer.Schedule();
        time.Advance(Delay);

        Assert.Equal(2, runs);
    }

    [Fact]
    public void Flush_PendingAction_RunsItAtOnceAndNothingFollows()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        using var debouncer = new Debouncer(Delay, time, () => runs++, Fail);
        debouncer.Schedule();

        debouncer.Flush();
        Assert.Equal(1, runs);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Flush_WithNothingPending_DoesNothing()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        using var debouncer = new Debouncer(Delay, time, () => runs++, Fail);

        debouncer.Flush();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void Dispose_WithAPendingAction_CancelsIt()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        var debouncer = new Debouncer(Delay, time, () => runs++, Fail);
        debouncer.Schedule();

        debouncer.Dispose();
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(0, runs);
    }

    [Fact]
    public void Schedule_AfterDispose_Throws()
    {
        var time = new FakeTimeProvider();
        var debouncer = new Debouncer(Delay, time, () => { }, Fail);
        debouncer.Dispose();

        Assert.Throws<ObjectDisposedException>(debouncer.Schedule);
    }

    [Fact]
    public void Flush_AfterDispose_DoesNothing()
    {
        var time = new FakeTimeProvider();
        int runs = 0;
        var debouncer = new Debouncer(Delay, time, () => runs++, Fail);
        debouncer.Schedule();
        debouncer.Dispose();

        debouncer.Flush();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void ActionThatThrows_ReachesOnError_AndALaterScheduleStillRuns()
    {
        var time = new FakeTimeProvider();
        var errors = new List<Exception>();
        int runs = 0;
        using var debouncer = new Debouncer(
            Delay,
            time,
            () =>
            {
                runs++;
                throw new InvalidOperationException("the save failed");
            },
            errors.Add
        );

        debouncer.Schedule();
        time.Advance(Delay);
        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(errors[0]);

        debouncer.Schedule();
        time.Advance(Delay);

        Assert.Equal(2, runs);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task TimerRunAndFlush_AtTheSameTime_NeverRunTheActionTwiceAtOnce()
    {
        var time = new FakeTimeProvider();
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int running = 0;
        int mostAtOnce = 0;
        int runs = 0;
        var errors = new ConcurrentQueue<Exception>();
        var debouncer = new Debouncer(
            Delay,
            time,
            () =>
            {
                Raise(ref mostAtOnce, Interlocked.Increment(ref running));
                Interlocked.Increment(ref runs);
                inside.Set();
                release.Wait();
                Interlocked.Decrement(ref running);
            },
            errors.Enqueue
        );

        try
        {
            debouncer.Schedule();
            var timerRun = Task.Run(() => time.Advance(Delay), TestContext.Current.CancellationToken);
            Assert.True(inside.Wait(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), "the timer never ran the action");

            var schedule = Task.Run(debouncer.Schedule, TestContext.Current.CancellationToken);
            await schedule.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var flushThread = new Thread(debouncer.Flush) { IsBackground = true };
            flushThread.Start();
            WaitUntil(() => (flushThread.ThreadState & ThreadState.WaitSleepJoin) != 0 || Volatile.Read(ref runs) == 2);
            release.Set();
            Assert.True(flushThread.Join(TimeSpan.FromMinutes(2)), "the flush never finished");
            await timerRun.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            debouncer.Dispose();
        }

        Assert.Empty(errors);
        Assert.Equal(1, Volatile.Read(ref mostAtOnce));
        Assert.Equal(2, runs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithANonPositiveDelay_Throws(int milliseconds)
    {
        var time = new FakeTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new Debouncer(TimeSpan.FromMilliseconds(milliseconds), time, () => { }, _ => { }));
    }

    private static void Fail(Exception exception) => throw new InvalidOperationException("OnError was called", exception);

    private static void WaitUntil(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)TimeSpan.FromMinutes(2).TotalMilliseconds;
        var spin = new SpinWait();
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("the condition never held");
            }

            spin.SpinOnce();
        }
    }

    private static void Raise(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
