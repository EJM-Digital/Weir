using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// The one scenario that runs the real ffprobe and ffmpeg, on a two-second file generated here. Skipped when ffmpeg and ffprobe are
/// not on PATH (or <c>WEIR_CONTRACT_REAL_FFMPEG_DIR</c>). The fake tools prove the orchestration; this proves the argv Weir builds
/// is one real ffmpeg accepts.
/// </summary>
[ContractArea("processing")]
public sealed class RealFfmpegTests
{
    [RealFfmpegFact]
    public async Task Real_ffmpeg_remuxes_a_handed_off_file_and_drops_the_unwanted_language()
    {
        await using var scenario = await Scenario.StartWithRealToolsAsync();
        var source = Path.Combine(scenario.Folders.Watched, "Tiny.Real.Film.2024", "tiny.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RealMedia.GenerateAsync(source, seconds: 2, "eng", "fre");
        Assert.Equal(["eng", "fre"], await RealMedia.AudioLanguagesAsync(source));
        var (fake, _) = await scenario.DelunoSetupAsync();

        await scenario.PostHandoffAsync("handoff-real-1", source);
        await scenario.WaitForHandoffStateAsync("handoff-real-1", "completed");

        var output = Path.Combine(scenario.Folders.Output, "Tiny.Real.Film.2024", "tiny.mkv");
        Assert.True(File.Exists(output));
        Assert.Equal(["eng"], await RealMedia.AudioLanguagesAsync(output));
        Assert.Equal(["completed"], Scenario.Callbacks(fake, "handoff-real-1").Select(report => (string)report["status"]!));
    }
}
