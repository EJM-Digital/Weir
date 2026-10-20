using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A folder hand-off reports once, when the last of its files finishes, not once per file. #652, #667.
/// </summary>
public sealed class FolderHandoffCompletionTests
{
    private const string ReportPath = "/api/integrations/processors/events";

    private static MediaManagerImportEvent Handoff(string handoffId, string path, string scope) => new()
    {
        SourceKey = "deluno",
        EventKind = "handoff",
        MediaScope = scope,
        FilePath = path,
        HandoffId = handoffId,
        CallbackPath = ReportPath,
        LibraryId = "lib-1",
    };

    private static string RelativeOf(Core.Jobs.ProcessingJob job) =>
        ((WireString)((WireObject)WireJsonParser.Parse(job.PayloadJson!))["relative_media_path"]).Value;

    /// <summary>The copy Weir would write for a target: the local output folder plus the target's own path segments.</summary>
    private static string OutputFileFor(string localFolder, string relative) => Path.Join([localFolder, .. relative.Split('/')]);

    private static WireObject Delivered(string relative, string outputFile, string localFolder) => new WireObject()
        .Set("ok", true)
        .Set("outcome", "live_output_written")
        .Set("output_file", outputFile)
        .Set("relative_media_path", relative)
        .Set("processing_output_folder_resolved", localFolder);

    private static WireObject FinalFailure(string relative, string reason) => new WireObject()
        .Set("ok", false)
        .Set("outcome", "failed_execution")
        .Set("reason", reason)
        .Set("retry_scheduled", false)
        .Set("pass_through_queued", false)
        .Set("reject_queued", false)
        .Set("relative_media_path", relative);

    private static Task MarkDeliveredInWeirAsync(MediaManagerFixture fixture, long jobId, string relative) =>
        fixture.Store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {jobId}; " +
            $"UPDATE files SET status = 'processed' WHERE relative_path = '{relative}'");

    [Fact]
    public async Task A_tv_season_pack_with_a_trailing_separator_and_brackets_queues_one_pass_per_episode()
    {
        using var fixture = new MediaManagerFixture();
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Example Show 2010 Season 1 Complete 720p x264 [grp]";
        Directory.CreateDirectory(Path.Join(watched, folder));
        foreach (var episode in Enumerable.Range(1, 6))
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, $"Example Show - S01E0{episode} - Episode.mkv"), new string('x', episode));
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("h1", Path.Join(watched, folder) + Path.DirectorySeparatorChar, "tv")));

        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(6, jobs.Count);
        Assert.All(jobs, job => Assert.StartsWith($"processing.file.remux_pass.v1:deluno:handoff:h1:{folder}/Example Show - S01E0", job.DedupeKey));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
        Assert.Equal(6, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoff_targets"));
    }

    [Fact]
    public async Task A_folder_hand_off_stays_working_until_every_episode_finishes_then_reports_once_with_the_folder_and_every_file()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S01";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = Enumerable.Range(1, 6).Select(n => $"Show.S01E0{n}.mkv").ToList();
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("pack", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(6, jobs.Count);
        var row = (await fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "pack")))!;
        var localFolder = fixture.Store.Home.Join("Refined");

        foreach (var job in jobs.Take(5))
        {
            var relative = RelativeOf(job);
            var status = await fixture.Db(
                uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, job.PayloadJson, Delivered(relative, OutputFileFor(localFolder, relative), localFolder)),
                commit: false);
            Assert.StartsWith("skipped:", status, StringComparison.Ordinal);
            Assert.Contains("not finished yet", status, StringComparison.Ordinal);
            await MarkDeliveredInWeirAsync(fixture, job.Id, relative);
        }

        Assert.Empty(fixture.Http.Requests);
        Assert.Equal("queued", (await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row))).State);

        var last = jobs[5];
        var lastRelative = RelativeOf(last);
        var finalStatus = await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, last.PayloadJson, Delivered(lastRelative, OutputFileFor(localFolder, lastRelative), localFolder)),
            commit: false);
        await MarkDeliveredInWeirAsync(fixture, last.Id, lastRelative);

        Assert.Equal("reported completed to Deluno on 192.0.2.30", finalStatus);
        var post = Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal(Path.Join(localFolder, folder), ((WireString)body["outputPath"]).Value);
        var outputFiles = ((WireArray)body["outputFiles"]).Items.Cast<WireString>().Select(item => item.Value).Order().ToList();
        Assert.Equal(episodes.Select(name => Path.Join(localFolder, folder, name)).Order().ToList(), outputFiles);

        var finished = (await fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "pack")))!;
        var finishedStatus = await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, finished));
        Assert.Equal("completed", finishedStatus.State);
        Assert.Equal(outputFiles.Count, finishedStatus.OutputFiles?.Count);
    }

    [Fact]
    public async Task A_folder_hand_off_with_one_failed_episode_is_reported_failed_and_names_it()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S02";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = new[] { "Show.S02E01.mkv", "Show.S02E02.mkv" };
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("partial", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(2, jobs.Count);
        var localFolder = fixture.Store.Home.Join("Refined");

        var okJob = jobs.Single(job => RelativeOf(job).EndsWith("E01.mkv", StringComparison.Ordinal));
        var failedJob = jobs.Single(job => RelativeOf(job).EndsWith("E02.mkv", StringComparison.Ordinal));
        var okRelative = RelativeOf(okJob);
        var failedRelative = RelativeOf(failedJob);

        await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, okJob.PayloadJson, Delivered(okRelative, OutputFileFor(localFolder, okRelative), localFolder)),
            commit: false);
        await MarkDeliveredInWeirAsync(fixture, okJob.Id, okRelative);

        var finalStatus = await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, failedJob.PayloadJson, FinalFailure(failedRelative, "ffmpeg died")), commit: false);

        Assert.Equal("reported failed to Deluno on 192.0.2.30", finalStatus);
        var post = Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("failed", ((WireString)body["status"]).Value);
        Assert.False(body.ContainsKey("outputPath"));
        Assert.Equal([OutputFileFor(localFolder, okRelative)], ((WireArray)body["outputFiles"]).Items.Cast<WireString>().Select(item => item.Value));
        Assert.Contains("Show.S02E02.mkv", ((WireString)body["message"]).Value, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'partial' AND state = 'failed'"));
    }

    [Fact]
    public async Task Two_episodes_finishing_at_once_report_the_pack_exactly_once()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S03";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = new[] { "Show.S03E01.mkv", "Show.S03E02.mkv" };
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("race", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(2, jobs.Count);
        var localFolder = fixture.Store.Home.Join("Refined");

        var results = await Task.WhenAll(jobs.Select(job =>
        {
            var relative = RelativeOf(job);
            return fixture.Db(
                uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, job.PayloadJson, Delivered(relative, OutputFileFor(localFolder, relative), localFolder)),
                commit: false);
        }));

        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        Assert.Single(results, status => status == "reported completed to Deluno on 192.0.2.30");
        Assert.Single(results, status => status.StartsWith("skipped:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retried_completion_event_for_an_already_reported_pack_is_not_resent()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S04";
        Directory.CreateDirectory(Path.Join(watched, folder));
        await File.WriteAllTextAsync(Path.Join(watched, folder, "Show.S04E01.mkv"), "x");
        await File.WriteAllTextAsync(Path.Join(watched, folder, "Show.S04E02.mkv"), "x");

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("dup", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        var localFolder = fixture.Store.Home.Join("Refined");

        foreach (var job in jobs)
        {
            var relative = RelativeOf(job);
            await fixture.Db(
                uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, job.PayloadJson, Delivered(relative, OutputFileFor(localFolder, relative), localFolder)),
                commit: false);
            await MarkDeliveredInWeirAsync(fixture, job.Id, relative);
        }

        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));

        // A duplicate delivery of the last file's completion event (a retried webhook, say) reports nothing more.
        var last = jobs[^1];
        var repeated = await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, last.PayloadJson, Delivered(RelativeOf(last), OutputFileFor(localFolder, RelativeOf(last)), localFolder)),
            commit: false);

        Assert.Equal("skipped: another pass already reported this hand-off", repeated);
        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
    }

    /// <summary>
    /// An episode cancelled in Weir before its pass ran still counts as settled for the pack: the rest finishing does
    /// not wait on it forever, and the report names only what was actually delivered.
    /// </summary>
    [Fact]
    public async Task An_episode_cancelled_in_weir_does_not_block_the_rest_of_the_pack_from_reporting()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S06";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = new[] { "Show.S06E01.mkv", "Show.S06E02.mkv" };
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("cancel-one", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(2, jobs.Count);
        var localFolder = fixture.Store.Home.Join("Refined");

        var cancelledJob = jobs[0];
        var goingOnJob = jobs[1];
        await fixture.Db(uow => fixture.Cancellation.CancelAsync(uow, cancelledJob.Id));

        var relative = RelativeOf(goingOnJob);
        var finalStatus = await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, goingOnJob.PayloadJson, Delivered(relative, OutputFileFor(localFolder, relative), localFolder)),
            commit: false);

        Assert.Equal("reported completed to Deluno on 192.0.2.30", finalStatus);
        var post = Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal([OutputFileFor(localFolder, relative)], ((WireArray)body["outputFiles"]).Items.Cast<WireString>().Select(item => item.Value));
    }

    /// <summary>
    /// The claim that makes a caller responsible for a hand-off's report, and the report itself, are persisted
    /// together before delivery is even attempted: an exception while sending it (standing in for a crash right
    /// there) still leaves the report durably owed, so a later retry — the heartbeat's normal path — delivers it
    /// exactly once, never zero times and never twice.
    /// </summary>
    [Fact]
    public async Task An_exception_between_the_claim_and_delivery_still_delivers_the_report_exactly_once_on_retry()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Throw(HttpMethod.Post, ReportPath, new NotSupportedException("simulated crash during delivery"));
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("movies");
        Directory.CreateDirectory(Path.Join(watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(watched, "Film", "film.mkv"), "x");
        await fixture.LibraryAsync("movie", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("crash", Path.Join(watched, "Film", "film.mkv"), "movie")));
        var job = Assert.Single(await fixture.Jobs.ListAsync());
        var localFolder = fixture.Store.Home.Join("Refined");

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, job.PayloadJson, Delivered("Film/film.mkv", OutputFileFor(localFolder, "Film/film.mkv"), localFolder)),
            commit: false));

        // Durably owed, even though nothing was ever successfully sent.
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'crash' AND pending_report_json IS NOT NULL"));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'crash' AND state = 'completed'"));
        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_reported'"));

        // The manager starts answering; the heartbeat sends the durably-owed report exactly once.
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        var answered = await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"));

        Assert.Equal(1, answered);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'crash' AND pending_report_json IS NULL"));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_reported'"));
    }

    /// <summary>The finish-then-cancel-last case: E1 finishes normally, then cancelling E2 (the pack's last file) reports E1 alone.</summary>
    [Fact]
    public async Task Finishing_one_episode_then_cancelling_the_packs_last_file_reports_the_one_that_finished()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S07";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = new[] { "Show.S07E01.mkv", "Show.S07E02.mkv" };
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("finish-then-cancel", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(2, jobs.Count);
        var localFolder = fixture.Store.Home.Join("Refined");

        var finishingJob = jobs[0];
        var cancelledJob = jobs[1];
        var finishingRelative = RelativeOf(finishingJob);
        var waitingStatus = await fixture.Db(
            uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, finishingJob.PayloadJson, Delivered(finishingRelative, OutputFileFor(localFolder, finishingRelative), localFolder)),
            commit: false);
        Assert.StartsWith("skipped:", waitingStatus, StringComparison.Ordinal);
        await MarkDeliveredInWeirAsync(fixture, finishingJob.Id, finishingRelative);
        Assert.Empty(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));

        await fixture.Db(uow => fixture.Cancellation.CancelAsync(uow, cancelledJob.Id));

        // Nothing is delivered synchronously from a cancellation; the heartbeat sends the report it now owes.
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'finish-then-cancel' AND pending_report_json IS NOT NULL"));
        var answered = await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"));
        Assert.Equal(1, answered);

        var post = Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal([OutputFileFor(localFolder, finishingRelative)], ((WireArray)body["outputFiles"]).Items.Cast<WireString>().Select(item => item.Value));
    }

    /// <summary>Cancelling every one of a pack's files delivers nothing to the manager: the whole hand-off just ends cancelled.</summary>
    [Fact]
    public async Task Cancelling_every_file_in_a_pack_sends_no_report()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Http.Json(HttpMethod.Post, ReportPath, "{}", HttpStatusCode.Accepted);
        fixture.DelunoListsNoLibraries();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("tv");
        const string folder = "Show.S08";
        Directory.CreateDirectory(Path.Join(watched, folder));
        var episodes = new[] { "Show.S08E01.mkv", "Show.S08E02.mkv" };
        foreach (var name in episodes)
        {
            await File.WriteAllTextAsync(Path.Join(watched, folder, name), "x");
        }

        await fixture.LibraryAsync("tv", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("cancel-all", Path.Join(watched, folder), "tv")));
        var jobs = await fixture.Jobs.ListAsync();
        Assert.Equal(2, jobs.Count);

        foreach (var job in jobs)
        {
            await fixture.Db(uow => fixture.Cancellation.CancelAsync(uow, job.Id));
        }

        var answered = await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"));
        Assert.Equal(0, answered);
        Assert.Empty(fixture.Http.RequestsTo(HttpMethod.Post, ReportPath));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'cancel-all' AND state = 'cancelled'"));
    }
}
