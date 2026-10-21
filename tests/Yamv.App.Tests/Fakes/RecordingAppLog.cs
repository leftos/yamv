using Yamv.Core.Diagnostics;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IAppLog"/> that keeps every entry, so a test can check what was logged. Safe to call from any thread.</summary>
public sealed class RecordingAppLog : IAppLog
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>A snapshot of every entry logged so far.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void LogInfo(string message) => Add("info", message, null);

    public void LogWarning(string message, Exception? exception = null) => Add("warning", message, exception);

    public void LogError(string message, Exception? exception = null) => Add("error", message, exception);

    private void Add(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(level, message, exception));
        }
    }
}

/// <summary>One call made to <see cref="RecordingAppLog"/>.</summary>
public sealed record LogEntry(string Level, string Message, Exception? Exception);
