namespace Yamv.Core.Instance;

/// <summary>The server's one reply to a request that expects one, written on the request's own connection.</summary>
public enum InstanceReply
{
    /// <summary>The app took the request and is opening it; it never means the window has shown. The launcher exits 0.</summary>
    Opened,

    /// <summary>The app will not carry the request out (a malformed payload, or it could not start opening it); exit 1.</summary>
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
