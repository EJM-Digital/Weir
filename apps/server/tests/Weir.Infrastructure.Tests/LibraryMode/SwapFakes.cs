using System.Security.Cryptography;
using System.Text;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>How an injected fault behaves.</summary>
public enum FaultMode
{
    /// <summary>The operation throws without taking effect; everything after it works.</summary>
    Throw,

    /// <summary>The operation takes effect, then throws (a lost reply); everything after it works.</summary>
    ThrowAfterEffect,

    /// <summary>The process dies at the operation: it does not take effect, and nothing after it runs.</summary>
    Crash,

    /// <summary>The process dies just after the operation took effect.</summary>
    CrashAfterEffect,
}

/// <summary>
/// An in-memory filesystem (plus the journal, through <see cref="Step"/>) that counts every operation and can fail any one of them.
/// </summary>
internal sealed class FakeSwapFileSystem : ISwapFileSystem
{
    private sealed class Node
    {
        public required string Content { get; set; }

        public long ModifiedTimeNs { get; set; }

        public ulong Inode { get; init; }

        public int Links { get; set; } = 1;

        public bool ReadOnly { get; set; }

        public bool InUse { get; set; }

        public string Permissions { get; set; } = "default";
    }

    private readonly Dictionary<string, Node> _files = new(StringComparer.Ordinal);
    private ulong _nextInode = 100;
    private long _clock = 1_000_000_000;
    private int _index;
    private bool _dead;

    /// <summary>Every operation in order, as "Name(args)".</summary>
    public List<string> Operations { get; } = [];

    /// <summary>Mutations that took effect, in order.</summary>
    public List<string> Effects { get; } = [];

    public int? FaultAt { get; set; }

    public FaultMode Mode { get; set; }

    public Func<Exception> FaultException { get; set; } = () => new IOException("injected failure");

    /// <summary>Runs before each operation (after it is counted): another program acting at exactly that moment.</summary>
    public Action<string>? OnStep { get; set; }

    public long? FreeBytes { get; set; } = 100L << 30;

    public string? WriteProblem { get; set; }

    public bool LinkCountUnknown { get; set; }

    /// <summary>Paths whose rename (or delete) fails with a sharing violation.</summary>
    public HashSet<string> LockedForRename { get; } = new(StringComparer.Ordinal);

    /// <summary>A folder prefix <see cref="Move"/> treats as a different volume from everywhere else, so moving between the
    /// two throws <see cref="CrossVolumeException"/> and the #735 copy-verify-delete fallback runs instead.</summary>
    public string? OtherVolumeFolder { get; set; }

    /// <summary>Makes the next <see cref="CopyOverReservation"/> write one byte short, to test that a corrupt copy is
    /// caught by its content before the source is deleted. Resets itself after firing once.</summary>
    public bool CorruptNextCopy { get; set; }

    public bool Crashed => _dead;

    public IEnumerable<string> Paths => _files.Keys.Order(StringComparer.Ordinal);

    /// <summary>A new process: faults off, operations work again.</summary>
    public void Restart()
    {
        _dead = false;
        FaultAt = null;
    }

    public void Put(string path, string content, int links = 1)
    {
        _files[path] = new Node { Content = content, ModifiedTimeNs = Tick(), Inode = _nextInode++, Links = links };
    }

    /// <summary>A write by someone else (the manager, the caller's ffmpeg). Not a faultable operation.</summary>
    public void Write(string path, string content)
    {
        if (_files.TryGetValue(path, out var node))
        {
            node.Content = content;
            node.ModifiedTimeNs = Tick();
        }
        else
        {
            Put(path, content);
        }
    }

    public string Read(string path) => _files[path].Content;

    public string PermissionsOf(string path) => _files[path].Permissions;

    public void SetPermissions(string path, string permissions) => _files[path].Permissions = permissions;

    public void SetReadOnly(string path) => _files[path].ReadOnly = true;

    public void SetInUse(string path) => _files[path].InUse = true;

    public bool Has(string path) => _files.ContainsKey(path);

    /// <summary>Counts an operation and applies the fault. Returns an action to call once the effect has happened.</summary>
    public Action Step(string operation)
    {
        var index = _index++;
        Operations.Add(operation);
        OnStep?.Invoke(operation);
        if (_dead)
        {
            throw new IOException("the process is dead");
        }

        if (FaultAt != index)
        {
            return () => Effects.Add(operation);
        }

        switch (Mode)
        {
            case FaultMode.Throw:
                throw FaultException();
            case FaultMode.Crash:
                _dead = true;
                throw new IOException("crash");
            case FaultMode.ThrowAfterEffect:
                return () =>
                {
                    Effects.Add(operation);
                    throw FaultException();
                };
            default:
                return () =>
                {
                    Effects.Add(operation);
                    _dead = true;
                    throw new IOException("crash");
                };
        }
    }

    public bool FileExists(string path)
    {
        Step($"FileExists({path})")();
        return _files.ContainsKey(path);
    }

    public SourceFingerprint Fingerprint(string path)
    {
        Step($"Fingerprint({path})")();
        var node = Get(path);
        return new SourceFingerprint(7, node.Inode, node.Content.Length, node.ModifiedTimeNs);
    }

    public int? LinkCount(string path)
    {
        Step($"LinkCount({path})")();
        return LinkCountUnknown ? null : Get(path).Links;
    }

    public bool IsReadOnly(string path)
    {
        Step($"IsReadOnly({path})")();
        return Get(path).ReadOnly;
    }

    public long? AvailableFreeBytes(string directory)
    {
        Step($"AvailableFreeBytes({directory})")();
        return FreeBytes;
    }

    public void ProbeWrite(string directory)
    {
        Step($"ProbeWrite({directory})")();
        if (WriteProblem is not null)
        {
            throw new UnauthorizedAccessException(WriteProblem);
        }
    }

    public bool IsInUse(string path)
    {
        Step($"IsInUse({path})")();
        return Get(path).InUse;
    }

    public void CopyPermissions(string source, string destination)
    {
        var effect = Step($"CopyPermissions({source} -> {destination})");
        Get(destination).Permissions = Get(source).Permissions;
        effect();
    }

    public void Move(string source, string destination)
    {
        var effect = Step($"Move({source} -> {destination})");
        if (LockedForRename.Contains(source))
        {
            throw new FileInUseException("The process cannot access the file because it is being used by another process.");
        }

        if (IsOtherVolume(source) != IsOtherVolume(destination))
        {
            throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.");
        }

        var node = Get(source);
        if (_files.ContainsKey(destination))
        {
            throw new IOException($"Cannot create a file when that file already exists: '{destination}'");
        }

        _files.Remove(source);
        _files[destination] = node;
        effect();
    }

    public void Delete(string path)
    {
        var effect = Step($"Delete({path})");
        if (LockedForRename.Contains(path))
        {
            throw new FileInUseException("The process cannot access the file because it is being used by another process.");
        }

        _files.Remove(path);
        effect();
    }

    public void EnsureDirectory(string directory) => Step($"EnsureDirectory({directory})")();

    public string ContentHash(string path)
    {
        Step($"ContentHash({path})")();
        return Hash(Get(path).Content);
    }

    public bool TryReserve(string path)
    {
        var effect = Step($"TryReserve({path})");
        if (_files.ContainsKey(path))
        {
            effect();
            return false;
        }

        _files[path] = new Node { Content = string.Empty, ModifiedTimeNs = Tick(), Inode = _nextInode++ };
        effect();
        return true;
    }

    public void ReplaceReservation(string source, string destination)
    {
        var effect = Step($"ReplaceReservation({source} -> {destination})");
        if (LockedForRename.Contains(source))
        {
            throw new FileInUseException("The process cannot access the file because it is being used by another process.");
        }

        if (IsOtherVolume(source) != IsOtherVolume(destination))
        {
            throw new CrossVolumeException($"'{source}' and '{destination}' are on different volumes.");
        }

        var node = Get(source);
        _files.Remove(source);
        _files[destination] = node;
        effect();
    }

    public void CopyOverReservation(string source, string destination)
    {
        var effect = Step($"CopyOverReservation({source} -> {destination})");
        var node = Get(source);
        var content = CorruptNextCopy ? node.Content[..^1] : node.Content;
        CorruptNextCopy = false;
        _files[destination] = new Node { Content = content, ModifiedTimeNs = Tick(), Inode = _nextInode++, Permissions = node.Permissions };
        effect();
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private bool IsOtherVolume(string path) => OtherVolumeFolder is not null && path.StartsWith(OtherVolumeFolder, StringComparison.Ordinal);

    public IEnumerable<string> EnumerateLeftovers(string folder)
    {
        Step($"EnumerateLeftovers({folder})")();
        var prefix = folder.EndsWith('/') ? folder : folder + "/";
        return _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && SafeSwapRules.TryParseLeftover(path, out _, out _))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private Node Get(string path) =>
        _files.TryGetValue(path, out var node) ? node : throw new FileNotFoundException($"No such file: '{path}'", path);

    private long Tick() => _clock += 1_000_000;
}

/// <summary>An in-memory journal whose writes are faultable operations of the same <see cref="FakeSwapFileSystem"/>.</summary>
internal sealed class FakeSwapJournal : ISwapJournal
{
    private readonly FakeSwapFileSystem _files;
    private readonly Dictionary<long, SwapJournalEntry> _entries = [];

    public FakeSwapJournal(FakeSwapFileSystem files)
    {
        _files = files;
    }

    public List<SwapJournalEntry> History { get; } = [];

    public SwapJournalEntry? Latest(long jobId) => _entries.GetValueOrDefault(jobId);

    public Task RecordAsync(SwapJournalEntry entry, CancellationToken cancellationToken = default)
    {
        var effect = _files.Step($"Journal({entry.State})");
        _entries[entry.JobId] = entry;
        History.Add(entry);
        effect();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SwapJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        _files.Step("Journal(list)")();
        return Task.FromResult<IReadOnlyList<SwapJournalEntry>>(_entries.Values.Where(entry => entry.IsUnfinished).OrderBy(entry => entry.JobId).ToList());
    }

    public Task<IReadOnlyList<string>> ListKeepConflictPathsAsync(CancellationToken cancellationToken = default)
    {
        _files.Step("Journal(conflicts)")();
        return Task.FromResult<IReadOnlyList<string>>(
            _entries.Values.Where(entry => entry.State == SwapJournalState.KeepConflict).OrderBy(entry => entry.JobId).Select(entry => entry.OriginalPath).ToList());
    }
}

/// <summary>A validator that passes, fails or throws as told, and can run a side effect (a concurrent change) while it checks.</summary>
internal sealed class FakeValidator : ISwapOutputValidator
{
    public SwapValidation Answer { get; set; } = SwapValidation.Pass;

    public Exception? Throw { get; set; }

    public Action? During { get; set; }

    public List<string> Checked { get; } = [];

    public Task<SwapValidation> ValidateAsync(string originalPath, string outputPath, double? originalDurationSeconds, CancellationToken cancellationToken)
    {
        Checked.Add(outputPath);
        During?.Invoke();
        return Throw is not null ? Task.FromException<SwapValidation>(Throw) : Task.FromResult(Answer);
    }
}
