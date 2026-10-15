using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// Pause means pause. While processing is paused nothing changes a media file: no pass starts, nothing is cleaned, removed or
/// written to an output folder. "Keep looking for new files while paused" lets Weir look (scan, record, queue) and nothing more.
/// A hand-off received while paused is accepted and waits. Work already running may finish. When the pause ends, what waited runs
/// once.
/// </summary>
[ContractArea("processing")]
public sealed class PauseTests
{
    // The folder watcher and the periodic scan both run, as on an installed Weir, so "keep looking" really is looking.
    private static readonly (string Name, string Value)[] ScansEverywhere =
    [
        ("WEIR_PROCESSING_WATCHER_ENABLED", "1"),
        ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", "1"),
    ];

    private static readonly TimeSpan LongEnoughForScans = TimeSpan.FromSeconds(14);

    private static (string Video, string Nfo, string Release) WriteDownload(Scenario scenario, string title)
    {
        var video = scenario.WriteRelease(title, "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var release = Path.GetDirectoryName(video)!;
        var nfo = Path.Combine(release, "film.nfo");
        File.WriteAllText(nfo, "metadata");
        return (video, nfo, release);
    }

    private static void AssertNothingProcessed(Scenario scenario, (string Video, string Nfo, string Release) download)
    {
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Empty(Directory.GetFileSystemEntries(scenario.Folders.Output));
        Assert.True(File.Exists(download.Video), "the original must stay while processing is paused");
        Assert.True(File.Exists(download.Nfo), "its .nfo must stay while processing is paused");
    }

    [Fact]
    public async Task Looking_for_new_files_while_paused_never_processes_or_removes_one()
    {
        await using var scenario = await Scenario.StartAsync(ScansEverywhere);
        // Paused before the workflow and the download exist. A workflow is scanned within a second of being saved, and the watcher scans a
        // new file after its burst delay; a scan that read the pause setting before it was set queues the file's pass, which is Weir
        // finishing work it had begun, not the looking this test judges.
        await scenario.SetPauseAsync(paused: true, keepLooking: true);
        var library = await scenario.CreateLibraryAsync(("remove_original_after_success", true), ("scan_interval_seconds", 10));
        var download = WriteDownload(scenario, "Paused.Weir.Only.2024");

        var scan = await scenario.EnqueueScanAsync(library);
        await scenario.WaitForJobFinishedAsync(scan);
        var waiting = await scenario.WaitForFileStatusAsync(library, "Paused.Weir.Only.2024/film.mkv", "out_of_schedule");
        Assert.StartsWith("Processing is paused", (string)waiting["status_reason"]!, StringComparison.Ordinal);

        // Long enough for the watcher's burst delay and a periodic tick of this workflow's 10 second interval.
        await Scenario.NeverWithinAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).Count > 0 || !File.Exists(download.Video) || !File.Exists(download.Nfo),
            LongEnoughForScans,
            "Weir queueing, cleaning or removing a file while processing is paused");
        AssertNothingProcessed(scenario, download);

        await scenario.SetPauseAsync(paused: false);

        var output = Path.Combine(scenario.Folders.Output, "Paused.Weir.Only.2024", "film.mkv");
        // Resuming queues nothing: the workflow's next periodic scan takes the waiting file up, so until then that scan is work Weir owes.
        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(File.Exists(output) && !Directory.Exists(download.Release)),
            "the waiting file to be cleaned and its original removed once processing resumed",
            async () => (string?)(await scenario.FileRowAsync(library, "Paused.Weir.Only.2024/film.mkv"))?["status"] == "out_of_schedule");
        await scenario.WaitForFileStatusAsync(library, "Paused.Weir.Only.2024/film.mkv", "processed");
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
    }

    [Fact]
    public async Task A_hand_off_received_while_paused_waits_and_runs_once_after_the_pause()
    {
        await using var scenario = await Scenario.StartAsync(ScansEverywhere);
        var download = WriteDownload(scenario, "Paused.Handoff.2024");
        var (fake, library) = await scenario.DelunoSetupAsync(library: [("remove_original_after_success", true), ("scan_interval_seconds", 10)]);
        await scenario.SetPauseAsync(paused: true, keepLooking: true);

        await scenario.PostHandoffAsync("handoff-paused-2", download.Video, "Paused.Handoff.2024");

        // Accepted and queued, and a scan of a workflow Deluno feeds is refused: nothing but the hand-off can start it.
        Assert.Equal("queued", (string)(await scenario.HandoffStatusAsync("handoff-paused-2"))["state"]!);
        var refused = await scenario.Admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
            new JsonObject { ["media_scope"] = "movie", ["library_id"] = (int)library["id"]!, ["enqueue_remux_jobs"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        await Scenario.NeverWithinAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).Any(job => (string)job["status"]! != "pending")
                || !File.Exists(download.Video)
                || (await scenario.HandoffStatusAsync("handoff-paused-2"))["state"]!.ToString() != "queued",
            LongEnoughForScans,
            "a hand-off being worked, or its original removed, while processing is paused");
        AssertNothingProcessed(scenario, download);
        Assert.Empty(Scenario.Callbacks(fake, "handoff-paused-2"));

        await scenario.SetPauseAsync(paused: false);
        await scenario.WaitForHandoffStateAsync("handoff-paused-2", "completed");

        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(Scenario.Callbacks(fake, "handoff-paused-2"));
        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Paused.Handoff.2024", "film.mkv")));
        Assert.True(File.Exists(download.Video), "a workflow Deluno feeds never has its original removed");
    }

    [Fact]
    public async Task Pausing_and_resuming_are_in_activity()
    {
        await using var scenario = await Scenario.StartAsync();

        await scenario.SetPauseAsync(paused: true);
        await scenario.SetPauseAsync(paused: false);

        var paused = Assert.Single(await scenario.ActivityAsync("system.processing_paused"));
        Assert.Contains("paused processing until you resume it", paused.ToJsonString(), StringComparison.Ordinal);
        Assert.Single(await scenario.ActivityAsync("system.processing_resumed"));
    }
}
