using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Checks the fake ffprobe and ffmpeg by running them directly, so the scenarios that script them can rely on them.</summary>
[ContractArea("harness")]
public sealed class FakeFfmpegTests : IDisposable
{
    private readonly FakeFfmpeg _tools = FakeFfmpeg.Install();
    private readonly TemporaryFolder _files = new();

    public void Dispose()
    {
        _files.Dispose();
        _tools.Dispose();
    }

    [Fact]
    public async Task Ffprobe_reports_the_answer_a_fake_media_file_carries()
    {
        var probe = FakeMedia.Probe(audioLanguages: ["eng", "fre"], subtitleLanguages: ["eng"], durationSeconds: 90);
        var file = Write("film.mkv", FakeMedia.Bytes(probe));

        var result = await RunAsync("ffprobe", "-v", "error", "-print_format", "json", file);

        Assert.Equal(0, result.ExitCode);
        var streams = JsonNode.Parse(result.Output)!["streams"]!.AsArray();
        Assert.Equal(["video", "audio", "audio", "subtitle"], streams.Select(s => (string)s!["codec_type"]!));
        Assert.Equal("fre", (string)streams[2]!["tags"]!["language"]!);
        Assert.Equal("90.000000", (string)streams[0]!["duration"]!);
        Assert.Equal("90.0", (string)JsonNode.Parse(result.Output)!["format"]!["duration"]!);
    }

    [Fact]
    public async Task Ffprobe_describes_any_other_file_by_the_first_matching_rule_then_the_default()
    {
        var other = FakeMedia.Probe(video: 0, audioLanguages: ["ger"]);
        _tools.SetFileRule("*.avi", new FileRule { Probe = other });
        _tools.SetFileRule("*.m??", new FileRule { Probe = FakeMedia.Probe(audioLanguages: []) });
        var avi = Write("a.avi", "not media"u8.ToArray());
        var mkv = Write("b.mkv", "not media"u8.ToArray());
        var mov = Write("c.MOV", "not media"u8.ToArray());
        var plain = Write("d.ts", "not media"u8.ToArray());

        var byRule = await RunAsync("ffprobe", avi);
        var byGlob = await RunAsync("ffprobe", mkv);
        var byDefault = await RunAsync("ffprobe", plain);
        var byCaseInsensitiveGlob = await RunAsync("ffprobe", mov);

        Assert.Equal(["audio"], Kinds(byRule.Output));
        Assert.Equal(["video"], Kinds(byGlob.Output));
        Assert.Equal(["video", "audio"], Kinds(byDefault.Output));
        Assert.Equal(OperatingSystem.IsWindows() ? ["video"] : ["video", "audio"], Kinds(byCaseInsensitiveGlob.Output));
    }

    [Fact]
    public async Task Ffprobe_fails_for_a_missing_file_and_for_a_scripted_error_but_not_for_a_staged_output()
    {
        _tools.SetFileRule("film.mkv", new FileRule { ProbeError = "Invalid data found when processing input" });
        var source = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        var staged = Write("film.processing.ab12cd34.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        _tools.SetFileRule("film.processing.*", new FileRule { ProbeError = "scripted" });

        var missing = await RunAsync("ffprobe", Path.Combine(_files.Path, "gone.mkv"));
        var scripted = await RunAsync("ffprobe", source);
        var stagedOutput = await RunAsync("ffprobe", staged);

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("No such file or directory", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, scripted.ExitCode);
        Assert.Equal("Invalid data found when processing input", scripted.Error.Trim());
        Assert.Equal(0, stagedOutput.ExitCode);
    }

    [Fact]
    public async Task Ffprobe_takes_the_time_a_rule_gives_it_before_it_answers()
    {
        const double delaySeconds = 0.5;
        _tools.SetFileRule("film.mkv", new FileRule { ProbeDelaySeconds = delaySeconds });
        var file = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));

        var clock = Stopwatch.StartNew();
        var result = await RunAsync("ffprobe", file);
        clock.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(delaySeconds), $"ffprobe answered after {clock.Elapsed.TotalSeconds:0.00}s.");
    }

    [Fact]
    public async Task Ffmpeg_answers_a_capability_query_and_logs_it()
    {
        var result = await RunAsync("ffmpeg", "-hide_banner", "-hwaccels");

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Hardware acceleration methods:", result.Output, StringComparison.Ordinal);
        var call = Assert.Single(_tools.Calls(tool: "ffmpeg", step: "query"));
        Assert.Contains("-hwaccels", call.Arguments);
    }

    [Fact]
    public async Task Ffmpeg_fails_the_integrity_read_through_only_when_scripted()
    {
        var file = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        var fine = await RunAsync("ffmpeg", "-v", "error", "-i", file, "-f", "null", "-");
        _tools.SetFileRule("film.mkv", new FileRule { IntegrityError = "moov atom not found" });

        var broken = await RunAsync("ffmpeg", "-v", "error", "-i", file, "-f", "null", "-");

        Assert.Equal(0, fine.ExitCode);
        Assert.Equal(1, broken.ExitCode);
        Assert.Equal("moov atom not found", broken.Error.Trim());
        Assert.Equal(2, _tools.Calls(tool: "ffmpeg", step: "integrity").Count);
        Assert.All(_tools.Calls(tool: "ffmpeg", step: "integrity"), call => Assert.Equal("film.mkv", call.File));
    }

    [Fact]
    public async Task A_remux_keeps_the_mapped_streams_renumbered_and_applies_the_dispositions()
    {
        var probe = FakeMedia.Probe(audioLanguages: ["eng", "fre"], subtitleLanguages: ["eng"]);
        ((JsonObject)probe["streams"]![2]!)["disposition"] = new JsonObject { ["default"] = 1, ["comment"] = 1 };
        var source = Write("film.mkv", FakeMedia.Bytes(probe));
        var output = Path.Combine(_files.Path, "film.processing.0001.mkv");

        var result = await RunAsync(
            "ffmpeg", "-i", source, "-map", "0:0", "-map", "0:2", "-map", "0:3", "-map", "0:a:0", "-map", "0:99",
            "-disposition:a:0", "+forced-default", "-disposition:s:0", "forced", "-progress", "pipe:1", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("progress=end", result.Output, StringComparison.Ordinal);
        var written = Encoding.UTF8.GetString(File.ReadAllBytes(output));
        Assert.StartsWith("FAKEMEDIA:", written, StringComparison.Ordinal);
        var streams = JsonNode.Parse(written["FAKEMEDIA:".Length..])!["streams"]!.AsArray();
        Assert.Equal(["video", "audio", "subtitle"], streams.Select(s => (string)s!["codec_type"]!));
        Assert.Equal([0, 1, 2], streams.Select(s => (int)s!["index"]!));
        Assert.Equal("fre", (string)streams[1]!["tags"]!["language"]!);
        Assert.Equal(0, (int)streams[1]!["disposition"]!["default"]!);
        Assert.Equal(1, (int)streams[1]!["disposition"]!["comment"]!);
        Assert.Equal(1, (int)streams[1]!["disposition"]!["forced"]!);
        Assert.Equal(1, (int)streams[2]!["disposition"]!["forced"]!);
        Assert.Equal(60.0, double.Parse((string)JsonNode.Parse(written["FAKEMEDIA:".Length..])!["format"]!["duration"]!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_remux_can_be_told_to_fail_only_the_first_times_and_counts_its_attempts()
    {
        var source = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        _tools.SetFileRule("film.mkv", new FileRule { RemuxError = "Conversion failed", RemuxFailTimes = 2 });
        var output = Path.Combine(_files.Path, "out.mkv");

        var exits = new List<int>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            exits.Add((await RunAsync("ffmpeg", "-i", source, "-map", "0:0", output)).ExitCode);
        }

        Assert.Equal([1, 1, 0], exits);
        Assert.Equal([1, 2, 3], _tools.Calls(tool: "ffmpeg", step: "remux").Select(call => call.Attempt!.Value));
    }

    [Fact]
    public async Task A_remux_with_a_release_file_stays_in_progress_with_a_partial_output_until_the_file_appears()
    {
        var source = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        var release = Path.Combine(_files.Path, "release");
        var output = Path.Combine(_files.Path, "out.mkv");
        _tools.SetFileRule("film.mkv", new FileRule { RemuxReleaseFile = release });

        using var process = Start("ffmpeg", "-i", source, "-map", "0:0", output);
        await Poll.UntilAsync(() => Task.FromResult(File.Exists(output)), "the partial output to be written");
        Assert.False(process.HasExited);
        Assert.Equal("partial fake output", await File.ReadAllTextAsync(output));

        await File.WriteAllTextAsync(release, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);
        Assert.StartsWith("FAKEMEDIA:", await File.ReadAllTextAsync(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remux_reports_how_far_it_has_come_step_by_step_each_once_the_file_it_waits_for_exists()
    {
        var source = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));
        var next = Path.Combine(_files.Path, "next");
        var release = Path.Combine(_files.Path, "release");
        var output = Path.Combine(_files.Path, "out.mkv");
        _tools.SetFileRule("film.mkv", new FileRule { RemuxReleaseFile = release, RemuxProgress = [new RemuxProgressStep(15), new RemuxProgressStep(36, next)] });
        using var process = Start("ffmpeg", "-i", source, "-map", "0:0", "-progress", "pipe:1", output);

        Assert.Equal("out_time_ms=15000000", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("progress=continue", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(process.HasExited);

        await File.WriteAllTextAsync(next, "go");

        Assert.Equal("out_time_ms=36000000", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        await File.WriteAllTextAsync(release, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Concurrent_tools_log_every_call_without_losing_any()
    {
        var source = Write("film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            RunAsync("ffmpeg", "-i", source, "-map", "0:0", Path.Combine(_files.Path, $"out{i}.mkv"))));

        var calls = _tools.Calls(tool: "ffmpeg", step: "remux");
        Assert.Equal(12, calls.Count);
        Assert.Equal(Enumerable.Range(1, 12), calls.Select(call => call.Attempt!.Value).Order());
    }

    private static string[] Kinds(string probeOutput) =>
        [.. JsonNode.Parse(probeOutput)!["streams"]!.AsArray().Select(stream => (string)stream!["codec_type"]!)];

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_files.Path, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private Process Start(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(_tools.Folder, tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string tool, params string[] arguments)
    {
        using var process = Start(tool, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await output, await error);
    }
}
