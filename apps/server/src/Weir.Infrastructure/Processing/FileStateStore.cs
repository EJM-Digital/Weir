using Microsoft.Data.Sqlite;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>What a watched-folder scan writes for one file it looked at (<see cref="FileStateStore.RecordScannedStateAsync"/>).</summary>
/// <param name="RelativePath">The file, relative to the watched folder.</param>
/// <param name="RowId">The file's row as the scan read it, or null when Weir had no row for it.</param>
/// <param name="ExpectedStatus">The status the scan read on that row; the write applies only while the row still has it.</param>
/// <param name="ResetStatus">For a source whose size changed: the status it starts again from, with its failures cleared. Null leaves them.</param>
/// <param name="Verdict">The file's new state, or null to leave the state as it is.</param>
/// <param name="SizeBytes">The size the scan saw.</param>
/// <param name="SizeChangedAt">When that size was first seen, or null to leave the stored time alone.</param>
public sealed record ScannedFileWrite(
    string RelativePath,
    long? RowId,
    string? ExpectedStatus,
    string? ResetStatus,
    FileStateVerdict? Verdict,
    long SizeBytes,
    DateTimeOffset? SizeChangedAt)
{
    /// <summary>Whether the write adds the file or moves it to another status: what Processing sorts its lists by.</summary>
    public bool ChangesStatus => RowId is null || (Verdict?.Status ?? ResetStatus ?? ExpectedStatus) != ExpectedStatus;
}

/// <summary>SQLite access for <c>files</c>: read, list and forget, plus the upsert and mark-status writes the
/// watched-folder scan performs.</summary>
public sealed partial class FileStateStore
{
    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once a delete or a cancel commits, so every open screen drops the file at once; none says nothing.</param>
    public FileStateStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    private const string Columns =
        "id, library_id, relative_path, status, status_reason, blocked_by_connection, size_bytes, video_width, video_height, " +
        "video_codec, audio_track_count, subtitle_track_count, duration_seconds, audio_codecs, video_bit_depth, size_changed_at, " +
        "hold_until, failure_class, failure_attempts, next_retry_at, output_collision_policy, output_collision_action, " +
        "output_collision_reason, hardware_method, hardware_fell_back_to_software, hardware_reason, last_seen_at, last_attempt_at, " +
        "created_at, updated_at, processed_source_size, processed_source_mtime_ns, fingerprint_size_bytes, fingerprint_mtime_ns, skip_kind";
    private const string InsertSql =
        "INSERT INTO files (library_id, relative_path, status, status_reason, blocked_by_connection, hold_until, " +
        "size_bytes, size_changed_at, last_seen_at, last_attempt_at, skip_kind) VALUES (@lib, @path, @status, @reason, @blocked, @hold, " +
        "@size, @size_changed, @seen, @attempt, @skip_kind)";

    public Task<ProcessingFileRecord?> GetAsync(UnitOfWork uow, long id) =>
        uow.QuerySingleAsync($"SELECT {Columns} FROM files WHERE id = @id", Read, ("@id", id));

    public Task<ProcessingFileRecord?> FindAsync(UnitOfWork uow, long libraryId, string relativePath) =>
        uow.QuerySingleAsync(
            $"SELECT {Columns} FROM files WHERE library_id = @lib AND relative_path = @path",
            Read, ("@lib", libraryId), ("@path", relativePath));

    /// <summary>Every rejected file, oldest first, in one workflow or in all of them. Never paged, because a bulk
    /// action on "all" must not silently stop short.</summary>
    public Task<List<ProcessingFileRecord>> ListRejectedAsync(UnitOfWork uow, long? libraryId)
    {
        var inLibrary = libraryId is null ? string.Empty : " AND library_id = @library_id";
        var sql = $"SELECT {Columns} FROM files WHERE status = @status{inLibrary} ORDER BY id";
        return libraryId is { } id
            ? uow.QueryAsync(sql, Read, ("@status", ProcessingFileStatuses.Rejected), ("@library_id", id))
            : uow.QueryAsync(sql, Read, ("@status", ProcessingFileStatuses.Rejected));
    }

    /// <summary>A count per known status, zero-filled.</summary>
    public async Task<Dictionary<string, long>> StatusCountsAsync(UnitOfWork uow, long? libraryId)
    {
        var counts = ProcessingFileStatuses.All.ToDictionary(s => s, _ => 0L, StringComparer.Ordinal);
        var sql = "SELECT status, COUNT(*) FROM files" + (libraryId is not null ? " WHERE library_id = @lib" : string.Empty) + " GROUP BY status";
        var rows = libraryId is { } id
            ? await uow.QueryAsync(sql, reader => (Status: reader.GetString(0), Count: reader.GetInt64(1)), ("@lib", id)).ConfigureAwait(false)
            : await uow.QueryAsync(sql, reader => (Status: reader.GetString(0), Count: reader.GetInt64(1))).ConfigureAwait(false);
        foreach (var (status, count) in rows)
        {
            if (counts.ContainsKey(status))
            {
                counts[status] = count;
            }
        }

        return counts;
    }

    /// <summary>Removes Weir's record; never touches the file on disk.</summary>
    public async Task ForgetAsync(UnitOfWork uow, long id)
    {
        if (await uow.ExecuteAsync("DELETE FROM files WHERE id = @id", ("@id", id)).ConfigureAwait(false) > 0)
        {
            AnnounceOnCommit(uow);
        }
    }

    /// <summary>
    /// A queued pass for this file was cancelled before Weir started on it (#643): the file reads cancelled, with
    /// <paramref name="reason"/>, and no retry is owed. Only a file still waiting, held or failed changes. One that is being
    /// processed, or already has an outcome, keeps it.
    /// </summary>
    public async Task MarkCancelledAsync(UnitOfWork uow, long libraryId, string relativePath, string reason)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var changed = await uow.ExecuteAsync(
            "UPDATE files SET status = @cancelled, status_reason = @reason, next_retry_at = NULL, hold_until = NULL, " +
            "blocked_by_connection = NULL, updated_at = CURRENT_TIMESTAMP " +
            "WHERE library_id = @library AND relative_path = @path AND status IN (@unprocessed, @held, @outside, @blocked, @failed)",
            ("@cancelled", ProcessingFileStatuses.Cancelled),
            ("@reason", reason),
            ("@library", libraryId),
            ("@path", relativePath),
            ("@unprocessed", ProcessingFileStatuses.Unprocessed),
            ("@held", ProcessingFileStatuses.OnHold),
            ("@outside", ProcessingFileStatuses.OutOfSchedule),
            ("@blocked", ProcessingFileStatuses.BlockedUpstream),
            ("@failed", ProcessingFileStatuses.ProcessingFailed)).ConfigureAwait(false);
        if (changed > 0)
        {
            AnnounceOnCommit(uow);
        }
    }

    /// <summary>
    /// Says, once <paramref name="uow"/> commits, that a file left the lists without a job moving, so Activity and the Dashboard
    /// drop it at once, on the topic that refreshes the file lists.
    /// </summary>
    private void AnnounceOnCommit(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.LibraryScan);

    /// <summary>Every row of the library, for a scan that decides about all of its files from one read.</summary>
    public Task<List<ProcessingFileRecord>> ListForLibraryAsync(UnitOfWork uow, long libraryId) =>
        uow.QueryAsync($"SELECT {Columns} FROM files WHERE library_id = @lib", Read, ("@lib", libraryId));

    /// <summary>
    /// Upserts one file's state. Safe to call on every scan. <paramref name="sizeBytes"/>
    /// and <paramref name="sizeChangedAt"/> of <see langword="null"/> mean "not supplied" (leave the
    /// stored value alone), distinct from a genuine zero.
    /// </summary>
    public async Task<long> RecordFileStateAsync(
        UnitOfWork uow,
        long libraryId,
        string relativePath,
        FileStateVerdict verdict,
        long? sizeBytes,
        DateTimeOffset? sizeChangedAt,
        DateTimeOffset seenAt,
        bool isAttempt = false)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(verdict);
        var existingId = await uow.ScalarAsync(
            "SELECT id FROM files WHERE library_id = @lib AND relative_path = @path",
            ("@lib", libraryId), ("@path", relativePath)).ConfigureAwait(false);
        var state = new StateWrite(verdict, sizeBytes, sizeChangedAt, Timestamp.FromDateTimeOffset(seenAt), isAttempt);
        if (existingId is null or DBNull)
        {
            await uow.ExecuteAsync(InsertSql, [("@lib", libraryId), ("@path", relativePath), .. state.InsertParameters()]).ConfigureAwait(false);
            return Convert.ToInt64(await uow.ScalarAsync(
                "SELECT id FROM files WHERE library_id = @lib AND relative_path = @path",
                ("@lib", libraryId), ("@path", relativePath)).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        var id = Convert.ToInt64(existingId, System.Globalization.CultureInfo.InvariantCulture);
        var (sets, parameters) = state.Assignments();
        await uow.ExecuteAsync($"UPDATE files SET {string.Join(", ", sets)} WHERE id = @id", [.. parameters, ("@id", id)]).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// A watched-folder scan's write for one file, applied only while the row still holds the status the scan read (#708): a
    /// hand-off, a pass or a person that changed the row since then wins, and nothing it wrote is overwritten. A file the scan
    /// found no row for is inserted unless something else recorded it first. Returns whether the write was applied.
    /// </summary>
    public async Task<bool> RecordScannedStateAsync(UnitOfWork uow, long libraryId, ScannedFileWrite write, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(write);
        var applied = await WriteScannedStateAsync(uow, libraryId, write, seenAt).ConfigureAwait(false);
        if (applied && write.ChangesStatus)
        {
            // A scan records no Activity row for a file it finds or holds, so nothing else would tell an open Processing page.
            ActivityNotifications.TrackFileListChange(uow);
        }

        return applied;
    }

    private static async Task<bool> WriteScannedStateAsync(UnitOfWork uow, long libraryId, ScannedFileWrite write, DateTimeOffset seenAt)
    {
        var seen = Timestamp.FromDateTimeOffset(seenAt);
        if (write.RowId is not { } id)
        {
            var verdict = write.Verdict ?? throw new ArgumentException("A file with no row needs a state to record.", nameof(write));
            var insert = new StateWrite(verdict, write.SizeBytes, write.SizeChangedAt, seen, IsAttempt: false);
            return await uow.ExecuteAsync(
                InsertSql + " ON CONFLICT (library_id, relative_path) DO NOTHING",
                [("@lib", libraryId), ("@path", write.RelativePath), .. insert.InsertParameters()]).ConfigureAwait(false) > 0;
        }

        var (sets, parameters) = write.Verdict is { } next
            ? new StateWrite(next, write.SizeBytes, write.SizeChangedAt, seen, IsAttempt: false).Assignments()
            : (["status = @status", "last_seen_at = @seen"], [("@status", write.ResetStatus ?? write.ExpectedStatus), ("@seen", SqliteValues.ToSqlite(seen))]);
        if (write.ResetStatus is not null)
        {
            // A changed source is a new processing opportunity: no failure count or retry carries over from the old bytes.
            sets.AddRange(["failure_class = NULL", "failure_attempts = 0", "next_retry_at = NULL"]);
        }

        return await uow.ExecuteAsync(
            $"UPDATE files SET {string.Join(", ", sets)} WHERE id = @id AND status = @expected",
            [.. parameters, ("@id", id), ("@expected", write.ExpectedStatus)]).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Records that a scan saw these rows' files on disk at <paramref name="seenAt"/>, changing nothing else. The vanished-file
    /// sweep (#645) reads this to tell a file that is gone from one a scan simply left alone.
    /// </summary>
    public async Task TouchLastSeenAsync(UnitOfWork uow, IReadOnlyCollection<long> fileIds, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return;
        }

        await uow.ExecuteAsync(
            "UPDATE files SET last_seen_at = @seen WHERE id IN (SELECT value FROM json_each(@ids))",
            ("@seen", SqliteValues.ToSqlite(Timestamp.FromDateTimeOffset(seenAt))),
            ("@ids", "[" + string.Join(",", fileIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]")).ConfigureAwait(false);
    }

    /// <summary>Moves a file Weir has already seen into a new state. Does nothing when the row does not
    /// exist.</summary>
    public async Task MarkFileStatusAsync(UnitOfWork uow, long libraryId, string relativePath, string status, string reason, string? skipKind = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var sets = new List<string> { "status = @status", "status_reason = @reason", "skip_kind = @skip_kind", "updated_at = CURRENT_TIMESTAMP" };
        var parameters = new List<(string, object?)>
        {
            ("@status", status), ("@reason", reason), ("@skip_kind", skipKind), ("@lib", libraryId), ("@path", relativePath),
        };
        if (status is ProcessingFileStatuses.Processing or ProcessingFileStatuses.Processed or ProcessingFileStatuses.ProcessingFailed)
        {
            sets.Add("last_attempt_at = CURRENT_TIMESTAMP");
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
            $"UPDATE files SET {string.Join(", ", sets)} WHERE library_id = @lib AND relative_path = @path",
            [.. parameters]).ConfigureAwait(false);
    }

    /// <summary>Every library's name, keyed by id (for the Files list's <c>library_name</c> field).</summary>
    public async Task<Dictionary<long, string>> LibraryNamesAsync(UnitOfWork uow)
    {
        var rows = await uow.QueryAsync("SELECT id, name FROM libraries", reader => (Id: reader.GetInt64(0), Name: reader.GetString(1))).ConfigureAwait(false);
        return rows.ToDictionary(x => x.Id, x => x.Name);
    }

    private static ProcessingFileRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        LibraryId = reader.GetInt64(1),
        RelativePath = SqliteValues.GetString(reader, 2),
        Status = SqliteValues.GetString(reader, 3),
        StatusReason = SqliteValues.GetString(reader, 4),
        BlockedByConnection = SqliteValues.GetStringOrNull(reader, 5),
        SizeBytes = SqliteValues.GetInt64(reader, 6),
        VideoWidth = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        VideoHeight = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        VideoCodec = SqliteValues.GetStringOrNull(reader, 9),
        AudioTrackCount = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        SubtitleTrackCount = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        DurationSeconds = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        AudioCodecs = SqliteValues.GetStringOrNull(reader, 13),
        VideoBitDepth = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        SizeChangedAt = SqliteValues.GetDateTimeOrNull(reader, 15),
        HoldUntil = SqliteValues.GetDateTimeOrNull(reader, 16),
        FailureClass = SqliteValues.GetStringOrNull(reader, 17),
        FailureAttempts = SqliteValues.GetInt64(reader, 18),
        NextRetryAt = SqliteValues.GetDateTimeOrNull(reader, 19),
        OutputCollisionPolicy = SqliteValues.GetStringOrNull(reader, 20),
        OutputCollisionAction = SqliteValues.GetStringOrNull(reader, 21),
        OutputCollisionReason = SqliteValues.GetStringOrNull(reader, 22),
        HardwareMethod = SqliteValues.GetStringOrNull(reader, 23),
        HardwareFellBackToSoftware = SqliteValues.GetBool(reader, 24),
        HardwareReason = SqliteValues.GetStringOrNull(reader, 25),
        LastSeenAt = SqliteValues.GetDateTimeOrNull(reader, 26),
        LastAttemptAt = SqliteValues.GetDateTimeOrNull(reader, 27),
        CreatedAt = SqliteValues.GetDateTime(reader, 28),
        UpdatedAt = SqliteValues.GetDateTime(reader, 29),
        ProcessedSourceSize = reader.IsDBNull(30) ? null : reader.GetInt64(30),
        ProcessedSourceMtimeNs = reader.IsDBNull(31) ? null : reader.GetInt64(31),
        FingerprintSizeBytes = reader.IsDBNull(32) ? null : reader.GetInt64(32),
        FingerprintMtimeNs = reader.IsDBNull(33) ? null : reader.GetInt64(33),
        SkipKind = SqliteValues.GetStringOrNull(reader, 34),
    };

    /// <summary>One state write's values, shared by the upsert and the scan's conditional write.</summary>
    private sealed record StateWrite(FileStateVerdict Verdict, long? SizeBytes, DateTimeOffset? SizeChangedAt, Timestamp Seen, bool IsAttempt)
    {
        private object HoldUntil => SqliteValues.ToSqlite(Verdict.HoldUntil is { } hold ? Timestamp.FromDateTimeOffset(hold) : (Timestamp?)null);

        private object ChangedAt => SqliteValues.ToSqlite(SizeChangedAt is { } changed ? Timestamp.FromDateTimeOffset(changed) : (Timestamp?)null);

        public (string Name, object? Value)[] InsertParameters() =>
        [
            ("@status", Verdict.Status),
            ("@reason", Verdict.Reason),
            ("@blocked", Verdict.BlockedByConnection),
            ("@hold", HoldUntil),
            ("@size", SizeBytes ?? 0),
            ("@size_changed", ChangedAt),
            ("@seen", SqliteValues.ToSqlite(Seen)),
            ("@attempt", IsAttempt ? SqliteValues.ToSqlite(Seen) : null),
            ("@skip_kind", Verdict.SkipKind),
        ];

        /// <summary>The <c>SET</c> list for an existing row: a size or change time of null leaves the stored value alone.</summary>
        public (List<string> Sets, List<(string Name, object? Value)> Parameters) Assignments()
        {
            var sets = new List<string>
            {
                "status = @status", "status_reason = @reason", "blocked_by_connection = @blocked", "hold_until = @hold",
                "last_seen_at = @seen", "updated_at = CURRENT_TIMESTAMP", "skip_kind = @skip_kind",
            };
            var parameters = new List<(string Name, object? Value)>
            {
                ("@status", Verdict.Status),
                ("@reason", Verdict.Reason),
                ("@blocked", Verdict.BlockedByConnection),
                ("@hold", HoldUntil),
                ("@seen", SqliteValues.ToSqlite(Seen)),
                ("@skip_kind", Verdict.SkipKind),
            };
            if (SizeBytes is { } size)
            {
                sets.Add("size_bytes = @size");
                parameters.Add(("@size", size));
            }

            if (SizeChangedAt is not null)
            {
                sets.Add("size_changed_at = @size_changed");
                parameters.Add(("@size_changed", ChangedAt));
            }

            if (IsAttempt)
            {
                sets.Add("last_attempt_at = @attempt");
                parameters.Add(("@attempt", SqliteValues.ToSqlite(Seen)));
            }

            return (sets, parameters);
        }
    }
}
