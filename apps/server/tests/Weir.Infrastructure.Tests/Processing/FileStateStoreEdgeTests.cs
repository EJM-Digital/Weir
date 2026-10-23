using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>The less common ways a scan or a pass writes a file's state: a reset alone, a recorded attempt, an empty id list.</summary>
public sealed class FileStateStoreEdgeTests : IDisposable
{
    private static readonly FileStateStore Store = new();
    private static readonly DateTimeOffset Seen = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly JobsTestDatabase _db = new();
    private readonly long _library;

    public FileStateStoreEdgeTests()
    {
        _db.Execute("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie')");
        _library = _db.Count("SELECT id FROM libraries");
    }

    public void Dispose() => _db.Dispose();

    private long InsertFile(string status)
    {
        _db.Execute(
            "INSERT INTO files (library_id, relative_path, status, last_seen_at, failure_attempts) VALUES (@lib, 'a.mkv', @status, CURRENT_TIMESTAMP, 2)",
            ("@lib", _library), ("@status", status));
        return _db.Count("SELECT id FROM files");
    }

    private static ScannedFileWrite Write(long? rowId, string? expected, string? reset) => new("a.mkv", rowId, expected, reset, null, 10, null);

    [Fact]
    public async Task A_scan_write_for_a_file_with_no_row_and_no_state_is_refused()
    {
        await using var uow = await UnitOfWork.OpenAsync(_db.Database);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Store.RecordScannedStateAsync(uow, _library, Write(null, null, null), Seen));

        Assert.Equal("write", refused.ParamName);
    }

    [Fact]
    public async Task A_changed_source_starts_again_from_the_reset_status_with_its_failures_cleared()
    {
        var id = InsertFile(ProcessingFileStatuses.ProcessingFailed);
        await using var uow = await UnitOfWork.OpenAsync(_db.Database);

        var applied = await Store.RecordScannedStateAsync(
            uow, _library, Write(id, ProcessingFileStatuses.ProcessingFailed, ProcessingFileStatuses.Unprocessed), Seen);
        await uow.CommitAsync();

        Assert.True(applied);
        Assert.Equal(ProcessingFileStatuses.Unprocessed, _db.Scalar("SELECT status FROM files"));
        Assert.Equal(0L, _db.Count("SELECT failure_attempts FROM files"));
    }

    [Fact]
    public async Task A_recorded_attempt_stamps_when_the_file_was_last_tried_and_a_status_that_means_a_try_does_too()
    {
        InsertFile(ProcessingFileStatuses.Unprocessed);
        await using (var uow = await UnitOfWork.OpenAsync(_db.Database))
        {
            await Store.RecordFileStateAsync(uow, _library, "a.mkv", new FileStateVerdict(ProcessingFileStatuses.Processing, "working"), null, null, Seen, isAttempt: true);
            await uow.CommitAsync();
        }

        Assert.Equal("2026-04-10 12:00:00.000000", _db.Scalar("SELECT last_attempt_at FROM files"));

        _db.Execute("UPDATE files SET last_attempt_at = NULL");
        await using (var uow = await UnitOfWork.OpenAsync(_db.Database))
        {
            await Store.MarkFileStatusAsync(uow, _library, "a.mkv", ProcessingFileStatuses.Processed, "done");
            await uow.CommitAsync();
        }

        Assert.IsNotType<DBNull>(_db.Scalar("SELECT last_attempt_at FROM files"));
    }
}
