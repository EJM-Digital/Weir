using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Tests;

internal enum FileCall
{
    DirectoryExists,
    ListFiles,
    GetLastWriteTimeUtc,
    DeleteFile,
}

/// <summary>
/// The real file system with chosen calls made to fail the way the operating system fails them, or to run an action
/// first (a file removed or locked "between two calls"), so a race with the outside world is a deterministic test.
/// </summary>
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

    public bool DirectoryExists(string path)
    {
        Enter(FileCall.DirectoryExists, path);
        return _real.DirectoryExists(path);
    }

    public IReadOnlyList<string> ListFiles(string path, EnumerationOptions options)
    {
        Enter(FileCall.ListFiles, path);
        return _real.ListFiles(path, options);
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        Enter(FileCall.GetLastWriteTimeUtc, path);
        return _real.GetLastWriteTimeUtc(path);
    }

    public void DeleteFile(string path)
    {
        Enter(FileCall.DeleteFile, path);
        _real.DeleteFile(path);
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
