using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// A workflow a Deluno feeds is processed only from Deluno's hand-off. In the world of Deluno's recordings, with the folder watcher and
/// the periodic scan switched on and a download Deluno has not handed over lying in the watched folder, every other way in is closed:
/// saving the workflow, the watcher, the timer, "scan now" and "process again" queue and process nothing, and the refusals read in
/// plain words. Then Deluno's recorded hand-off is processed, and neither download is ever removed or changed.
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoOnlyWorkflowTests
{
    private const string NotHandedOver = @"C:\Downloads\Completed\Movies\Gprecnothanded.2020.1080p.WEB-DL.x264-GOLDEN.mkv";
    private const string Refusal = "Deluno hands this workflow its downloads, so Weir does not scan its watched folder.";
    private const string NotHandedOverPath = "Gprecnothanded.2020.1080p.WEB-DL.x264-GOLDEN.mkv";

    private static readonly (string Name, string Value)[] ScansEverywhere =
    [
        ("WEIR_PROCESSING_WATCHER_ENABLED", "1"),
        ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", "1"),
    ];

    [Fact]
    public async Task Saving_the_watcher_the_timer_scan_now_and_process_again_all_leave_a_download_Deluno_has_not_handed_over_alone_and_the_hand_off_is_processed()
    {
        await using var world = await DelunoSide.StartWorldAsync(ScansEverywhere);
        var unhanded = Untouched.Of(world.PutFilm(NotHandedOver, needsWork: true));

        // Saving moves the watched folder's scan interval to the shortest a workflow allows, which on a Weir-only workflow starts the timer.
        var saved = await world.Scenario.UpdateLibraryAsync(world.Movies, ("scan_interval_seconds", 10));
        Assert.Equal("off", (string)saved["periodic_scan"]!);

        // "Scan now" is refused in words.
        var scanNow = await world.Scenario.Admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
            new JsonObject { ["media_scope"] = "movie", ["library_id"] = (int)saved["id"]!, ["enqueue_remux_jobs"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, scanNow.Status);
        Assert.Contains(Refusal, scanNow.Text, StringComparison.Ordinal);

        // "Process again" on a file kept by choice clears the choice and queues nothing, and says Deluno decides when.
        await world.Scenario.ExecuteAfterStopAsync(
            "INSERT INTO file_skip_markers (library_id, relative_path, size_bytes, mtime_ns) VALUES ($library, $path, 1, 1)",
            ("$library", (int)saved["id"]!),
            ("$path", NotHandedOverPath));
        var kept = Assert.Single((await world.Scenario.Admin.GetAsync($"{WeirClient.Api}/processing/kept-files")).Fields["files"]!.AsArray());
        var again = await world.Scenario.Admin.PostWithCsrfAsync($"{WeirClient.Api}/processing/kept-files/{(int)kept!["id"]!}/process-again");
        Assert.True(again.Status == HttpStatusCode.OK, again.ToString());
        Assert.Equal($"{Refusal} Weir processes this file when Deluno hands it over.", (string)again.Fields["detail"]!);

        // Weir goes round its periodic scans and its folder watcher, and finds nothing to do with a file Deluno has not handed over.
        await world.Scenario.WaitForARoundAsync();
        Assert.Empty(await world.Scenario.ScansOfAsync(world.Movies));
        Assert.Empty(await world.Scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Empty(await world.Scenario.FinishedJobsAsync(Scenario.RemuxKind));
        Assert.Null(await world.Scenario.FileRowAsync(world.Movies, NotHandedOverPath));
        Assert.Empty(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Empty(Directory.GetFileSystemEntries(world.Translation.Apply(RecordedWorld.RecordedMoviesOutput)));
        unhanded.AssertStill();

        // Deluno's own recorded hand-off is the way in.
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Exchanges[0].Request;
        var handed = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));
        await world.Replay.SendAsync(recorded, 0);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");

        Assert.Single(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Null(await world.Scenario.FileRowAsync(world.Movies, NotHandedOverPath));
        unhanded.AssertStill();
        handed.AssertStill();
    }
}
