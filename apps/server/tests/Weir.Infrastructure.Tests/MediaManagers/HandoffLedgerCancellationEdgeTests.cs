using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Dropping a hand-off that has not started when part of it is already over, and when it names no workflow.</summary>
public sealed class HandoffLedgerCancellationEdgeTests : IDisposable
{
    private const string Relative = "Film/Film.mkv";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string Payload(string relative) =>
        WireJsonWriter.Dumps(new WireObject().Set("relative_media_path", relative), WireJsonFormat.Compact);

    [Fact]
    public async Task A_hand_off_with_no_workflow_has_no_files_to_cancel()
    {
        await _fixture.Db(uow => _fixture.Ledger.RecordReceivedAsync(uow, "radarr", "h-1", libraryId: null, Relative));
        var waiting = await _fixture.Jobs.EnqueueOrGetAsync(IntakeRules.RemuxDedupeKey("radarr", "h-1"), IntakeRules.RemuxPassJobKind, Payload(Relative));
        var row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "radarr", "h-1")))!;

        var (cancelled, sentence) = await _fixture.Db(uow => _fixture.Ledger.CancelAsync(uow, _fixture.Jobs, row));

        Assert.True(cancelled, sentence);
        Assert.Equal("cancelled", await _fixture.Store.ScalarText($"SELECT status FROM jobs WHERE id = {waiting.Id}"));
    }

    [Fact]
    public async Task Only_the_passes_still_waiting_are_dropped_and_a_failure_left_by_an_earlier_hand_off_is_not_touched()
    {
        var libraryId = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("movies"));
        await _fixture.Db(uow => _fixture.Ledger.RecordReceivedAsync(uow, "radarr", "h-1", libraryId, Relative));
        var waiting = await _fixture.Jobs.EnqueueOrGetAsync(IntakeRules.RemuxDedupeKey("radarr", "h-1"), IntakeRules.RemuxPassJobKind, Payload(Relative));
        var earlier = await _fixture.Jobs.EnqueueOrGetAsync($"{IntakeRules.PassThroughJobKind}:{libraryId}:{Relative}", IntakeRules.PassThroughJobKind, Payload(Relative));
        await _fixture.Store.Execute($"UPDATE jobs SET status = 'failed', created_at = '2020-01-01 00:00:00.000000' WHERE id = {earlier.Id}");
        var row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "radarr", "h-1")))!;

        var (cancelled, sentence) = await _fixture.Db(uow => _fixture.Ledger.CancelAsync(uow, _fixture.Jobs, row));

        Assert.True(cancelled, sentence);
        Assert.Equal("cancelled", await _fixture.Store.ScalarText($"SELECT status FROM jobs WHERE id = {waiting.Id}"));
        Assert.Equal("failed", await _fixture.Store.ScalarText($"SELECT status FROM jobs WHERE id = {earlier.Id}"));
    }
}
