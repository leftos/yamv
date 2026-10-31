namespace Yamv.Core.Launch;

/// <summary>
/// Holds the jobs meant for the UI thread until the UI framework is ready to run them: <see cref="Post"/> may be called from
/// any thread and never blocks; before <see cref="Open"/> it queues the job, and <see cref="Open"/> releases the queued jobs
/// once, in arrival order, through the poster it is given. From then on a job goes straight to that poster.
/// </summary>
/// <remarks>
/// <see cref="Open"/> hands the queued jobs over outside the latch's lock, so a poster that waits on another thread never
/// deadlocks a <see cref="Post"/>; a job posted while the queue is being released joins the queue and is released after the
/// jobs already in it, so the arrival order holds.
/// </remarks>
public sealed class UiReadyLatch
{
    private readonly Lock _gate = new();
    private readonly List<Action> _queued = [];
    private Action<Action>? _post;
    private bool _opening;
    private bool _open;

    /// <summary>Queues <paramref name="job"/> until the latch opens, or hands it to the poster once it is open; from any thread.</summary>
    /// <param name="job">The work to run on the UI thread.</param>
    public void Post(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Action<Action> post;
        lock (_gate)
        {
            if (!_open)
            {
                _queued.Add(job);
                return;
            }

            post = _post!;
        }

        post(job);
    }

    /// <summary>
    /// Opens the latch: releases every queued job through <paramref name="post"/>, in arrival order, then forwards each later job
    /// straight to it. A second call does nothing.
    /// </summary>
    /// <param name="post">Queues a job on the UI thread and returns at once.</param>
    public void Open(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        lock (_gate)
        {
            if (_opening || _open)
            {
                return;
            }

            _opening = true;
            _post = post;
        }

        while (true)
        {
            Action[] batch;
            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    _open = true;
                    return;
                }

                batch = [.. _queued];
                _queued.Clear();
            }

            foreach (Action job in batch)
            {
                post(job);
            }
        }
    }
}
