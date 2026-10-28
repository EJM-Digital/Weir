using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace Weir.Infrastructure.Runtime;

/// <summary>Writes a snapshot of a process, with the stack of every thread, to a file.</summary>
internal interface IStackDumpWriter
{
    void Write(string path);
}

/// <summary>
/// A triage dump of a process through its own diagnostics channel (a named pipe on Windows, a socket in the temp folder on Linux): the
/// stack of every thread and little of the heap, with no tool installed beside Weir. A process started with
/// <c>DOTNET_EnableDiagnostics=0</c> has no channel, and the write then fails.
/// </summary>
internal sealed class DiagnosticsClientDumpWriter(int processId) : IStackDumpWriter
{
    public void Write(string path) => new DiagnosticsClient(processId).WriteDump(DumpType.Triage, path, logDumpGeneration: false);
}

/// <summary>
/// Records what every thread of the server is doing, when <see cref="ThreadPoolWatch"/> finds the server slow to get to work, so a stall
/// that comes once on a machine nobody is watching can be named afterwards: the log says that work waited, and only the stacks say
/// what held the threads.
/// <para>
/// At most <see cref="MostCaptures"/> a run. A snapshot stops the server for a moment and is tens of megabytes, and the cause of a stall
/// at start-up is the same the second time, so the first stall is recorded; a stall that comes again is a second chance to see a different
/// cause, so one more is recorded once <see cref="CoolDown"/> has passed since the first, long enough that it is not the same stall
/// still going on and short enough to be in the same sitting. The last <see cref="KeepFiles"/> snapshots are kept, and one larger than
/// the cap is deleted.
/// </para>
/// <para>
/// A snapshot holds the stacks and some of what they refer to: file names, addresses, and the text of a request being read can be in it.
/// It is kept in the logs folder, which the data folder's protection covers, and is for whoever supports the install.
/// </para>
/// </summary>
internal sealed class StallDumpCapture
{
    /// <summary>The folder inside the logs folder.</summary>
    internal const string FolderName = "diagnostics";

    /// <summary>How many snapshots a run takes at most.</summary>
    internal const int MostCaptures = 2;

    /// <summary>How many snapshots stay on disk, across runs.</summary>
    internal const int KeepFiles = 3;

    /// <summary>The largest snapshot kept.</summary>
    internal const long MostBytes = 256L * 1024 * 1024;

    /// <summary>How long after the first snapshot the second may be taken.</summary>
    internal static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(30);

    private readonly string _folder;
    private readonly IStackDumpWriter _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _mostBytes;
    private readonly Lock _gate = new();
    private int _taken;
    private long _firstAt;
    private Task _last = Task.CompletedTask;

    public StallDumpCapture(string logFolder, IStackDumpWriter writer, TimeProvider time, ILogger logger, long mostBytes = MostBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(logFolder);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _folder = Path.Join(logFolder, FolderName);
        _writer = writer;
        _time = time;
        _logger = logger;
        _mostBytes = mostBytes;
    }

    /// <summary>The snapshot being taken or last taken; a test waits on it.</summary>
    internal Task Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Takes a snapshot on a thread of its own if this run has not had its share; the pool is the thing that is short of threads.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_taken >= MostCaptures || (_taken > 0 && _time.GetElapsedTime(_firstAt) < CoolDown))
            {
                return;
            }

            if (_taken == 0)
            {
                _firstAt = _time.GetTimestamp();
            }

            _taken++;
            _last = BlockingWork.RunAsync(Capture);
        }
    }

    private void Capture()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Prune(KeepFiles - 1);
            var path = Path.Join(_folder, $"stall-{_time.GetUtcNow():yyyyMMddTHHmmss}Z-pid{Environment.ProcessId}.dmp");
            _writer.Write(path);
            var size = new FileInfo(path).Length;
            if (size > _mostBytes)
            {
                File.Delete(path);
                _logger.LogWarning(
                    "Weir's server was slow to get to work and tried to save what its threads were doing, but the snapshot was {Megabytes} MB, over the {Limit} MB it keeps, so it was deleted.",
                    size / (1024 * 1024),
                    _mostBytes / (1024 * 1024));
                return;
            }

            _logger.LogWarning(
                "Weir's server was slow to get to work, so it saved what each of its threads was doing to {File} ({Megabytes} MB). It is for whoever supports this install: it can hold names, addresses and text the server was working on, so share it only with someone you trust.",
                path,
                size / (1024 * 1024));
        }
#pragma warning disable CA1031 // Saving a snapshot is a help for diagnosis; whatever goes wrong with it is said and the server goes on.
        catch (Exception exception) when (exception is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Weir's server was slow to get to work and could not save what its threads were doing.");
        }
    }

    /// <summary>Deletes the oldest snapshots until at most <paramref name="keep"/> are left.</summary>
    private void Prune(int keep)
    {
        var files = new DirectoryInfo(_folder).GetFiles("stall-*.dmp").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(keep))
        {
            old.Delete();
        }
    }
}
