namespace Yamv.Core.Instance;

/// <summary>The messages a launch sends to the running main instance over its pipe.</summary>
public static class InstanceMessages
{
    /// <summary>Bring the main instance's window to the front. Fire and forget: the server never replies to it.</summary>
    public const string Activate = "activate";

    /// <summary>
    /// Open files in a viewer window: this word, a space, then a JSON array of full paths (see <see cref="OpenMessage"/>). The
    /// server answers it with one <see cref="InstanceReply"/> on the same connection.
    /// </summary>
    public const string Open = "open";
}
