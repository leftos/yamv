namespace Yamv.Core.Instance;

/// <summary>
/// One message a launch sent to the instance server, handed to the server's handler. The request object is the only handle that
/// can answer it: the reply goes back on the request's own connection, so it always reaches the launch that sent it.
/// </summary>
/// <remarks>
/// A request is pending until the app's UI job takes it with <see cref="TryBegin"/>, or until the server abandons it (the launcher
/// hung up, the server's reply bound passed, the server is stopping, or the handler threw), whichever comes first. An abandoned
/// request has already been answered: <see cref="InstanceReply.Closing"/> when the server is stopping, <see cref="InstanceReply.Refused"/>
/// when the handler threw, and nothing when its launcher is gone or the reply bound passed. It must never be carried out, so the
/// UI job's first act is <see cref="TryBegin"/>, and the app calls <see cref="Complete"/> right after it succeeds: <c>opened</c>
/// means the app took the request and is opening it, never that a window has shown. <c>activate</c> expects no reply; the
/// server never writes to its connection and nothing has to complete it.
/// </remarks>
public sealed class InstanceRequest
{
    private const int Pending = 0;
    private const int Begun = 1;
    private const int Ended = 2;

    private readonly TaskCompletionSource<InstanceReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state = Pending;

    internal InstanceRequest(string message, bool expectsReply)
    {
        Message = message;
        ExpectsReply = expectsReply;
    }

    /// <summary>The message text the launch sent, for example <c>activate</c> or an <c>open</c> message.</summary>
    public string Message { get; }

    /// <summary>False for <c>activate</c>, which its sender does not wait to hear about.</summary>
    public bool ExpectsReply { get; }

    /// <summary>
    /// Takes the request for carrying out. True once, for the first caller while the request is pending; false when it was
    /// already taken, completed or abandoned, and then the caller must not carry it out.
    /// </summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _state, Begun, Pending) == Pending;

    /// <summary>
    /// Answers the request; only the first answer counts, from any thread, and one given after the request has ended (answered
    /// or abandoned) is ignored. Returns at once: the reply is written on another thread. Refusing or closing a request that was
    /// never begun also ends it, so a later <see cref="TryBegin"/> returns false.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="reply"/> is <see cref="InstanceReply.Opened"/> and the request is still pending: <c>opened</c> means the
    /// request was taken, so <see cref="TryBegin"/> must succeed first.
    /// </exception>
    public void Complete(InstanceReply reply)
    {
        int state = Volatile.Read(ref _state);
        while (state != Ended)
        {
            if (reply == InstanceReply.Opened && state == Pending)
            {
                throw new InvalidOperationException("An instance request is answered opened only after TryBegin has taken it.");
            }

            int seen = Interlocked.CompareExchange(ref _state, Ended, state);
            if (seen == state)
            {
                _reply.TrySetResult(reply);
                return;
            }

            state = seen;
        }
    }

    /// <summary>Completes once the request has its answer, which the server writes back.</summary>
    internal Task<InstanceReply> Reply => _reply.Task;

    /// <summary>
    /// Ends a request nobody has begun with <paramref name="reply"/>. False when the UI job had already taken it or it was
    /// already answered; then the request is left as it is.
    /// </summary>
    internal bool TryAbandon(InstanceReply reply)
    {
        if (Interlocked.CompareExchange(ref _state, Ended, Pending) != Pending)
        {
            return false;
        }

        _reply.TrySetResult(reply);
        return true;
    }
}

/// <summary>
/// How a request sent with <see cref="InstanceCoordinator.RequestAsync(string, string, TimeSpan, Diagnostics.IAppLog, CancellationToken)"/>
/// ended, as the launcher sees it.
/// </summary>
public enum InstanceRequestOutcome
{
    /// <summary>The app answered <c>opened</c>: it took the request and is opening it.</summary>
    Confirmed,

    /// <summary>The app answered <c>refused</c>, or a reply the launcher does not know.</summary>
    Refused,

    /// <summary>
    /// The app answered <c>closing</c>, or the connection ended before any reply: the app is stopping and will never carry the
    /// request out, so the launcher may wait for the lock or send it again to the next app.
    /// </summary>
    Closing,

    /// <summary>The app took the request but did not answer within the budget; the launcher hung up before returning this.</summary>
    NoReply,

    /// <summary>No server accepted the connection within the budget, or the pipe cannot be opened at all.</summary>
    NoServer,

    /// <summary>The pipe belongs to another user, or the connection was refused in a way a retry cannot fix.</summary>
    Denied,
}
