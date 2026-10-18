using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What belongs to a download client and a media manager is not Weir's to remove. A workflow linked to a manager never has its
/// original, its sidecars or its release folder removed, whatever its remove-original setting says. A workflow a linked Deluno feeds is
/// processed only from Deluno's hand-off: saving it, the folder watcher, the timer and "scan now" queue nothing for it. A Weir-only
/// workflow keeps removing what it cleaned.
/// </summary>
[ContractArea("processing")]
public sealed class LinkedOriginalsTests
{
    private static readonly (string Name, string Value)[] ScansEverywhere =
    [
        ("WEIR_PROCESSING_WATCHER_ENABLED", "1"),
        ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", "1"),
    ];

    private static (string Video, string Nfo, string Txt, string Release) WriteDownload(Scenario scenario, string title)
    {
        var video = scenario.WriteRelease(title, "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var release = Path.GetDirectoryName(video)!;
        var nfo = Path.Combine(release, "film.nfo");
        var txt = Path.Combine(release, "readme.txt");
        File.WriteAllText(nfo, "metadata");
        File.WriteAllText(txt, "notes");
        return (video, nfo, txt, release);
    }

    private static void AssertDownloadIntact((string Video, string Nfo, string Txt, string Release) download)
    {
        Assert.True(File.Exists(download.Video), "the video must stay where the download client put it");
        Assert.True(File.Exists(download.Nfo), "its .nfo must stay");
        Assert.True(File.Exists(download.Txt), "its .txt must stay");
        Assert.True(Directory.Exists(download.Release), "its release folder must stay");
    }

    [Fact]
    public async Task A_deluno_workflow_is_only_processed_from_the_hand_off_and_nothing_is_removed()
    {
        await using var scenario = await Scenario.StartAsync(ScansEverywhere);
        var download = WriteDownload(scenario, "Seeding.Film.2024");
        var elsewhere = Directory.CreateDirectory(Path.Combine(scenario.Root, "elsewhere")).FullName;
        var (fake, library) = await scenario.DelunoSetupAsync(
            library: [("watched_folder", elsewhere), ("remove_original_after_success", true), ("scan_interval_seconds", 10)]);

        // Saving changes the watched folder to the one holding the download, which is what woke the folder watcher on a real install.
        library = await scenario.UpdateLibraryAsync(library, ("watched_folder", scenario.Folders.Watched));
        Assert.Equal("off", (string)library["periodic_scan"]!);

        var refused = await scenario.Admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
            new JsonObject { ["media_scope"] = "movie", ["library_id"] = (int)library["id"]!, ["enqueue_remux_jobs"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("hands this workflow its downloads, so Weir does not scan its watched folder", refused.Text, StringComparison.Ordinal);

        // Weir goes round its periodic scans and its folder watcher, and finds nothing to do with a file Deluno has not handed over.
        await scenario.WaitForARoundAsync();
        Assert.Empty(await scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Null(await scenario.FileRowAsync(library, "Seeding.Film.2024/film.mkv"));
        Assert.Empty(Directory.GetFileSystemEntries(scenario.Folders.Output));
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        AssertDownloadIntact(download);

        await scenario.PostHandoffAsync("handoff-linked-1", download.Video, "Seeding.Film.2024");
        await scenario.WaitForHandoffStateAsync("handoff-linked-1", "completed");

        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Seeding.Film.2024", "film.mkv")));
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(Scenario.Callbacks(fake, "handoff-linked-1"));
        AssertDownloadIntact(download);
        var completed = Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed"));
        Assert.Contains(
            "This workflow is linked to Deluno, so the original stays with your download client",
            completed.ToJsonString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_radarr_workflow_is_still_scanned_and_its_originals_are_kept()
    {
        await using var scenario = await Scenario.StartAsync();
        var (radarr, library) = await scenario.RadarrSetupAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario, "Radarr.Film.2024");

        await scenario.EnqueueScanAsync(library);

        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(File.Exists(Path.Combine(scenario.Folders.Output, "Radarr.Film.2024", "film.mkv"))),
            "the scan to process the film");
        await scenario.WaitForFileStatusAsync(library, "Radarr.Film.2024/film.mkv", "processed");
        AssertDownloadIntact(download);
        var completed = Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed"));
        Assert.Contains("This workflow is linked to Radarr", completed.ToJsonString(), StringComparison.Ordinal);
        AssertAskedAsRecorded(radarr, ArrRecording.Load("radarr", "queue-empty"));
    }

    [Fact]
    public async Task A_sonarr_workflow_is_still_scanned_and_its_originals_are_kept()
    {
        await using var scenario = await Scenario.StartAsync();
        var (sonarr, library) = await scenario.SonarrSetupAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario, "Sonarr.Show.S01E01");

        await scenario.EnqueueScanAsync(library);

        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(File.Exists(Path.Combine(scenario.Folders.Output, "Sonarr.Show.S01E01", "film.mkv"))),
            "the scan to process the episode");
        await scenario.WaitForFileStatusAsync(library, "Sonarr.Show.S01E01/film.mkv", "processed");
        AssertDownloadIntact(download);
        var completed = Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed"));
        Assert.Contains("This workflow is linked to Sonarr", completed.ToJsonString(), StringComparison.Ordinal);
        AssertAskedAsRecorded(sonarr, ArrRecording.Load("sonarr", "queue-empty"));
    }

    /// <summary>The fake answers in the shape of the recorded app, and Weir asked it for its queue the way the recording shows.</summary>
    private static void AssertAskedAsRecorded(FakeManager fake, ArrRecording queue)
    {
        var recorded = queue.Only.Request;
        var asked = fake.RequestsTo(recorded.Method, recorded.Path);
        Assert.NotEmpty(asked);
        Assert.All(asked, request => Assert.Equal(fake.ApiKey, request.Header("X-Api-Key")));
        Assert.All(asked, request => Assert.Equal(["1000"], request.Query["pageSize"]));
        Assert.Equal("pageSize=1000", recorded.Query);
    }

    [Fact]
    public async Task A_weir_only_workflow_that_asks_for_removal_still_removes_the_whole_release_folder()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario, "Weir.Only.2024");

        await scenario.EnqueueScanAsync(library);

        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(!Directory.Exists(download.Release)),
            "the cleaned release folder to be removed");
        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Weir.Only.2024", "film.mkv")));
        Assert.False(File.Exists(download.Nfo));
        Assert.False(File.Exists(download.Txt));
    }
}
