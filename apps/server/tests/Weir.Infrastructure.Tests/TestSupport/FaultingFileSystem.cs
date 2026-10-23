using Microsoft.Win32.SafeHandles;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Tests;

internal enum FileCall
{
    FileExists,
    DirectoryExists,
    ListFiles,
    EnumerateFiles,
    EnumerateFileSystemInfos,
    GetLength,
    GetLastWriteTimeUtc,
    GetLastAccessTimeUtc,
    GetCreationTimeUtc,
    GetDirectoryLastWriteTimeUtc,
    SetLastWriteTimeUtc,
    SetLastAccessTimeUtc,
    GetAttributes,
    GetLinkTarget,
    ResolveLinkTarget,
    GetAvailableFreeSpace,
    OpenFile,
    OpenHandle,
    CreateDirectory,
    WriteAllBytes,
    DeleteFile,
    DeleteDirectory,
    MoveFile,
    CopyFile,
}

/// <summary>
/// The real file system with chosen calls made to fail the way the operating system fails them, or to run an action
/// first (a file removed or locked "between two calls"), so a race with the outside world is a deterministic test.
/// </summary>
/// <remarks>A listing fails when it is asked for, not part-way through reading it.</remarks>
internal sealed class FaultingFileSystem : IFileSystem
{
    private readonly PhysicalFileSystem _real = PhysicalFileSystem.Instance;
    private readonly List<Rule> _rules = [];
    private readonly List<(FileCall Call, string Path)> _calls = [];

    public IReadOnlyList<(FileCall Call, string Path)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public static IOException SharingViolation(string path) =>
        new($"The process cannot access the file '{path}' because it is being used by another process.", unchecked((int)0x80070020));

    public static FileNotFoundException Vanished(string path) => new($"Could not find file '{path}'.", path);

    public static DirectoryNotFoundException FolderVanished(string path) => new($"Could not find a part of the path '{path}'.");

    public static UnauthorizedAccessException Refused(string path) => new($"Access to the path '{path}' is denied.");

    /// <summary>Makes the <paramref name="onCall"/>th call of this kind (to a path ending with <paramref name="pathEndsWith"/>, when given) throw.</summary>
    public FaultingFileSystem Fail(FileCall call, Func<string, Exception> exception, int onCall = 1, string? pathEndsWith = null)
    {
        _rules.Add(new Rule(call, onCall, pathEndsWith, exception, Before: null));
        return this;
    }

    /// <summary>Runs <paramref name="before"/> (for example removing the file) just before the <paramref name="onCall"/>th call of this kind.</summary>
    public FaultingFileSystem Before(FileCall call, Action<string> before, int onCall = 1, string? pathEndsWith = null)
    {
        _rules.Add(new Rule(call, onCall, pathEndsWith, Exception: null, before));
        return this;
    }

    public bool FileExists(string path) => Run(FileCall.FileExists, path, () => _real.FileExists(path));

    public bool DirectoryExists(string path) => Run(FileCall.DirectoryExists, path, () => _real.DirectoryExists(path));

    public IReadOnlyList<string> ListFiles(string path, EnumerationOptions options) => Run(FileCall.ListFiles, path, () => _real.ListFiles(path, options));

    public IEnumerable<string> EnumerateFiles(string path, string pattern, EnumerationOptions options) =>
        Run(FileCall.EnumerateFiles, path, () => _real.EnumerateFiles(path, pattern, options));

    public IEnumerable<FileSystemInfo> EnumerateFileSystemInfos(string path) =>
        Run(FileCall.EnumerateFileSystemInfos, path, () => _real.EnumerateFileSystemInfos(path));

    public long GetLength(string path) => Run(FileCall.GetLength, path, () => _real.GetLength(path));

    public DateTime GetLastWriteTimeUtc(string path) => Run(FileCall.GetLastWriteTimeUtc, path, () => _real.GetLastWriteTimeUtc(path));

    public DateTime GetLastAccessTimeUtc(string path) => Run(FileCall.GetLastAccessTimeUtc, path, () => _real.GetLastAccessTimeUtc(path));

    public DateTime GetCreationTimeUtc(string path) => Run(FileCall.GetCreationTimeUtc, path, () => _real.GetCreationTimeUtc(path));

    public DateTime GetDirectoryLastWriteTimeUtc(string path) =>
        Run(FileCall.GetDirectoryLastWriteTimeUtc, path, () => _real.GetDirectoryLastWriteTimeUtc(path));

    public void SetLastWriteTimeUtc(string path, DateTime value) => Run(FileCall.SetLastWriteTimeUtc, path, () => _real.SetLastWriteTimeUtc(path, value));

    public void SetLastAccessTimeUtc(string path, DateTime value) => Run(FileCall.SetLastAccessTimeUtc, path, () => _real.SetLastAccessTimeUtc(path, value));

    public FileAttributes GetAttributes(string path) => Run(FileCall.GetAttributes, path, () => _real.GetAttributes(path));

    public string? GetLinkTarget(string path, bool isDirectory) => Run(FileCall.GetLinkTarget, path, () => _real.GetLinkTarget(path, isDirectory));

    public string? ResolveLinkTarget(string path, bool isDirectory, bool returnFinalTarget) =>
        Run(FileCall.ResolveLinkTarget, path, () => _real.ResolveLinkTarget(path, isDirectory, returnFinalTarget));

    public long GetAvailableFreeSpace(string path) => Run(FileCall.GetAvailableFreeSpace, path, () => _real.GetAvailableFreeSpace(path));

    public FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        Run(FileCall.OpenFile, path, () => _real.OpenFile(path, mode, access, share, bufferSize, options));

    public SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share) =>
        Run(FileCall.OpenHandle, path, () => _real.OpenHandle(path, mode, access, share));

    public void CreateDirectory(string path) => Run(FileCall.CreateDirectory, path, () => _real.CreateDirectory(path));

    public void WriteAllBytes(string path, byte[] bytes) => Run(FileCall.WriteAllBytes, path, () => _real.WriteAllBytes(path, bytes));

    public void DeleteFile(string path) => Run(FileCall.DeleteFile, path, () => _real.DeleteFile(path));

    public void DeleteDirectory(string path, bool recursive) => Run(FileCall.DeleteDirectory, path, () => _real.DeleteDirectory(path, recursive));

    public void MoveFile(string source, string destination, bool overwrite) =>
        Run(FileCall.MoveFile, source, () => _real.MoveFile(source, destination, overwrite));

    public void CopyFile(string source, string destination, bool overwrite) =>
        Run(FileCall.CopyFile, source, () => _real.CopyFile(source, destination, overwrite));

    private void Run(FileCall call, string path, Action action) => Run(call, path, () =>
    {
        action();
        return 0;
    });

    private T Run<T>(FileCall call, string path, Func<T> action)
    {
        Enter(call, path);
        return action();
    }

    private void Enter(FileCall call, string path)
    {
        lock (_calls)
        {
            _calls.Add((call, path));
        }

        foreach (var rule in _rules.Where(r => r.Call == call && Matches(r, path)))
        {
            rule.Seen++;
            if (rule.Seen != rule.OnCall)
            {
                continue;
            }

            rule.Before?.Invoke(path);
            if (rule.Exception is not null)
            {
                throw rule.Exception(path);
            }
        }
    }

    private static bool Matches(Rule rule, string path) =>
        rule.PathEndsWith is null || path.EndsWith(rule.PathEndsWith, StringComparison.OrdinalIgnoreCase);

    private sealed record Rule(FileCall Call, int OnCall, string? PathEndsWith, Func<string, Exception>? Exception, Action<string>? Before)
    {
        public int Seen { get; set; }
    }
}
