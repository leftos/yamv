namespace Yamv.Core.Diagnostics;

/// <summary>Where YAMV reports what went wrong. Every caught exception goes here, never into an empty catch.</summary>
public interface IAppLog
{
    void LogInfo(string message);

    void LogWarning(string message, Exception? exception = null);

    void LogError(string message, Exception? exception = null);
}
