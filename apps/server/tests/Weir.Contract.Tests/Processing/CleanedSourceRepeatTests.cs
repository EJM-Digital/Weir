using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// One processing per source file, seen from the manager's side: when Deluno hands over a file Weir has already cleaned (the same
/// hand-off again, or another hand-off for the same download) Weir does not clean it a second time, writes no second output, answers
/// with the same completion it gave the first time (so Deluno never reads the repeat as a failure) and says in Activity that it
/// left the file alone. A different file, or the same path with different bytes, is processed as usual.
/// </summary>
[ContractArea("processing")]
public sealed class CleanedSourceRepeatTests
{
    private const string SkippedRepeat = "processing.file_skipped_repeat";

    /// <summary>The skip lines, once there are <paramref name="count"/>. The request or the pass that settles a repeat writes its line, so this waits as long as a worker has a job (see <see cref="Scenario.WaitWhileWorkedOnAsync{T}"/>).</summary>
    private static async Task<JsonObject> WaitForSkipLineAsync(Scenario scenario, int count) =>
        (await scenario.WaitWhileWorkedOnAsync(
            async () =>
            {
                var lines = await scenario.ActivityAsync(SkippedRepeat);
                return lines.Count >= count ? lines : null;
            },
            "Activity to say the repeat was left alone"))[0];

    private static void AssertCompletedWith(JsonObject status, string outputPath)
    {
        Assert.Equal("completed", (string)status["state"]!);
        Assert.Equal(outputPath, (string)status["outputPath"]!);
        Assert.Equal([outputPath], status["outputFiles"]!.AsArray().Select(file => (string)file!).ToArray());
    }

    [Fact]
    public async Task A_file_handed_over_again_is_not_cleaned_twice_and_is_answered_as_the_first_completion()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        var source = scenario.WriteRelease("Pulp.Fiction.1994", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var output = Path.Combine(scenario.Folders.Output, "Pulp.Fiction.1994", "film.mkv");

        await scenario.PostHandoffAsync("handoff-first", source);
        await scenario.WaitForHandoffStateAsync("handoff-first", "completed");
        var first = (await scenario.WaitForReportsAsync(fake, "handoff-first"))[0];
        var written = File.GetLastWriteTimeUtc(output);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));

        // The same hand-off sent again, as a resend or a replay would.
        await scenario.PostHandoffAsync("handoff-first", source);
        var again = (await scenario.WaitForReportsAsync(fake, "handoff-first", 2))[1];

        Assert.Equal("completed", (string)again["status"]!);
        Assert.Equal((string)first["outputPath"]!, (string)again["outputPath"]!);
        Assert.Null(again["disposition"]);

        // The status Deluno polls names the same copy, so a resend is never read as completed without an output.
        AssertCompletedWith(await scenario.HandoffStatusAsync("handoff-first"), (string)first["outputPath"]!);

        // Another hand-off for the same download.
        await scenario.PostHandoffAsync("handoff-second", source);
        var other = (await scenario.WaitForReportsAsync(fake, "handoff-second"))[0];

        Assert.Equal("completed", (string)other["status"]!);
        Assert.Equal((string)first["outputPath"]!, (string)other["outputPath"]!);
        Assert.Null(other["disposition"]);
        AssertCompletedWith(await scenario.WaitForHandoffStateAsync("handoff-second", "completed"), (string)first["outputPath"]!);

        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(output));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed"));
        var line = await WaitForSkipLineAsync(scenario, 2);
        Assert.Equal("Skipped: already done (film.mkv)", (string)line["title"]!);
        Assert.Equal("skipped", (string)line["result"]!);
        var reason = (string)JsonNode.Parse((string)line["detail"]!)!["user_message"]!;
        Assert.StartsWith("Already done: cleaned on ", reason, StringComparison.Ordinal);
        Assert.EndsWith($" into {output}", reason, StringComparison.Ordinal);
        Assert.Equal(2, (await scenario.ActivityAsync(SkippedRepeat)).Count);
    }

    [Fact]
    public async Task Process_again_on_a_cleaned_file_is_skipped_says_so_and_writes_no_second_output()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.DelunoSetupAsync();
        const string relative = "Retryfilm.2017/film.mkv";
        var source = scenario.WriteRelease("Retryfilm.2017", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var output = Path.Combine(scenario.Folders.Output, "Retryfilm.2017", "film.mkv");
        await scenario.PostHandoffAsync("handoff-retry", source);
        await scenario.WaitForHandoffStateAsync("handoff-retry", "completed");
        var written = File.GetLastWriteTimeUtc(output);
        var file = await scenario.WaitForFileStatusAsync(library, relative, "processed");

        var requeued = await scenario.Admin.PostWithCsrfAsync($"{WeirClient.Api}/processing/files/{(int)file["id"]!}/requeue");

        Assert.True(requeued.Status == HttpStatusCode.OK, requeued.ToString());
        Assert.Equal(0, (int)requeued.Fields["requeued"]!);
        Assert.Equal(1, (int)requeued.Fields["skipped"]!);
        Assert.StartsWith("Weir already cleaned this file, so it skipped it. Already done: cleaned on ", (string)requeued.Fields["detail"]!, StringComparison.Ordinal);
        var line = await WaitForSkipLineAsync(scenario, 1);
        Assert.Equal("Skipped: already done (film.mkv)", (string)line["title"]!);
        Assert.Equal("skipped", (string)line["result"]!);
        Assert.Equal("manual", (string)line["trigger"]!);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(output));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Equal("processed", (string)(await scenario.FileRowAsync(library, relative))!["status"]!);
    }

    [Fact]
    public async Task A_different_file_or_the_same_path_with_different_bytes_is_cleaned_as_usual()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        var source = scenario.WriteRelease("Heat.1995", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await scenario.PostHandoffAsync("handoff-heat", source);
        await scenario.WaitForHandoffStateAsync("handoff-heat", "completed");

        var other = scenario.WriteRelease("Collateral.2004", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "ger"])));
        await scenario.PostHandoffAsync("handoff-other", other);
        await scenario.WaitForHandoffStateAsync("handoff-other", "completed");
        Assert.Equal(2, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);

        // An upgrade: the same path now holds a different file.
        File.WriteAllBytes(source, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre", "spa"])));
        await scenario.PostHandoffAsync("handoff-upgrade", source);
        await scenario.WaitForHandoffStateAsync("handoff-upgrade", "completed");

        Assert.Equal(3, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.Equal(3, (await scenario.JobsAsync(Scenario.RemuxKind)).Count);
        Assert.Empty(await scenario.ActivityAsync(SkippedRepeat));
        Assert.Equal("completed", (string)Assert.Single(Scenario.Callbacks(fake, "handoff-upgrade"))["status"]!);
    }
}
