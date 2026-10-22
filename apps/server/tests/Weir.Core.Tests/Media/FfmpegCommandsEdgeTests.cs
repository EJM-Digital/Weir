using Weir.Core.Media;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Media;

/// <summary>The ffmpeg commands that only look at the streams a plan keeps, and the destinations that decide whether attachments are mapped.</summary>
public sealed class FfmpegCommandsEdgeTests
{
    private static RemuxPlan Plan() => new()
    {
        VideoIndices = [0],
        Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Default = true, Kind = TrackKind.Audio }],
        Subtitles = [new PlannedTrack { InputIndex = 3, LangLabel = "eng", Kind = TrackKind.Subtitle }],
    };

    [Fact]
    public void Measuring_the_kept_streams_maps_video_then_audio_then_subtitles_to_nowhere()
    {
        var argv = FfmpegCommands.BuildKeptStreamsDemuxArgv("ffmpeg", "in.mkv", Plan());

        var maps = argv.Zip(argv.Skip(1)).Where(pair => pair.First == "-map").Select(pair => pair.Second);
        Assert.Equal(["0:0", "0:1", "0:3"], maps);
        Assert.Equal(["-c", "copy", "-f", "null", "-"], argv.TakeLast(5));
    }

    [Theory]
    [InlineData("out.mkv", true)]
    [InlineData("out", true)]
    [InlineData("dir.v2/out", true)]
    [InlineData("out.mp4", false)]
    public void Attachments_are_mapped_only_into_a_container_that_can_hold_them(string destination, bool mapsAttachments)
    {
        var argv = FfmpegCommands.BuildRemuxArgv("ffmpeg", "in.mkv", destination, Plan());

        Assert.Equal(mapsAttachments, argv.Contains("0:t?"));
    }
}
