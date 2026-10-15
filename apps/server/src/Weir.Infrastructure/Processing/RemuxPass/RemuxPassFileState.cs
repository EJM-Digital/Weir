using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// The Files-row and processing-record writes a pass makes (status, failure, measured media facts, output collision and
/// file log), each inside the caller's unit of work.
/// </summary>
public static class RemuxPassFileState
{
    private sealed record FileRow(long Id, string StatusReason, long FailureAttempts);

    private static Task<FileRow?> FindAsync(UnitOfWork uow, long libraryId, string relativePath) =>
        uow.QuerySingleAsync(
            "SELECT id, status_reason, failure_attempts FROM files WHERE library_id = $library AND relative_path = $path LIMIT 1",
            reader => new FileRow(reader.GetInt64(0), SqliteValues.GetString(reader, 1), SqliteValues.GetInt64(reader, 2)),
            ("$library", libraryId),
            ("$path", relativePath));

    /// <summary>
    /// Whether a failed file is still waiting for an automatic retry: it is failed with a next attempt booked. A file that has been
    /// given up on (no attempt booked), finished, rejected or started again some other way is not.
    /// </summary>
    public static async Task<bool> RetryStillOwedAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var owed = await uow.QueryAsync(
            "SELECT 1 FROM files WHERE library_id = $library AND relative_path = $path AND status = $status AND next_retry_at IS NOT NULL LIMIT 1",
            reader => reader.GetInt64(0),
            ("$library", libraryId),
            ("$path", relativePath),
            ("$status", ProcessingFileStatuses.ProcessingFailed)).ConfigureAwait(false);
        return owed.Count > 0;
    }

    /// <summary>Moves a file Weir has already seen into a new state. False when there is no row.</summary>
    public static async Task<bool> MarkFileStatusAsync(UnitOfWork uow, long libraryId, string relativePath, string status, string reason, DateTimeOffset now, string? skipKind = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var row = await FindAsync(uow, libraryId, relativePath).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        var sets = new List<string> { "status = $status", "status_reason = $reason", "skip_kind = $skip_kind", "updated_at = CURRENT_TIMESTAMP" };
        if (status is ProcessingFileStatuses.Processing or ProcessingFileStatuses.Processed or ProcessingFileStatuses.ProcessingFailed)
        {
            sets.Add("last_attempt_at = $now");
        }

        if (status != ProcessingFileStatuses.BlockedUpstream)
        {
            sets.Add("blocked_by_connection = NULL");
        }

        if (status != ProcessingFileStatuses.OnHold)
        {
            sets.Add("hold_until = NULL");
        }

        await uow.ExecuteAsync(
            $"UPDATE files SET {string.Join(", ", sets)} WHERE id = $id",
            ("$status", status),
            ("$reason", reason),
            ("$skip_kind", skipKind),
            ("$now", TimestampColumns.Orm(now)),
            ("$id", row.Id)).ConfigureAwait(false);
        return true;
    }

    private const string UnfinishedStatuses = "$unprocessed, $processing, $held, $outside, $blocked, $failed, $cancelled";

    private static (string Name, object? Value)[] UnfinishedStatusParameters() =>
    [
        ("$unprocessed", ProcessingFileStatuses.Unprocessed),
        ("$processing", ProcessingFileStatuses.Processing),
        ("$held", ProcessingFileStatuses.OnHold),
        ("$outside", ProcessingFileStatuses.OutOfSchedule),
        ("$blocked", ProcessingFileStatuses.BlockedUpstream),
        ("$failed", ProcessingFileStatuses.ProcessingFailed),
        ("$cancelled", ProcessingFileStatuses.Cancelled),
    ];

    /// <summary>
    /// A file that was not there when its pass looked: held, with its reason, attempts and any hand-picked plan intact, until
    /// <paramref name="holdUntil"/>. It might be a share that dropped for a moment. Only a file without an outcome changes; one that
    /// is processed, passed through, rejected or skipped stays as history whether or not its original is still there. False when the
    /// file was already held for being gone, so a caller that says so does not say it twice.
    /// </summary>
    public static async Task<bool> HoldGoneAsync(UnitOfWork uow, long libraryId, string relativePath, DateTimeOffset holdUntil)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var alreadyHeld = await IsHeldAsGoneAsync(uow, libraryId, relativePath).ConfigureAwait(false);
        await uow.ExecuteAsync(
            $"UPDATE files SET status = $status, status_reason = $reason, hold_until = $until, updated_at = CURRENT_TIMESTAMP " +
            $"WHERE library_id = $library AND relative_path = $path AND status IN ({UnfinishedStatuses})",
            [
                ("$status", ProcessingFileStatuses.OnHold),
                ("$reason", GoneSourceText.HeldReason),
                ("$until", TimestampColumns.Orm(holdUntil)),
                ("$library", libraryId),
                ("$path", relativePath),
                .. UnfinishedStatusParameters(),
            ]).ConfigureAwait(false);
        return !alreadyHeld;
    }

    /// <summary>Whether the file is held for being gone, which means it has been recorded as no longer there.</summary>
    public static async Task<bool> IsHeldAsGoneAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var held = await uow.QueryAsync(
            "SELECT 1 FROM files WHERE library_id = $library AND relative_path = $path AND status = $status AND status_reason = $reason LIMIT 1",
            reader => reader.GetInt64(0),
            ("$library", libraryId),
            ("$path", relativePath),
            ("$status", ProcessingFileStatuses.OnHold),
            ("$reason", GoneSourceText.HeldReason)).ConfigureAwait(false);
        return held.Count > 0;
    }

    /// <summary>
    /// A file that stayed gone through the hold has nothing left to wait for. A row that carries the source Weir last cleaned is
    /// never deleted, because that is what stops the source being cleaned twice if the file comes back unchanged: it goes back to
    /// the outcome it had (<see cref="RestoreProcessed"/>), history again, and is no longer counted as waiting on anyone. Every
    /// other row is forgotten.
    /// </summary>
    public static async Task ForgetGoneAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        (string, object?)[] parameters = [("$library", libraryId), ("$path", relativePath), .. UnfinishedStatusParameters()];
        await uow.ExecuteAsync(
            $"UPDATE files SET {RestoreProcessed} WHERE library_id = $library AND relative_path = $path AND status IN ({UnfinishedStatuses}) AND {CarriesCleanedSource}",
            parameters).ConfigureAwait(false);
        await uow.ExecuteAsync(
            $"DELETE FROM files WHERE library_id = $library AND relative_path = $path AND status IN ({UnfinishedStatuses}) AND NOT ({CarriesCleanedSource})",
            parameters).ConfigureAwait(false);
    }

    /// <summary>Whether a <c>files</c> row records the source a pass last cleaned (<c>CleanedSources</c> reads these columns).</summary>
    public const string CarriesCleanedSource = "(processed_source_size IS NOT NULL OR processed_source_mtime_ns IS NOT NULL)";

    /// <summary>
    /// The assignments that put a row whose file is gone back to its earlier outcome: processed, with the cleaned source kept and every
    /// working field (attempts, reason, retry and look-again times) cleared.
    /// </summary>
    public static readonly string RestoreProcessed =
        $"status = '{ProcessingFileStatuses.Processed}', status_reason = '{GoneSourceText.CleanedReason}', failure_class = NULL, failure_attempts = 0, " +
        "next_retry_at = NULL, hold_until = NULL, blocked_by_connection = NULL, updated_at = CURRENT_TIMESTAMP";

    /// <summary>Keeps an on-hold file from being picked up again by a scan before <paramref name="lookAgainAt"/>, when its own next look is booked.</summary>
    public static Task HoldUntilAsync(UnitOfWork uow, long libraryId, string relativePath, DateTimeOffset lookAgainAt)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE files SET hold_until = $hold WHERE library_id = $library AND relative_path = $path",
            ("$hold", TimestampColumns.Orm(lookAgainAt)),
            ("$library", libraryId),
            ("$path", relativePath));
    }

    /// <summary>
    /// A rejection always upserts a Files row, whether or not a scan had already seen the file, so a hand-off rejected
    /// before any watched-folder scan still appears on the Files screen (#532). Sets status <c>rejected</c>, the reason and the failure class; clears
    /// <c>blocked_by_connection</c> and <c>hold_until</c> the same way <see cref="MarkFileStatusAsync"/> does for any status
    /// that is neither <c>blocked_upstream</c> nor <c>on_hold</c>. <paramref name="sourcePath"/>, when the source is still
    /// there to read, becomes the row's fingerprint (#785) — null when the reject route already removed it, since a
    /// removed file needs no fingerprint to protect.
    /// </summary>
    public static async Task UpsertRejectedAsync(UnitOfWork uow, long libraryId, string relativePath, string reason, string? failureClass, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (await FindAsync(uow, libraryId, relativePath).ConfigureAwait(false) is null)
        {
            await InsertFirstRecordAsync(uow, libraryId, relativePath, sourcePath).ConfigureAwait(false);
        }

        await uow.ExecuteAsync(
            "UPDATE files SET status = $status, status_reason = $reason, failure_class = $class, " +
            "blocked_by_connection = NULL, hold_until = NULL, updated_at = CURRENT_TIMESTAMP " +
            "WHERE library_id = $library AND relative_path = $path",
            ("$status", ProcessingFileStatuses.Rejected),
            ("$reason", WireStrings.Slice(reason, 10000)),
            ("$class", failureClass),
            ("$library", libraryId),
            ("$path", relativePath)).ConfigureAwait(false);
        await RecordFingerprintFromPathAsync(uow, libraryId, relativePath, sourcePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a file nothing has scanned yet, with the size it has on disk. The next scan reads a row whose size differs from the
    /// file as a file that changed, and starts it again, so a row with a made-up size would lose the outcome it was made to hold.
    /// </summary>
    private static async Task InsertFirstRecordAsync(UnitOfWork uow, long libraryId, string relativePath, string? sourcePath)
    {
        long size = 0;
        if (sourcePath is not null)
        {
            try
            {
                size = SourceFiles.Fingerprint(sourcePath).SizeBytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Nothing readable to measure; the scan records the size when it first sees the file.
            }
        }

        await uow.ExecuteAsync(
            "INSERT INTO files (library_id, relative_path, size_bytes) VALUES ($library, $path, $size)",
            ("$library", libraryId),
            ("$path", relativePath),
            ("$size", size)).ConfigureAwait(false);
    }

    private static string? WatchedSourceOrNull(ProcessingLibraryRecord library, string relativePath)
    {
        try
        {
            return RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, relativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The four failure fields cleared, after a success, a wait or a content rejection.</summary>
    public static Task ClearFailureFieldsAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE files SET failure_class = NULL, failure_attempts = 0, next_retry_at = NULL, hold_until = NULL, updated_at = CURRENT_TIMESTAMP " +
            "WHERE library_id = $library AND relative_path = $path",
            ("$library", libraryId),
            ("$path", relativePath));
    }

    /// <summary>
    /// Marks a file failed, classifies it and applies the retry policy. The decision's sentence becomes the
    /// reason on the record, so the screen says whether a retry is coming.
    /// </summary>
    public static async Task<RetryDecision> RecordFailureAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath, string failureClass, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(library);
        var row = await FindAsync(uow, library.Id, relativePath).ConfigureAwait(false);
        if (row is null)
        {
            await InsertFirstRecordAsync(uow, library.Id, relativePath, WatchedSourceOrNull(library, relativePath)).ConfigureAwait(false);
            row = (await FindAsync(uow, library.Id, relativePath).ConfigureAwait(false))!;
        }

        var value = WireStrings.Strip(failureClass).ToLowerInvariant();
        var attempts = row.FailureAttempts + 1;
        var decision = RetryPolicy.DecideRetry(library, value, attempts, now);
        await uow.ExecuteAsync(
            "UPDATE files SET status = $status, status_reason = $reason, failure_class = $class, failure_attempts = $attempts, " +
            "next_retry_at = $next, last_attempt_at = $now, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$status", ProcessingFileStatuses.ProcessingFailed),
            ("$reason", WireStrings.Slice(WireStrings.Strip($"{reason} {decision.Reason}"), 10000)),
            ("$class", value),
            ("$attempts", attempts),
            ("$next", decision.NextRetryAt is { } next ? TimestampColumns.Orm(next) : null),
            ("$now", TimestampColumns.Orm(now)),
            ("$id", row.Id)).ConfigureAwait(false);

        // #785: the file's identity the moment it became failed, so Activity's remove dialog can tell a later,
        // different release at the same path from the one that actually failed.
        await RecordCurrentFingerprintAsync(uow, library, relativePath).ConfigureAwait(false);

        return decision;
    }

    /// <summary>Resolves the source under the library's watched folder, then <see cref="RecordFingerprintFromPathAsync"/>.</summary>
    internal static Task RecordCurrentFingerprintAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath)
    {
        string? source;
        try
        {
            source = RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, relativePath);
        }
        catch (ArgumentException)
        {
            source = null;
        }

        return RecordFingerprintFromPathAsync(uow, library.Id, relativePath, source);
    }

    /// <summary>
    /// Stats <paramref name="sourcePath"/> now and records its size and modification time on the row, or clears them
    /// when it is null or cannot be read — never leaves a stale fingerprint from an earlier attempt standing in for
    /// this one (#785). <paramref name="sourcePath"/> is already resolved and contained by the caller.
    /// </summary>
    internal static async Task RecordFingerprintFromPathAsync(UnitOfWork uow, long libraryId, string relativePath, string? sourcePath)
    {
        SourceFingerprint? fingerprint = null;
        if (sourcePath is not null)
        {
            try
            {
                fingerprint = SourceFiles.Fingerprint(sourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Nothing readable to fingerprint; the columns are cleared below so a stale value never lingers.
            }
        }

        await uow.ExecuteAsync(
            "UPDATE files SET fingerprint_size_bytes = $size, fingerprint_mtime_ns = $mtime WHERE library_id = $library AND relative_path = $path",
            ("$size", fingerprint?.SizeBytes), ("$mtime", fingerprint?.ModifiedTimeNs), ("$library", libraryId), ("$path", relativePath)).ConfigureAwait(false);
    }

    /// <summary>
    /// The size and modification time of the source a successful pass cleaned (migration 0010), or null for both when the
    /// pass did not measure it. The watched-folder scan compares them with the file on disk for a library that keeps
    /// originals (<see cref="Weir.Core.Processing.ProcessedSourceRules"/>).
    /// </summary>
    public static Task RecordProcessedSourceAsync(UnitOfWork uow, long libraryId, string relativePath, long? sizeBytes, long? modifiedTimeNs) =>
        uow.ExecuteAsync(
            "UPDATE files SET processed_source_size = $size, processed_source_mtime_ns = $mtime WHERE library_id = $library AND relative_path = $path",
            ("$size", sizeBytes),
            ("$mtime", sizeBytes is null ? null : modifiedTimeNs),
            ("$library", libraryId),
            ("$path", relativePath));

    /// <summary>The row's reason, or null when there is no row.</summary>
    public static async Task<string?> StatusReasonAsync(UnitOfWork uow, long libraryId, string relativePath) =>
        (await FindAsync(uow, libraryId, relativePath).ConfigureAwait(false))?.StatusReason;

    /// <summary><c>row.status_reason = f"{row.status_reason} {sentence}".strip()[:10000]</c>.</summary>
    public static async Task AppendStatusReasonAsync(UnitOfWork uow, long libraryId, string relativePath, string sentence)
    {
        var row = await FindAsync(uow, libraryId, relativePath).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        await uow.ExecuteAsync(
            "UPDATE files SET status_reason = $reason, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$reason", WireStrings.Slice(WireStrings.Strip($"{row.StatusReason} {sentence}"), 10000)),
            ("$id", row.Id)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records measured media facts on the row matched by path within the pass's own library, not on every library that
    /// shares the path (#545); a value not measured leaves the column alone. A null
    /// <see cref="MeasuredMediaFacts.LibraryId"/> updates nothing rather than matching across libraries.
    /// </summary>
    public static Task RecordMeasuredMediaFactsAsync(UnitOfWork uow, MeasuredMediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.LibraryId is not { } libraryId)
        {
            return Task.CompletedTask;
        }

        var sets = new List<string>();
        var parameters = new List<(string, object?)> { ("$path", facts.RelativePath), ("$library", libraryId) };
        void Add(string column, object? value)
        {
            if (value is null)
            {
                return;
            }

            sets.Add($"{column} = ${column}");
            parameters.Add(($"${column}", value));
        }

        Add("video_width", facts.VideoWidth);
        Add("video_height", facts.VideoHeight);
        Add("video_codec", facts.VideoCodec is null ? null : WireStrings.Slice(facts.VideoCodec, 64));
        Add("audio_track_count", facts.AudioTrackCount);
        Add("subtitle_track_count", facts.SubtitleTrackCount);
        Add("duration_seconds", facts.DurationSeconds);
        Add("audio_codecs", facts.AudioCodecs is null
            ? null
            : WireStrings.Slice(string.Join(",", facts.AudioCodecs.Where(c => WireStrings.Strip(c).Length > 0).Select(c => WireStrings.Strip(c).ToLowerInvariant())), 1000));
        Add("video_bit_depth", facts.VideoBitDepth);
        if (sets.Count == 0)
        {
            return Task.CompletedTask;
        }

        sets.Add("updated_at = CURRENT_TIMESTAMP");
        return uow.ExecuteAsync($"UPDATE files SET {string.Join(", ", sets)} WHERE relative_path = $path AND library_id = $library", [.. parameters]);
    }

    /// <summary>
    /// Keeps the output-collision decision on the pass's own library's row for the path, not on every library that
    /// shares the path (#545). A null <paramref name="libraryId"/> updates nothing.
    /// </summary>
    public static Task RecordOutputCollisionAsync(UnitOfWork uow, string relativePath, CollisionDecision decision, long? libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(decision);
        if (libraryId is not { } library)
        {
            return Task.CompletedTask;
        }

        return uow.ExecuteAsync(
            "UPDATE files SET output_collision_policy = $policy, output_collision_action = $action, output_collision_reason = $reason, " +
            "updated_at = CURRENT_TIMESTAMP WHERE relative_path = $path AND library_id = $library",
            ("$policy", decision.Policy),
            ("$action", decision.Action),
            ("$reason", decision.Reason),
            ("$path", relativePath),
            ("$library", library));
    }

    /// <summary>Records one completed pass in the file log, bounded, beside the file and library it belongs to.</summary>
    public static async Task RecordFileLogAsync(UnitOfWork uow, string relativePath, string title, WireObject detail, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(detail);
        WireValue payload = detail;
        var text = WireJsonWriter.Dumps(payload, WireJsonFormat.Response);
        if (WireStrings.Length(text) > FileLogStore.MaxDetailChars)
        {
            payload = new WireObject()
                .Set("truncated", true)
                .Set("truncated_note", $"This record was longer than {FileLogStore.MaxDetailChars.ToString(CultureInfo.InvariantCulture)} characters and was shortened when it was saved.")
                .Set("outcome", detail.Get("outcome") ?? WireNull.Instance)
                .Set("detail_excerpt", WireStrings.Slice(text, FileLogStore.MaxDetailChars / 2));
            text = WireJsonWriter.Dumps(payload, WireJsonFormat.Response);
        }

        // The pass names its workflow, so the record goes to that workflow's file even when another workflow holds the same path.
        long? libraryId = detail.Get("library_id") is WireInteger wired ? (long)wired.Value : null;
        var file = await uow.QuerySingleAsync(
            "SELECT f.id, l.id, l.name FROM files f LEFT JOIN libraries l ON l.id = f.library_id " +
            "WHERE f.relative_path = $path AND ($library IS NULL OR f.library_id = $library) ORDER BY f.id LIMIT 1",
            reader => new object?[] { reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2) },
            ("$path", relativePath), ("$library", (object?)libraryId ?? DBNull.Value)).ConfigureAwait(false);
        var outcome = detail.Get("outcome") is { IsTruthy: true } value ? WireConvert.Str(value) : string.Empty;
        await uow.ExecuteAsync(
            "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) " +
            "VALUES ($file, $library, $path, $name, $outcome, $title, $detail, $recorded)",
            ("$file", file?[0]),
            ("$library", file?[1]),
            ("$path", relativePath),
            ("$name", file?[2] as string ?? string.Empty),
            ("$outcome", outcome),
            ("$title", title),
            ("$detail", text),
            ("$recorded", TimestampColumns.Orm(now))).ConfigureAwait(false);
    }
}
