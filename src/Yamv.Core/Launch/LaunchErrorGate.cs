using Yamv.Core.Platform;

namespace Yamv.Core.Launch;

/// <summary>
/// The lock one launch holds while its not-responding box is up, so a failed launch that comes while a box is already showing
/// shows nothing. The holder is the open file itself: another launch's attempt to take it fails while it is held.
/// </summary>
public static class LaunchErrorGate
{
    /// <summary>Takes <paramref name="path"/> for as long as the returned holder lives, creating its folder when it is missing.</summary>
    /// <param name="path">The lock file's full path; the app passes <see cref="AppPaths.LaunchErrorLockFile"/>.</param>
    public static LaunchErrorGateResult TryTake(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            return new LaunchErrorBoxTaken(File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception) when (ShareViolation.IsShareViolation(exception))
        {
            return new LaunchErrorBoxHeld();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LaunchErrorBoxCannotOpen(exception);
        }
    }
}

/// <summary>What taking the not-responding box's lock gave.</summary>
public abstract record LaunchErrorGateResult;

/// <summary>This launch took the lock, for as long as <paramref name="Holder"/> lives.</summary>
public sealed record LaunchErrorBoxTaken(IDisposable Holder) : LaunchErrorGateResult;

/// <summary>Another launch holds the lock, so this launch shows no box.</summary>
public sealed record LaunchErrorBoxHeld : LaunchErrorGateResult;

/// <summary>
/// The lock file cannot be opened for a reason other than a holder: read-only, a folder in its place, or no right to write it.
/// The launch shows its box without the lock, so the user still hears that the files were not opened.
/// </summary>
public sealed record LaunchErrorBoxCannotOpen(Exception Failure) : LaunchErrorGateResult;
