using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What Weir knows about a copy it handed back does not depend on the file being listed. A file taken off the list (Remove from the
/// list, or any other way its row goes) and handed over again, unchanged, is not cleaned a second time while its copy is still there.
/// Once the manager collected the copy and Weir released it, a new hand-off is cleaned again (#1401). A manager's answer that comes after the file left the list is
/// still recorded and still releases the copy.
/// </summary>
[ContractArea("processing")]
public sealed class ForgottenFileTests
{
    private const string FileName = "Pulp.Fiction.1994.mkv";
    private const string ImportedPath = "/deluno/library/movies/Pulp Fiction (1994)/Pulp Fiction (1994).mkv";

    /// <summary>A file directly in the watched folder (no release folder), handed over by a fake Deluno and finished.</summary>
    private static async Task<(FakeManager Fake, JsonObject Library, string Source)> FinishedHandoffAsync(Scenario scenario, string handoffId)
    {
        var (fake, library) = await scenario.DelunoSetupAsync();
        var source = Path.Combine(scenario.Folders.Watched, FileName);
        await File.WriteAllBytesAsync(source, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await scenario.PostHandoffAsync(handoffId, source);
        await scenario.WaitForHandoffStateAsync(handoffId, "completed");
        await scenario.WaitForReportsAsync(fake, handoffId);
        return (fake, library, source);
    }

    private static async Task ForgetAsync(Scenario scenario, JsonObject library)
    {
        var row = await scenario.FileRowAsync(library, FileName);
        var forgotten = await scenario.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/files/{(int)row!["id"]!}");
        Assert.Equal(HttpStatusCode.NoContent, forgotten.Status);
        Assert.Null(await scenario.FileRowAsync(library, FileName));
    }

    [Fact]
    public async Task A_forgotten_file_handed_over_again_after_Deluno_imported_it_is_cleaned_again_into_a_copy_that_exists()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library, source) = await FinishedHandoffAsync(scenario, "first");
        var output = Path.Combine(scenario.Folders.Output, FileName);
        var imported = await scenario.PostOutcomeAsync("first", "imported", ImportedPath);
        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.False(File.Exists(output), "Weir's copy is released once Deluno has the file");
        await ForgetAsync(scenario, library);

        await scenario.PostHandoffAsync("again", source);
        await scenario.WaitForHandoffStateAsync("again", "completed");

        // #1401: the copy the first send named was released, so the second send is a real pass and names the copy it wrote.
        var report = (await scenario.WaitForReportsAsync(fake, "again"))[0];
        Assert.Equal("completed", (string)report["status"]!);
        Assert.Equal((string)Scenario.Callbacks(fake, "first")[0]["outputPath"]!, (string)report["outputPath"]!);
        Assert.Null(report["disposition"]);
        Assert.Equal(2, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.Equal(2, (await scenario.JobsAsync(Scenario.RemuxKind)).Count);
        Assert.True(File.Exists(output), "the second send has a copy Deluno can import");
        Assert.Empty(await scenario.ActivityAsync("processing.file_skipped_repeat"));

        var row = await scenario.FileRowAsync(library, FileName);
        Assert.NotNull(row);
        Assert.Equal("processed", (string)row["status"]!);

        // Deluno answers the second send as it answers any hand-off.
        var again = await scenario.PostOutcomeAsync("again", "imported", ImportedPath);
        Assert.True(again.Status == HttpStatusCode.OK, again.ToString());
        Assert.Equal("imported", (string)(await scenario.FileRowAsync(library, FileName))!["handback"]!["outcome"]!);
    }

    [Fact]
    public async Task A_file_handed_over_again_while_its_copy_is_still_there_is_not_cleaned_twice()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _, source) = await FinishedHandoffAsync(scenario, "first");
        var output = Path.Combine(scenario.Folders.Output, FileName);
        Assert.True(File.Exists(output));

        await scenario.PostHandoffAsync("again", source);
        await scenario.WaitForHandoffStateAsync("again", "completed");

        var report = (await scenario.WaitForReportsAsync(fake, "again"))[0];
        Assert.Equal((string)Scenario.Callbacks(fake, "first")[0]["outputPath"]!, (string)report["outputPath"]!);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.True(File.Exists(output));
        Assert.Single(await scenario.ActivityAsync("processing.file_skipped_repeat"));
    }

    [Fact]
    public async Task A_managers_answer_after_the_file_left_the_list_is_recorded_and_releases_the_copy()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library, source) = await FinishedHandoffAsync(scenario, "first");
        var output = Path.Combine(scenario.Folders.Output, FileName);
        Assert.True(File.Exists(output));
        await ForgetAsync(scenario, library);

        var imported = await scenario.PostOutcomeAsync("first", "imported", ImportedPath);

        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.True((bool)imported.Fields["released"]!, imported.ToString());
        Assert.False(File.Exists(output), "Weir's own copy is released once Deluno has the file");
        var line = Assert.Single(await scenario.ActivityAsync("processing.handback_outcome"));
        Assert.Equal($"Deluno imported {FileName}", (string)line["title"]!);

        // The answer is on the copy and the copy was released, so when the file comes back it is cleaned again (#1401).
        await scenario.PostHandoffAsync("again", source);
        await scenario.WaitForHandoffStateAsync("again", "completed");
        Assert.Equal(2, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task A_scanned_file_Radarr_collected_and_taken_off_the_list_is_not_cleaned_again_when_the_scan_finds_it()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync();
        scenario.WriteRelease("Radarr.Film.2024", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        const string relative = "Radarr.Film.2024/film.mkv";
        await scenario.EnqueueScanAsync(library);
        var cleaned = await scenario.WaitForFileStatusAsync(library, relative, "processed");
        var output = Path.Combine(scenario.Folders.Output, "Radarr.Film.2024", "film.mkv");
        var collected = await scenario.Admin.PostAsync(
            $"{WeirClient.Api}/intake/webhook/radarr",
            new JsonObject
            {
                ["eventType"] = "Download",
                ["movie"] = new JsonObject { ["id"] = 7, ["title"] = "Radarr Film", ["year"] = 2024 },
                ["movieFile"] = new JsonObject { ["id"] = 12, ["path"] = "/movies/Radarr Film (2024)/Radarr Film (2024).mkv", ["sourcePath"] = output },
                ["downloadClient"] = "qBittorrent",
                ["downloadId"] = "RADARR-1",
            },
            new Dictionary<string, string> { ["X-Webhook-Secret"] = Scenario.WebhookSecret });
        Assert.True(collected.Status == HttpStatusCode.OK, collected.ToString());
        Assert.False(File.Exists(output), "Weir's copy is released once Radarr has the file");
        var forgotten = await scenario.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/files/{(int)cleaned["id"]!}");
        Assert.Equal(HttpStatusCode.NoContent, forgotten.Status);

        await scenario.WaitForJobFinishedAsync(await scenario.EnqueueScanAsync(library));

        var row = await scenario.WaitForFileStatusAsync(library, relative, "processed");
        Assert.StartsWith("Already imported: Radarr collected the cleaned copy on ", (string)row["status_reason"]!, StringComparison.Ordinal);
        Assert.Equal("imported", (string)row["handback"]!["outcome"]!);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
    }
}
