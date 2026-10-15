using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A file ffprobe cannot read is not a video Weir can vouch for, and not one it can condemn on one look either: a download still arriving
/// reads the same way. Weir waits and looks again; whatever the workflow does with files that need no change, nothing is written, handed
/// back, reported or deleted meanwhile, and no tool text, memory address or folder path reaches Activity or the media manager. The refusal
/// after the last look is covered with a clock Weir can move on, in <c>LeftInPlaceOriginalTests</c>.
/// </summary>
[ContractArea("processing")]
public sealed class UnreadableFileTests
{
    private const string NotReadableYet = "Weir can't read this file yet. It may still be arriving, so Weir will look again later.";

    private const string ProbeFailure =
        "[matroska,webm @ 000001b280590a00] EBML header parsing failed\nC:\\Weir\\Ready\\Movies\\Reject.Film.mkv: Invalid data found when processing input";

    private static byte[] Junk()
    {
        var bytes = new byte[256 * 1024];
        new Random(924).NextBytes(bytes);
        return bytes;
    }

    private static void AssertNoToolText(string text)
    {
        Assert.DoesNotContain("@ 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[matroska", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EBML", text, StringComparison.Ordinal);
        Assert.DoesNotContain("misdetection", text, StringComparison.Ordinal);
    }

    private static void AssertPlain(string text)
    {
        AssertNoToolText(text);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", text, StringComparison.Ordinal);
    }

    /// <summary>Every sentence Activity shows for the file is plain; the tool's own text is only ever in <c>technical_detail</c>.</summary>
    private static async Task AssertActivityIsPlainAsync(Scenario scenario)
    {
        var recent = await scenario.Admin.GetAsync($"{WeirClient.Api}/activity/recent", ("limit", 100));
        var failed = recent.Fields["items"]!.AsArray().OfType<JsonObject>()
            .Where(item => ((string)item["title"]!).Contains("film.mkv", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(failed);
        foreach (var item in failed)
        {
            AssertPlain((string)item["title"]!);
            var detail = JsonNode.Parse((string)item["detail"]!)!.AsObject();
            foreach (var key in new[] { "reason", "preflight_reason", "user_message", "failure_operator_message", "message" })
            {
                if (detail[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    AssertPlain(text);
                }
            }
        }
    }

    /// <summary>
    /// What a person reads in the Logs screen, the dashboard's log card and the export is plain: the tool's own text is only ever in an
    /// entry's exception. The sentence for the file is there to find, so a log that said nothing would not pass.
    /// </summary>
    private static async Task AssertLogsArePlainAsync(Scenario scenario)
    {
        var suite = await scenario.Admin.GetAsync($"{WeirClient.Api}/suite/logs", ("limit", 250));
        var lines = suite.Fields["items"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(lines, line => ((string)line["message"]!).Contains("film.mkv", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            AssertNoToolText((string)line["message"]!);
        }

        foreach (var line in lines.Where(line => ((string)line["message"]!).Contains("film.mkv", StringComparison.Ordinal)))
        {
            AssertPlain((string)line["message"]!);
        }

        var system = await scenario.Admin.GetAsync($"{WeirClient.Api}/system/log", ("limit", 100));
        foreach (var row in system.Fields["items"]!.AsArray().OfType<JsonObject>())
        {
            AssertNoToolText((string)row["title"]!);
            if (row["detail"] is JsonValue detail && detail.TryGetValue<string>(out var text))
            {
                AssertNoToolText(text);
            }
        }
    }

    private static async Task AssertNothingHappenedAsync(Scenario scenario, FakeManager fake, string handoffId, string source, byte[] junk)
    {
        // Long enough for a wrong verdict to reach the manager; the next look is minutes away. The manager may be told the file is held,
        // in plain words; it must never be told the release is bad, nor be given the tool's text.
        await Scenario.NeverWithinAsync(
            () =>
            {
                foreach (var report in Scenario.Callbacks(fake, handoffId))
                {
                    Assert.Equal("failed", (string)report["status"]!);
                    Assert.Equal("held", (string)report["disposition"]!);
                    Assert.False((bool)report["sourceRemoved"]!);
                    Assert.Equal(NotReadableYet, (string)report["message"]!);
                    AssertPlain(report.ToJsonString());
                }

                return Task.FromResult(false);
            },
            TimeSpan.FromSeconds(4),
            "Weir telling the manager anything but that it is waiting for a file it cannot read yet");
        await Scenario.NeverWithinAsync(
            async () => (await scenario.JobsAsync(Scenario.RejectKind)).Count > 0,
            TimeSpan.FromSeconds(1),
            "Weir queueing a rejection of a file it cannot read yet");
        Assert.Empty(Directory.GetFileSystemEntries(scenario.Folders.Output));
        Assert.Empty(await scenario.JobsAsync(Scenario.PassThroughKind));
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Equal(junk, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task A_junk_file_handed_off_with_pass_through_on_waits_and_is_neither_written_handed_back_reported_nor_touched()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "pass_through"), ("rejected_file_action", "delete_file")]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { ProbeError = ProbeFailure });
        var junk = Junk();
        var source = scenario.WriteRelease("Reject.Film.2013", "film.mkv", junk);

        await scenario.PostHandoffAsync("handoff-junk-1", source);
        var row = await scenario.WaitForFileStatusAsync(library, "Reject.Film.2013/film.mkv", "on_hold");

        Assert.Equal(NotReadableYet, (string)row["status_reason"]!);
        await AssertNothingHappenedAsync(scenario, fake, "handoff-junk-1", source, junk);
        // One look made, the next already booked: a wait, not a failure and not a retry.
        var attempts = await scenario.JobsAsync(Scenario.RemuxKind);
        Assert.Equal(1, attempts.Count(job => (string)job["status"]! == "completed"));
        Assert.Equal(1, attempts.Count(job => (string)job["status"]! == "pending"));
        await AssertActivityIsPlainAsync(scenario);
        await AssertLogsArePlainAsync(scenario);
    }

    [Fact]
    public async Task A_junk_file_under_the_reject_policy_is_not_reported_rejected_on_the_first_look()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library) = await scenario.DelunoSetupAsync(
            capabilities: ["processor-reject-regrab"], library: [("failure_policy", "reject"), ("retry_preflight_failures", true)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { ProbeError = ProbeFailure });
        var junk = Junk();
        var source = scenario.WriteRelease("Reject.Film.2013", "film.mkv", junk);

        await scenario.PostHandoffAsync("handoff-junk-2", source);
        var row = await scenario.WaitForFileStatusAsync(library, "Reject.Film.2013/film.mkv", "on_hold");

        Assert.Equal(NotReadableYet, (string)row["status_reason"]!);
        await AssertNothingHappenedAsync(scenario, fake, "handoff-junk-2", source, junk);
        await AssertActivityIsPlainAsync(scenario);
        await AssertLogsArePlainAsync(scenario);
    }

    [Fact]
    public async Task A_readable_file_that_needs_no_change_still_passes_through()
    {
        await using var scenario = await Scenario.StartAsync();
        var library = await scenario.CreateLibraryAsync(("failure_policy", "pass_through"));
        var original = FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"]));
        scenario.WriteRelease("Already.Clean.2020", "film.mkv", original);

        await scenario.EnqueueFilePassAsync("Already.Clean.2020/film.mkv", library, passThroughUnchanged: true);

        // A manual pass with no prior scan may leave no Files row, so the job is what is watched.
        await scenario.WaitWhileWorkedOnAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).Any(job => (string)job["status"]! == "completed"),
            "the pass to finish");
        var completed = Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed"));
        Assert.Equal("film.mkv was passed through unchanged", (string)completed["title"]!);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(scenario.Folders.Output, "Already.Clean.2020", "film.mkv")));
        Assert.Empty(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
    }
}
