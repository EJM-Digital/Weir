using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// A read whose caller has gone away ends as the cancellation it is, never wrapped in an <see cref="AggregateException"/>, so a request
/// that was abandoned is told apart from one that failed.
/// </summary>
public sealed class StoreReadCancellationTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Reading_a_libraries_kept_file_markers_after_the_caller_left_is_a_plain_cancellation()
    {
        using var left = new CancellationTokenSource();
        var uow = await UnitOfWork.OpenAsync(_store.Database, left.Token);
        await using (uow)
        {
            await left.CancelAsync();

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSkipMarkerStore().ForLibraryAsync(uow, 1));

            Assert.IsNotType<AggregateException>(thrown);
        }
    }

    [Fact]
    public async Task Reading_the_library_names_after_the_caller_left_is_a_plain_cancellation()
    {
        using var left = new CancellationTokenSource();
        var uow = await UnitOfWork.OpenAsync(_store.Database, left.Token);
        await using (uow)
        {
            await left.CancelAsync();

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileStateStore().LibraryNamesAsync(uow));

            Assert.IsNotType<AggregateException>(thrown);
        }
    }

    [Fact]
    public async Task The_markers_and_names_are_still_read_when_the_caller_stays()
    {
        await _store.Execute("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie')");
        var libraryId = await _store.Scalar("SELECT id FROM libraries WHERE name = 'Films'");
        await _store.Execute($"INSERT INTO file_skip_markers (library_id, relative_path, size_bytes, mtime_ns) VALUES ({libraryId}, 'a.mkv', 5, 9)");

        var (markers, names) = await _store.WithUnitOfWork(
            async uow => (await new FileSkipMarkerStore().ForLibraryAsync(uow, libraryId), await new FileStateStore().LibraryNamesAsync(uow)),
            commit: false);

        Assert.Equal(new FileSkipMarker(5, 9), markers["a.mkv"]);
        Assert.Equal("Films", names[libraryId]);
    }
}
