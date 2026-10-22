using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>Whether a device can play a file as it is: what makes the answer yes, maybe, no or not known yet, and how each reason reads.</summary>
public sealed class DirectPlayVerdictTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static HashSet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static DeviceProfile Profile(
        IReadOnlyDictionary<string, DeviceVideoLimits>? video = null,
        IReadOnlyDictionary<string, DeviceVideoLimits>? videoMaybe = null,
        IReadOnlySet<string>? audioYes = null,
        IReadOnlySet<string>? audioMaybe = null) =>
        new(
            "tv",
            "Living room TV",
            "built in",
            string.Empty,
            Set("mkv", "mp4"),
            Set("avi"),
            video ?? new Dictionary<string, DeviceVideoLimits> { ["h264"] = new(1080, 8, null) },
            videoMaybe ?? new Dictionary<string, DeviceVideoLimits>(),
            audioYes ?? Set("aac", "ac3"),
            audioMaybe ?? None);

    private static MediaFacts Facts(string? container = "mkv", string? video = "h264", long? height = 1080, long? depth = 8, params string[]? audio) =>
        new(container, video, height, depth, audio ?? ["aac"]);

    [Theory]
    [InlineData("Films/Heat.MKV", "mkv")]
    [InlineData("heat.mp4", "mp4")]
    [InlineData("no-extension", null)]
    [InlineData("trailing-dot.", null)]
    public void The_container_is_the_files_extension_in_lower_case(string path, string? container)
    {
        Assert.Equal(container, DirectPlayEvaluation.ContainerForPath(path));
    }

    [Fact]
    public void A_file_the_device_plays_throughout_is_a_yes_with_no_reasons()
    {
        var verdict = DirectPlayEvaluation.Evaluate(Profile(), Facts());

        Assert.Equal(("tv", "Living room TV", "yes"), (verdict.DeviceId, verdict.DeviceName, verdict.Verdict));
        Assert.Empty(verdict.Reasons);
    }

    [Theory]
    [InlineData(null, "h264", "unknown")]
    [InlineData("mkv", null, "unknown")]
    [InlineData("mkv", "  ", "unknown")]
    public void A_fact_that_has_not_been_measured_makes_the_answer_unknown(string? container, string? video, string verdict)
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(container, video));

        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(["Weir has not measured this file yet"], result.Reasons);
    }

    [Fact]
    public void A_file_whose_audio_has_not_been_measured_is_unknown_too()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), new MediaFacts("mkv", "h264", 1080, 8, null));

        Assert.Equal("unknown", result.Verdict);
    }

    [Fact]
    public void A_known_problem_outweighs_an_unmeasured_fact()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(container: "wmv", video: null));

        Assert.Equal("no", result.Verdict);
        Assert.Equal(["cannot play WMV files"], result.Reasons);
    }

    [Fact]
    public void A_container_the_device_plays_only_sometimes_is_a_maybe()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(container: "avi"));

        Assert.Equal("maybe", result.Verdict);
        Assert.Equal(["may not play AVI files"], result.Reasons);
    }

    [Fact]
    public void A_video_taller_than_the_device_allows_is_a_no_naming_both_heights()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(height: 2160));

        Assert.Equal(["cannot play H.264 video (its 2160p video is above 1080p)"], result.Reasons);
    }

    [Fact]
    public void A_video_with_more_bits_than_the_device_allows_is_a_no_naming_both_depths()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(depth: 10));

        Assert.Equal(["cannot play H.264 video (its 10-bit video is above 8-bit)"], result.Reasons);
    }

    [Fact]
    public void A_video_measured_without_a_height_or_depth_is_not_held_to_the_limits()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(height: null, depth: null));

        Assert.Equal("yes", result.Verdict);
    }

    [Fact]
    public void A_video_the_device_plays_only_inside_certain_containers_is_a_no_in_the_others()
    {
        var video = new Dictionary<string, DeviceVideoLimits> { ["h264"] = new(null, null, Set("mp4")) };

        var result = DirectPlayEvaluation.Evaluate(Profile(video), Facts(container: "mkv"));

        Assert.Equal(["cannot play H.264 video (it plays that video only in MP4)"], result.Reasons);
    }

    [Fact]
    public void A_video_over_the_usual_limit_but_inside_the_sometimes_limit_is_a_maybe()
    {
        var video = new Dictionary<string, DeviceVideoLimits> { ["hevc"] = new(1080, null, null) };
        var sometimes = new Dictionary<string, DeviceVideoLimits> { ["hevc"] = new(2160, null, null) };

        var result = DirectPlayEvaluation.Evaluate(Profile(video, sometimes), Facts(video: "hevc", height: 2160));

        Assert.Equal("maybe", result.Verdict);
        Assert.Equal(["may not play HEVC video (its 2160p video is above 1080p)"], result.Reasons);
    }

    [Fact]
    public void A_video_the_device_plays_only_sometimes_is_a_maybe_or_a_no_by_its_limits()
    {
        var sometimes = new Dictionary<string, DeviceVideoLimits> { ["vp9"] = new(1080, null, null) };
        var profile = Profile(videoMaybe: sometimes);

        var fits = DirectPlayEvaluation.Evaluate(profile, Facts(video: "vp9", height: 720));
        var tooTall = DirectPlayEvaluation.Evaluate(profile, Facts(video: "vp9", height: 2160));

        Assert.Equal(("maybe", "may not play VP9 video"), (fits.Verdict, Assert.Single(fits.Reasons)));
        Assert.Equal(("no", "cannot play VP9 video (its 2160p video is above 1080p)"), (tooTall.Verdict, Assert.Single(tooTall.Reasons)));
    }

    [Fact]
    public void A_video_codec_the_device_does_not_list_is_a_no_named_by_its_own_letters()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(video: " Theora "));

        Assert.Equal(["cannot play THEORA video"], result.Reasons);
    }

    [Theory]
    [InlineData("flac,opus", "no", "cannot play FLAC, Opus audio")]
    [InlineData("aac,flac", "maybe", "may not play FLAC audio on some tracks")]
    [InlineData("pcm_s16le", "no", "cannot play PCM audio")]
    [InlineData("aac,ac3", "yes", null)]
    public void Audio_the_device_cannot_play_decides_the_answer_by_whether_anything_else_is_playable(string codecs, string verdict, string? reason)
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), Facts(audio: codecs.Split(',')));

        Assert.Equal(verdict, result.Verdict);
        string[] expected = reason is null ? [] : [reason];
        Assert.Equal(expected, result.Reasons);
    }

    [Fact]
    public void Audio_the_device_plays_only_sometimes_is_a_maybe_when_nothing_else_is_certain()
    {
        var profile = Profile(audioYes: Set("aac"), audioMaybe: Set("dts", "eac3"));

        var result = DirectPlayEvaluation.Evaluate(profile, Facts(audio: ["eac3", "dts", "eac3"]));

        Assert.Equal("maybe", result.Verdict);
        Assert.Equal(["may not play DTS, Dolby Digital Plus audio"], result.Reasons);
    }

    [Fact]
    public void A_file_with_no_audio_tracks_is_not_held_against_the_device()
    {
        var result = DirectPlayEvaluation.Evaluate(Profile(), new MediaFacts("mkv", "h264", 1080, 8, []));

        Assert.Equal("yes", result.Verdict);
    }
}
