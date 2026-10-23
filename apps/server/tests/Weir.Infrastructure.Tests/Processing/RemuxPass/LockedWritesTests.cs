using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Short metadata writes of a pass: a busy database is waited out, and a database that is gone never fails the pass.</summary>
public sealed class LockedWritesTests : IDisposable
{
    private const int SqliteBusy = 5;
    private const int SqliteCantOpen = 14;

    private readonly TempDirectory _temp = new();
    private readonly ListLogger<SqliteRemuxPassData> _logger = new();
    private readonly SqliteDatabase _database;

    public LockedWritesTests() => _database = new SqliteDatabase(_temp.Join("weir.sqlite3"));

    public void Dispose()
    {
        _database.ClearPool();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_write_that_finds_the_database_busy_is_tried_again_and_the_wait_is_logged()
    {
        var attempts = 0;

        var result = await LockedWrites.RunAsync(
            _database,
            _ => ++attempts < 3 ? throw new SqliteException("database is locked", SqliteBusy) : Task.FromResult("saved"),
            _logger,
            "optional note");

        Assert.Equal("saved", result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("optional note", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_write_that_fails_for_any_other_reason_is_not_tried_again()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => LockedWrites.RunAsync<bool>(
            _database,
            _ =>
            {
                attempts++;
                throw new SqliteException("disk I/O error", 10);
            },
            _logger,
            "optional note"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Optional_file_notes_never_fail_the_pass_when_the_database_cannot_be_opened()
    {
        await using (await _database.OpenAsync())
        {
        }

        _database.ClearPool();
        File.Delete(_temp.Join("weir.sqlite3"));
        using var fixture = new MediaManagerFixture();
        var data = new SqliteRemuxPassData(_database, fixture.Connections, _logger);
        var decision = new CollisionDecision("replace", "replaced", "/out/heat.mkv", true, "A better copy replaced it.");

        await data.RecordOutputCollisionAsync("Heat/heat.mkv", decision, libraryId: 1, CancellationToken.None);

        var warning = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("optional file metadata (output collision)", warning.Message, StringComparison.Ordinal);
        Assert.Contains(_logger.Exceptions, exception => exception is SqliteException { SqliteErrorCode: SqliteCantOpen });
    }
}
