using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Yamv.Core.Theming;

namespace Yamv.App.Platform;

/// <summary>What a shell delete reported: its return code, and whether it stopped before finishing.</summary>
/// <param name="Code">The shell's return code; zero is success.</param>
/// <param name="Aborted">Whether the shell reported the operation aborted, which a cancel from the user does.</param>
internal readonly record struct TrashResult(int Code, bool Aborted);

/// <summary>What a test drives in place of the real platform: the shell delete, and the drive type it runs on.</summary>
/// <param name="Delete">A full path and the owner window's handle in, what the shell reported out.</param>
/// <param name="DriveType">A drive root in, its <c>GetDriveTypeW</c> value out.</param>
internal sealed record TrashSeams(Func<string, nint, TrashResult> Delete, Func<string, uint> DriveType);

/// <summary>
/// Moves a file to the platform's trash, where the user can put it back: the Recycle Bin on Windows through
/// <c>SHFileOperationW</c>, the Trash on macOS through <c>NSFileManager</c>. A failure throws; it never falls back to a delete.
/// </summary>
internal sealed partial class PlatformTrash : IFileTrash
{
    /// <summary>The drive type <c>GetDriveTypeW</c> reports for a local fixed disk, the only kind with a Recycle Bin.</summary>
    internal const uint FixedDrive = 3;

    /// <summary>
    /// The flags a Windows delete runs with: undo allowed, no dialog of its own, and a prompt before a file the shell
    /// cannot recycle is destroyed, which the user's No reports as an aborted operation.
    /// </summary>
    internal const ushort DeleteFlags = Silent | NoConfirmation | AllowUndo | NoErrorUi | WantNukeWarning;

    private const uint FileOperationDelete = 3;
    private const ushort Silent = 0x0004;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;
    private const ushort WantNukeWarning = 0x4000;

    private readonly TrashSeams? _seams;
    private readonly Func<nint> _owner;

    /// <summary>The platform's own trash, chosen when a file is moved.</summary>
    /// <param name="owner">
    /// The handle of the window that owns the shell's prompts, read at each move, since a window has one only once shown; zero
    /// for none. macOS shows no prompt and ignores it.
    /// </param>
    public PlatformTrash(Func<nint> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _seams = null;
        _owner = owner;
    }

    /// <summary>A trash a test drives: <paramref name="delete"/> stands in for the shell call of a fixed drive, with no owner.</summary>
    /// <param name="delete">A full path in, what the shell reported out.</param>
    internal PlatformTrash(Func<string, TrashResult> delete)
        : this(new TrashSeams((path, _) => delete(path), _ => FixedDrive), () => 0) { }

    /// <summary>A trash a test drives, whose drive type and owner window are its own as well.</summary>
    /// <param name="seams">The delete call, and the drive type it runs on.</param>
    /// <param name="owner">The owner window's handle, read at each move.</param>
    internal PlatformTrash(TrashSeams seams, Func<nint> owner)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(owner);
        _seams = seams;
        _owner = owner;
    }

    /// <summary>Moves the file at <paramref name="path"/> to the Recycle Bin or Trash.</summary>
    /// <param name="path">The file to move, relative or full.</param>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">The platform refused the move, or its drive has no Recycle Bin.</exception>
    /// <exception cref="PlatformNotSupportedException">The system has no trash YAMV knows how to use.</exception>
    public void MoveToTrash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"There is no file at {fullPath} to move to the trash.", fullPath);
        }

        if (_seams is not null)
        {
            RequireRecycleBin(fullPath, _seams.DriveType);
            TrashResult result = _seams.Delete(fullPath, _owner());
            ThrowOnFailure(result.Code, result.Aborted, fullPath);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteOnWindows(fullPath, _owner());
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            _ = Mac.Delete(fullPath);
            return;
        }

        throw new PlatformNotSupportedException($"Moving a file to the trash is not supported on this platform: {fullPath}");
    }

    /// <summary>
    /// Throws unless the drive that holds <paramref name="fullPath"/> is a local fixed disk, the only kind with a Recycle
    /// Bin: on a network, mapped, removable or unknown drive the shell would destroy the file instead of recycling it.
    /// </summary>
    /// <param name="fullPath">The file to move.</param>
    /// <param name="driveType">A drive root in, its <c>GetDriveTypeW</c> value out.</param>
    private static void RequireRecycleBin(string fullPath, Func<string, uint> driveType)
    {
        if (Path.GetPathRoot(fullPath) is string root && driveType(root) == FixedDrive)
        {
            return;
        }

        throw new IOException($"Could not move {fullPath} to the trash: its drive has no Recycle Bin, so it was not deleted.");
    }

    /// <summary>Throws the failure the window shows unless the shell reported a completed delete.</summary>
    /// <param name="code">The shell call's return code; zero is success.</param>
    /// <param name="aborted">Whether the shell reported the operation aborted.</param>
    /// <param name="path">The file the shell was given, named in the message.</param>
    private static void ThrowOnFailure(int code, bool aborted, string path)
    {
        if (code == 0 && !aborted)
        {
            return;
        }

        string reason = aborted ? "the shell reported the operation aborted" : $"the shell returned 0x{code:X}";
        throw new IOException($"Could not move {path} to the trash: {reason}.", code);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteOnWindows(string fullPath, nint owner)
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException($"Moving {fullPath} to the Recycle Bin is not supported in a 32-bit process.");
        }

        RequireRecycleBin(fullPath, GetDriveType);

        // SHFileOperationW reads pFrom until a double null, and needs a full path to reach the Recycle Bin.
        char[] from = [.. fullPath, '\0', '\0'];
        nint pFrom = Marshal.AllocHGlobal(from.Length * sizeof(char));
        try
        {
            Marshal.Copy(from, 0, pFrom, from.Length);
            var operation = new ShFileOpStruct
            {
                Hwnd = owner,
                WFunc = FileOperationDelete,
                PFrom = pFrom,
                FFlags = DeleteFlags,
            };
            ThrowOnFailure(SHFileOperation(ref operation), operation.FAnyOperationsAborted != 0, fullPath);
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }

    [SupportedOSPlatform("windows")]
    private static uint GetDriveType(string root) => GetDriveTypeW(root);

    /// <summary>
    /// <c>SHFILEOPSTRUCTW</c>, field for field. shellapi.h packs it to 1 on 32-bit (<c>#if !defined(_WIN64) #include
    /// &lt;pshpack1.h&gt;</c>) and leaves the default alignment on x64, which is this layout; a 32-bit process is refused.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint WFunc;
        public nint PFrom;
        public nint PTo;
        public ushort FFlags;
        public int FAnyOperationsAborted;
        public nint HNameMappings;
        public nint LpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct operation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string rootPathName);

    /// <summary>The macOS Trash through <c>NSFileManager</c>, which no managed API reaches.</summary>
    [SupportedOSPlatform("macos")]
    internal static partial class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
        private const string TrashItem = "trashItemAtURL:resultingItemURL:error:";

        private static readonly Lazy<nint> FoundationHandle = new(() => NativeLibrary.Load(FoundationPath));

        /// <summary>Loads Foundation, whose <c>NSFileManager</c>, <c>NSURL</c> and <c>NSString</c> the lookups need.</summary>
        /// <returns>Foundation's handle, held for the life of the process.</returns>
        internal static nint LoadFoundation() => FoundationHandle.Value;

        /// <summary>Moves <paramref name="fullPath"/> to the Trash through <c>trashItemAtURL:resultingItemURL:error:</c>.</summary>
        /// <param name="fullPath">The file to move.</param>
        /// <returns>The path the Trash holds the file at, which Finder renames on a clash.</returns>
        /// <exception cref="IOException"><c>NSFileManager</c> refused the move.</exception>
        public static string Delete(string fullPath)
        {
            nint pool = AutoreleasePoolPush();
            nint errorSlot = Marshal.AllocHGlobal(IntPtr.Size);
            nint resultSlot = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(errorSlot, 0);
                Marshal.WriteIntPtr(resultSlot, 0);
                nint fileUrl = FileUrl(fullPath);
                byte moved = MsgSendBoolThree(DefaultManager(), SelectorNamed(TrashItem), fileUrl, resultSlot, errorSlot);
                if (moved == 0)
                {
                    throw new IOException($"Could not move {fullPath} to the Trash: {Failure(errorSlot)}");
                }

                return UrlPath(Marshal.ReadIntPtr(resultSlot)) ?? fullPath;
            }
            finally
            {
                Marshal.FreeHGlobal(resultSlot);
                Marshal.FreeHGlobal(errorSlot);
                AutoreleasePoolPop(pool);
            }
        }

        /// <summary>The <c>NSURL</c> of <paramref name="path"/>, which Foundation has to load before it can make one.</summary>
        /// <param name="path">The file path to wrap.</param>
        /// <exception cref="IOException">Foundation could not make a string of the path.</exception>
        internal static nint FileUrl(string path)
        {
            nint utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                nint text = MsgSendOne(Class("NSString"), SelectorNamed("stringWithUTF8String:"), utf8);
                if (text == 0)
                {
                    throw new IOException($"Could not make a string of {path}.");
                }

                return MsgSendOne(Class("NSURL"), SelectorNamed("fileURLWithPath:"), text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        /// <summary>The Objective-C class <paramref name="name"/> names, with Foundation loaded first.</summary>
        /// <param name="name">The class's name, such as <c>NSURL</c>.</param>
        internal static nint Class(string name)
        {
            _ = LoadFoundation();
            return ClassNamed(name);
        }

        private static nint DefaultManager() => MsgSend(Class("NSFileManager"), SelectorNamed("defaultManager"));

        /// <summary>The file system path of the <c>NSURL</c> at <paramref name="url"/>, or null when there is none.</summary>
        private static string? UrlPath(nint url)
        {
            if (url == 0)
            {
                return null;
            }

            nint text = MsgSend(url, SelectorNamed("path"));
            return text == 0 ? null : Marshal.PtrToStringUTF8(MsgSend(text, SelectorNamed("UTF8String")));
        }

        /// <summary>The <c>localizedDescription</c> of the error the call left in <paramref name="errorSlot"/>.</summary>
        private static string Failure(nint errorSlot)
        {
            nint error = Marshal.ReadIntPtr(errorSlot);
            if (error == 0)
            {
                return "no reason given";
            }

            nint description = MsgSend(error, SelectorNamed("localizedDescription"));
            return Marshal.PtrToStringUTF8(MsgSend(description, SelectorNamed("UTF8String"))) ?? "no reason given";
        }

        [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
        internal static partial nint AutoreleasePoolPush();

        [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
        internal static partial void AutoreleasePoolPop(nint pool);

        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint ClassNamed(string name);

        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SelectorNamed(string name);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static partial nint MsgSend(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nint MsgSendOne(nint receiver, nint selector, nint argument);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial byte MsgSendBoolThree(nint receiver, nint selector, nint first, nint second, nint third);
    }
}
