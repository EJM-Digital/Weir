using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// "Retry failures of the tools" on a workflow: a tool that fails on a file is tried again after the wait, up to the workflow's attempts,
/// or not at all when the workflow does not retry them. Switched off, the file waits for the person, with no retry on the clock.
/// </summary>
[ContractArea("processing")]
public sealed class RetryExecutionFailuresTests
{
    private const string RemuxCrash = "Conversion failed: the fake ffmpeg was told to fail";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tool_that_fails_is_put_on_the_clock_for_another_try_only_when_the_workflow_retries_it(bool retry)
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.DelunoSetupAsync(
            library: [("failure_policy", "hold"), ("max_attempts", 10), ("retry_backoff_seconds", 3600), ("retry_execution_failures", retry)]);
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxError = RemuxCrash });
        var source = scenario.WriteRelease("Tool.Fails.Once", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));

        await scenario.PostHandoffAsync("handoff-retry-execution", source);
        var failed = await scenario.WaitForFileStatusAsync(library, "Tool.Fails.Once/film.mkv", "processing_failed");

        Assert.Equal(1, (int)failed["failure_attempts"]!);
        Assert.Equal(retry, failed["next_retry_at"] is not null);
    }
}
