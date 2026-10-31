using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Yamv.Core.Platform;

namespace Yamv.App.Platform;

/// <summary>
/// Shows a failed launch's error with the platform's own dialog, since a launch that only sent its files to another instance
/// has no Avalonia app to draw in: a message box on Windows, an osascript caution dialog on macOS.
/// </summary>
internal static partial class LaunchErrorBox
{
    private const uint Ok = 0x00000000;
    private const uint IconWarning = 0x00000030;
    private const uint SetForeground = 0x00010000;
    private const string Title = "YAMV";

    /// <summary>Shows <paramref name="text"/> in a modal warning box titled YAMV and returns once it is dismissed.</summary>
    /// <exception cref="Win32Exception">The message box could not be shown; the caller logs it.</exception>
    [SupportedOSPlatform("windows")]
    public static void ShowOnWindows(string text)
    {
        if (MessageBoxW(0, text, Title, Ok | IconWarning | SetForeground) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"MessageBoxW failed (error {error}).");
        }
    }

    /// <summary>Shows <paramref name="text"/> in a caution dialog titled YAMV through osascript and returns once it is dismissed.</summary>
    /// <exception cref="IOException">osascript exited with an error; the caller logs it.</exception>
    /// <exception cref="Win32Exception">osascript could not be started; the caller logs it.</exception>
    [SupportedOSPlatform("macos")]
    public static void ShowOnMac(string text)
    {
        var command = AlertCommand.For(text);
        var start = new ProcessStartInfo(command.Program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in command.ArgumentList)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new IOException("Could not start osascript to show the box.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new IOException($"osascript exited with code {process.ExitCode}: {error.Result.Trim()}");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
