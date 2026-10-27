using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Text;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>What a requeue attempt did. <paramref name="Skipped"/> counts every file not queued, <paramref name="AlreadyCleaned"/> of them because Weir had already cleaned that exact source.</summary>
public sealed record RequeueResult(int Requeued, int Skipped, string Detail, int AlreadyCleaned = 0);

/// <summary>
/// Putting a failed file back to work by hand. Automatic, policy-governed retries are decided by the remux-pass
/// failure handling, not here.
/// </summary>
public sealed class RequeueStore(ProcessingJobStore jobStore, LibraryStore libraries)
{
    /// <summary>Job kind of a manual remux requeue, run by
    /// <see cref="RemuxPass.RemuxPassHandler"/>.</summary>
    public const string RemuxPassJobKind = "processing.file.remux_pass.v1";

    /// <summary>Whether <see cref="RequeueFileAsync"/> would queue this file rather than refuse it.</summary>
    public async Task<bool> CanRequeueAsync(UnitOfWork uow, ProcessingFileRecord row)
    {
        var check = await CheckAsync(uow, row).ConfigureAwait(false);
        return check.Refusal is null && check.Cleaned is null;
    }

    /// <summary>
    /// Manual reset: attempt count cleared, backoff ignored. A source Weir has already cleaned is left alone, with Activity
    /// saying so: once per source (<see cref="CleanedSources"/>).
    /// </summary>
    public async Task<RequeueResult> RequeueFileAsync(UnitOfWork uow, ProcessingFileRecord row)
    {
        var (found, refusal, cleaned) = await CheckAsync(uow, row).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new RequeueResult(0, 1, refusal);
        }

        // CheckAsync refuses a file whose workflow is gone, so a file that is not refused has one.
        var library = found!;

        if (cleaned is not null)
        {
            await CleanedSources.RecordSkipAsync(uow, library.Id, row.RelativePath, cleaned, "manual").ConfigureAwait(false);
            return new RequeueResult(0, 1, $"Weir already cleaned this file, so it skipped it. {cleaned.Reason}.", AlreadyCleaned: 1);
        }

        var payload = new WireObject()
            .Set("relative_media_path", row.RelativePath)
            .Set("media_scope", library.MediaType == "tv" ? "tv" : "movie")
            .Set("library_id", library.Id)
            .Set("trigger", "manual");
        // A requeued hand-off keeps its origin, so its outcome still reaches the manager (#531).
        if (await RemuxPass.HandoffOriginCarry.FindAsync(uow, library.Id, row.RelativePath).ConfigureAwait(false) is { } origin)
        {
            payload.Set("origin", origin);
        }

        // The commit at the end of this method releases uow's write lock for every later file in a bulk
        // requeue, but not for the first one: an API request arrives with the lock already taken whenever
        // RequireUserAsync refreshed user_sessions.last_seen_at on its way in, and EnqueueOrGetAsync below
        // opens its own connection and BEGIN IMMEDIATEs on it. Committing here makes the first call behave
        // exactly like the second and every one after it, rather than introducing a new ordering.
        await uow.CommitAsync().ConfigureAwait(false);
        await jobStore.EnqueueOrGetAsync(
            $"{RemuxPassJobKind}:requeue:{Guid.NewGuid():N}",
            RemuxPassJobKind,
            WireJsonWriter.Dumps(payload, WireJsonFormat.Compact),
            priority: (int)library.Priority).ConfigureAwait(false);

        const string detail = "Queued again by hand. It starts as soon as there is capacity for it.";
        await uow.ExecuteAsync(
            "UPDATE files SET status = @status, status_reason = @reason, failure_attempts = 0, failure_class = NULL, " +
            "next_retry_at = NULL, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            ("@status", ProcessingFileStatuses.Unprocessed), ("@reason", detail), ("@id", row.Id)).ConfigureAwait(false);

        // ProcessingJobStore writes through its own connection, outside uow's. Committing here (rather than
        // leaving it to the caller) releases uow's write lock before the next file's EnqueueOrGetAsync call
        // in a bulk requeue needs one — otherwise the two connections deadlock against each other's
        // uncommitted BEGIN IMMEDIATE / deferred-write locks until SQLite's busy timeout.
        await uow.CommitAsync().ConfigureAwait(false);
        return new RequeueResult(1, 0, detail);
    }

    /// <summary>Requeues many files by hand, reporting a total rather than stopping at the first problem.</summary>
    public async Task<RequeueResult> RequeueFilesAsync(UnitOfWork uow, IReadOnlyList<ProcessingFileRecord> rows)
    {
        var requeued = 0;
        var skipped = 0;
        var alreadyCleaned = 0;
        foreach (var row in rows)
        {
            var result = await RequeueFileAsync(uow, row).ConfigureAwait(false);
            requeued += result.Requeued;
            skipped += result.Skipped;
            alreadyCleaned += result.AlreadyCleaned;
        }

        return new RequeueResult(requeued, skipped, BulkDetail(requeued, skipped - alreadyCleaned, alreadyCleaned), alreadyCleaned);
    }

    /// <summary>The sentence a bulk requeue reports, counted in English: <paramref name="skipped"/> files lost their workflow or original, <paramref name="alreadyCleaned"/> were cleaned before.</summary>
    internal static string BulkDetail(int requeued, int skipped, int alreadyCleaned = 0)
    {
        var left = $"{Plural.Of(alreadyCleaned, "file")} {Plural.Noun(alreadyCleaned, "was", "were")} already cleaned, so Weir left {(alreadyCleaned == 1 ? "it" : "them")} alone.";
        return (requeued, skipped, alreadyCleaned) switch
        {
            (0, 0, > 0) => $"Nothing was queued: {left}",
            (_, _, > 0) => $"{BulkDetail(requeued, skipped)} {left}",
            _ => BulkDetail(requeued, skipped),
        };
    }

    private static string BulkDetail(int requeued, int skipped) => (requeued, skipped) switch
    {
        (> 0, 0) => $"Queued {Plural.Of(requeued, "file")} again. {(requeued == 1 ? "It starts" : "They start")} as capacity frees up.",
        (> 0, _) => $"Queued {Plural.Of(requeued, "file")} again. {skipped} could not be queued because {(skipped == 1 ? "its" : "their")} workflow or original is gone.",
        (0, > 0) => $"Nothing was queued: {Plural.Of(skipped, "file")} {Plural.Noun(skipped, "has", "have")} lost {(skipped == 1 ? "its" : "their")} workflow or original.",
        _ => "Nothing matched, so nothing was queued.",
    };

    internal const string OriginalGoneDetail =
        "The original of this file is no longer in the watched folder, so Weir has nothing to process again.";

    private const string WorkflowGoneDetail =
        "The workflow this file belonged to no longer exists, so there is nowhere to queue it.";

    /// <summary>
    /// The file's workflow and why it cannot be queued, if it cannot. A queued pass on a missing original only fails
    /// later with a confusing reason, so a file Weir is done with is refused while its original is gone, as a skip when Weir
    /// had already cleaned it.
    /// </summary>
    private async Task<(ProcessingLibraryRecord? Library, string? Refusal, CleanedEarlier? Cleaned)> CheckAsync(UnitOfWork uow, ProcessingFileRecord row)
    {
        var library = await libraries.GetAsync(uow, row.LibraryId).ConfigureAwait(false);
        if (library is null)
        {
            return (null, WorkflowGoneDetail, null);
        }

        var originalGone = ProcessingFileStatuses.Concluded.Contains(row.Status) && !OriginalIsInWatchedFolder(library, row.RelativePath);
        if (originalGone)
        {
            var cleanedBefore = await CleanedSources.FindForMissingOriginalAsync(uow, library.Id, row.RelativePath).ConfigureAwait(false);
            return (library, cleanedBefore is null ? OriginalGoneDetail : null, cleanedBefore);
        }

        return (library, null, await CleanedSources.FindAsync(uow, library.Id, library.WatchedFolder, row.RelativePath).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether the file's original is still where the pass would read it. A watched folder that is gone, or a path that
    /// would leave it, has no original to read either.
    /// </summary>
    private static bool OriginalIsInWatchedFolder(ProcessingLibraryRecord library, string relativePath)
    {
        try
        {
            return File.Exists(RemuxPass.RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, relativePath));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
