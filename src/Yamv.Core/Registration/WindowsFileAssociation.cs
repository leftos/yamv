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
    /// <remarks>A <c>YAMV.Markdown</c> tree that already opens a program other than <c>yamv.exe</c> is overwritten, and
    /// the message says which command it replaced.</remarks>
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
            string replaced;
            (changed, replaced) = WriteProgId();
            foreach (string extension in Extensions)
            {
                changed |= AddOpenWithProgid(extension);
            }

            changed |= WriteApplicationKey();
            string message = changed
                ? $"Registered YAMV for .md and .markdown in Open with ({exePath}).{replaced}"
                : $"YAMV is already registered for .md and .markdown ({exePath}).";
            return new RegistrationResult(Succeeded: true, Changed: changed, Message: message);
        }
        catch (Exception exception) when (IsRegistryError(exception))
        {
            return new RegistrationResult(Succeeded: false, Changed: changed, Message: $"Could not register: {exception.Message}");
        }
    }

    /// <summary>Removes the YAMV entries that point at this exe, leaving the ones that point at another program.</summary>
    /// <remarks>A <c>YAMV.Markdown</c> tree that opens another <c>yamv.exe</c> is left in place and named with that path;
    /// a tree that opens any other program, or has no command at all, is left in place and the message says it does not
    /// open <c>yamv.exe</c>. Both extensions' Open with values are removed only when the <c>YAMV.Markdown</c> tree is
    /// gone or opens this exe. An <c>Applications\yamv.exe</c> key is removed when it opens this exe or has no command,
    /// and is otherwise left in place and named the same way.</remarks>
    /// <returns>Whether the work succeeded, whether anything was removed, and the line to print.</returns>
    public RegistrationResult Unregister()
    {
        bool changed = false;
        try
        {
            TreeState progId = InspectTree(ProgId, unclaimedWhenCommandMissing: false);
            TreeState application = InspectTree(ApplicationKeyPath, unclaimedWhenCommandMissing: true);

            changed |= RemoveProgIdEntries(progId);
            changed |= RemoveApplicationKey(application);

            string message = changed ? "Removed YAMV from Open with for .md and .markdown." : "Nothing to remove.";
            message += TreeNote(ProgId, progId);
            message += TreeNote(ApplicationKeyPath, application);
            return new RegistrationResult(Succeeded: true, Changed: changed, Message: message);
        }
        catch (Exception exception) when (IsRegistryError(exception))
        {
            return new RegistrationResult(Succeeded: false, Changed: changed, Message: $"Could not unregister: {exception.Message}");
        }
    }

    /// <summary>Removes the extensions' Open with values, and the tree itself, when the tree is absent or opens this exe.</summary>
    private bool RemoveProgIdEntries(TreeState progId)
    {
        if (progId.Ownership is not (TreeOwnership.Absent or TreeOwnership.Ours))
        {
            return false;
        }

        bool changed = false;
        foreach (string extension in Extensions)
        {
            changed |= RemoveOpenWithProgid(extension);
        }

        if (progId.Ownership == TreeOwnership.Ours)
        {
            changed |= DeleteTree(ProgId);
        }

        return changed;
    }

    /// <summary>Removes the Applications key when it opens this exe or holds no command at all.</summary>
    private bool RemoveApplicationKey(TreeState application) =>
        (application.Ownership is TreeOwnership.Ours or TreeOwnership.Unclaimed) && DeleteTree(ApplicationKeyPath);

    private static bool IsRegistryError(Exception exception) => exception is UnauthorizedAccessException or IOException or SecurityException;

    private (bool Changed, string Replaced) WriteProgId()
    {
        using RegistryKey progId = EnsureSubKey(classesRoot, ProgId);
        using RegistryKey open = EnsureSubKey(progId, @"shell\open\command");
        string replaced = ReplacedCommandNote(open);
        bool changed = SetString(open, name: null, CommandLine);

        using RegistryKey icon = EnsureSubKey(progId, "DefaultIcon");
        changed |= SetString(icon, name: null, $"\"{exePath}\",0");

        changed |= SetString(progId, name: null, FriendlyTypeName);
        return (changed, replaced);
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

    private static string ReplacedCommandNote(RegistryKey open)
    {
        if (open.GetValue(null) is not string previous || string.IsNullOrWhiteSpace(previous))
        {
            return string.Empty;
        }

        string? target = CommandTarget(previous);
        if (target is not null && IsYamvExe(target))
        {
            return string.Empty;
        }

        return $" It replaced a {ProgId} command that opened {target ?? previous}.";
    }

    private TreeState InspectTree(string path, bool unclaimedWhenCommandMissing)
    {
        using RegistryKey? tree = classesRoot.OpenSubKey(path, writable: false);
        if (tree is null)
        {
            return new TreeState(TreeOwnership.Absent, Target: null);
        }

        using RegistryKey? open = tree.OpenSubKey(@"shell\open\command", writable: false);
        if (open is null || !HasValue(open, string.Empty))
        {
            TreeOwnership ownership = unclaimedWhenCommandMissing ? TreeOwnership.Unclaimed : TreeOwnership.Foreign;
            return new TreeState(ownership, Target: null);
        }

        return InspectCommand(open.GetValue(null) as string);
    }

    private TreeState InspectCommand(string? command)
    {
        string? target = CommandTarget(command);
        if (target is null || !IsYamvExe(target))
        {
            return new TreeState(TreeOwnership.Foreign, Target: null);
        }

        return SamePath(target, exePath) ? new TreeState(TreeOwnership.Ours, Target: null) : new TreeState(TreeOwnership.AnotherYamv, target);
    }

    private static string TreeNote(string path, TreeState state) =>
        state.Ownership switch
        {
            TreeOwnership.AnotherYamv => $" {path} was left in place: it opens {state.Target}.",
            TreeOwnership.Foreign => $" {path} was left in place: it does not open {ExeName}.",
            _ => string.Empty,
        };

    /// <summary>The program a command line opens, or null when the line does not start with a quoted path.</summary>
    private static string? CommandTarget(string? command)
    {
        string trimmed = command?.TrimStart() ?? string.Empty;
        if (!trimmed.StartsWith('"'))
        {
            return null;
        }

        int close = trimmed.IndexOf('"', 1);
        return close <= 1 ? null : trimmed[1..close];
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    private enum TreeOwnership
    {
        Absent,
        Unclaimed,
        Ours,
        AnotherYamv,
        Foreign,
    }

    private readonly record struct TreeState(TreeOwnership Ownership, string? Target);
}
