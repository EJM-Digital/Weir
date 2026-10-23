using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>Another program holds the file: a Windows sharing or lock violation, or <c>EBUSY</c>/<c>ETXTBSY</c> elsewhere.</summary>
public sealed class FileInUseException : IOException
{
    public FileInUseException()
    {
    }

    public FileInUseException(string message)
        : base(message)
    {
    }

    public FileInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <see cref="ISwapFileSystem.Move"/> refused because the source and destination are on different volumes (Windows
/// <c>ERROR_NOT_SAME_DEVICE</c>, POSIX <c>EXDEV</c>): #735's originals-keeping falls back to a verified copy and delete.
/// </summary>
public sealed class CrossVolumeException : IOException
{
    public CrossVolumeException()
    {
    }

    public CrossVolumeException(string message)
        : base(message)
    {
    }

    public CrossVolumeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Every filesystem operation the swap and its sweep perform, and nothing else, so a test can make any one of them fail
/// (or "crash" the process at it) and prove no step can lose the file.
/// </summary>
/// <remarks>Failures are <see cref="IOException"/> (including <see cref="FileInUseException"/>) or <see cref="UnauthorizedAccessException"/>.</remarks>
public interface ISwapFileSystem
{
    bool FileExists(string path);

    /// <summary>The remux pass's source fingerprint (<see cref="SourceFiles.Fingerprint"/>): device, inode, size, mtime.</summary>
    SourceFingerprint Fingerprint(string path);

    /// <summary>Hard links to the file; <see langword="null"/> when the platform cannot say.</summary>
    int? LinkCount(string path);

    /// <summary>Whether the file itself refuses replacement (the Windows read-only attribute).</summary>
    bool IsReadOnly(string path);

    /// <summary>Bytes the current user may still write on the volume holding <paramref name="directory"/>; <see langword="null"/> when unknown.</summary>
    long? AvailableFreeBytes(string directory);

    /// <summary>Creates and removes a probe file in <paramref name="directory"/>; throws when that is not allowed.</summary>
    void ProbeWrite(string directory);

    /// <summary>Whether another program holds the file so that it cannot be renamed now.</summary>
    bool IsInUse(string path);

    /// <summary>Best effort: give <paramref name="destination"/> the permissions (and, where allowed, the owner) of <paramref name="source"/>. Never the timestamps.</summary>
    void CopyPermissions(string source, string destination);

    /// <summary>An atomic same-volume rename that never replaces an existing <paramref name="destination"/>.</summary>
    void Move(string source, string destination);

    /// <summary>Removes the file; a missing file is not an error.</summary>
    void Delete(string path);

    /// <summary>Every file under <paramref name="folder"/> (recursively) whose name is a swap leftover.</summary>
    IEnumerable<string> EnumerateLeftovers(string folder);

    /// <summary>Creates <paramref name="directory"/>, and any missing parents, if it does not already exist. #735: the
    /// originals folder a kept original moves into.</summary>
    void EnsureDirectory(string directory);

    /// <summary>A hash of the file's whole contents. #735: verifies a cross-volume copy against its source before the
    /// source is deleted — a same-size but corrupt or preallocated copy must not pass on size alone.</summary>
    string ContentHash(string path);

    /// <summary>
    /// Atomically creates an empty file at <paramref name="path"/>, or does nothing and returns false when a file is
    /// already there. #735: how a kept-original destination is claimed — a name a plain existence check just found free
    /// could still be claimed by another swap between that check and the claim; this cannot.
    /// </summary>
    bool TryReserve(string path);

    /// <summary>
    /// A same-volume atomic rename that replaces <paramref name="destination"/>. #735 only: <paramref name="destination"/>
    /// must be the caller's own reservation from <see cref="TryReserve"/> — overwriting is safe only because nothing but
    /// the reservation holder could ever have put anything there.
    /// </summary>
    void ReplaceReservation(string source, string destination);

    /// <summary>Copies <paramref name="source"/> onto <paramref name="destination"/>, replacing it. #735 only, the same
    /// way as <see cref="ReplaceReservation"/>: used when the reservation is on a different volume from the source.</summary>
    void CopyOverReservation(string source, string destination);
}

/// <summary>The real filesystem.</summary>
/// <remarks>
/// <para>
/// <b>Renames.</b> On Windows <see cref="Move"/> calls <c>MoveFileExW</c> with only <c>MOVEFILE_WRITE_THROUGH</c>: no
/// <c>MOVEFILE_REPLACE_EXISTING</c>, so a file that appeared at the destination (say, the manager importing a newer copy while
/// Weir worked) is never overwritten, and no <c>MOVEFILE_COPY_ALLOWED</c>, so a rename can never silently become a non-atomic
/// copy. <c>File.Replace</c> (<c>ReplaceFile</c>) was not used: it folds both renames of the commit into one call whose partial
/// failures (<c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c>) leave the same states as two renames, but hides which one happened.
/// Elsewhere <see cref="File.Move(string, string, bool)"/> without overwrite is used: .NET implements it with
/// <c>link</c> + <c>unlink</c> (falling back to an existence check and <c>rename</c> where links are unsupported), which also
/// refuses an existing destination.
/// </para>
/// <para>
/// <b>Link count.</b> <c>GetFileInformationByHandle</c>'s <c>nNumberOfLinks</c> on Windows (a handle opened for attributes only,
/// so it works on a file another program holds); <c>statx</c>'s <c>stx_nlink</c> on Linux. Other platforms report unknown.
/// </para>
/// <para>
/// <b>Permissions.</b> Windows: the file's DACL is copied (owner changes need privileges Weir does not ask for). POSIX: the mode
/// bits are copied, and the owner and group too when running as root. The modification time is deliberately not copied: the
/// manager's rescan uses size and mtime to notice the change.
/// </para>
/// </remarks>
public sealed partial class PhysicalSwapFileSystem : ISwapFileSystem
{
    private readonly IFileSystem _files;

    public PhysicalSwapFileSystem(IFileSystem? files = null)
    {
        _files = files ?? PhysicalFileSystem.Instance;
    }

    public static PhysicalSwapFileSystem Instance { get; } = new();

    public bool FileExists(string path) => _files.FileExists(path);

    public SourceFingerprint Fingerprint(string path) => SourceFiles.Fingerprint(path, _files);

    public int? LinkCount(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsHandle(path, FileReadAttributes);
            return GetFileInformationByHandle(handle, out var information)
                ? (int)information.NumberOfLinks
                : throw WindowsError(Marshal.GetLastPInvokeError(), $"'{path}'");
        }

        return UnixStat(path) is { } stat ? (int)stat.Links : null;
    }

    public bool IsReadOnly(string path) =>
        OperatingSystem.IsWindows() && _files.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    public long? AvailableFreeBytes(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetDiskFreeSpaceExW(directory, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }

            return _files.GetAvailableFreeSpace(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void ProbeWrite(string directory)
    {
        var probe = Path.Join(directory, $".weir-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using var stream = _files.OpenFile(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
            stream.WriteByte(0);
        }
        finally
        {
            try
            {
                _files.DeleteFile(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public bool IsInUse(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // POSIX renames are not blocked by open handles; EBUSY surfaces from Move itself.
            return false;
        }

        var handle = CreateFileW(path, DeleteAccess, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return error is ErrorSharingViolation or ErrorLockViolation ? true : throw WindowsError(error, $"'{path}'");
    }

    public void CopyPermissions(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            // A security object read from a file persists nothing unless it is marked modified, so the DACL is copied into a
            // new one. Inherited entries in it are ignored by Windows and inherited afresh from the (same) folder.
            var read = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
            var copy = new FileSecurity();
            copy.SetSecurityDescriptorBinaryForm(read.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new FileInfo(destination).SetAccessControl(copy);
            return;
        }

        var mode = File.GetUnixFileMode(source);
        if (UnixIsRoot() && UnixStat(source) is { } stat && UnixChown(destination, stat.Uid, stat.Gid) != 0)
        {
            throw new IOException($"chown failed with errno {Marshal.GetLastPInvokeError()} for '{destination}'");
        }

        // After chown, which clears set-user-ID and set-group-ID bits.
        File.SetUnixFileMode(destination, mode);
    }

    public void Move(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(source, destination, MoveFileWriteThrough))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNotSameDevice)
                {
                    throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.");
                }

                throw WindowsError(error, $"'{source}' -> '{destination}'");
            }

            return;
        }

        try
        {
            _files.MoveFile(source, destination, overwrite: false);
        }
        catch (IOException exception) when (exception is not FileInUseException && exception.HResult is Ebusy or Etxtbsy)
        {
            throw new FileInUseException(exception.Message, exception);
        }
        catch (IOException exception) when (exception is not CrossVolumeException && exception.HResult == Exdev)
        {
            throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.", exception);
        }
    }

    public void Delete(string path)
    {
        try
        {
            _files.DeleteFile(path);
        }
        catch (IOException exception) when (exception is not FileInUseException && IsInUseError(exception))
        {
            throw new FileInUseException(exception.Message, exception);
        }
    }

    public void EnsureDirectory(string directory) => _files.CreateDirectory(directory);

    public string ContentHash(string path)
    {
        using var stream = _files.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public bool TryReserve(string path)
    {
        try
        {
            using var stream = _files.OpenFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void ReplaceReservation(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(source, destination, MoveFileWriteThrough | MoveFileReplaceExisting))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNotSameDevice)
                {
                    throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.");
                }

                throw WindowsError(error, $"'{source}' -> '{destination}'");
            }

            return;
        }

        try
        {
            _files.MoveFile(source, destination, overwrite: true);
        }
        catch (IOException exception) when (exception is not FileInUseException && exception.HResult is Ebusy or Etxtbsy)
        {
            throw new FileInUseException(exception.Message, exception);
        }
        catch (IOException exception) when (exception is not CrossVolumeException && exception.HResult == Exdev)
        {
            throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.", exception);
        }
    }

    public void CopyOverReservation(string source, string destination) => _files.CopyFile(source, destination, overwrite: true);

    public IEnumerable<string> EnumerateLeftovers(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseSensitive,
            // Never follow a link out of the library, and never skip a hidden leftover.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return _files.EnumerateFiles(folder, "*.weir-*", options)
            .Where(path => SafeSwapRules.TryParseLeftover(path, out _, out _));
    }

    private static bool IsInUseError(IOException exception)
    {
        var code = OperatingSystem.IsWindows() ? exception.HResult & 0xFFFF : exception.HResult;
        return OperatingSystem.IsWindows() ? code is ErrorSharingViolation or ErrorLockViolation : code is Ebusy or Etxtbsy;
    }
}
