using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// "Keep a failed file's half-written copy for a day" (Settings › Performance): a pass that fails after the tool began writing leaves
/// the copy in the work folder when the setting is on, and takes it away when it is off.
/// </summary>
[ContractArea("processing")]
public sealed class KeepFailedWorkFilesTests
{
    private const string RemuxCrash = "Conversion failed: the fake ffmpeg was told to fail";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_pass_leaves_its_half_written_copy_only_when_the_setting_is_on(bool keep)
    {
        await using var scenario = await Scenario.StartAsync();
        var saved = await scenario.Admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/operator-settings", new JsonObject { ["keep_failed_work_files"] = keep });
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.Status);
        var (_, library) = await scenario.DelunoSetupAsync(library: [("failure_policy", "hold"), ("max_attempts", 1), ("retry_backoff_seconds", 1)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxError = RemuxCrash, RemuxDelaySeconds = 0.1 });
        var source = scenario.WriteRelease("Half.Written", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));

        await scenario.PostHandoffAsync("handoff-keep-failed", source);
        await scenario.WaitForFileStatusAsync(library, "Half.Written/film.mkv", "processing_failed");

        var left = Directory.EnumerateFiles(scenario.Folders.Work, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(keep, left.Count > 0);
    }
}
