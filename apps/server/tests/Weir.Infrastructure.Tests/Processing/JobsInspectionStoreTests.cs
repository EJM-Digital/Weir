using Weir.Core.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// The jobs list behind the Processing screen's "active" filter (pending + leased), which the working-on
/// area uses for library cleans running in place.
/// </summary>
public sealed class JobsInspectionStoreTests
{
    private static readonly JobsInspectionStore Store = new();

    private static void InsertJob(JobsTestDatabase db, string dedupeKey, string status, string updatedAt) =>
        db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, created_at, updated_at) " +
            "VALUES (@key, 'processing.library.clean.v1', @status, @updated, @updated)",
            ("@key", dedupeKey), ("@status", status), ("@updated", updatedAt));

    [Fact]
    public void A_time_a_job_has_is_read_as_utc_and_one_it_has_not_is_nothing()
    {
        var stamp = Weir.Core.Time.Timestamp.FromUtc(new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc));

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero), JobsInspectionStore.ToOffset(stamp));
        Assert.Null(JobsInspectionStore.ToOffset(null));
    }

    [Fact]
    public async Task A_leased_job_is_listed_ahead_of_the_limit_even_behind_a_backlog_of_newer_pending_ones()
    {
        // A library clean that is actually running must never lose its row on the Processing screen just because a
        // backlog of pending clean jobs was queued (or requeued) more recently (#781).
        using var db = new JobsTestDatabase();
        InsertJob(db, "running", ProcessingJobStatus.Leased, "2020-01-01T00:00:00Z");
        InsertJob(db, "backlog-1", ProcessingJobStatus.Pending, "2030-01-01T00:00:00Z");
        InsertJob(db, "backlog-2", ProcessingJobStatus.Pending, "2030-01-01T00:00:01Z");
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var (rows, defaultSlice) = await Store.ListAsync(uow, limit: 1, statuses: [ProcessingJobStatus.Pending, ProcessingJobStatus.Leased]);

        Assert.False(defaultSlice);
        var row = Assert.Single(rows);
        Assert.Equal("running", row.DedupeKey);
    }

    [Fact]
    public async Task A_leased_job_is_listed_with_who_holds_it_what_it_carries_and_what_went_wrong_last()
    {
        using var db = new JobsTestDatabase();
        InsertJob(db, "running", ProcessingJobStatus.Leased, "2020-01-01T00:00:00Z");
        db.Execute("UPDATE jobs SET lease_owner = 'worker-1', payload_json = '{\"a\":1}', last_error = 'earlier failure'");
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var (rows, _) = await Store.ListAsync(uow, limit: 10, statuses: [ProcessingJobStatus.Leased]);

        var row = Assert.Single(rows);
        Assert.Equal(("worker-1", "{\"a\":1}", "earlier failure"), (row.LeaseOwner, row.PayloadJson, row.LastError));
    }

    [Fact]
    public async Task With_no_leased_job_the_newest_pending_one_still_wins_the_limit()
    {
        using var db = new JobsTestDatabase();
        InsertJob(db, "older", ProcessingJobStatus.Pending, "2020-01-01T00:00:00Z");
        InsertJob(db, "newer", ProcessingJobStatus.Pending, "2020-01-02T00:00:00Z");
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var (rows, _) = await Store.ListAsync(uow, limit: 1, statuses: [ProcessingJobStatus.Pending, ProcessingJobStatus.Leased]);

        var row = Assert.Single(rows);
        Assert.Equal("newer", row.DedupeKey);
    }
}
