using System.Runtime.Versioning;
using Microsoft.Win32;
using Yamv.Core.Registration;

namespace Yamv.Core.Tests.Registration;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociationTests : IDisposable
{
    private const string ExeA = @"C:\Program Files\YAMV Test\yamv.exe";
    private const string ExeB = @"D:\Apps\YAMV\yamv.exe";
    private const string ProgId = WindowsFileAssociation.ProgId;
    private const string QuotedA = "\"" + ExeA + "\" \"%1\"";
    private const string QuotedB = "\"" + ExeB + "\" \"%1\"";
    private const string ForeignCommand = "\"C:\\x\\other.exe\" \"%1\"";

    private static readonly string[] Extensions = [".md", ".markdown"];

    private const string ParentPath = @"Software\YamvTests";

    private readonly string _rootPath = ParentPath + @"\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        bool empty;
        using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(ParentPath, writable: false))
        {
            empty = parent is { SubKeyCount: 0 };
        }

        if (empty)
        {
            Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Register_WritesProgIdWithFriendlyNameIconAndQuotedCommand()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();

        Register(root, ExeA);

        Assert.Equal("Markdown document", DefaultValue(root, ProgId));
        Assert.Equal(RegistryValueKind.String, DefaultKind(root, ProgId));
        Assert.Equal("\"" + ExeA + "\",0", DefaultValue(root, $@"{ProgId}\DefaultIcon"));
        Assert.Equal(RegistryValueKind.String, DefaultKind(root, $@"{ProgId}\DefaultIcon"));
        Assert.Equal(QuotedA, DefaultValue(root, $@"{ProgId}\shell\open\command"));
        Assert.Equal(RegistryValueKind.String, DefaultKind(root, $@"{ProgId}\shell\open\command"));
    }

    [Fact]
    public void Register_AddsRegNoneOpenWithProgidsValueForBothExtensions()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();

        Register(root, ExeA);

        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Equal(RegistryValueKind.None, progIds.GetValueKind(ProgId));
            object? data = progIds.GetValue(ProgId);
            Assert.IsType<byte[]>(data);
            Assert.Empty((byte[])data!);
        }
    }

    [Fact]
    public void Register_WritesApplicationsKeyWithFriendlyAppNameSupportedTypesAndCommand()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();

        Register(root, ExeA);

        using RegistryKey application = Open(root, @"Applications\yamv.exe");
        Assert.Equal("YAMV", application.GetValue("FriendlyAppName") as string);
        Assert.Equal(RegistryValueKind.String, application.GetValueKind("FriendlyAppName"));

        using RegistryKey supportedTypes = Open(application, "SupportedTypes");
        string[] names = [.. supportedTypes.GetValueNames().Order(StringComparer.Ordinal)];
        Assert.Equal([".markdown", ".md"], names);
        foreach (string extension in Extensions)
        {
            Assert.Equal(string.Empty, supportedTypes.GetValue(extension) as string);
            Assert.Equal(RegistryValueKind.String, supportedTypes.GetValueKind(extension));
        }

        Assert.Equal(QuotedA, DefaultValue(root, @"Applications\yamv.exe\shell\open\command"));
    }

    [Fact]
    public void Register_NeverWritesExtensionDefaultValueUserChoiceOrFileExts()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey markdown = root.CreateSubKey(".md", writable: true)!)
        {
            markdown.SetValue(null, "SomeEditor.md", RegistryValueKind.String);
        }

        Register(root, ExeA);

        Assert.Equal("SomeEditor.md", DefaultValue(root, ".md"));
        Assert.Null(DefaultValue(root, ".markdown"));

        string[] keyNames = [.. AllSubKeyNames(root)];
        Assert.DoesNotContain(keyNames, name => string.Equals(name, "UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keyNames, name => string.Equals(name, "FileExts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Register_Twice_SecondCallReportsAlreadyRegisteredAndChangesNothing()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        RegistrationResult first = Register(root, ExeA);
        Assert.True(first.Succeeded);
        Assert.True(first.Changed);
        Assert.Equal($"Registered YAMV for .md and .markdown in Open with ({ExeA}).", first.Message);

        List<string> before = Snapshot(root);
        RegistrationResult second = Register(root, ExeA);

        Assert.True(second.Succeeded);
        Assert.False(second.Changed);
        Assert.Equal($"YAMV is already registered for .md and .markdown ({ExeA}).", second.Message);
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public void Register_UpdatesCommandWhenExePathMoved()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);

        RegistrationResult moved = Register(root, ExeB);

        Assert.True(moved.Succeeded);
        Assert.True(moved.Changed);
        Assert.Equal($"Registered YAMV for .md and .markdown in Open with ({ExeB}).", moved.Message);
        Assert.Equal("\"" + ExeB + "\",0", DefaultValue(root, $@"{ProgId}\DefaultIcon"));
        Assert.Equal(QuotedB, DefaultValue(root, $@"{ProgId}\shell\open\command"));
        Assert.Equal(QuotedB, DefaultValue(root, @"Applications\yamv.exe\shell\open\command"));
    }

    [Theory]
    [InlineData(@"C:\dotnet\dotnet.exe")]
    [InlineData(@"C:\x\notyamv.exe")]
    [InlineData("yamv.exe")]
    [InlineData(@".\yamv.exe")]
    public void Register_RefusesNonYamvExe(string exePath)
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();

        RegistrationResult refused = Register(root, exePath);

        Assert.False(refused.Succeeded);
        Assert.False(refused.Changed);
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal($@"Not registering: {exePath} is not yamv.exe. Run --register from the installed yamv.exe.", refused.Message);
        Assert.Empty(Snapshot(root));
    }

    [Fact]
    public void Register_accepts_upper_case_exe_name()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        const string Upper = @"C:\Program Files\YAMV\YAMV.EXE";

        RegistrationResult registered = Register(root, Upper);

        Assert.True(registered.Succeeded);
        Assert.True(registered.Changed);
        Assert.Equal($"\"{Upper}\" \"%1\"", DefaultValue(root, $@"{ProgId}\shell\open\command"));
    }

    [Fact]
    public void Register_over_foreign_progid_notes_what_it_replaced()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey command = root.CreateSubKey($@"{ProgId}\shell\open\command", writable: true)!)
        {
            command.SetValue(null, ForeignCommand, RegistryValueKind.String);
        }

        RegistrationResult registered = Register(root, ExeA);

        Assert.True(registered.Succeeded);
        Assert.True(registered.Changed);
        Assert.Equal(
            $"Registered YAMV for .md and .markdown in Open with ({ExeA}). It replaced a {ProgId} command that opened C:\\x\\other.exe.",
            registered.Message
        );
    }

    [Fact]
    public void Register_over_unparseable_progid_command_notes_the_raw_text()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey command = root.CreateSubKey($@"{ProgId}\shell\open\command", writable: true)!)
        {
            command.SetValue(null, "notepad %1", RegistryValueKind.String);
        }

        RegistrationResult registered = Register(root, ExeA);

        Assert.True(registered.Succeeded);
        Assert.True(registered.Changed);
        Assert.EndsWith($" It replaced a {ProgId} command that opened notepad %1.", registered.Message);
    }

    [Fact]
    public void Register_over_unquoted_program_with_quoted_argument_notes_the_raw_text()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey command = root.CreateSubKey($@"{ProgId}\shell\open\command", writable: true)!)
        {
            command.SetValue(null, "notepad.exe \"%1\"", RegistryValueKind.String);
        }

        RegistrationResult registered = Register(root, ExeA);

        Assert.True(registered.Succeeded);
        Assert.True(registered.Changed);
        Assert.Equal(
            $"Registered YAMV for .md and .markdown in Open with ({ExeA}). It replaced a {ProgId} command that opened notepad.exe \"%1\".",
            registered.Message
        );
    }

    [Fact]
    public void Register_failure_after_partial_writes_reports_changed()
    {
        SkipOffWindows();
        using RegistryKey writable = OpenRoot();
        using RegistryKey root = OpenRootForReading();

        RegistrationResult failed = Register(root, ExeA);

        Assert.False(failed.Succeeded);
        Assert.False(failed.Changed);
        Assert.StartsWith("Could not register: ", failed.Message);
        Assert.Empty(Snapshot(writable));
    }

    [Fact]
    public void Register_registry_error_returns_could_not_register()
    {
        SkipOffWindows();
        using RegistryKey writable = OpenRoot();
        using RegistryKey root = OpenRootForReading();

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult failed = association.Register();

        Assert.False(failed.Succeeded);
        Assert.Equal(1, failed.ExitCode);
        Assert.StartsWith("Could not register: ", failed.Message);
        Assert.Empty(Snapshot(writable));
    }

    [Fact]
    public void Unregister_registry_error_returns_could_not_unregister()
    {
        SkipOffWindows();
        using RegistryKey writable = OpenRoot();
        Register(writable, ExeA);

        using RegistryKey root = OpenRootForReading();
        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult failed = association.Unregister();

        Assert.False(failed.Succeeded);
        Assert.Equal(1, failed.ExitCode);
        Assert.StartsWith("Could not unregister: ", failed.Message);
    }

    [Fact]
    public void Unregister_removes_progid_tree_that_has_only_its_command()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey command = root.CreateSubKey($@"{ProgId}\shell\open\command", writable: true)!)
        {
            command.SetValue(null, QuotedA, RegistryValueKind.String);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown.", removed.Message);
        using RegistryKey? progId = root.OpenSubKey(ProgId);
        Assert.Null(progId);
    }

    [Fact]
    public void Unregister_RemovesExactlyWhatRegisterAdded()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown.", removed.Message);
        List<string> expected = [".markdown", @".markdown\OpenWithProgids", ".md", @".md\OpenWithProgids", "Applications"];
        Assert.Equal(expected, Snapshot(root));
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Empty(progIds.GetValueNames());
        }
    }

    [Fact]
    public void Unregister_KeepsOtherProgidsAndExtensionDefault()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey markdown = root.CreateSubKey(".md", writable: true)!)
        {
            markdown.SetValue(null, "SomeEditor.md", RegistryValueKind.String);
            using RegistryKey progIds = markdown.CreateSubKey("OpenWithProgids", writable: true)!;
            progIds.SetValue("Other.md", string.Empty, RegistryValueKind.String);
        }
        Register(root, ExeA);

        using WindowsFileAssociation association = new(root, ExeA);
        association.Unregister();

        Assert.Equal("SomeEditor.md", DefaultValue(root, ".md"));
        using RegistryKey kept = Open(root, @".md\OpenWithProgids");
        Assert.Equal(string.Empty, kept.GetValue("Other.md") as string);
        Assert.DoesNotContain(kept.GetValueNames(), name => string.Equals(name, ProgId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unregister_KeepsOtherApplicationsEntries()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey other = root.CreateSubKey(@"Applications\other.exe", writable: true)!)
        {
            other.SetValue("FriendlyAppName", "Other", RegistryValueKind.String);
        }
        Register(root, ExeA);

        using WindowsFileAssociation association = new(root, ExeA);
        association.Unregister();

        using RegistryKey kept = Open(root, @"Applications\other.exe");
        Assert.Equal("Other", kept.GetValue("FriendlyAppName") as string);
        using RegistryKey? ours = root.OpenSubKey(@"Applications\yamv.exe");
        Assert.Null(ours);
    }

    [Fact]
    public void Unregister_WhenNothingRegistered_ReportsNothingToRemove()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult nothing = association.Unregister();

        Assert.True(nothing.Succeeded);
        Assert.False(nothing.Changed);
        Assert.Equal("Nothing to remove.", nothing.Message);
        Assert.Empty(Snapshot(root));
    }

    [Fact]
    public void Unregister_LeavesForeignProgIdTree()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);
        using (RegistryKey command = root.OpenSubKey($@"{ProgId}\shell\open\command", writable: true)!)
        {
            command.SetValue(null, ForeignCommand, RegistryValueKind.String);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult left = association.Unregister();

        Assert.True(left.Succeeded);
        Assert.True(left.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown. YAMV.Markdown was left in place: it does not open yamv.exe.", left.Message);
        Assert.Equal(ForeignCommand, DefaultValue(root, $@"{ProgId}\shell\open\command"));
        Assert.Equal("\"" + ExeA + "\",0", DefaultValue(root, $@"{ProgId}\DefaultIcon"));
        using RegistryKey? application = root.OpenSubKey(@"Applications\yamv.exe");
        Assert.Null(application);
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Contains(progIds.GetValueNames(), name => string.Equals(name, ProgId, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Unregister_from_another_exe_leaves_everything_and_names_the_owner()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);
        List<string> before = Snapshot(root);

        using WindowsFileAssociation association = new(root, ExeB);
        RegistrationResult left = association.Unregister();

        Assert.True(left.Succeeded);
        Assert.False(left.Changed);
        Assert.Equal(
            $"Nothing to remove. {ProgId} was left in place: it opens {ExeA}. Applications\\yamv.exe was left in place: it opens {ExeA}.",
            left.Message
        );
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public void Unregister_removes_own_progid_but_leaves_applications_key_of_another_exe()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);
        using (RegistryKey command = root.OpenSubKey(@"Applications\yamv.exe\shell\open\command", writable: true)!)
        {
            command.SetValue(null, QuotedB, RegistryValueKind.String);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal(
            $"Removed YAMV from Open with for .md and .markdown. Applications\\yamv.exe was left in place: it opens {ExeB}.",
            removed.Message
        );
        using (RegistryKey? progId = root.OpenSubKey(ProgId))
        {
            Assert.Null(progId);
        }
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Empty(progIds.GetValueNames());
        }
        Assert.Equal(QuotedB, DefaultValue(root, @"Applications\yamv.exe\shell\open\command"));
    }

    [Fact]
    public void Unregister_removes_orphan_open_with_values_when_progid_is_absent()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = root.CreateSubKey($@"{extension}\OpenWithProgids", writable: true)!;
            progIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown.", removed.Message);
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Empty(progIds.GetValueNames());
        }
    }

    [Fact]
    public void Unregister_removes_applications_key_without_a_command()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey application = root.CreateSubKey(@"Applications\yamv.exe", writable: true)!)
        {
            application.SetValue("FriendlyAppName", "YAMV", RegistryValueKind.String);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown.", removed.Message);
        using RegistryKey? left = root.OpenSubKey(@"Applications\yamv.exe");
        Assert.Null(left);
    }

    [Fact]
    public void Result_ExitCode_IsZeroOnSuccessAndOneOnFailure()
    {
        Assert.Equal(0, new RegistrationResult(Succeeded: true, Changed: true, "ok").ExitCode);
        Assert.Equal(0, new RegistrationResult(Succeeded: true, Changed: false, "ok").ExitCode);
        Assert.Equal(1, new RegistrationResult(Succeeded: false, Changed: false, "no").ExitCode);
    }

    [Fact]
    public void Unregister_matches_the_exe_path_case_insensitively()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        Register(root, ExeA);

        using WindowsFileAssociation association = new(root, ExeA.ToUpperInvariant());
        RegistrationResult removed = association.Unregister();

        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.Equal("Removed YAMV from Open with for .md and .markdown.", removed.Message);
        using (RegistryKey? progId = root.OpenSubKey(ProgId))
        {
            Assert.Null(progId);
        }
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Empty(progIds.GetValueNames());
        }
        using RegistryKey? application = root.OpenSubKey(@"Applications\yamv.exe");
        Assert.Null(application);
    }

    [Fact]
    public void Unregister_leaves_progid_tree_without_a_command()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey progId = root.CreateSubKey(ProgId, writable: true)!)
        {
            progId.SetValue(null, "Markdown document", RegistryValueKind.String);
        }
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = root.CreateSubKey($@"{extension}\OpenWithProgids", writable: true)!;
            progIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult left = association.Unregister();

        Assert.True(left.Succeeded);
        Assert.False(left.Changed);
        Assert.EndsWith($" {ProgId} was left in place: it does not open yamv.exe.", left.Message);
        Assert.Equal("Markdown document", DefaultValue(root, ProgId));
        foreach (string extension in Extensions)
        {
            using RegistryKey progIds = Open(root, $@"{extension}\OpenWithProgids");
            Assert.Contains(progIds.GetValueNames(), name => string.Equals(name, ProgId, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Unregister_leaves_applications_key_of_a_foreign_program()
    {
        SkipOffWindows();
        using RegistryKey root = OpenRoot();
        using (RegistryKey command = root.CreateSubKey(@"Applications\yamv.exe\shell\open\command", writable: true)!)
        {
            command.SetValue(null, ForeignCommand, RegistryValueKind.String);
        }

        using WindowsFileAssociation association = new(root, ExeA);
        RegistrationResult left = association.Unregister();

        Assert.True(left.Succeeded);
        Assert.False(left.Changed);
        Assert.EndsWith(@" Applications\yamv.exe was left in place: it does not open yamv.exe.", left.Message);
        Assert.Equal(ForeignCommand, DefaultValue(root, @"Applications\yamv.exe\shell\open\command"));
    }

    private static void SkipOffWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry exists only on Windows.");

    private static RegistrationResult Register(RegistryKey root, string exePath)
    {
        using WindowsFileAssociation association = new(root, exePath);
        return association.Register();
    }

    private RegistryKey OpenRoot() => Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;

    private RegistryKey OpenRootForReading() => Registry.CurrentUser.OpenSubKey(_rootPath, writable: false)!;

    private static RegistryKey Open(RegistryKey parent, string path) =>
        parent.OpenSubKey(path) ?? throw new InvalidOperationException($"{parent.Name}\\{path} is missing.");

    private static string? DefaultValue(RegistryKey root, string path)
    {
        using RegistryKey key = Open(root, path);
        return key.GetValue(null) as string;
    }

    private static RegistryValueKind DefaultKind(RegistryKey root, string path)
    {
        using RegistryKey key = Open(root, path);
        return key.GetValueKind(null);
    }

    private static IEnumerable<string> AllSubKeyNames(RegistryKey root)
    {
        foreach (string name in root.GetSubKeyNames())
        {
            yield return name;
            using RegistryKey child = Open(root, name);
            foreach (string nested in AllSubKeyNames(child))
            {
                yield return nested;
            }
        }
    }

    private static List<string> Snapshot(RegistryKey root, string prefix = "")
    {
        List<string> lines = [];
        foreach (string name in root.GetValueNames())
        {
            lines.Add($"{prefix}[{name}]={root.GetValueKind(name)}:{Describe(root.GetValue(name))}");
        }

        foreach (string child in root.GetSubKeyNames())
        {
            lines.Add(prefix + child);
            using RegistryKey childKey = Open(root, child);
            lines.AddRange(Snapshot(childKey, prefix + child + "\\"));
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string Describe(object? value) =>
        value switch
        {
            null => "null",
            byte[] bytes => Convert.ToHexString(bytes),
            _ => value.ToString() ?? string.Empty,
        };
}
