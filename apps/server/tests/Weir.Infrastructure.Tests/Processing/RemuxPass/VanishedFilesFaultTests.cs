using System.Globalization;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>A look for files that left the watched folder, when the disk will not answer it.</summary>
public sealed class VanishedFilesFaultTests : IDisposable
{
    private const string Gone = "Gone/Film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private long _libraryId;

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private async Task SeedAsync(int lastSeenMinutesAgo)
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, sidecar_patterns_csv) " +
            "VALUES ('Library', 'movie', $w, $o, $k, 'pass_through', 3, 'leave', 60, 0, 0, 1, '.srt') RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
        var seen = "'" + _fixture.Store.Clock.GetUtcNow().AddMinutes(-lastSeenMinutesAgo).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
        await _fixture.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at, created_at, updated_at) " +
            $"VALUES ({_libraryId}, '{Gone}', 'unprocessed', 'seeded', 2000, {seen}, {seen}, {seen})");
    }

    private Task<VanishedFiles.Changes> LookAsync(IFileSystem files, Func<CancellationToken, Task>? lookAgain = null) =>
        VanishedFiles.SettleAsync(
            _fixture.Store.Database, _libraryId, _folders.Watched, "movie", _fixture.Store.Clock.GetUtcNow(), "scheduled",
            lookAgain ?? (_ => Task.CompletedTask), _ => { }, files, CancellationToken.None);

    private Task<long> RowsAsync() => _fixture.Store.Scalar($"SELECT count(*) FROM files WHERE relative_path = '{Gone}' AND status = 'unprocessed'");

    private Task<long> LeftActivityAsync() =>
        _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder'");

    [Fact]
    public async Task A_disk_that_will_not_say_whether_a_file_is_there_changes_no_row_and_the_next_look_forgets_it()
    {
        await SeedAsync(lastSeenMinutesAgo: 60);
        var files = new FaultingFileSystem().Fail(FileCall.FileExists, FaultingFileSystem.SharingViolation);

        await Assert.ThrowsAsync<IOException>(() => LookAsync(files));

        Assert.Equal(1, await RowsAsync());
        Assert.Equal(0, await LeftActivityAsync());

        var changes = await LookAsync(files);

        Assert.Equal([Gone], changes.Forgotten);
        Assert.Equal(0, await RowsAsync());
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_disk_that_stops_answering_between_the_two_looks_changes_no_row()
    {
        await SeedAsync(lastSeenMinutesAgo: 60);
        var files = new FaultingFileSystem().Fail(FileCall.FileExists, FaultingFileSystem.SharingViolation, onCall: 2);

        await Assert.ThrowsAsync<IOException>(() => LookAsync(files));

        Assert.Equal(1, await RowsAsync());
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_file_that_comes_back_between_the_two_looks_is_neither_held_nor_forgotten()
    {
        await SeedAsync(lastSeenMinutesAgo: 60);
        var files = new FaultingFileSystem();

        var changes = await LookAsync(files, _ =>
        {
            _folders.Source(Gone);
            return Task.CompletedTask;
        });

        Assert.False(changes.Any);
        Assert.Equal(1, await RowsAsync());
        Assert.Equal(0, await LeftActivityAsync());
    }
}
