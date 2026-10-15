using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Jobs;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// A media manager connection a workflow still comes from is not removed: the workflow would become one Weir feeds itself, scan its
/// folder, and remove the originals a download client is still seeding. These run a real server with a fake Radarr that feeds a
/// workflow whose remove-original setting is on, and a download in its watched folder, and show that the refusal, each of the two
/// ways to let go of the workflow, and the removal that follows never touch that download.
/// </summary>
[ContractArea("media_managers")]
public sealed class ConnectionRemovalTests
{
    private const string Release = "Radarr.Film.2024";
    private const string Video = Release + "/film.mkv";

    /// <summary>Long enough for a queued scan to be picked up if anything would pick it up.</summary>
    private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(8);

    private sealed record Download(string Video, string Nfo, string Folder)
    {
        public void AssertIntact()
        {
            Assert.True(File.Exists(Video), "the video must stay where the download client put it");
            Assert.True(File.Exists(Nfo), "its sidecar must stay");
            Assert.True(Directory.Exists(Folder), "its release folder must stay");
        }
    }

    private static Download WriteDownload(Scenario scenario)
    {
        var video = scenario.WriteRelease(Release, "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var folder = Path.GetDirectoryName(video)!;
        var nfo = Path.Combine(folder, "film.nfo");
        File.WriteAllText(nfo, "metadata");
        return new Download(video, nfo, folder);
    }

    private static async Task<int> ConnectionIdAsync(WeirClient admin)
    {
        var listed = await admin.GetAsync(ManagerConnections.Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return JsonFields.Id(Assert.Single(listed.Elements)!);
    }

    private static Task<WeirResponse> UnlinkWorkflowsAsync(WeirClient admin, int connectionId) =>
        admin.PostWithCsrfAsync($"{ManagerConnections.Route}/{connectionId}/unlink-workflows");

    private static async Task<JsonObject> LibraryAsync(WeirClient admin, JsonObject library)
    {
        var read = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{(int)library["id"]!}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        return read.Fields;
    }

    /// <summary>
    /// Saving a workflow gets it a scan, queued by the scheduler on its next tick, and a workflow with work queued or running is not
    /// removed, so removal waits for that scan to run. The scan is work Weir owes on its own timer, and no job is there to see until it
    /// is queued, so the wait is for the scan itself to have finished and not for a moment when no job is active.
    /// </summary>
    private static async Task WaitForSaveScanAsync(Scenario scenario) =>
        await scenario.WaitWhileWorkedOnAsync(
            async () => (await scenario.FinishedJobsAsync(JobsApi.ScanDispatchKind)).Count > 0 ? "scanned" : null,
            "the scan queued when the workflow was saved to finish",
            owed: () => Task.FromResult(true));

    private static async Task AssertConnectionKeptAsync(WeirClient admin, int connectionId) =>
        Assert.Equal([connectionId], (await admin.GetAsync(ManagerConnections.Route)).Elements.Select(row => JsonFields.Id(row!)));

    [Fact]
    public async Task A_connection_that_feeds_a_workflow_is_refused_with_the_workflow_named_and_the_download_is_left_alone()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario);
        var connectionId = await ConnectionIdAsync(scenario.Admin);

        var refused = await ManagerConnections.DeleteAsync(scenario.Admin, connectionId);

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("connection_in_use", (string)refused.Fields["code"]!);
        var name = (string)library["name"]!;
        var label = (string)(await scenario.Admin.GetAsync($"{ManagerConnections.Route}/{connectionId}")).Fields["name"]!;
        Assert.Equal(
            $"{name} still comes from {label}. Nothing was removed. Before removing it, unlink or remove that workflow.",
            (string)refused.Fields["detail"]!);
        Assert.Equal([((int)library["id"]!, name)], refused.Fields["workflows"]!.AsArray().Select(item => ((int)item!["id"]!, (string)item["name"]!)));
        Assert.Equal((0, 0), ((int)refused.Fields["handoffs_in_progress"]!, refused.Fields["reports"]!.AsArray().Count));
        await AssertConnectionKeptAsync(scenario.Admin, connectionId);
        var unchanged = await LibraryAsync(scenario.Admin, library);
        Assert.Equal([connectionId], unchanged["manager_connection_ids"]!.AsArray().Select(id => (int)id!));
        Assert.True((bool)unchanged["enabled"]!);
        download.AssertIntact();
    }

    [Fact]
    public async Task Unlinking_switches_the_workflow_off_so_it_leaves_the_download_alone_and_the_connection_can_then_go()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario);
        var connectionId = await ConnectionIdAsync(scenario.Admin);

        var unlinked = await UnlinkWorkflowsAsync(scenario.Admin, connectionId);

        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());
        Assert.Equal([((int)library["id"]!, (string)library["name"]!)], unlinked.Fields["workflows"]!.AsArray().Select(item => ((int)item!["id"]!, (string)item["name"]!)));
        var kept = await LibraryAsync(scenario.Admin, library);
        Assert.Empty(kept["manager_connection_ids"]!.AsArray());
        Assert.False((bool)kept["enabled"]!);
        Assert.Equal((string)library["watched_folder"]!, (string)kept["watched_folder"]!);

        // The workflow is Weir's own now, with the setting that removes what it cleaned still on, and it still does nothing to the download.
        await scenario.EnqueueScanAsync(kept);
        await Scenario.NeverWithinAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).Count > 0 || !File.Exists(download.Video) || !File.Exists(download.Nfo),
            ScanWindow,
            "Weir cleaning or removing the download of a workflow that is switched off");
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        download.AssertIntact();

        var removed = await ManagerConnections.DeleteAsync(scenario.Admin, connectionId);

        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        Assert.Empty((await scenario.Admin.GetAsync(ManagerConnections.Route)).Elements);
        Assert.Equal((string)library["name"]!, (string)(await LibraryAsync(scenario.Admin, library))["name"]!);
        download.AssertIntact();
    }

    [Fact]
    public async Task Removing_the_workflow_first_leaves_the_download_alone_and_the_connection_can_then_go()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync(("remove_original_after_success", true));
        var download = WriteDownload(scenario);
        var connectionId = await ConnectionIdAsync(scenario.Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await ManagerConnections.DeleteAsync(scenario.Admin, connectionId)).Status);
        await WaitForSaveScanAsync(scenario);

        var workflowRemoved = await scenario.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/libraries/{(int)library["id"]!}");
        var removed = await ManagerConnections.DeleteAsync(scenario.Admin, connectionId);

        Assert.True(workflowRemoved.Status == HttpStatusCode.NoContent, workflowRemoved.ToString());
        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        download.AssertIntact();
    }

    [Fact]
    public async Task A_workflow_still_fed_by_another_connection_keeps_running_when_one_of_its_connections_is_unlinked()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync(("remove_original_after_success", true));
        var second = scenario.Own(FakeManager.StartArr("radarr", rootFolders: [Path.Combine(scenario.Root, "library")]));
        var other = await scenario.CreateConnectionAsync(second);
        var otherId = (int)other["id"]!;
        var firstId = (await scenario.Admin.GetAsync(ManagerConnections.Route)).Elements.Select(row => JsonFields.Id(row!)).Single(id => id != otherId);
        await scenario.UpdateLibraryAsync(library, ("manager_connection_ids", new JsonArray(firstId, otherId)));
        var download = WriteDownload(scenario);

        var unlinked = await UnlinkWorkflowsAsync(scenario.Admin, firstId);

        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());
        var kept = await LibraryAsync(scenario.Admin, library);
        Assert.Equal([otherId], kept["manager_connection_ids"]!.AsArray().Select(id => (int)id!));
        Assert.True((bool)kept["enabled"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await ManagerConnections.DeleteAsync(scenario.Admin, firstId)).Status);
        await scenario.EnqueueScanAsync(kept);
        await scenario.WaitForFileStatusAsync(kept, Video, "processed");
        download.AssertIntact();
    }
}
