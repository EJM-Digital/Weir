using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// The look a pass books at a file it found gone: a pending remux pass or pass-through held back until the scan's grace for a
/// vanished file is over (<see cref="GoneSources.LookAgainAfter"/>). Until then it is the file's pass, so nothing else queues or
/// starts one. A file that comes back before the look is due has nothing left to wait for, so whatever sees it back starts the
/// look at once.
/// </summary>
internal static class GoneLooks
{
    /// <summary>The payload and result key counting how many times a pass has found its file gone.</summary>
    public const string Key = "gone_looks";

    private const string StartSql =
        "UPDATE jobs SET not_before = NULL, updated_at = CURRENT_TIMESTAMP " +
        "WHERE id = @id AND status = '" + ProcessingJobStatus.Pending + "' AND not_before IS NOT NULL";

    private const string ActivePassThroughSql =
        $"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE job_kind = '" + IntakeRules.PassThroughJobKind + "' AND status IN ('" +
        ProcessingJobStatus.Pending + "', '" + ProcessingJobStatus.Leased + "')";

    private const string WaitingPassThroughSql =
        $"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE job_kind = '" + IntakeRules.PassThroughJobKind + "' AND status = '" +
        ProcessingJobStatus.Pending + "' AND not_before IS NOT NULL";

    /// <summary>Whether <paramref name="pass"/> is a look booked at a gone file and still waiting for its time.</summary>
    public static bool IsWaiting(ProcessingJob pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Status != ProcessingJobStatus.Pending || pass.NotBefore is null)
        {
            return false;
        }

        try
        {
            return WireJsonParser.Parse(pass.PayloadJson ?? string.Empty) is WireObject payload &&
                   payload.Get(Key) is WireInteger { Value.Sign: > 0 };
        }
        catch (WireJsonDecodeException)
        {
            return false;
        }
    }

    /// <summary>The pass-through look waiting at this file, read inside the caller's write transaction.</summary>
    public static ProcessingJob? WaitingPassThrough(SqliteConnection connection, SqliteTransaction transaction, long libraryId, string relativePath) =>
        ProcessingJobStore.Query(connection, transaction, WaitingPassThroughSql + " AND json_extract(payload_json, '$.relative_media_path') = @path ORDER BY id", ("@path", relativePath))
            .FirstOrDefault(job => IsWaiting(job) && Names(job, libraryId, relativePath));

    /// <summary>Starts the look now if <paramref name="pass"/> is a waiting look, inside the caller's write transaction. True when it did.</summary>
    public static bool StartIfWaiting(SqliteConnection connection, SqliteTransaction transaction, ProcessingJob pass) =>
        IsWaiting(pass) && ProcessingJobStore.Execute(connection, transaction, StartSql, ("@id", pass.Id)) > 0;

    /// <summary>
    /// Cancels every look still waiting at this file except <paramref name="kept"/>, the pass that has taken the file over, inside the
    /// caller's write transaction. The send a look was booked for is closed by that takeover, so the look would only come round later to
    /// a file already dealt with. Returns what it cancelled, so the caller can announce the change.
    /// </summary>
    public static IReadOnlyList<ProcessingJob> RetireOthers(
        SqliteConnection connection, SqliteTransaction transaction, ProcessingJob kept, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var looks = ProcessingJobStore.Query(connection, transaction, ActiveRemuxPasses.ForPathSql, ("@path", relativePath))
            .Concat(ProcessingJobStore.Query(connection, transaction, WaitingPassThroughSql + " AND json_extract(payload_json, '$.relative_media_path') = @path ORDER BY id", ("@path", relativePath)))
            .Where(job => job.Id != kept.Id && IsWaiting(job) && Names(job, libraryId, relativePath))
            .ToList();
        foreach (var look in looks)
        {
            ProcessingJobStore.Execute(
                connection,
                transaction,
                "UPDATE jobs SET dedupe_key = @dedupe, status = @status, last_error = @error, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                ("@dedupe", JobQueueRules.TombstoneCancelledDedupeKey(look.DedupeKey, look.Id)),
                ("@status", ProcessingJobStatus.Cancelled),
                ("@error", JobQueueRules.ReplacedByResendError),
                ("@id", look.Id));
        }

        return looks;
    }

    /// <summary>The path of every file in the library with a pass-through pending or leased: waiting for its look, started or running.</summary>
    public static async Task<HashSet<string>> ActivePassThroughPathsAsync(UnitOfWork uow, long libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var looks = await uow.QueryAsync(ActivePassThroughSql, ProcessingJobStore.ReadJob).ConfigureAwait(false);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var look in looks)
        {
            var payload = FollowUpJobPayload.Parse(look.PayloadJson);
            if (FollowUpJobPayload.LibraryId(payload) == libraryId)
            {
                paths.Add(FollowUpJobPayload.RelativeMediaPath(payload));
            }
        }

        return paths;
    }

    /// <summary>
    /// Starts the waiting look at this file, remux or pass-through, if it has one, inside the caller's unit of work. True when one was
    /// started; <paramref name="started"/> is told the kind of each, so the caller can wake the slots that run it.
    /// </summary>
    public static async Task<bool> StartForFileAsync(UnitOfWork uow, string relativePath, string mediaScope, long libraryId, Action<string> started)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(started);
        var passThrough = await uow.QueryAsync(
            WaitingPassThroughSql + " AND json_extract(payload_json, '$.relative_media_path') = @path ORDER BY id",
            ProcessingJobStore.ReadJob,
            ("@path", relativePath)).ConfigureAwait(false);
        var looks = (await ActiveRemuxPasses.ForRelativePathAsync(uow, relativePath, mediaScope, libraryId).ConfigureAwait(false))
            .Concat(passThrough.Where(job => Names(job, libraryId, relativePath)));
        var any = false;
        foreach (var look in looks)
        {
            if (IsWaiting(look) && await uow.ExecuteAsync(StartSql, ("@id", look.Id)).ConfigureAwait(false) > 0)
            {
                any = true;
                started(look.JobKind);
            }
        }

        return any;
    }

    internal static bool Names(ProcessingJob job, long libraryId, string relativePath)
    {
        var payload = FollowUpJobPayload.Parse(job.PayloadJson);
        return FollowUpJobPayload.LibraryId(payload) == libraryId && FollowUpJobPayload.RelativeMediaPath(payload) == relativePath;
    }
}
