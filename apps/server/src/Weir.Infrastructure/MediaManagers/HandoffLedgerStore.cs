using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// One <c>media_manager_handoffs</c> row. <see cref="Outcome"/> is the manager's own word on what became of the file
/// (<c>imported</c> or <c>not-imported</c>, #652), with what Weir answered and whether it released its copy.
/// <see cref="ConnectionId"/> is the connection this hand-off belongs to when that was unambiguous at intake; a
/// hand-off route that reveals file paths requires that connection's own secret, never another same-kind one.
/// <see cref="ReportedStatus"/> is what Weir last reported to the manager, and <see cref="OutputFiles"/> the output files
/// that report named.
/// </summary>
public sealed record HandoffLedgerRow(
    long Id,
    string SourceKey,
    string HandoffId,
    long? LibraryId,
    string RelativePath,
    string State,
    string? OutputPath,
    string? Message,
    DateTimeOffset? LastChangedAt,
    string? Outcome = null,
    string? OutcomeMessage = null,
    bool OutcomeReleased = false,
    string? DownloadId = null,
    DateTimeOffset? ReceivedAt = null,
    long? ConnectionId = null,
    string? ReportedStatus = null,
    IReadOnlyList<string>? OutputFiles = null);

/// <summary>The <c>files</c> columns the ledger reads.</summary>
public sealed record HandoffFileRow(long Id, string RelativePath, string Status, string StatusReason, DateTimeOffset? NextRetryAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// The hand-off ledger: state is worked out live from the job queue and the Files rows
/// while they exist, and the row keeps the last answer so it survives job-row pruning.
/// </summary>
/// <remarks>
/// Working the live state out from the job queue and Files rows lives in the <c>.Status</c> partial, and dropping
/// or settling a hand-off in the <c>.Cancellation</c> partial.
/// </remarks>
public sealed partial class HandoffLedgerStore
{
    private const string LedgerColumns =
        "id, source_key, handoff_id, library_id, relative_path, state, output_path, message, last_changed_at, outcome, outcome_message, " +
        "outcome_released, download_id, created_at, connection_id, reported_status, output_files_json";

    private readonly TimeProvider _time;
    private readonly HandoffTargetStore _targets;
    private readonly FileStateStore _files;

    public HandoffLedgerStore(TimeProvider time, HandoffTargetStore targets, FileStateStore files)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>The ledger row for a manager's hand-off id, or null.</summary>
    public static Task<HandoffLedgerRow?> FindAsync(UnitOfWork uow, string sourceKey, string handoffId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync(
            $"SELECT {LedgerColumns} FROM media_manager_handoffs WHERE source_key = $source AND handoff_id = $id LIMIT 1",
            ReadLedger,
            ("$source", sourceKey),
            ("$id", handoffId));
    }

    /// <summary>
    /// Record a hand-off at intake. A repeat of a finished hand-off starts it over, and so forgets what the manager said
    /// about the last copy (#652), what Weir reported, and the files it covered. The manager's download id, when it sent
    /// one, is kept so a Sonarr or Radarr import can be matched by it. <paramref name="connectionId"/> is who the intake
    /// webhook attributed the event to (<see cref="MediaManagerIntake.AuthoriseAsync"/>); it is recorded once, at first
    /// receipt, and never overwritten by a resend, so a hand-off keeps the same owner across its whole lifetime.
    /// Returns the row's id.
    /// </summary>
    public async Task<long> RecordReceivedAsync(
        UnitOfWork uow, string sourceKey, string handoffId, long? libraryId, string relativePath, long? connectionId = null, string? downloadId = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var now = TimestampColumns.Orm(_time.GetUtcNow());
        var row = await FindAsync(uow, sourceKey, handoffId).ConfigureAwait(false);
        if (row is null)
        {
            var inserted = await uow.ExecuteScalarWriteAsync(
                "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state, output_path, message, created_at, last_changed_at, download_id, connection_id) " +
                "VALUES ($source, $id, $library, $path, $state, NULL, NULL, $now, $now, $download, $connection) RETURNING id",
                ("$source", sourceKey),
                ("$id", handoffId),
                ("$library", libraryId),
                ("$path", relativePath),
                ("$state", HandoffLedgerRules.Queued),
                ("$now", now),
                ("$download", string.IsNullOrWhiteSpace(downloadId) ? null : downloadId.Trim()),
                ("$connection", connectionId)).ConfigureAwait(false);
            return Convert.ToInt64(inserted, CultureInfo.InvariantCulture);
        }

        if (HandoffLedgerRules.TerminalStates.Contains(row.State))
        {
            await uow.ExecuteAsync(
                "UPDATE media_manager_handoffs SET library_id = $library, relative_path = $path, state = $state, output_path = NULL, " +
                "message = NULL, last_changed_at = $now, outcome = NULL, outcome_at = NULL, outcome_message = NULL, outcome_released = 0, " +
                "pending_report_json = NULL, reported_status = NULL, output_files_json = NULL, " +
                "download_id = coalesce($download, download_id), connection_id = coalesce($connection, connection_id) WHERE id = $row",
                ("$library", libraryId),
                ("$path", relativePath),
                ("$state", HandoffLedgerRules.Queued),
                ("$now", now),
                ("$download", string.IsNullOrWhiteSpace(downloadId) ? null : downloadId.Trim()),
                ("$connection", connectionId),
                ("$row", row.Id)).ConfigureAwait(false);
            await _targets.ClearAsync(uow, row.Id).ConfigureAwait(false);
        }

        return row.Id;
    }

    /// <summary>The manager's own word on a finished hand-off (#652), and what Weir answered.</summary>
    public async Task RecordManagerOutcomeAsync(UnitOfWork uow, long rowId, string outcome, DateTimeOffset occurredAt, string message, bool released)
    {
        ArgumentNullException.ThrowIfNull(uow);
        await uow.ExecuteAsync(
            "UPDATE media_manager_handoffs SET outcome = $outcome, outcome_at = $at, outcome_message = $message, outcome_released = $released, " +
            "last_changed_at = $now WHERE id = $row",
            ("$outcome", outcome),
            ("$at", TimestampColumns.Orm(occurredAt)),
            ("$message", WireStrings.Slice(message, 2000)),
            ("$released", released ? 1 : 0),
            ("$now", TimestampColumns.Orm(_time.GetUtcNow())),
            ("$row", rowId)).ConfigureAwait(false);
    }

    /// <summary>Hand-offs whose manager gave this download id, newest first.</summary>
    public static Task<List<HandoffLedgerRow>> WithDownloadIdAsync(UnitOfWork uow, string downloadId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            $"SELECT {LedgerColumns} FROM media_manager_handoffs WHERE download_id = $download ORDER BY id DESC",
            ReadLedger,
            ("$download", downloadId));
    }

    /// <summary>
    /// A report Weir owes the manager because it was not answering when the pass ended (null clears it). The heartbeat
    /// sends it once the manager answers (<see cref="HandoffCompletionReporter.SendWaitingReportsAsync"/>).
    /// </summary>
    public static Task SetPendingReportAsync(UnitOfWork uow, string sourceKey, string handoffId, string? reportJson)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE media_manager_handoffs SET pending_report_json = $report WHERE source_key = $source AND handoff_id = $id",
            ("$report", reportJson),
            ("$source", sourceKey),
            ("$id", handoffId));
    }

    /// <summary>
    /// Notes that the manager accepted Weir's rejection of this hand-off, so a job that runs again (after a crash, or a save that
    /// failed once the manager had answered) finishes the rejection without reporting it a second time. A hand-off already
    /// reported as anything but failed is left as it was.
    /// </summary>
    public static Task<int> RecordRejectionReportedAsync(UnitOfWork uow, string sourceKey, string handoffId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE media_manager_handoffs SET reported_status = $rejected WHERE source_key = $source AND handoff_id = $id " +
            "AND (reported_status IS NULL OR reported_status = $failed)",
            ("$rejected", HandoffLedgerRules.Rejected),
            ("$failed", HandoffLedgerRules.Failed),
            ("$source", sourceKey),
            ("$id", handoffId));
    }

    /// <summary>Every report Weir still owes a manager of this kind: the hand-off id and the saved report.</summary>
    public static Task<List<(string HandoffId, string ReportJson)>> PendingReportsAsync(UnitOfWork uow, string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            "SELECT handoff_id, pending_report_json FROM media_manager_handoffs WHERE source_key = $source AND pending_report_json IS NOT NULL ORDER BY id",
            reader => (SqliteValues.GetString(reader, 0), SqliteValues.GetString(reader, 1)),
            ("$source", sourceKey));
    }

    /// <summary>
    /// Record a result Weir reached, with the output it reported: <paramref name="outputPath"/> is the one file, or the
    /// folder a hand-off of several files was handed back in, and <paramref name="outputFiles"/> every file. Unknown and
    /// cancelled hand-offs are left alone.
    /// </summary>
    public async Task RecordOutcomeAsync(
        UnitOfWork uow, string sourceKey, string? handoffId, string state, string? outputPath = null, string? message = null, IReadOnlyList<string>? outputFiles = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (string.IsNullOrEmpty(handoffId))
        {
            return;
        }

        var row = await FindAsync(uow, sourceKey, handoffId).ConfigureAwait(false);
        if (row is null || row.State == HandoffLedgerRules.Cancelled)
        {
            return;
        }

        var files = outputFiles is null ? null : HandoffOutputFiles.Serialize(outputFiles);
        var storedFiles = row.OutputFiles is null ? null : HandoffOutputFiles.Serialize(row.OutputFiles);
        if (row.State != state || row.OutputPath != outputPath || storedFiles != files)
        {
            await uow.ExecuteAsync(
                "UPDATE media_manager_handoffs SET state = $state, output_path = $output, output_files_json = $files, message = $message, " +
                "last_changed_at = $now WHERE id = $row",
                ("$state", state),
                ("$output", outputPath),
                ("$files", files),
                ("$message", string.IsNullOrEmpty(message) ? null : WireStrings.Slice(message, 2000)),
                ("$now", TimestampColumns.Orm(_time.GetUtcNow())),
                ("$row", row.Id)).ConfigureAwait(false);
        }
    }

    private static HandoffLedgerRow ReadLedger(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetString(reader, 1),
        SqliteValues.GetString(reader, 2),
        reader.IsDBNull(3) ? null : SqliteValues.GetInt64(reader, 3),
        SqliteValues.GetString(reader, 4),
        SqliteValues.GetString(reader, 5),
        SqliteValues.GetStringOrNull(reader, 6),
        SqliteValues.GetStringOrNull(reader, 7),
        TimestampColumns.Parse(reader.GetValue(8)),
        SqliteValues.GetStringOrNull(reader, 9),
        SqliteValues.GetStringOrNull(reader, 10),
        SqliteValues.GetBool(reader, 11),
        SqliteValues.GetStringOrNull(reader, 12),
        TimestampColumns.Parse(reader.GetValue(13)),
        reader.IsDBNull(14) ? null : SqliteValues.GetInt64(reader, 14),
        SqliteValues.GetStringOrNull(reader, 15),
        HandoffOutputFiles.Parse(SqliteValues.GetStringOrNull(reader, 16)));
}
