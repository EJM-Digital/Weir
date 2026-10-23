using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Jobs;

/// <summary>What startup recovery found and removed.</summary>
public sealed record StartupRecoveryReport(StartupJobRecoveryResult Jobs, int PartialOutputsRemoved, int WorkTempFilesRemoved);

/// <summary>A job that was leased when the previous process stopped.</summary>
public sealed record InterruptedJob(long Id, string JobKind, string? PayloadJson);

/// <summary>
/// Startup crash recovery: requeue or fail jobs left leased, remove hidden <c>.partial</c> output files, and
/// remove remux temp output left in work folders (#534).
/// </summary>
/// <remarks>
/// Startup is a hard process boundary for this single-node app: no worker is running yet, so a leased
/// row belongs to a dead worker, and a Weir temp file belongs to interrupted work.
/// <para>
/// A crashed remux can leave multi-gigabyte temp output in the work folder, so recovery removes (1) the
/// remux temp output of every job that was in progress, found from its payload's library and file, and
/// (2) any other remux temp output in a library work folder (#534). Both only ever match the exact names Weir
/// creates (<see cref="WeirTempFiles"/>), never other files, and only at the top of the work folder,
/// where Weir writes them.
/// </para>
/// <para>
/// A failed copy kept on request ("Keep a failed file's half-written copy for a day") is one of those files too. While that setting is on, the
/// second sweep leaves any file younger than the work file sweep's stale age, so a restart never shortens the day the copy
/// is kept for.
/// </para>
/// </remarks>
public static class StartupRecovery
{
    /// <summary>
    /// Recovers the jobs and folders the last run left behind. While "Keep a failed file's half-written copy for a day" is on, a leftover work file
    /// is removed only once it is older than <paramref name="keptCopyMinAge"/>, so a copy kept on purpose lasts as long as the
    /// work file sweep would let it.
    /// </summary>
    public static async Task<StartupRecoveryReport> RunAsync(
        ProcessingJobStore queue,
        string weirHome,
        DateTimeOffset now,
        TimeSpan keptCopyMinAge,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(logger);
        var (result, interrupted, libraries, keepsFailedCopies) = await queue.InTransactionAsync(
            (connection, transaction) =>
            {
                var (jobs, rows) = RecoverIncompleteJobs(connection, transaction, now);
                return (jobs, rows, ProcessingLibraryFolders.List(connection, transaction), KeepsFailedWorkFiles(connection, transaction));
            },
            cancellationToken).ConfigureAwait(false);

        var tempRemoved = RemoveInterruptedRemuxTempFiles(interrupted, libraries, weirHome, logger);
        tempRemoved += SweepWorkFolderTempFiles(libraries, weirHome, keepsFailedCopies ? now.UtcDateTime - keptCopyMinAge : null, logger);
        var partialRemoved = CleanupPartialOutputFiles(libraries, weirHome);

        if (result.TotalRecovered > 0 || partialRemoved > 0 || tempRemoved > 0)
        {
            logger.LogWarning(
                "Weir startup recovered interrupted work recovered_jobs={RecoveredJobs} partial_outputs_removed={PartialOutputsRemoved} work_temp_files_removed={WorkTempFilesRemoved}",
                $"{{'processing_requeued': {result.ProcessingRequeued}, 'processing_failed': {result.ProcessingFailed}}}",
                partialRemoved,
                tempRemoved);
        }

        return new StartupRecoveryReport(result, partialRemoved, tempRemoved);
    }

    /// <summary>Requeue or fail every leased job, inside the caller's transaction.</summary>
    public static (StartupJobRecoveryResult Result, List<InterruptedJob> Interrupted) RecoverIncompleteJobs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now)
    {
        var when = Timestamp.TruncateToMicroseconds(now.ToUniversalTime());
        var requeued = 0;
        var failed = 0;
        var interrupted = new List<InterruptedJob>();
        var rows = ProcessingJobStore.Query(
            connection,
            transaction,
            "SELECT id, dedupe_key, job_kind, payload_json, status, lease_owner, lease_expires_at, attempt_count, max_attempts, " +
            "last_error, not_before, runner_cost, priority, created_at, updated_at FROM jobs WHERE status = @leased",
            ("@leased", ProcessingJobStatus.Leased));
        foreach (var row in rows)
        {
            var decision = StartupJobRecovery.Decide(row.AttemptCount, row.MaxAttempts, when);
            ProcessingJobStore.Execute(
                connection,
                transaction,
                "UPDATE jobs SET lease_owner = NULL, lease_expires_at = NULL, status = @status, last_error = @error, " +
                "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                ("@status", decision.Status),
                ("@error", decision.LastError),
                ("@id", row.Id));
            if (decision.Requeued)
            {
                requeued++;
            }
            else
            {
                failed++;
            }

            interrupted.Add(new InterruptedJob(row.Id, row.JobKind, row.PayloadJson));
        }

        return (new StartupJobRecoveryResult(requeued, failed), interrupted);
    }

    /// <summary>
    /// #534: remove the remux temp output of jobs that were in progress. The name is
    /// <c>{stem}.processing.XXXXXXXX{suffix}</c> of the job's <c>relative_media_path</c>, in its library's
    /// effective work folder.
    /// </summary>
    public static int RemoveInterruptedRemuxTempFiles(
        IReadOnlyList<InterruptedJob> interrupted,
        IReadOnlyList<ProcessingLibraryFolderRow> libraries,
        string weirHome,
        ILogger logger,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(interrupted);
        ArgumentNullException.ThrowIfNull(logger);
        var removed = 0;
        foreach (var job in interrupted)
        {
            var payload = JobPayload.ParseObject(job.PayloadJson);
            var relative = JobPayload.StringProperty(payload, "relative_media_path")?.Trim();
            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var scope = JobPayload.StringProperty(payload, "media_scope");
            var library = ProcessingLibraryFolders.Resolve(libraries, JobPayload.LooseInteger(payload, "library_id"), scope);
            var workFolder = library is not null
                ? ProcessingLibraryFolders.EffectiveWorkFolder(library, weirHome)
                : ProcessingMediaScopes.Normalize(scope) == "tv"
                    ? ProcessingLibraryFolders.DefaultTvWorkFolder(weirHome)
                    : ProcessingLibraryFolders.DefaultMovieWorkFolder(weirHome);
            var pattern = WeirTempFiles.RemuxTempNameFor(relative);
            removed += DeleteMatching(workFolder, pattern.IsMatch, notNewerThanUtc: null, logger, job.Id, fileSystem ?? PhysicalFileSystem.Instance);
        }

        return removed;
    }

    /// <summary>
    /// #534: remove remux temp output left at the top of every library work folder and the default ones. With a
    /// <paramref name="notNewerThanUtc"/>, a file written after that moment is left: it may be a failed copy someone asked
    /// Weir to keep.
    /// </summary>
    public static int SweepWorkFolderTempFiles(IReadOnlyList<ProcessingLibraryFolderRow> libraries, string weirHome, DateTime? notNewerThanUtc, ILogger logger, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var roots = WorkRoots(libraries, weirHome);
        return roots.Sum(root => DeleteMatching(root, WeirTempFiles.IsRemuxTempName, notNewerThanUtc, logger, jobId: null, fileSystem ?? PhysicalFileSystem.Instance));
    }

    private static bool KeepsFailedWorkFiles(SqliteConnection connection, SqliteTransaction transaction) =>
        ProcessingJobStore.Scalar(connection, transaction, "SELECT keep_failed_work_files FROM operator_settings WHERE id = 1") is { } value
        && value is not DBNull
        && WorkAdmissionReader.Bool(value);

    /// <summary>
    /// Remove hidden <c>*.partial</c> files under every library output
    /// folder and <c>WEIR_HOME/processing-output</c>, searched recursively.
    /// </summary>
    public static int CleanupPartialOutputFiles(IReadOnlyList<ProcessingLibraryFolderRow> libraries, string weirHome, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var files = fileSystem ?? PhysicalFileSystem.Instance;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer);
        foreach (var library in libraries)
        {
            var text = library.OutputFolder.Trim();
            if (text.Length > 0)
            {
                roots.Add(ProcessingLibraryFolders.ExpandForFilesystem(text));
            }
        }

        roots.Add(ProcessingLibraryFolders.ExpandForFilesystem(Path.Join(weirHome, "processing-output")));

        var removed = 0;
        foreach (var root in roots)
        {
            if (!files.DirectoryExists(root))
            {
                continue;
            }

            IReadOnlyList<string> candidates;
            try
            {
                candidates = files.ListFiles(root, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    // Hidden files are Weir's too; a link is never followed, so nothing outside the root is swept.
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in candidates)
            {
                if (!WeirTempFiles.IsPartialOutputName(Path.GetFileName(path), ignoreCase: OperatingSystem.IsWindows()))
                {
                    continue;
                }

                try
                {
                    files.DeleteFile(path);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file that cannot be removed now is skipped.
                }
            }
        }

        return removed;
    }

    private static HashSet<string> WorkRoots(IReadOnlyList<ProcessingLibraryFolderRow> libraries, string weirHome)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer)
        {
            ProcessingLibraryFolders.DefaultMovieWorkFolder(weirHome),
            ProcessingLibraryFolders.DefaultTvWorkFolder(weirHome),
        };
        foreach (var library in libraries)
        {
            roots.Add(ProcessingLibraryFolders.ExpandForFilesystem(ProcessingLibraryFolders.EffectiveWorkFolder(library, weirHome)));
        }

        return roots;
    }

    private static int DeleteMatching(string folder, Func<string, bool> isWeirTempName, DateTime? notNewerThanUtc, ILogger logger, long? jobId, IFileSystem files)
    {
        string root;
        try
        {
            root = ProcessingLibraryFolders.ExpandForFilesystem(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return 0;
        }

        if (!files.DirectoryExists(root))
        {
            return 0;
        }

        IReadOnlyList<string> inFolder;
        try
        {
            inFolder = files.ListFiles(root, new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Weir startup could not read work folder {Folder} to remove interrupted temp files: {Error}", root, exception.Message);
            return 0;
        }

        var removed = 0;
        foreach (var path in inFolder)
        {
            if (!isWeirTempName(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                if (notNewerThanUtc is { } cutoff && files.GetLastWriteTimeUtc(path) > cutoff)
                {
                    continue;
                }

                files.DeleteFile(path);
                removed++;
                logger.LogInformation(
                    "Removed interrupted temp output {Path}{JobSuffix}",
                    path,
                    jobId is { } id ? string.Create(CultureInfo.InvariantCulture, $" job_id={id}") : string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Weir startup could not remove interrupted temp file {Path}: {Error}", path, exception.Message);
            }
        }

        return removed;
    }
}
