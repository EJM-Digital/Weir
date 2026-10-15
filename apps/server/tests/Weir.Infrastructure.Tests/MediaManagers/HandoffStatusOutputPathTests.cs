using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// When a hand-off's status names the cleaned copy: every completed or passed-through hand-off whose report named one, which since a
/// resend is answered with the copy Weir already wrote (#956) is every one Weir finished; never a hand-off that did not deliver a file,
/// and not one finished by a release that did not keep the path.
/// </summary>
public sealed class HandoffStatusOutputPathTests
{
    private const string Output = "/refined/Film/film.mkv";

    private static async Task<HandoffStatus> StatusAfterAsync(string state, string? outputPath, IReadOnlyList<string>? outputFiles)
    {
        using var fixture = new MediaManagerFixture();
        await fixture.Db(async uow =>
        {
            await fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId: null, "Film/film.mkv");
            await fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h1", state, outputPath, "Done.", outputFiles);
            return 0;
        });
        var row = (await fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h1"), commit: false))!;
        return await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row), commit: false);
    }

    [Theory]
    [InlineData(HandoffLedgerRules.Completed)]
    [InlineData(HandoffLedgerRules.PassedThrough)]
    public async Task A_hand_off_Weir_delivered_names_the_copy_it_reported(string state)
    {
        var status = await StatusAfterAsync(state, Output, [Output]);

        Assert.Equal(state, status.State);
        Assert.Equal(Output, status.OutputPath);
        Assert.Equal([Output], status.OutputFiles);
    }

    [Fact]
    public async Task A_hand_off_finished_before_the_path_was_kept_says_completed_with_no_path()
    {
        var status = await StatusAfterAsync(HandoffLedgerRules.Completed, outputPath: null, outputFiles: null);

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
        Assert.Null(status.OutputPath);
        Assert.Null(status.OutputFiles);
    }

    [Theory]
    [InlineData(HandoffLedgerRules.Failed)]
    [InlineData(HandoffLedgerRules.Rejected)]
    public async Task A_hand_off_that_delivered_no_file_names_no_path_whatever_its_row_holds(string state)
    {
        var status = await StatusAfterAsync(state, Output, [Output]);

        Assert.Equal(state, status.State);
        Assert.Null(status.OutputPath);
        Assert.Null(status.OutputFiles);
    }
}
