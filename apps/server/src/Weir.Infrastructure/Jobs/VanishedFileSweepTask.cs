using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// <c>processing-vanished-file-sweep</c>: every five minutes, say so for the rows of files that have left each library's watched
/// folder and forget them once they have been gone a while (#645), whether or not that library's watched-folder scan runs.
/// </summary>
/// <remarks>
/// A library fed by a media manager's hand-offs often has its periodic scan off, so a rule that ran only inside a scan would
/// never clear its rows. This runs the same rule on its own clock. It never queues work, so it cannot pick up a stray file a scan
/// would have processed. A library whose watched folder cannot be read is skipped, so an unmounted share never empties the list.
/// </remarks>
public sealed partial class VanishedFileSweepTask : IPeriodicTask
{
    private readonly SqliteDatabase _database;
    private readonly WeirOptions _options;
    private readonly LibraryStore _libraries;
    private readonly IFileSystem _fs;
    private readonly TimeProvider _time;
    private readonly ILogger<VanishedFileSweepTask> _logger;
    private readonly DataChangePublisher? _changes;
    private readonly ProcessingJobStore? _jobs;

    public VanishedFileSweepTask(SqliteDatabase database, WeirOptions options, LibraryStore libraries, TimeProvider time, ILogger<VanishedFileSweepTask> logger, DataChangePublisher? changes = null, ProcessingJobStore? jobs = null, IFileSystem? fileSystem = null)
    {
        _fs = fileSystem ?? PhysicalFileSystem.Instance;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _changes = changes;
        _jobs = jobs;
    }

    /// <summary>Test seam: the wait between the first look at a file that was not there and the look that believes it is gone.</summary>
    internal Func<CancellationToken, Task> GoneLookAgain { get; init; } = cancellationToken => Task.Delay(GoneSources.DefaultSettle, cancellationToken);

    public string Name => "processing-vanished-file-sweep";

    public string? Label => "Look for vanished files";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public bool RunAtStart => true;

    public TimeSpan? FailureCooldown => PeriodicSchedule.FailureCooldown;

    public string FailureMessage => "The sweep for files that left their watched folder failed.";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        IReadOnlyList<ProcessingLibraryRecord> libraries;
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            libraries = await _libraries.ListAsync(uow, enabledOnly: false).ConfigureAwait(false);
        }

        foreach (var library in libraries)
        {
            var (runtime, _) = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, _options.WeirHome, _fs);
            if (runtime is null || !_fs.DirectoryExists(runtime.WatchedFolder))
            {
                continue;
            }

            var changes = await VanishedFiles.SettleAsync(
                _database, library.Id, runtime.WatchedFolder, ProcessingMediaScopes.Normalize(library.MediaType), now, "scheduled", GoneLookAgain, WakeForStartedLook, _fs, cancellationToken).ConfigureAwait(false);
            if (!changes.Any)
            {
                continue;
            }

            // In the server log as well as Activity, so it can be checked on a machine where nobody signs in.
            if (changes.Held.Count > 0)
            {
                LogHeld(changes.Held.Count, library.Name, string.Join(", ", changes.Held));
            }

            if (changes.Forgotten.Count > 0)
            {
                LogForgotten(changes.Forgotten.Count, library.Name, string.Join(", ", changes.Forgotten));
            }

            if (changes.Released.Count > 0)
            {
                LogReleased(changes.Released.Count, library.Name, string.Join(", ", changes.Released));
            }

            // A file that left the folder no longer says it is waiting; no job moved to say so.
            _changes?.Publish(DataTopics.LibraryScan);
        }
    }

    /// <summary>A waiting look that a returning file started is claimable now, so the slots that run it are woken.</summary>
    private void WakeForStartedLook(string jobKind) => _jobs?.WakeWorkers(jobKind);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} file(s) of workflow {Library} are no longer in the watched folder and are listed until they have been gone a while: {Paths}")]
    private partial void LogHeld(int count, string library, string paths);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} file(s) of workflow {Library} that were no longer in the watched folder are back: {Paths}")]
    private partial void LogReleased(int count, string library, string paths);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forgot {Count} file(s) that left the watched folder of workflow {Library}: {Paths}")]
    private partial void LogForgotten(int count, string library, string paths);
}
