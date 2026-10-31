namespace Yamv.Core.Instance;

/// <summary>The server's one reply to a request, written on the request's own connection.</summary>
public enum InstanceReply
{
    /// <summary>
    /// The launcher exits 0. For an <c>open</c>, the app took the request and is opening it, which never means the window has shown;
    /// for an <c>activate</c>, the app carried it out: a window came forward.
    /// </summary>
    Opened,

    /// <summary>
    /// The app will not carry the request out (a malformed payload, it could not start opening it, or no window came forward); exit 1.
    /// </summary>
    Refused,

    /// <summary>The app is stopping and abandoned the request, which it will never carry out; the launcher may send it again.</summary>
    Closing,
}

/// <summary>The wire text of each <see cref="InstanceReply"/>.</summary>
public static class InstanceReplies
{
    private const string OpenedText = "opened";
    private const string RefusedText = "refused";
    private const string ClosingText = "closing";

    /// <summary>The text written for <paramref name="reply"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reply"/> is not a defined value.</exception>
    public static string ToText(InstanceReply reply) =>
        reply switch
        {
            InstanceReply.Opened => OpenedText,
            InstanceReply.Refused => RefusedText,
            InstanceReply.Closing => ClosingText,
            _ => throw new ArgumentOutOfRangeException(nameof(reply), reply, "Not an instance reply."),
        };

    /// <summary>The reply <paramref name="text"/> names; matching is exact and case-sensitive, and any other text counts as refused.</summary>
    public static InstanceReply Parse(string text) =>
        text switch
        {
            OpenedText => InstanceReply.Opened,
            ClosingText => InstanceReply.Closing,
            _ => InstanceReply.Refused,
        };
}
