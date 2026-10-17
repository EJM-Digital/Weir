using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What happens when a file cannot be processed: retries, handing the original back, giving up after exactly the workflow's
/// number of attempts, and rejecting a bad release through Deluno or a Radarr queue.
/// </summary>
[ContractArea("processing")]
public sealed class FailurePoliciesTests
{
    private const string RemuxCrash = "Conversion failed: the fake ffmpeg was told to fail";

    [Fact]
    public async Task Failure_is_retried_then_the_original_is_passed_through_unchanged()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library) = await scenario.DelunoSetupAsync(
            library: [("failure_policy", "pass_through"), ("max_attempts", 2), ("retry_backoff_seconds", 1)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxError = RemuxCrash });
        var original = FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]));
        var source = scenario.WriteRelease("Broken.Remux.2021", "film.mkv", original);
        const string rel = "Broken.Remux.2021/film.mkv";

        await scenario.PostHandoffAsync("handoff-pt-1", source);

        var first = await scenario.WaitForFileStatusAsync(library, rel, "processing_failed");
        Assert.Equal("execution", (string)first["failure_class"]!);
        Assert.Equal(1, (int)first["failure_attempts"]!);
        Assert.NotNull(first["next_retry_at"]);
        // A failure that will be retried is not final, so nothing is reported to the manager yet.
        Assert.Empty(Scenario.Callbacks(fake, "handoff-pt-1"));

        // Once the second attempt fails the retries are spent, and the original is handed back.
        await scenario.DriveRetriesUntilAsync(
            library,
            async () => (string)(await scenario.HandoffStatusAsync("handoff-pt-1"))["state"]! == "passed-through",
            "the hand-off to be passed through");

        var delivered = Path.Combine(scenario.Folders.Output, "Broken.Remux.2021", "film.mkv");
        Assert.Equal(original, await File.ReadAllBytesAsync(delivered));
        // The source is never deleted by a pass-through.
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(2, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.Equal(["completed"], (await scenario.JobsAsync(Scenario.PassThroughKind)).Select(job => (string)job["status"]!));
        // No failure report ever reaches the manager: the file was delivered, not lost.
        Assert.All(Scenario.Callbacks(fake, "handoff-pt-1"), report => Assert.NotEqual("failed", (string)report["status"]!));
        var row = await scenario.FileStateAfterStopAsync(library, rel);
        Assert.Equal("passed_through", row["status"]);
        Assert.Equal(2L, row["failure_attempts"]);
        Assert.Contains("handed the original back unchanged", (string)row["status_reason"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_that_allows_five_attempts_gives_up_on_the_fifth_and_the_handoff_reads_failed()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.DelunoSetupAsync(
            library: [("failure_policy", "hold"), ("max_attempts", 5), ("retry_backoff_seconds", 1)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxError = RemuxCrash });
        var source = scenario.WriteRelease("Always.Broken.2022", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        const string rel = "Always.Broken.2022/film.mkv";

        await scenario.PostHandoffAsync("handoff-held-1", source);
        var row = await scenario.FailureAttemptsReachAsync(library, rel, 5);

        Assert.Equal("processing_failed", (string)row["status"]!);
        Assert.Equal(5, (int)row["failure_attempts"]!);
        Assert.Null(row["next_retry_at"]);
        Assert.Contains("tried this file 5 times and stopped", (string)row["status_reason"]!, StringComparison.Ordinal);
        var status = await scenario.WaitForHandoffStateAsync("handoff-held-1", "failed");
        Assert.Null(status["outputPath"]);
        Assert.Empty(await scenario.JobsAsync(Scenario.PassThroughKind));
        Assert.True(File.Exists(source));
        Assert.Equal(5, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        // The fifth attempt's own job is still finishing when its failure is recorded; let it end first, then watch for a sixth.
        await scenario.WaitWhileWorkedOnAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).All(job => (string)job["status"]! is not ("pending" or "leased")),
            "the fifth attempt to finish");
        Assert.Equal(5, (await scenario.JobsAsync(Scenario.RemuxKind)).Count);
        // Given up means given up: no further attempt is queued once Weir has gone round its scans again.
        await scenario.WaitForARoundAsync();
        Assert.DoesNotContain(await scenario.JobsAsync(Scenario.RemuxKind), job => (string)job["status"]! is "pending" or "leased");
        Assert.Equal(5, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        var failed = await scenario.FileRowAsync(library, rel);
        Assert.NotNull(failed);
        Assert.Equal("processing_failed", (string)failed["status"]!);
    }

    [Fact]
    public async Task Content_rejection_under_reject_policy_reports_rejected_to_deluno_and_leaves_the_download_for_it_to_remove()
    {
        // This scenario never runs a scan before the hand-off. That such a rejection still gets a Files row
        // (#532) is asserted in RejectWithoutPriorScanTests.
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync(
            capabilities: ["processor-reject-regrab"], library: [("failure_policy", "reject")]);
        // A video with no audio at all: the release itself is bad. This is a *preflight* content rejection the fake
        // ffmpeg can simulate directly. An unreadable file needs real ffmpeg to classify (#494/#539 item 1) and is
        // covered in RealFfmpegClassifiesDamagedMediaTests.
        var source = scenario.WriteRelease("No.Audio.2023", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: [])));

        await scenario.PostHandoffAsync("handoff-reject-1", source);

        await scenario.WaitForHandoffStateAsync("handoff-reject-1", "rejected");
        var reports = Scenario.Callbacks(fake, "handoff-reject-1");
        var report = Assert.Single(reports);
        Assert.Equal("failed", (string)report["status"]!);
        Assert.Equal("rejected", (string)report["disposition"]!);
        // The workflow is linked to Deluno, so the download is Deluno's and its client's to remove, not Weir's.
        Assert.False((bool)report["sourceRemoved"]!);
        Assert.Equal("preflight", (string)report["failureClass"]!);
        Assert.Contains("no retainable audio", (string)report["message"]!, StringComparison.Ordinal);
        Assert.Contains(await scenario.JobsAsync(Scenario.RejectKind), job => (string)job["status"]! == "completed");
        Assert.True(File.Exists(source));
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        var rejected = Assert.Single(await scenario.ActivityAsync("processing.file_rejected"));
        Assert.Equal("film.mkv was rejected so a different release can be found", (string)rejected["title"]!);
        Assert.Contains("accepted that this release is bad", (string)rejected["detail"]!, StringComparison.Ordinal);
        Assert.Contains("will remove the download; Weir left it in place.", (string)rejected["detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_rejection_under_reject_policy_removes_and_blocklists_the_radarr_queue_item()
    {
        await using var scenario = await Scenario.StartAsync();
        var (radarr, library) = await scenario.RadarrSetupAsync(("failure_policy", "reject"));
        var source = scenario.WriteRelease(
            "Bad.Movie.2019.1080p", "Bad.Movie.2019.1080p.mkv", FakeMedia.Bytes(FakeMedia.Probe(video: 0, audioLanguages: ["eng"])));
        radarr.Queue.Add(ArrShapes.QueueRow(
            "radarr",
            ("id", 4242),
            ("downloadId", "SABnzbd_nzo_contract"),
            ("title", "Bad.Movie.2019.1080p"),
            ("status", "completed"),
            ("trackedDownloadState", "importPending"),
            ("outputPath", Path.GetDirectoryName(source)),
            ("movieId", 7)));

        await scenario.EnqueueFilePassAsync("Bad.Movie.2019.1080p/Bad.Movie.2019.1080p.mkv", library);

        var deleted = await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(radarr.RequestsTo("DELETE", "/api/v3/queue/4242") is [var first, ..] ? first : null),
            "the reject job to remove the release from Radarr's queue");
        Assert.Equal(
            new Dictionary<string, string[]> { ["removeFromClient"] = ["true"], ["blocklist"] = ["true"] }, deleted.Query);
        Assert.Equal(radarr.ApiKey, deleted.Header("X-Api-Key"));
        Assert.Equal(0, radarr.Queue.Count);
        await scenario.WaitWhileWorkedOnAsync(
            async () => (await scenario.JobsAsync(Scenario.RejectKind)).Any(job => (string)job["status"]! == "completed"),
            "the reject job to finish");
        // The download client removes the data; Weir deletes nothing itself on this route.
        Assert.True(File.Exists(source));
        var rejected = Assert.Single(await scenario.ActivityAsync("processing.file_rejected"));
        Assert.Contains("blocklisted the release", (string)rejected["detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rules_rejection_with_no_media_manager_is_a_rejected_file_the_remove_dialog_can_act_on()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"));
        var source = scenario.WriteRelease("No.Audio.2023", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: [])));
        const string rel = "No.Audio.2023/film.mkv";

        await scenario.EnqueueFilePassAsync(rel, library);

        var row = await scenario.WaitForFileStatusAsync(library, rel, "rejected");
        Assert.Equal(
            "Rejected: this file has no audio tracks, so there would be nothing to keep. The file was left where it is.",
            (string)row["status_reason"]!);
        Assert.True(File.Exists(source));
        Assert.Empty(await scenario.JobsAsync(Scenario.RejectKind));
        var options = await scenario.Admin.GetAsync($"{WeirClient.Api}/processing/files/{(int)row["id"]!}/remove-options");
        Assert.Equal(HttpStatusCode.OK, options.Status);
        Assert.True((bool)options.Fields["requires_choice"]!);
        Assert.False((bool)options.Fields["delete_handled_by_manager"]!);
    }
}
