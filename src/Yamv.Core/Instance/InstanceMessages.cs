namespace Yamv.Core.Instance;

/// <summary>The messages a launch sends to the running main instance over its pipe.</summary>
public static class InstanceMessages
{
    /// <summary>Bring the main instance's window to the front.</summary>
    public const string Activate = "activate";
}
