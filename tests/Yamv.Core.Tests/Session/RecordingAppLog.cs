using Yamv.Core.Diagnostics;

namespace Yamv.Core.Tests.Session;

/// <summary>An <see cref="IAppLog"/> that keeps every entry, so a test can check what was logged.</summary>
public sealed class RecordingAppLog : IAppLog
{
    public List<LogEntry> Entries { get; } = [];

    public void LogInfo(string message) => Entries.Add(new LogEntry("info", message, null));

    public void LogWarning(string message, Exception? exception = null) => Entries.Add(new LogEntry("warning", message, exception));

    public void LogError(string message, Exception? exception = null) => Entries.Add(new LogEntry("error", message, exception));
}

/// <summary>One call made to <see cref="RecordingAppLog"/>.</summary>
public sealed record LogEntry(string Level, string Message, Exception? Exception);
