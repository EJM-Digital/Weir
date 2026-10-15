using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>SQLite access for <c>file_logs</c>.</summary>
public sealed class FileLogStore
{
    public const int MaxDetailChars = 200_000;

    private const string Columns = "id, file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at";

    /// <summary>
    /// Whether a history row still has its file: a <c>files</c> row exists in the row's workflow, or anywhere once the
    /// workflow is gone (its history rows keep the path and lose the workflow).
    /// </summary>
    private const string FileStillKnown =
        "EXISTS (SELECT 1 FROM files f WHERE f.relative_path = file_logs.relative_path " +
        "AND (file_logs.library_id IS NULL OR f.library_id = file_logs.library_id))";

    /// <summary>
    /// The rows that belong to one file: those written for its workflow and path, and those written before the workflow was known
    /// (no workflow) only while no other workflow holds the path, since otherwise there is no telling whose they are.
    /// </summary>
    private const string BelongsToFile =
        "relative_path = @path AND (library_id = @library OR (library_id IS NULL AND NOT EXISTS (" +
        "SELECT 1 FROM files other WHERE other.relative_path = file_logs.relative_path AND other.library_id <> @library)))";

    /// <summary>Every retained pass over this file of this workflow, newest first.</summary>
    public Task<List<ProcessingFileLogRecord>> LogsForFileAsync(UnitOfWork uow, long libraryId, string relativePath, int limit) =>
        uow.QueryAsync(
            $"SELECT {Columns} FROM file_logs WHERE {BelongsToFile} ORDER BY recorded_at DESC, id DESC LIMIT {Math.Max(1, Math.Min(limit, 500))}",
            Read, ("@path", relativePath), ("@library", libraryId));

    /// <summary>How many passes over this file of this workflow are retained, however many a read returns.</summary>
    public async Task<long> CountForFileAsync(UnitOfWork uow, long libraryId, string relativePath) =>
        await uow.CountAsync($"SELECT count(*) FROM file_logs WHERE {BelongsToFile}", ("@path", relativePath), ("@library", libraryId)).ConfigureAwait(false);

    /// <summary>Parse a stored detail payload as a <see cref="WireObject"/>, never raising.</summary>
    public WireObject ParseDetail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new WireObject();
        }

        try
        {
            return WireJsonParser.Parse(raw) switch
            {
                WireObject dict => dict,
                WireValue other => new WireObject().Set("detail", other),
            };
        }
        catch (WireJsonDecodeException)
        {
            return new WireObject().Set("unparsed_detail", raw);
        }
    }

    /// <summary>
    /// A plain-text rendering for attaching to a bug report. <paramref name="retained"/> is how many passes are kept in all: when
    /// that is more than <paramref name="rows"/> holds, the text says it shows only the newest.
    /// </summary>
    public string RenderLogText(IReadOnlyList<ProcessingFileLogRecord> rows, long retained)
    {
        if (rows.Count == 0)
        {
            return "Weir has no retained processing records for this file.\n";
        }

        var lines = new List<string>();
        var first = rows[0];
        lines.Add($"Weir — processing record for {first.RelativePath}");
        if (first.LibraryName.Length > 0)
        {
            lines.Add($"Workflow: {first.LibraryName}");
        }

        lines.Add(retained > rows.Count
            ? $"Showing the newest {rows.Count.ToString("N0", CultureInfo.InvariantCulture)} of {retained.ToString("N0", CultureInfo.InvariantCulture)} entries"
            : $"Records retained: {rows.Count} (newest first)");
        lines.Add(string.Empty);

        foreach (var row in rows)
        {
            lines.Add(new string('=', 78));
            lines.Add($"{row.RecordedAt.ToWireText()}  —  {(row.Outcome.Length > 0 ? row.Outcome : "no outcome recorded")}");
            if (row.Title.Length > 0)
            {
                lines.Add(row.Title);
            }

            lines.Add(new string('-', 78));
            var detail = ParseDetail(row.DetailJson);
            foreach (var key in detail.Keys.Order(StringComparer.Ordinal))
            {
                var value = detail[key];
                var rendered = value is WireObject or WireArray ? WireJsonWriter.Dumps(value, WireJsonFormat.Compact) : PlainValue(value);
                lines.Add($"{key}: {rendered}");
            }

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string PlainValue(WireValue value) => value switch
    {
        WireString s => s.Value,
        WireInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        WireNumber f => f.Value.ToString(CultureInfo.InvariantCulture),
        WireBool b => b.Value ? "True" : "False",
        WireNull => "None",
        _ => string.Empty,
    };

    /// <summary>
    /// Keeps a file's history for as long as Weir still knows the file, then for <paramref name="retentionDays"/> days after
    /// it is gone or forgotten. The days count from when a history row was first seen without its file (<c>orphaned_at</c>),
    /// so a file that is forgotten long after it was processed still keeps its history for the full period. Returns how
    /// many rows were removed; 0 days keeps every row.
    /// </summary>
    public async Task<int> PruneAsync(SqliteDatabase database, long retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await WriteLockTurns.TakeAsync(() => MarkOrphansAsync(database, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = now.AddDays(-retentionDays);
        return await BatchedDeletes.DeleteAsync(
            database, "file_logs", "orphaned_at IS NOT NULL AND orphaned_at < @cutoff", [("@cutoff", ToStored(cutoff))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stamps the rows whose file has just gone, and clears the stamp on rows whose file is known again.</summary>
    private static async Task MarkOrphansAsync(SqliteDatabase database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            await uow.ExecuteAsync(
                $"UPDATE file_logs SET orphaned_at = @now WHERE orphaned_at IS NULL AND NOT {FileStillKnown}", ("@now", ToStored(now))).ConfigureAwait(false);
            await uow.ExecuteAsync($"UPDATE file_logs SET orphaned_at = NULL WHERE orphaned_at IS NOT NULL AND {FileStillKnown}").ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    private static object? ToStored(DateTimeOffset moment) => SqliteValues.ToSqlite(Timestamp.FromUtc(moment.UtcDateTime));

    private static ProcessingFileLogRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        FileId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        LibraryId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        RelativePath = SqliteValues.GetString(reader, 3),
        LibraryName = SqliteValues.GetString(reader, 4),
        Outcome = SqliteValues.GetString(reader, 5),
        Title = SqliteValues.GetString(reader, 6),
        DetailJson = SqliteValues.GetString(reader, 7),
        RecordedAt = SqliteValues.GetDateTime(reader, 8),
    };
}
