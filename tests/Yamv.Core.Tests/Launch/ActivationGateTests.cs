using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class ActivationGateTests
{
    [Fact]
    public void Request_BeforeReady_RaisesOnceAtReady()
    {
        ActivationGate gate = new();
        int raised = 0;
        gate.Activate += (_, _) => raised++;

        gate.Request();
        Assert.Equal(0, raised);

        gate.MarkReady();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Request_ManyBeforeReady_RaisesOnceAtReady()
    {
        ActivationGate gate = new();
        int raised = 0;
        gate.Activate += (_, _) => raised++;

        for (int index = 0; index < 5; index++)
        {
            gate.Request();
        }

        Assert.Equal(0, raised);

        gate.MarkReady();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Request_WhileReady_RaisesImmediately()
    {
        ActivationGate gate = new();
        int raised = 0;
        gate.Activate += (_, _) => raised++;

        gate.MarkReady();
        gate.Request();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Request_TwiceWhileReady_RaisesTwice()
    {
        ActivationGate gate = new();
        int raised = 0;
        gate.Activate += (_, _) => raised++;

        gate.MarkReady();
        gate.Request();
        gate.Request();

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Request_FromEightThreadsBeforeReady_RaisesExactlyOnce()
    {
        ActivationGate gate = new();
        int raised = 0;
        int arrived = 0;
        gate.Activate += (_, _) => Interlocked.Increment(ref raised);
        using Barrier start = new(8);
        Thread[] threads =
        [
            .. Enumerable
                .Range(0, 8)
                .Select(_ => new Thread(() =>
                {
                    if (start.SignalAndWait(TimeSpan.FromMinutes(2)))
                    {
                        Interlocked.Increment(ref arrived);
                    }

                    gate.Request();
                })
                {
                    IsBackground = true,
                }),
        ];

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "a worker thread did not finish");
        }

        Assert.Equal(8, arrived);
        Assert.Equal(0, raised);

        gate.MarkReady();
        Assert.Equal(1, raised);
    }
}
