using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// <see cref="PhysicalSwapFileSystem"/>'s Windows-only interop: the <c>kernel32</c> calls and error codes its
/// cross-platform operations branch into on Windows, kept apart from those operations' own logic.
/// </summary>
public sealed partial class PhysicalSwapFileSystem
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotSameDevice = 17;

    private const uint FileReadAttributes = 0x80;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint MoveFileWriteThrough = 0x8;
    private const uint MoveFileReplaceExisting = 0x1;

    internal static Exception WindowsError(int error, string subject) => error switch
    {
        ErrorSharingViolation or ErrorLockViolation => new FileInUseException($"{new Win32Exception(error).Message} ({subject})"),
        5 => new UnauthorizedAccessException($"{new Win32Exception(error).Message} ({subject})"),
        2 or 3 => new FileNotFoundException($"{new Win32Exception(error).Message} ({subject})"),
        _ => new IOException($"{new Win32Exception(error).Message} ({subject})", unchecked((int)0x80070000) | error),
    };

    private static SafeFileHandle OpenWindowsHandle(string path, uint access)
    {
        var handle = CreateFileW(path, access, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw WindowsError(error, $"'{path}'");
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalBytes, out ulong totalFreeBytes);
}
