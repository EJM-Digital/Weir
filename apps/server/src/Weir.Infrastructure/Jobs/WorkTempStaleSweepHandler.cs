using System.Globalization;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Handles <c>processing.work_temp_stale_sweep.v1</c>: removes Weir's own remux temp output that has been left in a
/// work folder. The job was queued every hour but nothing handled it, so it waited in the queue for ever and the files
/// it was for were only ever cleared when Weir restarted.
/// </summary>
/// <remarks>
/// It removes exactly what start-up recovery removes (<see cref="WeirTempFiles.IsRemuxTempName"/>, only at the top of
/// a work folder: the scope's default one and every library's effective one) with two differences that make it safe
/// while Weir is running. A file must be untouched for the configured stale age (a day by default), and a file that
/// belongs to a pass running right now is never touched, however old.
/// </remarks>
public sealed class WorkTempStaleSweepHandler : IJobHandler
{
    private readonly ProcessingJobStore _store;
    private readonly WeirOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkTempStaleSweepHandler> _logger;
    private readonly IFileSystem _files;

    public WorkTempStaleSweepHandler(ProcessingJobStore store, WeirOptions options, TimeProvider time, ILogger<WorkTempStaleSweepHandler> logger, IFileSystem? files = null)
    {
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
        _files = files ?? PhysicalFileSystem.Instance;
    }

    public string JobKind => PeriodicJobKinds.WorkTempStaleSweep;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = JobPayload.ParseObject(context.PayloadJson);
        var scope = ProcessingMediaScopes.Normalize(JobPayload.StringProperty(payload, "media_scope"));
        var trigger = JobPayload.StringProperty(payload, "trigger");

        var (libraries, running) = await _store.InTransactionAsync(
            (connection, transaction) => (
                ProcessingLibraryFolders.List(connection, transaction),
                ProcessingJobStore.Query(
                    connection,
                    transaction,
                    "SELECT id, dedupe_key, job_kind, payload_json, status, lease_owner, lease_expires_at, attempt_count, max_attempts, " +
                    "last_error, not_before, runner_cost, priority, created_at, updated_at FROM jobs WHERE status = @leased",
                    ("@leased", ProcessingJobStatus.Leased))),
            cancellationToken).ConfigureAwait(false);

        var roots = WorkRoots(libraries, scope, _options.WeirHome);
        var protectedNames = running
            .Select(job => JobPayload.StringProperty(JobPayload.ParseObject(job.PayloadJson), "relative_media_path")?.Trim())
            .Where(relative => !string.IsNullOrEmpty(relative))
            .Select(relative => WeirTempFiles.RemuxTempNameFor(relative!))
            .ToList();
        var cutoff = _time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(_options.ProcessingWorkTempStaleSweepMinStaleAgeSeconds);

        var tally = new SweepTally();
        foreach (var root in roots)
        {
            Sweep(root, cutoff, protectedNames, tally);
        }

        var removed = tally.Removed;
        var label = scope == "tv" ? "TV" : "Movies";
        var detail = new WireObject()
            .Set("job_id", context.Id)
            .Set("media_scope", scope)
            .Set("files_removed", removed)
            .Set("files_not_removed", tally.NotRemoved)
            .Set("folders_unreadable", tally.UnreadableFolders)
            .Set("min_stale_age_seconds", _options.ProcessingWorkTempStaleSweepMinStaleAgeSeconds)
            .Set("result", "success");
        if (trigger is not null)
        {
            detail.Set("trigger", trigger);
        }

        var title = Title(label, tally);
        await LockedWrites.RunAsync(
            _store.Database,
            uow => SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                ActivityEventTypes.ProcessingWorkTempStaleSweepCompleted, "processing", title, WireJsonWriter.Dumps(detail, WireJsonFormat.Compact))),
            _logger,
            "work file sweep completed",
            cancellationToken).ConfigureAwait(false);
    }

    private static string Title(string label, SweepTally tally)
    {
        var problems = new List<string>();
        if (tally.NotRemoved > 0)
        {
            problems.Add($"{tally.NotRemoved.ToString(CultureInfo.InvariantCulture)} could not be removed");
        }

        if (tally.UnreadableFolders > 0)
        {
            problems.Add($"{tally.UnreadableFolders.ToString(CultureInfo.InvariantCulture)} work {(tally.UnreadableFolders == 1 ? "folder" : "folders")} could not be read");
        }

        var problemText = string.Join("; ", problems);
        if (tally.Removed == 0)
        {
            return $"Work files checked ({label}): {(problems.Count == 0 ? "nothing left behind" : problemText)}";
        }

        var cleared = $"Cleared {tally.Removed.ToString(CultureInfo.InvariantCulture)} left-behind work {(tally.Removed == 1 ? "file" : "files")} ({label})";
        return problems.Count == 0 ? cleared : $"{cleared}; {problemText}";
    }

    /// <summary>The scope's default work folder and the effective work folder of every library of that scope.</summary>
    private static HashSet<string> WorkRoots(IReadOnlyList<ProcessingLibraryFolderRow> libraries, string scope, string weirHome)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer)
        {
            scope == "tv" ? ProcessingLibraryFolders.DefaultTvWorkFolder(weirHome) : ProcessingLibraryFolders.DefaultMovieWorkFolder(weirHome),
        };
        foreach (var library in libraries.Where(l => ProcessingMediaScopes.Normalize(l.MediaType) == scope))
        {
            roots.Add(ProcessingLibraryFolders.EffectiveWorkFolder(library, weirHome));
        }

        return roots;
    }

    private void Sweep(string folder, DateTime cutoffUtc, IReadOnlyList<System.Text.RegularExpressions.Regex> protectedNames, SweepTally tally)
    {
        string root;
        try
        {
            root = ProcessingLibraryFolders.ExpandForFilesystem(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!_files.DirectoryExists(root))
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = _files.ListFiles(root, new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 });
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException)
        {
            // The folder went between the check and the listing: there is nothing in it to leave behind.
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The work file sweep could not read {Folder}: {Error}", root, exception.Message);
            tally.UnreadableFolders++;
            return;
        }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (!WeirTempFiles.IsRemuxTempName(name) || protectedNames.Any(pattern => pattern.IsMatch(name)))
            {
                continue;
            }

            try
            {
                if (_files.GetLastWriteTimeUtc(path) > cutoffUtc)
                {
                    continue;
                }

                _files.DeleteFile(path);
                tally.Removed++;
                _logger.LogInformation("The work file sweep removed left-behind temp output {Path}", path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Someone else removed it first, which is what the sweep was for.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The work file sweep could not remove {Path}: {Error}", path, exception.Message);
                tally.NotRemoved++;
            }
        }
    }

    private sealed class SweepTally
    {
        public int Removed { get; set; }

        public int NotRemoved { get; set; }

        public int UnreadableFolders { get; set; }
    }
}
