using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// The last step of a pass is the one that puts a file where the media manager will look for it, and it must never leave half of one
/// there. Each test runs the real ffmpeg on a real file, blocks the output name with a folder so that step cannot complete, and reads
/// what is left on disk and what Activity says.
/// </summary>
[ContractArea("processing")]
public sealed class PublishFailureTests
{
    private const string Release = "Tiny.Real.Film.2024";
    private const string Relative = "Tiny.Real.Film.2024/tiny.mkv";

    private static (string, JsonNode?)[] GiveUpAtOnce() => [("max_attempts", 1), ("retry_backoff_seconds", 1)];

    [RealFfmpegFact]
    public async Task A_cleaned_file_that_cannot_be_published_leaves_no_partial_and_Activity_says_so_in_plain_words()
    {
        await using var scenario = await Scenario.StartWithRealToolsAsync();
        var (_, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "hold"), .. GiveUpAtOnce()]);
        var source = await WriteRealReleaseAsync(scenario);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var blocker = BlockOutput(scenario);

        await scenario.PostHandoffAsync("handoff-publish-1", source);
        var row = await scenario.WaitForFileStatusAsync(library, Relative, "processing_failed");

        AssertNothingPublished(scenario, blocker);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.EnumerateFiles(scenario.Folders.Work, "*", SearchOption.AllDirectories));
        var reason = (string)row["status_reason"]!;
        AssertPlain(reason);
        await Poll.UntilAsync(
            async () => (await scenario.ActivityAsync("processing.file_remux_pass_completed")).FirstOrDefault(),
            "Activity to say the pass finished");
        var completed = DetailOf(Assert.Single(await scenario.ActivityAsync("processing.file_remux_pass_completed")));
        Assert.Equal("failed", (string)completed["result"]!);
        Assert.Equal("tiny.mkv could not be processed", (string)completed["user_message"]!);
        AssertPlain((string)completed["reason"]!);
        Assert.Contains("Could not safely finalize", (string)completed["technical_detail"]!, StringComparison.Ordinal);
    }

    [RealFfmpegFact]
    public async Task An_original_that_cannot_be_handed_back_leaves_no_partial_and_Activity_says_so_in_plain_words()
    {
        await using var scenario = await Scenario.StartWithRealToolsAsync();
        var (_, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "pass_through"), .. GiveUpAtOnce()]);
        var source = await WriteRealReleaseAsync(scenario);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var blocker = BlockOutput(scenario);

        await scenario.PostHandoffAsync("handoff-publish-2", source);
        var failed = await Poll.UntilAsync(
            async () => (await scenario.ActivityAsync("processing.file_pass_through_failed")).FirstOrDefault(),
            "Activity to say the original could not be handed back");

        AssertNothingPublished(scenario, blocker);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal("tiny.mkv could not be handed back", (string)failed["title"]!);
        var detail = DetailOf(failed);
        AssertPlain((string)detail["message"]!);
        Assert.Contains("The original is untouched in the watched folder.", (string)detail["next_action"]!, StringComparison.Ordinal);
        Assert.Contains("Could not safely publish", (string)detail["technical_detail"]!, StringComparison.Ordinal);
        var row = await scenario.FileRowAsync(library, Relative);
        Assert.NotNull(row);
        AssertPlain((string)row["status_reason"]!);
    }

    [RealFfmpegFact]
    public async Task An_unchanged_copy_that_cannot_be_published_leaves_no_partial_and_Activity_says_so_in_plain_words()
    {
        await using var scenario = await Scenario.StartWithRealToolsAsync();
        var (_, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "hold"), .. GiveUpAtOnce()]);
        var source = await WriteRealReleaseAsync(scenario);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var blocker = BlockOutput(scenario);

        await scenario.EnqueueFilePassAsync(Relative, library, passThroughUnchanged: true);
        var row = await scenario.WaitForFileStatusAsync(library, Relative, "processing_failed");

        AssertNothingPublished(scenario, blocker);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        AssertPlain((string)row["status_reason"]!);
    }

    private static JsonObject DetailOf(JsonObject activity) => JsonNode.Parse((string)activity["detail"]!)!.AsObject();

    private static async Task<string> WriteRealReleaseAsync(Scenario scenario)
    {
        var source = Path.Combine(scenario.Folders.Watched, Release, "tiny.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RealMedia.GenerateAsync(source, seconds: 2, "eng", "fre");
        return source;
    }

    /// <summary>A folder with a file in it where the output file belongs: the final step cannot put a file under that name.</summary>
    private static string BlockOutput(Scenario scenario)
    {
        var blocker = Path.Combine(scenario.Folders.Output, Release, "tiny.mkv");
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "keep.txt"), "not Weir's to remove");
        return blocker;
    }

    /// <summary>Nothing is in the output folder but the blocking folder, untouched: no half-written file, hidden or not.</summary>
    private static void AssertNothingPublished(Scenario scenario, string blocker)
    {
        var inRelease = Directory.EnumerateFileSystemEntries(Path.Combine(scenario.Folders.Output, Release)).ToList();
        Assert.Equal([blocker], inRelease);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(blocker).Select(Path.GetFileName));
        Assert.Equal(
            [Path.Combine(blocker, "keep.txt")],
            Directory.EnumerateFiles(scenario.Folders.Output, "*", SearchOption.AllDirectories));
    }

    /// <summary>A sentence for a person: it says what happened and what to do, and carries no path or exception text of the system.</summary>
    private static void AssertPlain(string text)
    {
        Assert.True(!text.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal), $"a path in: {text}");
        Assert.True(!text.Contains("Could not safely", StringComparison.Ordinal), $"exception text in: {text}");
        Assert.True(!text.Contains("Access to the path", StringComparison.Ordinal), $"the system's words in: {text}");
        Assert.True(text.Contains("output folder", StringComparison.Ordinal), $"no mention of the output folder in: {text}");
    }
}
