using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Whether the ledger already holds a hand-off's outcome as delivered, so a pass that runs again does not report it twice.</summary>
public sealed class HandoffOutcomeAcknowledgedTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private SqliteRemuxPassData Data() => new(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);

    [Fact]
    public async Task Only_a_hand_off_that_ended_as_completed_or_passed_through_is_acknowledged()
    {
        var library = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("movies"));
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        var origin = new HandoffOrigin("deluno", "h1", "/callback", null);

        foreach (var (state, acknowledged) in new[] { (HandoffLedgerRules.Completed, true), (HandoffLedgerRules.PassedThrough, true), (HandoffLedgerRules.Rejected, false) })
        {
            await _fixture.Store.Execute($"UPDATE media_manager_handoffs SET state = '{state}' WHERE handoff_id = 'h1'");

            Assert.Equal(acknowledged, await Data().HandoffOutcomeAcknowledgedAsync(origin, CancellationToken.None));
        }

        Assert.False(await Data().HandoffOutcomeAcknowledgedAsync(origin with { HandoffId = "never-received" }, CancellationToken.None));
        Assert.False(await Data().HandoffOutcomeAcknowledgedAsync(origin with { HandoffId = "" }, CancellationToken.None));
        Assert.False(await Data().HandoffOutcomeAcknowledgedAsync(null, CancellationToken.None));
    }
}
