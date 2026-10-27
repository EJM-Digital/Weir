using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// A file's identity and content state: device, inode, size and modification time in nanoseconds.
/// </summary>
/// <remarks>
/// The device and inode come from <c>GetFileInformationByHandle</c> on Windows (the volume serial number and file index).
/// .NET exposes no portable inode, so elsewhere they are 0 and a replaced file is caught by its size and modification time
/// alone.
/// </remarks>
public readonly record struct SourceFingerprint(ulong Device, ulong Inode, long SizeBytes, long ModifiedTimeNs);

/// <summary>A held source handle that permits readers but prevents writers.</summary>
public sealed class SourceReadGuard : IDisposable
{
    private FileStream? _handle;

    internal SourceReadGuard(FileStream handle)
    {
        _handle = handle;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

/// <summary>Reading, reserving and checking the source of a pass.</summary>
public static partial class SourceFiles
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Reserves a source for read-only processing, or explains why Processing must wait. The
    /// handle shares reading and deletion but not writing, so it both detects a writer and keeps one from starting.
    /// </summary>
    public static (SourceReadGuard? Guard, string? Problem) AcquireReadGuard(string path, IFileSystem? files = null)
    {
        try
        {
            var handle = (files ?? PhysicalFileSystem.Instance).OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1);
            return (new SourceReadGuard(handle), null);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return (null, "This file is still open for writing by another program. Weir will wait until the downloader or importer closes it.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, "Weir could not reserve this file for safe read-only processing, so it will wait. " +
                          $"The system reported: {exception.Message}.");
        }
    }

    /// <summary>Why another program's writer keeps this source from being reserved, or null when it can be.</summary>
    public static string? SourceWriterProblem(string path, IFileSystem? files = null)
    {
        var (guard, problem) = AcquireReadGuard(path, files);
        guard?.Dispose();
        return problem;
    }

    /// <summary>
    /// Checks the source opens for reading and the output folder accepts a write. Null when both hold.
    /// </summary>
    public static string? CheckFileAccess(ProcessingLibraryRecord library, string filePath, string outputFolder, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        files ??= PhysicalFileSystem.Instance;
        if (library.SkipAccessTests)
        {
            return null;
        }

        try
        {
            using var handle = files.OpenFile(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            _ = handle.ReadByte();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Weir could not open this file for reading — it is usually locked by whatever is still " +
                   $"writing it. The system reported: {exception.Message}.";
        }

        if (SourceWriterProblem(filePath, files) is { } writer)
        {
            return writer;
        }

        var probe = Path.Join(outputFolder, $".weir-write-test-{Environment.ProcessId}");
        try
        {
            files.CreateDirectory(outputFolder);
            files.WriteAllBytes(probe, "0"u8.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Weir cannot write to the output folder ({outputFolder}), so it did not start work that " +
                   $"would have nowhere to go. The system reported: {exception.Message}.";
        }
        finally
        {
            FileLifecycle.BestEffortDelete(probe, files);
        }

        return null;
    }

    /// <summary>The file's current fingerprint. Throws <see cref="IOException"/> when the file cannot be read.</summary>
    public static SourceFingerprint Fingerprint(string path, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        if (!files.FileExists(path))
        {
            throw new FileNotFoundException($"{path} could not be found.", path);
        }

        var modifiedNs = (files.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).Ticks * 100;
        ulong device = 0;
        ulong inode = 0;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var handle = files.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (GetFileInformationByHandle(handle, out var byHandle))
                {
                    device = byHandle.VolumeSerialNumber;
                    inode = ((ulong)byHandle.FileIndexHigh << 32) | byHandle.FileIndexLow;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Size and time still describe the file.
            }
        }

        return new SourceFingerprint(device, inode, files.GetLength(path), modifiedNs);
    }

    /// <summary>
    /// A short, stable tag for a dedupe key (#545) so a pass-through or reject job for a since-replaced file
    /// (same relative path, different content — e.g. a re-download with the same name) queues again instead of being
    /// silently absorbed by a finished or failed row still sitting under the old key. Built from the fingerprint's size
    /// and modification time (not the device/inode, which are 0 off Windows); a file that cannot be read yet (already
    /// gone, or a race with the writer) falls back to a fixed tag, so two enqueue attempts for the same still-unreadable
    /// file keep deduping against each other rather than piling up job rows.
    /// </summary>
    public static string DedupeFingerprintTag(string absolutePath, IFileSystem? files = null)
    {
        try
        {
            var fingerprint = Fingerprint(absolutePath, files);
            return string.Create(CultureInfo.InvariantCulture, $"{fingerprint.SizeBytes}-{fingerprint.ModifiedTimeNs}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? code is ErrorSharingViolation or ErrorLockViolation
            : exception.Message.Contains("process", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("lock", StringComparison.OrdinalIgnoreCase);
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
