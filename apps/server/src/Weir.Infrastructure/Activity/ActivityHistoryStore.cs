using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Activity;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Activity;

/// <summary>One page of events, newest first, and whether older matching events remain.</summary>
public sealed record ActivityPage(List<ActivityEventRow> Items, bool HasMore);

/// <summary>Where the next page starts: after the row <paramref name="Id"/>, created at <paramref name="CreatedAt"/> (null when that row is gone).</summary>
internal sealed record PageCursor(long Id, string? CreatedAt);

/// <summary>
/// Reading and removing Activity history: the event queries plus the processing-record statements that go
/// with a file's history. The count, date filter, paging order and file-history library fallback follow the
/// rules fixed in #543, each documented where it applies.
/// </summary>
public sealed class ActivityHistoryStore
{
    private const string DateOnlyFormat = "yyyy-MM-dd";

    /// <summary>How far a stored time's written date can be from its UTC date (a UTC offset is at most 14 hours).</summary>
    private static readonly TimeSpan StoredDateSlack = TimeSpan.FromDays(1);

    /// <summary>
    /// Keeps, of the events about one file, only the newest one that still describes it, so a list of what finished shows
    /// each file as it stands now. An event that finished nothing (a skip) describes no file, and a recorded failure stops
    /// describing a file once the file has been processed or passed through; an event is passed over when a newer one of the
    /// same kind still describes the file. Events about no file stay. The history itself is untouched.
    /// </summary>
    private static readonly string CurrentOnlyClause =
        "(coalesce(activity_events.relative_path, '') = '' OR (" +
        Describes("activity_events") +
        " AND NOT EXISTS (SELECT 1 FROM activity_events newer WHERE newer.event_type = activity_events.event_type " +
        "AND newer.relative_path = activity_events.relative_path AND newer.library_id IS activity_events.library_id " +
        "AND newer.id > activity_events.id AND " + Describes("newer") + ")))";

    /// <summary>
    /// Whether the event under <paramref name="alias"/> describes its file as it stands: it is not a skip, and not a failure of a
    /// file that has since been processed or passed through.
    /// </summary>
    private static string Describes(string alias) =>
        $"(coalesce({alias}.result, '') <> 'skipped' AND (coalesce({alias}.result, '') <> 'failed' OR NOT EXISTS (" +
        $"SELECT 1 FROM files WHERE files.relative_path = {alias}.relative_path AND files.library_id = {alias}.library_id " +
        $"AND files.status IN ('{ProcessingFileStatuses.Processed}', '{ProcessingFileStatuses.PassedThrough}'))))";

    internal const string Columns =
        "activity_events.id, activity_events.created_at, activity_events.event_type, activity_events.module, activity_events.title, " +
        "activity_events.detail, activity_events.\"trigger\", activity_events.result, activity_events.library_id, " +
        "activity_events.relative_path, activity_events.run_key";

    /// <summary>
    /// Newest first, at most <see cref="ActivityHistory.RecentMaxLimit"/>. Ordered by <c>(created_at DESC, id DESC)</c>,
    /// a total order that does not depend on which plan SQLite chooses for ties (#543). <paramref name="beforeId"/>
    /// pages by that same key: everything strictly after the cursor row in the order, not just a smaller id, so a row
    /// tied on <c>created_at</c> with the cursor is never skipped or repeated.
    /// </summary>
    public async Task<ActivityPage> ListRecentAsync(UnitOfWork uow, ActivityFilter filter, long limit, long? beforeId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        PageCursor? cursor = null;
        if (beforeId is { } before)
        {
            var createdAt = await uow.ScalarAsync(
                "SELECT activity_events.created_at FROM activity_events WHERE activity_events.id = @before_id",
                ("@before_id", before)).ConfigureAwait(false);
            cursor = new PageCursor(before, createdAt as string);
        }

        var pageSize = (int)Math.Clamp(limit, 1, ActivityHistory.RecentMaxLimit);
        var (sql, parameters) = PageQuery(filter, cursor, pageSize);
        var rows = await uow.QueryAsync(sql, ReadRow, parameters).ConfigureAwait(false);
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(pageSize);
        }

        return new ActivityPage(rows, hasMore);
    }

    /// <summary>
    /// One page, plus one row more than <paramref name="pageSize"/>: that extra row is how the page knows older
    /// events remain, without counting them.
    /// </summary>
    internal static (string Sql, (string Name, object? Value)[] Parameters) PageQuery(ActivityFilter filter, PageCursor? cursor, int pageSize)
    {
        var (where, parameters) = Where(filter);
        if (cursor is { CreatedAt: { } beforeCreatedAt })
        {
            where.Add(
                "(activity_events.created_at < @before_created_at OR " +
                "(activity_events.created_at = @before_created_at AND activity_events.id < @before_id))");
            parameters.Add(("@before_created_at", beforeCreatedAt));
            parameters.Add(("@before_id", cursor.Id));
        }
        else if (cursor is not null)
        {
            // The cursor row is gone (deleted since the page it came from was read): no created_at to
            // anchor on, so fall back to the best available guess, an id-only cursor.
            where.Add("activity_events.id < @before_id");
            parameters.Add(("@before_id", cursor.Id));
        }

        parameters.Add(("@limit", pageSize + 1));
        return (
            $"SELECT {Columns} FROM activity_events{WhereText(where)} ORDER BY activity_events.created_at DESC, activity_events.id DESC LIMIT @limit OFFSET 0",
            [.. parameters]);
    }

    /// <summary>Events for export: oldest first, up to <see cref="ActivityHistory.ExportMaxRows"/>.</summary>
    public Task<List<ActivityEventRow>> ListForExportAsync(UnitOfWork uow, ActivityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (where, parameters) = Where(filter);
        parameters.Add(("@limit", ActivityHistory.ExportMaxRows));
        return uow.QueryAsync(
            $"SELECT {Columns} FROM activity_events{WhereText(where)} ORDER BY activity_events.created_at, activity_events.id LIMIT @limit OFFSET 0",
            ReadRow,
            [.. parameters]);
    }

    /// <summary>
    /// Counts matching events. Always counts from <c>activity_events</c>, filtered or not: a bare
    /// <c>SELECT count(*)</c> without a FROM clause counts one whatever the table holds (#543).
    /// </summary>
    public Task<long> CountAsync(UnitOfWork uow, ActivityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (sql, parameters) = CountQuery(filter);
        return uow.CountAsync(sql, parameters);
    }

    internal static (string Sql, (string Name, object? Value)[] Parameters) CountQuery(ActivityFilter filter)
    {
        var (where, parameters) = Where(filter);
        return ($"SELECT count(*) AS count_1 FROM activity_events{WhereText(where)}", [.. parameters]);
    }

    /// <summary>When the oldest event was recorded, or null when there are none.</summary>
    public async Task<Timestamp?> OldestCreatedAtAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync("SELECT min(activity_events.created_at) AS min_1 FROM activity_events", reader => SqliteValues.GetDateTimeOrNull(reader, 0)).ConfigureAwait(false);
        return rows[0];
    }

    /// <summary>The newest event id: the cheap freshness probe of the live stream.</summary>
    public async Task<long?> LatestIdAsync(SqliteDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        object value;
        var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT max(activity_events.id) AS max_1 FROM activity_events";
                value = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }
        }

        return value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Counts one file's events and processing records.</summary>
    public async Task<FileHistoryCounts> CountFileHistoryAsync(UnitOfWork uow, long? libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (clause, parameters) = FileHistoryClause(libraryId, relativePath);
        var events = await uow.CountAsync($"SELECT count(*) AS count_1 FROM activity_events WHERE {clause}", parameters).ConfigureAwait(false);
        var (recordsClause, recordsParameters) = ProcessingRecordsClause(libraryId, relativePath);
        var records = await uow.CountAsync(
            "SELECT count(*) AS count_1 FROM (SELECT file_logs.id AS id, file_logs.file_id AS file_id, " +
            "file_logs.library_id AS library_id, file_logs.relative_path AS relative_path, " +
            "file_logs.library_name AS library_name, file_logs.outcome AS outcome, file_logs.title AS title, " +
            "file_logs.detail_json AS detail_json, file_logs.recorded_at AS recorded_at FROM file_logs " +
            $"WHERE {recordsClause}) AS anon_1",
            recordsParameters).ConfigureAwait(false);
        return new FileHistoryCounts(relativePath, events, records);
    }

    /// <summary>Deletes one file's events and processing records.</summary>
    public async Task<FileHistoryCounts> DeleteFileHistoryAsync(UnitOfWork uow, long? libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var (clause, parameters) = FileHistoryClause(libraryId, relativePath);
        var events = await uow.ExecuteAsync($"DELETE FROM activity_events WHERE {clause}", parameters).ConfigureAwait(false);
        var (recordsClause, recordsParameters) = ProcessingRecordsClause(libraryId, relativePath);
        var records = await uow.ExecuteAsync($"DELETE FROM file_logs WHERE {recordsClause}", recordsParameters).ConfigureAwait(false);
        return new FileHistoryCounts(relativePath, events, records);
    }

    /// <summary>The WHERE clauses and parameters for an Activity filter.</summary>
    internal static (List<string> Where, List<(string Name, object? Value)> Parameters) Where(ActivityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var where = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        if (!string.IsNullOrEmpty(filter.Trigger))
        {
            where.Add("activity_events.\"trigger\" = @trigger");
            parameters.Add(("@trigger", Core.Json.WireStrings.Strip(filter.Trigger).ToLowerInvariant()));
        }

        if (!string.IsNullOrEmpty(filter.Result))
        {
            where.Add("activity_events.result = @result");
            parameters.Add(("@result", Core.Json.WireStrings.Strip(filter.Result).ToLowerInvariant()));
        }

        if (filter.LibraryId is { } libraryId)
        {
            where.Add("activity_events.library_id = @library_id");
            parameters.Add(("@library_id", libraryId));
        }

        // System › Logs shows Weir's own events and Activity shows the files: "weir" keeps the events that are
        // not about one file, "files" keeps the ones that are. Anything else filters nothing.
        switch (Core.Json.WireStrings.Strip(filter.About ?? string.Empty).ToLowerInvariant())
        {
            case "weir":
                // Written exactly as the partial index ix_activity_events_about_weir_created_at is, so SQLite reads it (#714).
                where.Add("coalesce(activity_events.relative_path, '') = ''");
                break;
            case "files":
                where.Add("(activity_events.relative_path IS NOT NULL AND activity_events.relative_path <> '')");
                break;
        }

        if (filter.KnownFilesOnly)
        {
            // A live "just finished" list, not an audit trail: an event about a file Weir has forgotten (no more
            // `files` row for it) drops out here, while System › Logs, which never sets this, keeps showing it.
            where.Add(
                "(coalesce(activity_events.relative_path, '') = '' OR EXISTS (" +
                "SELECT 1 FROM files WHERE files.relative_path = activity_events.relative_path AND files.library_id = activity_events.library_id))");
        }

        if (filter.CurrentOnly)
        {
            where.Add(CurrentOnlyClause);
        }

        if (!string.IsNullOrEmpty(filter.File))
        {
            // A file's whole history: its path, or its name anywhere in a path.
            where.Add("lower(activity_events.relative_path) LIKE lower(@relative_path)");
            parameters.Add(("@relative_path", "%" + Core.Json.WireStrings.Strip(filter.File) + "%"));
        }

        if (!string.IsNullOrEmpty(filter.Module))
        {
            var module = Core.Json.WireStrings.Strip(filter.Module).ToLowerInvariant();
            if (module == "system")
            {
                var names = ActivityHistory.SystemModules.Select((_, index) => $"@module_{index}").ToArray();
                where.Add($"(activity_events.module NOT IN ({string.Join(", ", names)}))");
                parameters.AddRange(ActivityHistory.SystemModules.Select((name, index) => ($"@module_{index}", (object?)name)));
            }
            else
            {
                where.Add("activity_events.module = @module");
                parameters.Add(("@module", module));
            }
        }

        if (!string.IsNullOrEmpty(filter.EventType))
        {
            where.Add("activity_events.event_type = @event_type");
            parameters.Add(("@event_type", Core.Json.WireStrings.Strip(filter.EventType)));
        }

        if (!string.IsNullOrEmpty(filter.Search))
        {
            where.Add(
                "(lower(activity_events.title) LIKE lower(@search_title) OR lower(activity_events.detail) LIKE lower(@search_detail) " +
                "OR lower(activity_events.event_type) LIKE lower(@search_event_type) OR lower(activity_events.module) LIKE lower(@search_module))");
            var pattern = "%" + Core.Json.WireStrings.Strip(filter.Search) + "%";
            parameters.Add(("@search_title", pattern));
            parameters.Add(("@search_detail", pattern));
            parameters.Add(("@search_event_type", pattern));
            parameters.Add(("@search_module", pattern));
        }

        // Dates compare as instants, not raw text (#543): the query value is normalized to UTC (a naive value
        // is already the server's own clock; only an aware one needs converting) and both sides go through
        // julianday(), so a stored row without ".000000" (an exact second) still matches the same instant.
        // julianday() cannot use an index, so a day-granular text range on created_at comes first and narrows
        // the rows it has to check (#714).
        if (filter.DateFrom is { } from)
        {
            where.Add("activity_events.created_at >= @date_from_floor AND julianday(activity_events.created_at) >= julianday(@date_from)");
            parameters.Add(("@date_from_floor", DateFloor(from.AsUtc)));
            parameters.Add(("@date_from", Timestamp.FromUtc(from.AsUtc).ToSqlite()));
        }

        if (filter.DateTo is { } to)
        {
            where.Add("activity_events.created_at < @date_to_ceiling AND julianday(activity_events.created_at) <= julianday(@date_to)");
            parameters.Add(("@date_to_ceiling", DateCeiling(to.AsUtc)));
            parameters.Add(("@date_to", Timestamp.FromUtc(to.AsUtc).ToSqlite()));
        }

        return (where, parameters);
    }

    /// <summary>
    /// The lowest stored text an event at or after <paramref name="utc"/> can have. Stored times begin with their date,
    /// and one written with a UTC offset can carry the day before its UTC date, hence the day of slack.
    /// </summary>
    private static string DateFloor(DateTime utc) => utc.Date.Subtract(StoredDateSlack).ToString(DateOnlyFormat, CultureInfo.InvariantCulture);

    /// <summary>Text every event at or before <paramref name="utc"/> sorts below: the day after its date, plus the day of slack.</summary>
    private static string DateCeiling(DateTime utc) => utc.Date.AddDays(1).Add(StoredDateSlack).ToString(DateOnlyFormat, CultureInfo.InvariantCulture);

    internal static string WhereText(List<string> where) => where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);

    /// <summary>One file's events: with a library, events that never recorded one still belong to the file.</summary>
    private static (string Clause, (string Name, object? Value)[] Parameters) FileHistoryClause(long? libraryId, string relativePath)
    {
        var path = Core.Json.WireStrings.Strip(relativePath);
        return libraryId is { } id
            ? ("activity_events.relative_path = @relative_path AND (activity_events.library_id = @library_id OR activity_events.library_id IS NULL)",
                [("@relative_path", path), ("@library_id", id)])
            : ("activity_events.relative_path = @relative_path", [("@relative_path", path)]);
    }

    /// <summary>
    /// One file's processing records, with the same null-library fallback as <see cref="FileHistoryClause"/>,
    /// so removing a file's history also removes records written before its library was known (#543).
    /// </summary>
    private static (string Clause, (string Name, object? Value)[] Parameters) ProcessingRecordsClause(long? libraryId, string relativePath) =>
        libraryId is { } id
            ? ("file_logs.relative_path = @relative_path AND (file_logs.library_id = @library_id OR file_logs.library_id IS NULL)",
                [("@relative_path", relativePath), ("@library_id", id)])
            : ("file_logs.relative_path = @relative_path", [("@relative_path", relativePath)]);

    internal static ActivityEventRow ReadRow(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetDateTime(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetString(reader, 3),
        SqliteValues.GetString(reader, 4),
        SqliteValues.GetStringOrNull(reader, 5),
        SqliteValues.GetStringOrNull(reader, 6),
        SqliteValues.GetStringOrNull(reader, 7),
        reader.IsDBNull(8) ? null : SqliteValues.GetInt64(reader, 8),
        SqliteValues.GetStringOrNull(reader, 9),
        SqliteValues.GetStringOrNull(reader, 10));
}
