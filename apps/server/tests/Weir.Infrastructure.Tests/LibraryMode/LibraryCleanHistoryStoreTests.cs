using Weir.Core.Activity;
using Weir.Core.Time;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>The latest thing a library clean did to each file, as Activity lists it, with the filters its download list takes.</summary>
public sealed class LibraryCleanHistoryStoreTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly LibraryCleanHistoryStore _history = new();
    private long _library;

    public void Dispose() => _store.Dispose();

    private async Task SeedAsync()
    {
        _library = await _store.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        await _store.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title, relative_path, library_id, \"trigger\") VALUES " +
            $"('2026-01-01 00:00:01', '{LibraryActivityEventTypes.FileFailed}', 'library', 'First attempt', 'a.mkv', NULL, NULL), " +
            $"('2026-01-01 00:00:02', '{LibraryActivityEventTypes.FileCleaned}', 'library', 'Cleaned a.mkv', 'a.mkv', NULL, 'manual'), " +
            $"('2026-01-02 00:00:00', '{LibraryActivityEventTypes.FileSkipped}', 'library', 'Left b_c.mkv alone', 'b_c.mkv', {_library}, 'scan'), " +
            "('2026-01-03 00:00:00', 'processing.file_rejected', 'processing', 'Something else', 'd.mkv', NULL, NULL)");
    }

    private Task<List<LibraryCleanHistoryRow>> ListAsync(LibraryCleanHistoryFilter filter) =>
        _store.WithUnitOfWork(uow => _history.ListAsync(uow, filter), commit: false);

    [Fact]
    public async Task Each_file_has_one_row_its_newest_and_a_clean_with_no_workflow_recorded_has_none()
    {
        await SeedAsync();

        var rows = await ListAsync(new LibraryCleanHistoryFilter());

        Assert.Equal(["b_c.mkv", "a.mkv"], rows.Select(row => row.RelativePath));
        Assert.Equal([_library, (long?)null], rows.Select(row => row.LibraryId));
        Assert.Equal(["scan", "manual"], rows.Select(row => row.Trigger));
        Assert.Equal("Cleaned a.mkv", rows[1].Detail);
    }

    [Fact]
    public async Task The_list_can_be_cut_to_a_workflow_a_part_of_the_path_a_time_and_a_number_of_rows()
    {
        await SeedAsync();

        Assert.Equal(["b_c.mkv"], (await ListAsync(new LibraryCleanHistoryFilter { LibraryId = _library })).Select(row => row.RelativePath));
        Assert.Equal(["b_c.mkv"], (await ListAsync(new LibraryCleanHistoryFilter { PathContains = "b_c" })).Select(row => row.RelativePath));
        Assert.Empty(await ListAsync(new LibraryCleanHistoryFilter { PathContains = "b%c" }));
        Assert.Equal(["b_c.mkv"], (await ListAsync(new LibraryCleanHistoryFilter { Since = Timestamp.FromUtc(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)) })).Select(row => row.RelativePath));
        Assert.Single(await ListAsync(new LibraryCleanHistoryFilter { Limit = 1 }));
    }

    [Fact]
    public async Task A_list_asked_for_nothing_or_too_much_is_cut_to_what_the_download_list_allows()
    {
        Assert.Equal(1, new LibraryCleanHistoryFilter { Limit = 0 }.ClampedLimit);
        Assert.Equal(LibraryCleanHistoryFilter.MaxLimit, new LibraryCleanHistoryFilter { Limit = 5000 }.ClampedLimit);
        Assert.Empty(await ListAsync(new LibraryCleanHistoryFilter()));
    }

    [Fact]
    public async Task The_store_refuses_a_missing_argument()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.WithUnitOfWork(uow => _history.ListAsync(uow, null!), commit: false));
        Assert.Throws<ArgumentNullException>(() => { _ = _history.ListAsync(null!, new LibraryCleanHistoryFilter()); });
    }
}
