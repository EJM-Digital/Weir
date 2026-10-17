using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What Activity's remove dialog does to a rejected or failed file, with the requests the web app sends, a real file in a real watched folder and
/// the real database: Delete removes the file and its row, Keep leaves the file and stops Weir looking at it, Try again queues it, and
/// Just remove it touches nothing on disk. A workflow linked to a media manager refuses Delete and says so. Every choice that acts
/// leaves an Activity entry.
/// </summary>
[ContractArea("processing")]
public sealed class RemovingAFileFromActivityTests
{
    private const string Api = WeirClient.Api;
    private const string Release = "No.Audio.2023";
    private const string RelativePath = Release + "/film.mkv";

    private static byte[] NoAudio => FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: []));

    /// <summary>A file the rules reject: video with no audio, left where it is in the watched folder.</summary>
    private static async Task<(JsonObject Library, JsonObject Row, string Source)> RejectedFileAsync(Scenario scenario)
    {
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"));
        var source = scenario.WriteRelease(Release, "film.mkv", NoAudio);
        await scenario.EnqueueFilePassAsync(RelativePath, library);
        var row = await scenario.WaitForFileStatusAsync(library, RelativePath, "rejected");
        return (library, row, source);
    }

    /// <summary>
    /// The request of the remove dialog, exactly as the web app builds it: the choice, and the file's size and time when the dialog showed
    /// them to confirm (<c>fixtures/web-bodies</c>, written by the web tests).
    /// </summary>
    private static async Task<WeirResponse> RemoveAsync(Scenario scenario, JsonObject row, string? resolution, long sizeShownOff = 0)
    {
        var id = (int)row["id"]!;
        var options = await scenario.Admin.GetAsync($"{Api}/processing/files/{id}/remove-options");
        Assert.True(options.Status == HttpStatusCode.OK, options.ToString());
        var confirmed = resolution is not null
            && options.Fields["fingerprint_recorded"]!.GetValue<bool>() is false
            && options.Fields["unconfirmed_size_bytes"] is not null;
        var name = $"remove-file/{resolution ?? "plain"}{(confirmed ? "-unrecorded" : string.Empty)}";
        var sample = confirmed
            ? new WebSample(FileId: id, SizeBytes: (long)options.Fields["unconfirmed_size_bytes"]! + sizeShownOff, ModifiedAt: options.Fields["unconfirmed_modified_at"]!.DeepClone())
            : new WebSample(FileId: id);
        return await WebBodies.Get(name, sample).SendAsync(scenario.Admin);
    }

    /// <summary>1 while the file has a row on the Files list, otherwise 0. Read over HTTP: a server restart to read SQLite signs in again, and a sign-in is the dearest request there is.</summary>
    private static async Task<int> RowsOfAsync(Scenario scenario, JsonObject library) =>
        await scenario.FileRowAsync(library, RelativePath) is null ? 0 : 1;

    private static async Task<JsonObject> OneActivityAsync(Scenario scenario, string eventType) =>
        Assert.Single(await scenario.ActivityAsync(eventType));

    [Fact]
    public async Task Delete_in_a_workflow_with_no_manager_removes_the_file_and_its_row()
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileAsync(scenario);

        var removed = await RemoveAsync(scenario, row, "delete");

        Assert.True(removed.Status == HttpStatusCode.OK, removed.ToString());
        Assert.True((bool)removed.Fields["done"]!, removed.ToString());
        Assert.False(File.Exists(source));
        Assert.Equal(0, await RowsOfAsync(scenario, library));
        var activity = await OneActivityAsync(scenario, "processing.file_removal_deleted");
        Assert.Equal("film.mkv was removed from Activity and its download deleted", (string)activity["title"]!);
        Assert.Contains("\"result\":\"success\"", (string)activity["detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_leaves_the_file_and_the_next_scan_does_not_bring_it_back()
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileAsync(scenario);

        var kept = await RemoveAsync(scenario, row, "keep");

        Assert.True(kept.Status == HttpStatusCode.OK, kept.ToString());
        Assert.True(File.Exists(source));
        Assert.Equal(0, await RowsOfAsync(scenario, library));
        var keptFiles = await scenario.Admin.GetAsync($"{Api}/processing/kept-files");
        var listed = Assert.Single(keptFiles.Fields["files"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(RelativePath, (string)listed["relative_path"]!);
        var activity = await OneActivityAsync(scenario, "processing.file_removal_kept");
        Assert.Equal("film.mkv was kept without processing it again", (string)activity["title"]!);

        var passesBefore = (await scenario.JobsAsync(Scenario.RemuxKind)).Count;
        await scenario.WaitForJobFinishedAsync(await scenario.EnqueueScanAsync(library));

        Assert.Null(await scenario.FileRowAsync(library, RelativePath));
        Assert.Equal(passesBefore, (await scenario.JobsAsync(Scenario.RemuxKind)).Count);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Try_again_queues_the_file_and_leaves_it_where_it_is()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, row, source) = await RejectedFileAsync(scenario);
        var passesBefore = (await scenario.JobsAsync(Scenario.RemuxKind)).Count;

        var retried = await RemoveAsync(scenario, row, "retry");

        Assert.True(retried.Status == HttpStatusCode.OK, retried.ToString());
        Assert.True((bool)retried.Fields["done"]!, retried.ToString());
        Assert.True(File.Exists(source));
        Assert.True((await scenario.JobsAsync(Scenario.RemuxKind)).Count > passesBefore);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData(null)]
    public async Task Just_remove_it_touches_nothing_on_disk_and_keeps_no_marker(string? resolution)
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileAsync(scenario);

        var removed = await RemoveAsync(scenario, row, resolution);

        Assert.True(removed.Status == HttpStatusCode.NoContent, removed.ToString());
        Assert.True(File.Exists(source));
        Assert.Equal(0, await RowsOfAsync(scenario, library));
        Assert.Empty((await scenario.Admin.GetAsync($"{Api}/processing/kept-files")).Fields["files"]!.AsArray());
        Assert.Empty(await scenario.ActivityAsync("processing.file_removal_deleted"));
        Assert.Empty(await scenario.ActivityAsync("processing.file_removal_kept"));
    }

    [Fact]
    public async Task Delete_refuses_a_file_that_changed_since_it_was_rejected()
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileAsync(scenario);
        await File.WriteAllBytesAsync(source, [.. NoAudio, .. "a different release"u8.ToArray()]);

        var refused = await RemoveAsync(scenario, row, "delete");

        Assert.True(refused.Status == HttpStatusCode.BadGateway, refused.ToString());
        Assert.Contains("changed", (string)refused.Fields["detail"]!, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await RowsOfAsync(scenario, library));
        var activity = await OneActivityAsync(scenario, "processing.file_removal_deleted");
        Assert.Equal("film.mkv could not be deleted", (string)activity["title"]!);
    }

    /// <summary>A file Weir rejected before it recorded what a file looked like, as a row from an older Weir is: the dialog asks the person to confirm it.</summary>
    private static async Task<(JsonObject Library, JsonObject Row, string Source)> RejectedFileWithoutFingerprintAsync(Scenario scenario)
    {
        var (library, row, source) = await RejectedFileAsync(scenario);
        await scenario.ExecuteAfterStopAsync("UPDATE files SET fingerprint_size_bytes = NULL, fingerprint_mtime_ns = NULL");
        return (library, row, source);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("keep")]
    public async Task A_choice_confirmed_against_the_file_the_dialog_showed_is_carried_out(string resolution)
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileWithoutFingerprintAsync(scenario);

        var done = await RemoveAsync(scenario, row, resolution);

        Assert.True(done.Status == HttpStatusCode.OK, done.ToString());
        Assert.Equal(resolution == "keep", File.Exists(source));
        Assert.Equal(0, await RowsOfAsync(scenario, library));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("keep")]
    public async Task A_choice_confirmed_against_another_file_than_the_one_on_disk_is_refused_and_changes_nothing(string resolution)
    {
        await using var scenario = await Scenario.StartAsync();
        var (library, row, source) = await RejectedFileWithoutFingerprintAsync(scenario);
        var before = await File.ReadAllBytesAsync(source);

        var refused = await RemoveAsync(scenario, row, resolution, sizeShownOff: 1);

        Assert.True(refused.Status == HttpStatusCode.BadGateway, refused.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Equal(1, await RowsOfAsync(scenario, library));
        Assert.Empty((await scenario.Admin.GetAsync($"{Api}/processing/kept-files")).Fields["files"]!.AsArray());
    }

    [Fact]
    public async Task A_workflow_linked_to_a_manager_refuses_delete_says_why_and_leaves_the_download()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "hold"), ("max_attempts", 1)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxError = "Conversion failed: the fake ffmpeg was told to fail" });
        var source = scenario.WriteRelease(Release, "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await scenario.PostHandoffAsync("handoff-remove-1", source, Release);
        var row = await scenario.FailureAttemptsReachAsync(library, RelativePath, 1);

        var options = await scenario.Admin.GetAsync($"{Api}/processing/files/{(int)row["id"]!}/remove-options");
        var refused = await RemoveAsync(scenario, row, "delete");

        Assert.True(options.Status == HttpStatusCode.OK, options.ToString());
        var reason = (string?)options.Fields["delete_refused_reason"];
        Assert.False(string.IsNullOrWhiteSpace(reason), "The dialog is told why Delete is not offered: " + options);
        Assert.True(refused.Status == HttpStatusCode.BadGateway, refused.ToString());
        Assert.StartsWith(reason!, (string)refused.Fields["detail"]!, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await RowsOfAsync(scenario, library));
        var activity = await OneActivityAsync(scenario, "processing.file_removal_deleted");
        Assert.Equal("film.mkv could not be deleted", (string)activity["title"]!);
        Assert.Contains("remove the download from your download client", (string)activity["detail"]!, StringComparison.Ordinal);
    }
}
