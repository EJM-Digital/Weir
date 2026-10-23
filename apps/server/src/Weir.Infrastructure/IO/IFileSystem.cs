using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Weir.Infrastructure.IO;

/// <summary>
/// The file-system calls Weir's file handling makes, each a one-line pass-through to <c>System.IO</c>. A test substitutes
/// an implementation that fails a chosen call with the exception the operating system gives, so the handling of a file that
/// vanishes, is locked or is refused can be proved without racing a real disk.
/// </summary>
/// <remarks>Failures are <see cref="IOException"/> (including <see cref="FileNotFoundException"/> and <see cref="DirectoryNotFoundException"/>) or <see cref="UnauthorizedAccessException"/>.</remarks>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>The files under <paramref name="path"/>, all listed before the first is returned.</summary>
    IReadOnlyList<string> ListFiles(string path, EnumerationOptions options);

    /// <summary>The files under <paramref name="path"/> matching <paramref name="pattern"/>, listed as the caller reads them.</summary>
    IEnumerable<string> EnumerateFiles(string path, string pattern, EnumerationOptions options);

    /// <summary>The files and folders directly inside <paramref name="path"/>, as the listing itself describes them: their size and times need no further look at the disk.</summary>
    IEnumerable<FileSystemInfo> EnumerateFileSystemInfos(string path);

    long GetLength(string path);

    DateTime GetLastWriteTimeUtc(string path);

    DateTime GetLastAccessTimeUtc(string path);

    DateTime GetCreationTimeUtc(string path);

    DateTime GetDirectoryLastWriteTimeUtc(string path);

    void SetLastWriteTimeUtc(string path, DateTime value);

    void SetLastAccessTimeUtc(string path, DateTime value);

    FileAttributes GetAttributes(string path);

    /// <summary>The path a symbolic link points to, or <see langword="null"/> when <paramref name="path"/> is not a link. <paramref name="isDirectory"/> says whether it is a folder link.</summary>
    string? GetLinkTarget(string path, bool isDirectory);

    /// <summary>The full path the link at <paramref name="path"/> leads to (the end of the chain when <paramref name="returnFinalTarget"/>, else the next step), or <see langword="null"/> when it is not a link.</summary>
    string? ResolveLinkTarget(string path, bool isDirectory, bool returnFinalTarget);

    long GetAvailableFreeSpace(string path);

    FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None);

    SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share);

    void CreateDirectory(string path);

    /// <summary>Creates the folder, and any parent it makes, with <paramref name="mode"/> (a Unix-only call).</summary>
    [UnsupportedOSPlatform("windows")]
    void CreateDirectory(string path, UnixFileMode mode);

    /// <summary>Sets the permissions of the file or folder (a Unix-only call).</summary>
    [UnsupportedOSPlatform("windows")]
    void SetUnixFileMode(string path, UnixFileMode mode);

    void WriteAllBytes(string path, byte[] bytes);

    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);

    void MoveFile(string source, string destination, bool overwrite);

    void CopyFile(string source, string destination, bool overwrite);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public static PhysicalFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> ListFiles(string path, EnumerationOptions options) => [.. Directory.EnumerateFiles(path, "*", options)];

    public IEnumerable<string> EnumerateFiles(string path, string pattern, EnumerationOptions options) => Directory.EnumerateFiles(path, pattern, options);

    public IEnumerable<FileSystemInfo> EnumerateFileSystemInfos(string path) => new DirectoryInfo(path).EnumerateFileSystemInfos();

    public long GetLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public DateTime GetLastAccessTimeUtc(string path) => File.GetLastAccessTimeUtc(path);

    public DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(path);

    public DateTime GetDirectoryLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime value) => File.SetLastWriteTimeUtc(path, value);

    public void SetLastAccessTimeUtc(string path, DateTime value) => File.SetLastAccessTimeUtc(path, value);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public string? GetLinkTarget(string path, bool isDirectory) => Info(path, isDirectory).LinkTarget;

    public string? ResolveLinkTarget(string path, bool isDirectory, bool returnFinalTarget) =>
        Info(path, isDirectory).ResolveLinkTarget(returnFinalTarget)?.FullName;

    public long GetAvailableFreeSpace(string path) => new DriveInfo(path).AvailableFreeSpace;

    public FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        new(path, mode, access, share, bufferSize, options);

    public SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share) => File.OpenHandle(path, mode, access, share);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    [UnsupportedOSPlatform("windows")]
    public void CreateDirectory(string path, UnixFileMode mode) => Directory.CreateDirectory(path, mode);

    [UnsupportedOSPlatform("windows")]
    public void SetUnixFileMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);

    private static FileSystemInfo Info(string path, bool isDirectory) => isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
}
