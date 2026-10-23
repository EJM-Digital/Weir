using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>What the ledger does when it is told the same thing again: a finished hand-off sent once more, and an outcome that has not changed.</summary>
public sealed class HandoffLedgerRepeatTests : IDisposable
{
    private const string Relative = "Film (2024)/Film (2024).mkv";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<HandoffLedgerRow?> RowAsync() => _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h1"));

    [Fact]
    public async Task A_finished_hand_off_sent_again_with_a_download_id_starts_over_and_keeps_that_id()
    {
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId: null, Relative);
            await _fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h1", HandoffLedgerRules.Completed, "/out/film.mkv", "Done.", null);
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId: null, Relative, downloadId: " dl-9 ");
            return 0;
        });

        var row = (await RowAsync())!;

        Assert.Equal((HandoffLedgerRules.Queued, "dl-9", null), (row.State, row.DownloadId, row.OutputPath));
    }

    [Fact]
    public async Task An_outcome_told_again_unchanged_leaves_the_row_as_it_was()
    {
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId: null, Relative);
            await _fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h1", HandoffLedgerRules.Completed, "/out/film.mkv", "Done.", ["/out/film.mkv"]);
            return 0;
        });
        var first = (await RowAsync())!;
        _fixture.Store.Clock.Advance(TimeSpan.FromHours(1));

        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h1", HandoffLedgerRules.Completed, "/out/film.mkv", "Done.", ["/out/film.mkv"]);
            return 0;
        });

        var again = (await RowAsync())!;
        Assert.Equal(first.LastChangedAt, again.LastChangedAt);
        Assert.Equal(["/out/film.mkv"], again.OutputFiles);
    }
}
