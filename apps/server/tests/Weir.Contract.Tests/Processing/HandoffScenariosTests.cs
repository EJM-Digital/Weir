using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>A media manager hands Weir a file and hears back: hand-off, remux, completion callback and folder hand-offs, on a running server with a fake Deluno and fake ffprobe/ffmpeg.</summary>
[ContractArea("processing")]
public sealed class HandoffScenariosTests
{
    [Fact]
    public async Task Handoff_is_remuxed_and_reported_complete_with_the_managers_output_path()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        // English and French audio; the default rules keep English, so a remux is required.
        var source = scenario.WriteRelease("Blade.Runner.2049", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var release = Path.GetDirectoryName(source)!;

        var accepted = await scenario.PostHandoffAsync("handoff-ok-1", source);
        Assert.Equal("ok", (string)accepted["status"]!);
        Assert.Equal("handoff", (string)accepted["event"]!);
        Assert.Equal(Scenario.RemuxKind, (string)accepted["enqueued"]!);

        var done = await scenario.WaitForHandoffStateAsync("handoff-ok-1", "completed");
        var body = (await scenario.WaitForReportsAsync(fake, "handoff-ok-1"))[0];
        Assert.Equal(fake.ApiKey, Assert.Single(fake.RequestsTo("POST", Scenario.EventsPath)).Header("X-Api-Key"));
        Assert.Equal("handoff-ok-1", (string)body["handoffId"]!);
        Assert.Equal("completed", (string)body["status"]!);
        Assert.Equal("Weir", (string)body["processorName"]!);
        Assert.Equal(Scenario.DelunoLibraryKey, (string)body["libraryId"]!);
        Assert.Equal("Contract.Release.2024", (string)body["releaseName"]!);
        Assert.Equal("Removed 1 audio track.", (string)body["message"]!);
        // Rebuilt under the manager's own processor output folder, not Weir's local path.
        var expected = $"{Scenario.DelunoOutputRoot}/Blade.Runner.2049/film.mkv";
        Assert.Equal(expected, ((string)body["outputPath"]!).Replace('\\', '/'));
        Assert.Equal(expected, ((string)done["outputPath"]!).Replace('\\', '/'));

        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Blade.Runner.2049", "film.mkv")));
        var remuxes = scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux");
        Assert.Single(remuxes);
        // The French track (input stream 2) was not mapped into the output.
        Assert.DoesNotContain("0:2", remuxes[0].Arguments);
        // The workflow is linked to Deluno, so the download stays where its download client put it.
        Assert.True(File.Exists(source));
        Assert.True(Directory.Exists(release));
        Assert.Single(Scenario.Callbacks(fake, "handoff-ok-1"));
    }

    [Fact]
    public async Task Folder_handoff_with_a_sample_queues_and_processes_only_the_main_video()
    {
        await using var scenario = await Scenario.StartAsync();
        // Pause first, so the queue can be inspected before any work starts.
        await scenario.SetPauseAsync(paused: true);
        var (fake, _) = await scenario.DelunoSetupAsync();
        var release = Path.Combine(scenario.Folders.Watched, "Some.Film.2020.1080p");
        Directory.CreateDirectory(Path.Combine(release, "Sample"));
        await File.WriteAllBytesAsync(
            Path.Combine(release, "Some.Film.2020.1080p.mkv"), FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await File.WriteAllBytesAsync(Path.Combine(release, "Sample", "some.film.sample.mkv"), FakeMedia.Bytes(FakeMedia.Probe()));
        await File.WriteAllTextAsync(Path.Combine(release, "Some.Film.2020.1080p.nfo"), "info");

        await scenario.PostHandoffAsync("handoff-folder-1", release);

        var remuxJobs = await scenario.JobsAsync(Scenario.RemuxKind);
        var job = Assert.Single(remuxJobs);
        var payload = JsonNode.Parse((string)job["payload_json"]!)!;
        Assert.Equal("Some.Film.2020.1080p/Some.Film.2020.1080p.mkv", (string)payload["relative_media_path"]!);
        Assert.Equal("handoff-folder-1", (string)payload["origin"]!["handoff_id"]!);
        Assert.Equal("queued", (string)(await scenario.HandoffStatusAsync("handoff-folder-1"))["state"]!);

        await scenario.SetPauseAsync(paused: false);
        await scenario.WaitForHandoffStateAsync("handoff-folder-1", "completed");
        var probed = scenario.FakeTools.Calls(tool: "ffprobe").Select(call => call.File).ToHashSet();
        Assert.DoesNotContain("some.film.sample.mkv", probed);
        Assert.Equal(["Some.Film.2020.1080p.mkv"], scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Select(call => call.File));
        var reports = Scenario.Callbacks(fake, "handoff-folder-1");
        var reported = Assert.Single(reports);
        Assert.EndsWith("Some.Film.2020.1080p/Some.Film.2020.1080p.mkv", ((string)reported["outputPath"]!).Replace('\\', '/'), StringComparison.Ordinal);
    }
}
