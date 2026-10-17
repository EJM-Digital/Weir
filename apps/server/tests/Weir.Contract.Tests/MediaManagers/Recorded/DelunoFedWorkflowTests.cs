using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// A workflow Deluno feeds, in Deluno's recorded world, is never scanned and never has the download it was handed removed. A scan job that runs anyway (one queued before the workflow was
/// linked to Deluno, say) stops at once and queues nothing, and a hand-off that fails retries itself, without any scan, until it works,
/// each failure adding to the count; Deluno's recorded hand-off is what is processed.
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoFedWorkflowTests
{
    private const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";
    private const string NotHandedOver = @"C:\Downloads\Completed\Movies\Gprecnothanded.2020.1080p.WEB-DL.x264-GOLDEN.mkv";
    private const string NotHandedOverPath = "Gprecnothanded.2020.1080p.WEB-DL.x264-GOLDEN.mkv";
    private const string Crash = "Conversion failed: the fake ffmpeg was told to fail";

    [Fact]
    public async Task A_scan_job_that_runs_anyway_queues_nothing_and_leaves_a_download_Deluno_has_not_handed_over_alone()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var unhanded = Untouched.Of(world.PutFilm(NotHandedOver, needsWork: true));
        var payload = new JsonObject
        {
            ["library_id"] = (int)world.Movies["id"]!,
            ["media_scope"] = "movie",
            ["scan_trigger"] = "periodic",
            ["enqueue_remux_jobs"] = true,
        };

        await world.Scenario.ExecuteAfterStopAsync(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, created_at, updated_at) VALUES ($key, $kind, $payload, 'pending', $now, $now)",
            ("$key", "scan-queued-before-the-link"), ("$kind", ScanKind), ("$payload", payload.ToJsonString()), ("$now", SeedSql.UtcText(DateTime.UtcNow)));
        var finished = await new JobWait().UntilAsync(
            world.Scenario.JobStatusesAsync,
            async () => (await world.Scenario.FinishedJobsAsync(ScanKind)).FirstOrDefault(),
            "the scan job to finish");

        Assert.Equal("completed", (string)finished["status"]!);
        Assert.Empty(await world.Scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Null(await world.Scenario.FileRowAsync(world.Movies, NotHandedOverPath));
        Assert.Empty(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        unhanded.AssertStill();
    }

    [Fact]
    public async Task A_hand_off_that_fails_retries_itself_without_any_scan_and_is_finished_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("failure_policy", "hold"), ("max_attempts", 5), ("retry_backoff_seconds", 1));
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Only.Request;
        var handed = world.PutFilm(hand.SourcePath(), needsWork: true);
        var original = Untouched.Of(handed);
        world.Scenario.FakeTools.SetFileRule("*.mkv", new FileRule { RemuxError = Crash, RemuxFailTimes = 2 });

        await world.Replay.SendAsync(recorded);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");

        Assert.Equal(3, world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.Empty(await world.Scenario.JobsAsync(ScanKind));
        Assert.Empty(await world.Scenario.FinishedJobsAsync(ScanKind));
        original.AssertStill();
    }

    [Fact]
    public async Task A_workflow_asked_to_remove_originals_keeps_the_download_Deluno_handed_over_and_says_it_is_linked()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("remove_original_after_success", true));
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Only.Request;
        var handed = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));
        var unhanded = Untouched.Of(world.PutFilm(NotHandedOver, needsWork: true));

        await world.Replay.SendAsync(recorded);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");

        Assert.Single(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        handed.AssertStill();
        unhanded.AssertStill();
        var completed = Assert.Single(await world.Scenario.ActivityAsync("processing.file_remux_pass_completed"));
        Assert.Contains("This workflow is linked to", completed.ToJsonString(), StringComparison.Ordinal);
    }
}
