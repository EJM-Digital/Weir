using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A hand-off an earlier version left waiting (queued, every job finished, its folder gone) gets a fresh pass when Weir starts, and ends
/// as gone with the manager told, exactly as a send of it would have. One that is final, or has work open, is left alone.
/// </summary>
public sealed partial class HeldFileReturnTests
{
    private StuckHandoffRepair Repair() => new(_fixture.Store.Database, _fixture.Ledger, _fixture.Intake, NullLogger<StuckHandoffRepair>.Instance);

    /// <summary>The state found on the rig: the hand-off is queued, every job it ever had is completed, nothing was reported, and its folder is gone.</summary>
    private async Task LeaveHandoffWaitingWithNoWorkAsync()
    {
        await CleanThenSendAgainAfterTheFolderHasGoneAsync();
        await _fixture.Store.Execute("DELETE FROM media_manager_handoff_targets");
        await _fixture.Store.Execute(
            "UPDATE media_manager_handoffs SET state = 'queued', outcome = NULL, reported_status = NULL, pending_report_json = NULL, output_files_json = NULL");
        await _fixture.Store.Execute(
            $"INSERT INTO media_manager_handoff_targets (handoff_row_id, relative_path) SELECT id, '{ReleaseName}' FROM media_manager_handoffs");
        await _fixture.Store.Execute("DELETE FROM jobs WHERE status IN ('pending', 'leased')");
        Assert.Equal(HandoffLedgerRules.Queued, (await HandoffStatusAsync("first")).State);
    }

    private async Task StartUpAndDrainAsync()
    {
        await Repair().RepairAsync(CancellationToken.None);
        await DrainAsync();
        Advance(GoneSources.LookAgainAfter + TimeSpan.FromMinutes(1));
        await DrainAsync();
        await FlushOwedReportsAsync();
    }

    private async Task<long> JobCountAsync() =>
        (await _fixture.Db(uow => uow.QueryAsync("SELECT count(*) FROM jobs", reader => reader.GetInt64(0)))).Single();

    [Fact]
    public async Task A_hand_off_left_queued_with_every_job_finished_ends_as_gone_with_the_manager_told_after_startup()
    {
        await LeaveHandoffWaitingWithNoWorkAsync();
        var toldBefore = ReportsFor("first").Count;

        await StartUpAndDrainAsync();

        Assert.Equal(HandoffLedgerRules.Failed, (await HandoffStatusAsync("first")).State);
        var report = Assert.Single(ReportsFor("first").Skip(toldBefore));
        Assert.Equal("failed", WireConvert.Str(report["status"]));
        Assert.Equal("source_gone", WireConvert.Str(report["failureClass"]));
    }

    [Fact]
    public async Task A_second_startup_with_the_fresh_pass_still_waiting_queues_nothing_more()
    {
        await LeaveHandoffWaitingWithNoWorkAsync();

        Assert.Equal(1, await Repair().RepairAsync(CancellationToken.None));
        var jobs = await JobCountAsync();
        Assert.Equal(0, await Repair().RepairAsync(CancellationToken.None));

        Assert.Equal(jobs, await JobCountAsync());
    }

    [Fact]
    public async Task A_hand_off_with_a_pass_still_waiting_is_left_alone()
    {
        await SetUpAsync(releaseFolder: true);
        await HandOffAsync("first");
        var jobs = await JobCountAsync();

        Assert.Equal(0, await Repair().RepairAsync(CancellationToken.None));

        Assert.Equal(jobs, await JobCountAsync());
    }

    [Fact]
    public async Task A_hand_off_that_has_ended_is_left_alone()
    {
        await SetUpAsync(releaseFolder: true);
        await HandOffAsync("first");
        await DrainAsync();
        Assert.Equal(HandoffLedgerRules.Completed, (await HandoffStatusAsync("first")).State);
        var jobs = await JobCountAsync();

        Assert.Equal(0, await Repair().RepairAsync(CancellationToken.None));

        Assert.Equal(jobs, await JobCountAsync());
        Assert.Equal(HandoffLedgerRules.Completed, (await HandoffStatusAsync("first")).State);
    }
}
