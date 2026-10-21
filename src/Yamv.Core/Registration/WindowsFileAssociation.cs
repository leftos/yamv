using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Yamv.Core.Registration;

/// <summary>Writes and removes the per-user keys that put YAMV in Explorer's "Open with" list for markdown files.</summary>
/// <param name="classesRoot">The key that stands for <c>HKEY_CURRENT_USER\Software\Classes</c>; the caller owns it.</param>
/// <param name="exePath">The full path of the installed <c>yamv.exe</c>.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociation(RegistryKey classesRoot, string exePath) : IDisposable
{
    /// <summary>The programmatic identifier the markdown extensions point at.</summary>
    public const string ProgId = "YAMV.Markdown";

    /// <summary>The type name Explorer shows for a markdown document.</summary>
    public const string FriendlyTypeName = "Markdown document";

    private const string ExeName = "yamv.exe";
    private const string ApplicationKeyPath = @"Applications\yamv.exe";

    private static readonly string[] Extensions = [".md", ".markdown"];

    private readonly bool _ownsRoot;

    private WindowsFileAssociation(RegistryKey classesRoot, string exePath, bool ownsRoot)
        : this(classesRoot, exePath)
    {
        _ownsRoot = ownsRoot;
    }

    /// <summary>Opens <c>HKEY_CURRENT_USER\Software\Classes</c>, creating it when absent, as the key to register under.</summary>
    /// <param name="exePath">The full path of the installed <c>yamv.exe</c>.</param>
    /// <returns>An association that owns the key it opened.</returns>
    public static WindowsFileAssociation ForCurrentUser(string exePath)
    {
        RegistryKey root =
            Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true)
            ?? throw new IOException(@"Could not open HKEY_CURRENT_USER\Software\Classes.");
        return new WindowsFileAssociation(root, exePath, ownsRoot: true);
    }

    private string CommandLine => $"\"{exePath}\" \"%1\"";

    /// <summary>Releases the classes key, but only when this instance opened it.</summary>
    public void Dispose()
    {
        if (_ownsRoot)
        {
            classesRoot.Dispose();
        }
    }

    /// <summary>Writes every key and value that puts YAMV in the "Open with" list for markdown files.</summary>
    /// <returns>Whether the work succeeded, whether anything changed, and the line to print.</returns>
    public RegistrationResult Register()
    {
        if (!Path.IsPathFullyQualified(exePath) || !IsYamvExe(exePath))
        {
            return new RegistrationResult(
                Succeeded: false,
                Changed: false,
                Message: $"Not registering: {exePath} is not {ExeName}. Run --register from the installed {ExeName}."
            );
        }

        bool changed = false;
        try
        {
            changed = WriteProgId();
            foreach (string extension in Extensions)
            {
                changed |= AddOpenWithProgid(extension);
            }

            changed |= WriteApplicationKey();
            string message = changed
                ? $"Registered YAMV for .md and .markdown in Open with ({exePath})."
                : $"YAMV is already registered for .md and .markdown ({exePath}).";
            return new RegistrationResult(Succeeded: true, Changed: changed, Message: message);
        }
        catch (Exception exception) when (IsRegistryError(exception))
        {
            return new RegistrationResult(Succeeded: false, Changed: changed, Message: $"Could not register: {exception.Message}");
        }
    }

    /// <summary>Removes only what <see cref="Register"/> added, leaving every other association in place.</summary>
    /// <returns>Whether the work succeeded, whether anything was removed, and the line to print.</returns>
    public RegistrationResult Unregister()
    {
        bool changed = false;
        try
        {
            foreach (string extension in Extensions)
            {
                changed |= RemoveOpenWithProgid(extension);
            }

            bool foreign = IsForeignProgIdTree();
            if (!foreign)
            {
                changed |= DeleteTree(ProgId);
            }

            changed |= DeleteTree(ApplicationKeyPath);
            string message = changed ? "Removed YAMV from Open with for .md and .markdown." : "Nothing to remove.";
            if (foreign)
            {
                message += $" {ProgId} was left in place: it does not open {ExeName}.";
            }

            return new RegistrationResult(Succeeded: true, Changed: changed, Message: message);
        }
        catch (Exception exception) when (IsRegistryError(exception))
        {
            return new RegistrationResult(Succeeded: false, Changed: changed, Message: $"Could not unregister: {exception.Message}");
        }
    }

    private static bool IsRegistryError(Exception exception) => exception is UnauthorizedAccessException or IOException or SecurityException;

    private bool WriteProgId()
    {
        using RegistryKey progId = EnsureSubKey(classesRoot, ProgId);
        using RegistryKey open = EnsureSubKey(progId, @"shell\open\command");
        bool changed = SetString(open, name: null, CommandLine);

        using RegistryKey icon = EnsureSubKey(progId, "DefaultIcon");
        changed |= SetString(icon, name: null, $"\"{exePath}\",0");

        changed |= SetString(progId, name: null, FriendlyTypeName);
        return changed;
    }

    private bool AddOpenWithProgid(string extension)
    {
        using RegistryKey extensionKey = EnsureSubKey(classesRoot, extension);
        using RegistryKey progIds = EnsureSubKey(extensionKey, "OpenWithProgids");
        return SetNoData(progIds, ProgId);
    }

    private bool WriteApplicationKey()
    {
        using RegistryKey application = EnsureSubKey(classesRoot, ApplicationKeyPath);
        bool changed = SetString(application, "FriendlyAppName", "YAMV");

        using RegistryKey supportedTypes = EnsureSubKey(application, "SupportedTypes");
        foreach (string extension in Extensions)
        {
            changed |= SetString(supportedTypes, extension, string.Empty);
        }

        using RegistryKey open = EnsureSubKey(application, @"shell\open\command");
        changed |= SetString(open, name: null, CommandLine);
        return changed;
    }

    private bool RemoveOpenWithProgid(string extension)
    {
        using RegistryKey? progIds = classesRoot.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
        if (progIds is null || !HasValue(progIds, ProgId))
        {
            return false;
        }

        progIds.DeleteValue(ProgId, throwOnMissingValue: false);
        return true;
    }

    private bool IsForeignProgIdTree()
    {
        using RegistryKey? progId = classesRoot.OpenSubKey(ProgId, writable: false);
        if (progId is null)
        {
            return false;
        }

        using RegistryKey? open = progId.OpenSubKey(@"shell\open\command", writable: false);
        return !TargetsYamv(open?.GetValue(null) as string);
    }

    private bool DeleteTree(string path)
    {
        using (RegistryKey? existing = classesRoot.OpenSubKey(path, writable: false))
        {
            if (existing is null)
            {
                return false;
            }
        }

        classesRoot.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return true;
    }

    private static RegistryKey EnsureSubKey(RegistryKey parent, string path) =>
        parent.CreateSubKey(path, writable: true) ?? throw new IOException($"{parent.Name}\\{path} could not be created.");

    private static bool SetString(RegistryKey key, string? name, string value)
    {
        if (MatchesString(key, name, value))
        {
            return false;
        }

        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    private static bool MatchesString(RegistryKey key, string? name, string value)
    {
        if (!HasValue(key, name ?? string.Empty) || key.GetValueKind(name) != RegistryValueKind.String)
        {
            return false;
        }

        return key.GetValue(name) is string current && string.Equals(current, value, StringComparison.Ordinal);
    }

    private static bool SetNoData(RegistryKey key, string name)
    {
        if (HasValue(key, name) && key.GetValueKind(name) == RegistryValueKind.None)
        {
            return false;
        }

        key.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
        return true;
    }

    private static bool HasValue(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsYamvExe(string path) => string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase);

    private static bool TargetsYamv(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        int open = command.IndexOf('"');
        int close = open < 0 ? -1 : command.IndexOf('"', open + 1);
        if (close < 0)
        {
            return false;
        }

        return IsYamvExe(command[(open + 1)..close]);
    }
}
