using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// Real-SQLite proof for #530: a <c>files</c> row at <c>passed_through</c> or <c>rejected</c> lists, filters
/// and counts exactly like any other status, so a list containing one does not fail and a filter naming one
/// is accepted.
/// </summary>
public sealed class FileStateStoreTests
{
    private static readonly FileStateStore Store = new();

    private static long InsertLibrary(JobsTestDatabase db, string name = "Movies bug530")
    {
        db.Execute(
            "INSERT INTO libraries (name, media_type) VALUES (@name, 'movie')",
            ("@name", name));
        return Convert.ToInt64(db.Scalar("SELECT id FROM libraries WHERE name = @name", ("@name", name)));
    }

    private static void InsertFile(JobsTestDatabase db, long libraryId, string relativePath, string status) =>
        db.Execute(
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES (@lib, @path, @status, CURRENT_TIMESTAMP)",
            ("@lib", libraryId), ("@path", relativePath), ("@status", status));

    [Theory]
    [InlineData(ProcessingFileStatuses.PassedThrough)]
    [InlineData(ProcessingFileStatuses.Rejected)]
    public async Task Listing_by_passed_through_or_rejected_returns_the_row_without_raising(string status)
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "Movie (2020)/movie.mkv", status);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(uow, new ProcessingFileListFilter { Statuses = [status] });

        var row = Assert.Single(rows);
        Assert.Equal(status, row.Status);
        Assert.Equal("Movie (2020)/movie.mkv", row.RelativePath);
    }

    [Fact]
    public async Task Status_counts_include_passed_through_and_rejected_alongside_every_other_status()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "a.mkv", ProcessingFileStatuses.PassedThrough);
        InsertFile(db, libraryId, "b.mkv", ProcessingFileStatuses.Rejected);
        InsertFile(db, libraryId, "c.mkv", ProcessingFileStatuses.Processed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var counts = await Store.StatusCountsAsync(uow, libraryId);

        Assert.Equal(ProcessingFileStatuses.All.Count, counts.Count);
        Assert.Equal(1, counts[ProcessingFileStatuses.PassedThrough]);
        Assert.Equal(1, counts[ProcessingFileStatuses.Rejected]);
        Assert.Equal(1, counts[ProcessingFileStatuses.Processed]);
        Assert.Equal(0, counts[ProcessingFileStatuses.Unprocessed]);
    }

    [Fact]
    public async Task Listing_with_no_filter_returns_files_across_every_status_newest_first()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "a.mkv", ProcessingFileStatuses.PassedThrough);
        InsertFile(db, libraryId, "b.mkv", ProcessingFileStatuses.Unprocessed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(uow, new ProcessingFileListFilter());

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Path_contains_filters_case_insensitively()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "Show/S01E01.mkv", ProcessingFileStatuses.Processed);
        InsertFile(db, libraryId, "Other/file.mkv", ProcessingFileStatuses.Processed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(uow, new ProcessingFileListFilter { PathContains = "show" });

        var row = Assert.Single(rows);
        Assert.Equal("Show/S01E01.mkv", row.RelativePath);
    }

    [Fact]
    public async Task Filtering_by_several_statuses_at_once_returns_every_row_in_any_of_them()
    {
        // The Processing screen asks for one status ("processing") in its own uncapped page (#781), but the
        // filter takes several: a comma-separated file_status is split into this list by the endpoint.
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "a.mkv", ProcessingFileStatuses.Processing);
        InsertFile(db, libraryId, "b.mkv", ProcessingFileStatuses.Unprocessed);
        InsertFile(db, libraryId, "c.mkv", ProcessingFileStatuses.Processed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(
            uow,
            new ProcessingFileListFilter { Statuses = [ProcessingFileStatuses.Processing, ProcessingFileStatuses.Unprocessed] });

        Assert.Equal(["a.mkv", "b.mkv"], rows.Select(row => row.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_empty_status_list_filters_nothing_same_as_no_filter_at_all()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "a.mkv", ProcessingFileStatuses.Processing);
        InsertFile(db, libraryId, "b.mkv", ProcessingFileStatuses.Unprocessed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(uow, new ProcessingFileListFilter { Statuses = [] });

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task A_window_in_days_lists_a_file_no_scan_has_seen_yet_by_the_time_it_was_recorded()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFileWithTimes(db, libraryId, "unseen-now.mkv", lastSeenAt: null, recordedAt: "CURRENT_TIMESTAMP");
        InsertFileWithTimes(db, libraryId, "unseen-long-ago.mkv", lastSeenAt: null, recordedAt: "'2020-01-01 00:00:00'");
        InsertFileWithTimes(db, libraryId, "seen-long-ago.mkv", lastSeenAt: "'2020-01-01 00:00:00'", recordedAt: "CURRENT_TIMESTAMP");
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var rows = await Store.ListAsync(uow, new ProcessingFileListFilter { Since = Timestamp.FromUtc(DateTime.UtcNow.AddDays(-7)) });

        Assert.Equal(["unseen-now.mkv"], rows.Select(row => row.RelativePath));
    }

    private static void InsertFileWithTimes(JobsTestDatabase db, long libraryId, string relativePath, string? lastSeenAt, string recordedAt) =>
        db.Execute(
            $"INSERT INTO files (library_id, relative_path, status, last_seen_at, created_at, updated_at) VALUES (@lib, @path, @status, {lastSeenAt ?? "NULL"}, {recordedAt}, {recordedAt})",
            ("@lib", libraryId), ("@path", relativePath), ("@status", ProcessingFileStatuses.ProcessingFailed));

    [Fact]
    public async Task Forgetting_a_file_removes_its_record_only()
    {
        using var db = new JobsTestDatabase();
        var libraryId = InsertLibrary(db);
        InsertFile(db, libraryId, "a.mkv", ProcessingFileStatuses.Processed);
        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var row = Assert.Single(await Store.ListAsync(uow, new ProcessingFileListFilter()));

        await Store.ForgetAsync(uow, row.Id);

        Assert.Null(await Store.GetAsync(uow, row.Id));
    }
}
