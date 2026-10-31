using System.Globalization;
using System.Text;

namespace Yamv.Core.Diagnostics;

/// <summary>
/// Appends timestamped lines to one log file, rolling it to <c>.1</c> once it passes <see cref="MaxBytes"/>.
/// Safe to call from any thread.
/// </summary>
public sealed class FileAppLog(string filePath) : IAppLog
{
    public const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();

    public string FilePath { get; } = filePath;

    /// <summary>Runs inside the lock with the append stream open and the line not yet written; a test deletes the folder here.</summary>
    internal Action? WhileAppending { get; init; }

    public void LogInfo(string message) => Write("INFO", message, null);

    public void LogWarning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void LogError(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        string line = $"{stamp} {level} {LogText.Clean(message)}\n";
        if (exception is not null)
        {
            var details = new StringBuilder(line);
            foreach (string exceptionLine in exception.ToString().ReplaceLineEndings("\n").Split('\n'))
            {
                details.Append("    ").Append(LogText.Clean(exceptionLine)).Append('\n');
            }

            line = details.ToString();
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RollIfFull();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                WhileAppending?.Invoke();
                using var writer = new StreamWriter(stream);
                writer.Write(line);
            }
            catch (IOException ioException)
            {
                // The log is the last resort: when it cannot be written, stderr is all that is left.
                Console.Error.WriteLine($"yamv: cannot write log {FilePath}: {ioException.Message}\n{line}");
            }
            catch (UnauthorizedAccessException accessException)
            {
                Console.Error.WriteLine($"yamv: cannot write log {FilePath}: {accessException.Message}\n{line}");
            }
        }
    }

    private void RollIfFull()
    {
        var info = new FileInfo(FilePath);
        if (info.Exists && info.Length > MaxBytes)
        {
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
    }
}
