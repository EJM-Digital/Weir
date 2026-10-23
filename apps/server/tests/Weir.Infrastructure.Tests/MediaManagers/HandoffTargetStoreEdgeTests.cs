using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Which copies a hand-off's report named, when a path the report carries is not one the system can resolve.</summary>
public sealed class HandoffTargetStoreEdgeTests : IDisposable
{
    private const string Relative = "Film/Film.mkv";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static HandbackRow Copy(string outputPath) => new(
        1, 1, Relative, outputPath, 4, 1, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, "/watched", "/out", "movie");

    private async Task<HandoffLedgerRow> ReportedHandoffAsync(string reportedOutputFile)
    {
        var libraryId = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("movies"));
        var rowId = await _fixture.Db(async uow =>
        {
            var id = await _fixture.Ledger.RecordReceivedAsync(uow, "radarr", "h-1", libraryId, Relative);
            await _fixture.Targets.AddAsync(uow, id, [Relative]);
            return id;
        });
        await _fixture.Store.Execute($"UPDATE media_manager_handoffs SET reported_status = '{HandoffLedgerRules.Completed}' WHERE id = {rowId}");
        await _fixture.Db(uow => uow.ExecuteAsync(
            "UPDATE media_manager_handoff_targets SET result = $result, output_file = $file WHERE handoff_row_id = $row",
            ("$result", HandoffLedgerRules.Completed),
            ("$file", reportedOutputFile),
            ("$row", rowId)));
        return (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "radarr", "h-1")))!;
    }

    [Fact]
    public async Task A_reported_copy_is_the_one_whose_path_the_report_named()
    {
        var path = _fixture.Store.Home.Join("out", "Film.mkv");
        var row = await ReportedHandoffAsync(path);

        var named = await _fixture.Db(uow => _fixture.Targets.ReportedCopiesAsync(uow, row));

        Assert.True(named(Copy(path)));
        Assert.False(named(Copy(_fixture.Store.Home.Join("out", "Other.mkv"))));
    }

    [Fact]
    public async Task A_path_the_system_cannot_resolve_names_no_copy()
    {
        var row = await ReportedHandoffAsync("/out/Film\0.mkv");

        var named = await _fixture.Db(uow => _fixture.Targets.ReportedCopiesAsync(uow, row));

        Assert.False(named(Copy(_fixture.Store.Home.Join("out", "Film.mkv"))));
    }
}
