namespace Yamv.Core.Documents;

/// <summary>
/// Why a document that exists on disk could not be read, as the window says it. The mapping is pure, so it is tested without
/// a window and without a locked file.
/// </summary>
public static class ReadFailure
{
    /// <summary>The reason when the file's permissions refuse the read.</summary>
    public const string AccessDenied = "Access is denied.";

    /// <summary>The reason when another program holds a share or a lock on the file.</summary>
    public const string InUseByAnotherProgram = "It is in use by another program.";

    /// <summary>The reason for any other read failure; the exception's message is logged, not shown.</summary>
    public const string Unknown = "It could not be read.";

    /// <summary>The reason a failed read of a document that is still on disk shows.</summary>
    /// <param name="exception">The exception the read threw.</param>
    public static string ReasonFor(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            UnauthorizedAccessException => AccessDenied,
            IOException io when IsSharingOrLockViolation(io) => InUseByAnotherProgram,
            _ => Unknown,
        };
    }

    /// <summary>True when the HResult is Windows' ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsSharingOrLockViolation(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;
}
