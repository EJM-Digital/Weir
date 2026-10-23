using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Time;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>How many jobs ran in some stretch of time, and how many of them failed.</summary>
public sealed record FinishedJobs(long Run, long Failed);

/// <summary>Read-only <c>jobs</c> listing for operators.</summary>
public sealed class JobsInspectionStore
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        ProcessingJobStatus.Pending, ProcessingJobStatus.Leased, ProcessingJobStatus.Completed,
        ProcessingJobStatus.Failed, ProcessingJobStatus.HandlerOkFinalizeFailed, ProcessingJobStatus.Cancelled,
    };

    /// <summary>The per-file download job kinds: their payload names the file a "known files only" listing checks for.</summary>
    private static readonly string[] PerFileJobKinds = [RemuxPassOutcomes.JobKind, IntakeRules.PassThroughJobKind, IntakeRules.RejectJobKind];

    /// <summary>
    /// A per-file job (one of <see cref="PerFileJobKinds"/>) whose file Weir has forgotten (its <c>files</c> row is gone)
    /// is excluded; every other job passes through untouched. This only narrows what the live alert counts as a current
    /// problem — the job row itself, and System's own Jobs list, are unaffected.
    /// </summary>
    private static string KnownFilesOnlyClause((string Name, object? Value)[] kindParameters) =>
        $"(job_kind NOT IN ({string.Join(", ", kindParameters.Select(p => p.Name))}) OR EXISTS (" +
        "SELECT 1 FROM files WHERE files.relative_path = json_extract(jobs.payload_json, '$.relative_media_path') " +
        "AND files.library_id = json_extract(jobs.payload_json, '$.library_id')))";

    private static (string Name, object? Value)[] KindParameters() =>
        [.. PerFileJobKinds.Select((kind, index) => ($"@per_file_kind{index}", (object?)kind))];

    /// <summary>Throws <see cref="ArgumentException"/> naming any status filter value that is not a known job status.</summary>
    public void ValidateStatuses(IReadOnlyList<string> statuses)
    {
        var unknown = statuses.Where(s => !AllowedStatuses.Contains(s)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid status filter values: {string.Join(", ", unknown)}; allowed={string.Join(", ", AllowedStatuses.Order(StringComparer.Ordinal))}");
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> rows, newest <c>updated_at</c> first. With no status filter, excludes completed
    /// watched-folder scan-dispatch rows so frequent, successful periodic checks do not crowd out real work.
    /// <paramref name="knownFilesOnly"/> is for a live alert, not an audit trail: it leaves out a per-file job whose file
    /// was forgotten, but never removes anything a full System › Jobs listing (the default, <paramref name="knownFilesOnly"/>
    /// false) still shows.
    /// </summary>
    public async Task<(List<ProcessingJob> Rows, bool DefaultRecentSlice)> ListAsync(UnitOfWork uow, int limit, IReadOnlyList<string>? statuses, bool knownFilesOnly = false)
    {
        const string columns = ProcessingJobStore.JobColumns;
        if (statuses is { Count: > 0 })
        {
            var placeholders = string.Join(",", statuses.Select((_, i) => $"@status{i}"));
            var parameters = statuses.Select((s, i) => ($"@status{i}", (object?)s))
                .Append(("@leased", (object?)ProcessingJobStatus.Leased)).ToList();
            var where = $"status IN ({placeholders})";
            if (knownFilesOnly)
            {
                var kindParameters = KindParameters();
                where += " AND " + KnownFilesOnlyClause(kindParameters);
                parameters.AddRange(kindParameters);
            }

            // The Processing screen's "active" filter (pending + leased) shares this page with every job kind, so a
            // backlog of pending library cleans can otherwise outrank a job that is actually running and push it
            // off the page. Leased jobs sort first, so a running one is never dropped by the limit.
            var rows = await uow.QueryAsync(
                $"SELECT {columns} FROM jobs WHERE {where} ORDER BY (status = @leased) DESC, updated_at DESC LIMIT {limit}",
                Read, [.. parameters]).ConfigureAwait(false);
            return (rows, false);
        }

        var (sql, recentParameters) = RecentQuery(limit, knownFilesOnly);
        var recent = await uow.QueryAsync(sql, Read, recentParameters).ConfigureAwait(false);
        return (recent, true);
    }

    /// <summary>The default slice: the most recently changed jobs, leaving out completed scan dispatches.</summary>
    internal static (string Sql, (string Name, object? Value)[] Parameters) RecentQuery(int limit, bool knownFilesOnly = false)
    {
        var parameters = new List<(string Name, object? Value)> { ("@completed", ProcessingJobStatus.Completed), ("@scan_kind", "processing.watched_folder.remux_scan_dispatch.v1") };
        var where = "NOT (status = @completed AND job_kind = @scan_kind)";
        if (knownFilesOnly)
        {
            var kindParameters = KindParameters();
            where += " AND " + KnownFilesOnlyClause(kindParameters);
            parameters.AddRange(kindParameters);
        }

        return ($"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE {where} ORDER BY updated_at DESC LIMIT {limit}", [.. parameters]);
    }

    /// <summary>
    /// The jobs that finished since <paramref name="since"/>: how many ran, and how many of those failed. A watched-folder scan that
    /// found nothing wrong is left out, as it is from the Jobs list: periodic scans would otherwise outnumber real work.
    /// </summary>
    public async Task<FinishedJobs> CountFinishedSinceAsync(UnitOfWork uow, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync(
            "SELECT status, COUNT(*) FROM jobs WHERE status IN (@completed, @failed, @finalize_failed) AND updated_at >= @since " +
            "AND NOT (status = @completed AND job_kind = @scan_kind) GROUP BY status",
            reader => (Status: reader.GetString(0), Count: reader.GetInt64(1)),
            ("@completed", ProcessingJobStatus.Completed),
            ("@failed", ProcessingJobStatus.Failed),
            ("@finalize_failed", ProcessingJobStatus.HandlerOkFinalizeFailed),
            ("@since", Timestamp.FromUtc(since.UtcDateTime).ToSqlite()),
            ("@scan_kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch)).ConfigureAwait(false);
        var failed = rows.Where(row => row.Status != ProcessingJobStatus.Completed).Sum(row => row.Count);
        return new FinishedJobs(rows.Sum(row => row.Count), failed);
    }

    private static DateTimeOffset? ToOffset(Timestamp? value) => value is { } v ? Utc(v) : null;

    private static DateTimeOffset Utc(Timestamp value) => new(value.AsUtc, TimeSpan.Zero);

    // Timestamps go through the strict ISO reader in SqliteValues rather than ProcessingJobStore.ReadJob's parser: the two
    // differ on unusual stored text, and the inspection API keeps its existing output and errors.
    internal static ProcessingJob Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        ToOffset(SqliteValues.GetDateTimeOrNull(reader, 6)),
        (int)SqliteValues.GetInt64(reader, 7),
        (int)SqliteValues.GetInt64(reader, 8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        ToOffset(SqliteValues.GetDateTimeOrNull(reader, 10)),
        (int)SqliteValues.GetInt64(reader, 11),
        (int)SqliteValues.GetInt64(reader, 12),
        Utc(SqliteValues.GetDateTime(reader, 13)),
        Utc(SqliteValues.GetDateTime(reader, 14)));
}
