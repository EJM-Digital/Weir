using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What the pass's database access reads from the hand-off ledger.</summary>
public sealed class SqliteRemuxPassDataTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private SqliteRemuxPassData Data() =>
        new(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);

    [Theory]
    [InlineData(HandoffLedgerRules.Completed, true)]
    [InlineData(HandoffLedgerRules.PassedThrough, true)]
    [InlineData(HandoffLedgerRules.Working, false)]
    public async Task A_hand_off_is_acknowledged_once_it_was_completed_or_passed_through(string state, bool expected)
    {
        var library = await _fixture.Store.Scalar("SELECT id FROM libraries ORDER BY id LIMIT 1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        await _fixture.Store.Execute($"UPDATE media_manager_handoffs SET state = '{state}' WHERE handoff_id = 'h1'");

        Assert.Equal(expected, await Data().HandoffOutcomeAcknowledgedAsync(new HandoffOrigin("deluno", "h1", null, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("deluno", "never-seen")]
    [InlineData("deluno", "")]
    [InlineData("deluno", null)]
    public async Task A_hand_off_the_ledger_does_not_know_is_not_acknowledged(string source, string? handoffId)
    {
        Assert.False(await Data().HandoffOutcomeAcknowledgedAsync(new HandoffOrigin(source, handoffId, null, null), CancellationToken.None));
        Assert.False(await Data().HandoffOutcomeAcknowledgedAsync(null, CancellationToken.None));
    }
}
