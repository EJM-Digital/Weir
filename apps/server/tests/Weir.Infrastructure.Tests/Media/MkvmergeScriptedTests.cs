using System.Text;
using System.Text.Json;
using Weir.Core.Media;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>Everything mkvmerge can answer that a real mkvmerge will not give on request: a timeout, an error, and output that is not JSON.</summary>
public sealed class MkvmergeScriptedTests
{
    private const string EmptyIdentification = """{"tracks":[],"attachments":[]}""";

    private static readonly RemuxPlan EmptyPlan = new() { VideoIndices = [], Audio = [], Subtitles = [] };

    private static MediaTools Tools(ScriptedRunner runner, string? mkvmerge = "mkvmerge") =>
        new(runner, new FixedResolver(mkvmerge: mkvmerge), new ListLogger<MediaTools>(), TimeProvider.System);

    private static ScriptedRunner Answering(ScriptedRun run) => new(_ => run);

    [Fact]
    public async Task An_identification_that_runs_out_of_time_is_a_timeout()
    {
        var runner = Answering(new ScriptedRun { Timeout = ProcessTimeoutKind.Overall });

        await Assert.ThrowsAsync<MediaToolTimeoutException>(() => Tools(runner).IdentifyMkvmergeAsync("mkvmerge", "film.mkv"));
    }

    [Fact]
    public async Task An_identification_that_fails_says_what_mkvmerge_said()
    {
        var runner = Answering(new ScriptedRun { ExitCode = 2, Stderr = Encoding.UTF8.GetBytes("Error: The file 'film.mkv' could not be opened for reading.") });

        var error = await Assert.ThrowsAsync<MediaToolException>(() => Tools(runner).IdentifyMkvmergeAsync("mkvmerge", "film.mkv"));

        Assert.StartsWith("mkvmerge could not identify the file: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("could not be opened for reading", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identification_that_is_not_JSON_is_refused_in_one_sentence()
    {
        var runner = Answering(new ScriptedRun { Stdout = "not json at all"u8.ToArray() });

        var error = await Assert.ThrowsAsync<MediaToolException>(() => Tools(runner).IdentifyMkvmergeAsync("mkvmerge", "film.mkv"));

        Assert.Equal("mkvmerge's identification output was not valid JSON.", error.Message);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task A_write_that_runs_out_of_time_is_a_timeout()
    {
        var runner = Answering(new ScriptedRun { Timeout = ProcessTimeoutKind.Overall });

        await Assert.ThrowsAsync<MediaToolTimeoutException>(() => Tools(runner).RunMkvmergeAsync(["mkvmerge"]));
    }

    [Fact]
    public void Ffmpeg_can_write_any_container()
    {
        var writer = new FfmpegRemuxWriter(Tools(Answering(new ScriptedRun())));

        Assert.True(writer.CanWrite("out.mkv"));
        Assert.True(writer.CanWrite("out.mp4"));
        Assert.Equal("ffmpeg", writer.Name);
    }

    [Fact]
    public async Task A_write_without_mkvmerge_installed_says_so()
    {
        var tools = Tools(Answering(new ScriptedRun()), mkvmerge: null);
        var writer = new MkvmergeRemuxWriter(tools, new FixedResolver());
        var request = new RemuxWriteRequest("film.mkv", "out.mkv", EmptyPlan, JsonDocument.Parse("{}").RootElement);

        var error = await Assert.ThrowsAsync<MediaToolException>(() => writer.WriteAsync(request));

        Assert.Equal("mkvmerge was not found, so it cannot write this file.", error.Message);
        Assert.Equal(ToolFailureText.WriterMissing, error.PlainMessage);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"streams":"none"}""")]
    public async Task A_source_probe_without_a_list_of_streams_is_written_as_having_none(string probe)
    {
        var runner = new ScriptedRunner(request => request.Argv.Contains("-J")
            ? new ScriptedRun { Stdout = Encoding.UTF8.GetBytes(EmptyIdentification) }
            : new ScriptedRun());
        var tools = Tools(runner);
        var writer = new MkvmergeRemuxWriter(tools, new FixedResolver(mkvmerge: "mkvmerge"));
        using var document = JsonDocument.Parse(probe);

        await writer.WriteAsync(new RemuxWriteRequest("film.mkv", "out.mkv", EmptyPlan, document.RootElement));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("--no-video", runner.Requests[1].Argv);
    }
}
