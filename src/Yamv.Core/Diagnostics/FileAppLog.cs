using System.Globalization;

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

    public void LogInfo(string message) => Write("INFO", message, null);

    public void LogWarning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void LogError(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        string line = exception is null ? $"{stamp} {level} {message}\n" : $"{stamp} {level} {message}\n{exception}\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RollIfFull();
                File.AppendAllText(FilePath, line);
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
