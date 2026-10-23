using Weir.Core.Activity;
using Weir.Infrastructure.Tests.Platform;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>What Activity lists as the latest clean of each file.</summary>
public sealed class LibraryCleanHistoryStoreTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task A_clean_whose_workflow_has_since_been_removed_is_still_listed_without_one()
    {
        await _store.Execute(
            "INSERT INTO activity_events (event_type, module, title, library_id, relative_path) VALUES " +
            $"('{LibraryActivityEventTypes.FileCleaned}', 'library', 'Cleaned a.mkv', NULL, 'a.mkv'), " +
            $"('{LibraryActivityEventTypes.FileCleaned}', 'library', 'Cleaned b.mkv', 7, 'b.mkv')");

        var rows = await _store.WithUnitOfWork(uow => new LibraryCleanHistoryStore().ListAsync(uow, new LibraryCleanHistoryFilter()));

        Assert.Equal([7L, null], rows.OrderByDescending(row => row.LibraryId).Select(row => row.LibraryId));
        Assert.Equal(["a.mkv", "b.mkv"], rows.Select(row => row.RelativePath).Order());
    }
}
