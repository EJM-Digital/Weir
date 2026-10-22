using Weir.Core.Processing;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Processing;

/// <summary>A hand-picked track choice: what is refused, what is skipped, and how the removed tracks and channel counts read.</summary>
public sealed class ManualTrackPlanEdgeTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    private static readonly ProbeStreamInfo Video = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");
    private static readonly ProbeStreamInfo Poster = Stream("""{"index": 5, "codec_type": "video", "codec_name": "mjpeg", "disposition": {"attached_pic": 1}}""");
    private static readonly ProbeStreamInfo English = Stream("""{"index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6, "tags": {"language": "eng"}}""");
    private static readonly ProbeStreamInfo Untagged = Stream("""{"index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2.5}""");
    private static readonly ProbeStreamInfo EnglishSubtitle = Stream("""{"index": 3, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng"}}""");
    private static readonly ProbeStreamInfo UntaggedSubtitle = Stream("""{"index": 4, "codec_type": "subtitle", "codec_name": "subrip"}""");

    private static readonly SplitProbeStreams Streams = new([Video, Poster], [English, Untagged], [EnglishSubtitle, UntaggedSubtitle]);

    private static ManualKeepEntry Keep(int index, bool isDefault = false, bool forced = false) => new(index, isDefault, forced);

    private static ManualPlanChoice Choice(int[] order, params ManualKeepEntry[] keep) => new(keep, order);

    private static IReadOnlyDictionary<int, ManualTrackKind> Kinds => ManualTrackPlan.ClassifyIndices(Streams);

    [Fact]
    public void Streams_without_an_index_and_embedded_pictures_cannot_be_chosen()
    {
        var indexless = Stream("""{"codec_type": "audio"}""");
        var kinds = ManualTrackPlan.ClassifyIndices(new SplitProbeStreams([Video, Poster, Stream("""{"codec_type": "video"}""")], [indexless, English], [indexless, EnglishSubtitle]));

        Assert.Equal(
            new Dictionary<int, ManualTrackKind> { [0] = ManualTrackKind.Video, [1] = ManualTrackKind.Audio, [3] = ManualTrackKind.Subtitle },
            kinds);
    }

    public static TheoryData<ManualPlanChoice, string> Refusals => new()
    {
        { Choice([]), "At least one track must be kept." },
        { Choice([0, 0], Keep(0), Keep(0)), "Stream 0 is listed more than once." },
        { Choice([0, 9], Keep(0), Keep(9)), "Stream 9 does not exist on this file." },
        { Choice([5, 1], Keep(5), Keep(1)), "Stream 5 does not exist on this file." },
        { Choice([1], Keep(1)), "At least one video track must be kept." },
        { Choice([0], Keep(0)), "At least one audio track must be kept." },
        { Choice([0, 1, 2], Keep(0), Keep(1, isDefault: true), Keep(2, isDefault: true)), "Only one audio track can be set as default." },
        { Choice([0, 1, 3, 4], Keep(0), Keep(1), Keep(3, isDefault: true), Keep(4, isDefault: true)), "Only one subtitle track can be set as default." },
        { Choice([0, 1], Keep(0), Keep(1), Keep(3)), "The track order must list every kept track exactly once." },
        { Choice([0, 1, 1], Keep(0), Keep(1), Keep(3)), "The track order must list every kept track exactly once." },
        { Choice([0, 1, 4], Keep(0), Keep(1), Keep(3)), "The track order must list every kept track exactly once." },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_choice_that_cannot_make_a_file_is_refused_with_the_reason(ManualPlanChoice choice, string expected)
    {
        Assert.False(ManualTrackPlan.TryValidate(choice, Kinds, out var problem));
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void A_complete_choice_is_accepted()
    {
        Assert.True(ManualTrackPlan.TryValidate(Choice([1, 0, 3], Keep(0), Keep(1, isDefault: true), Keep(3, forced: true)), Kinds, out var problem));
        Assert.Equal(string.Empty, problem);
    }

    [Fact]
    public void A_plan_skips_a_stream_the_file_cannot_select_and_describes_what_was_removed()
    {
        var plan = ManualTrackPlan.BuildPlan(Streams, Choice([0, 5, 9, 2, 4], Keep(0), Keep(5), Keep(9), Keep(2), Keep(4)));

        Assert.Equal([0], plan.VideoIndices);
        Assert.Equal([2], plan.Audio.Select(track => track.InputIndex));
        Assert.Equal([4], plan.Subtitles.Select(track => track.InputIndex));
        Assert.Equal(["eng (stream 1): removed by manual track choice"], plan.RemovedAudio);
        Assert.Equal(["eng"], plan.RemovedSubtitles);
    }

    [Fact]
    public void A_removed_track_with_no_language_reads_as_undetermined()
    {
        var plan = ManualTrackPlan.BuildPlan(Streams, Choice([0, 1, 3], Keep(0), Keep(1), Keep(3)));

        Assert.Equal(["und (stream 2): removed by manual track choice"], plan.RemovedAudio);
        Assert.Equal(["und"], plan.RemovedSubtitles);
    }

    [Fact]
    public void A_track_with_no_whole_number_of_channels_has_none_counted_and_the_first_audio_track_becomes_the_default()
    {
        var plan = ManualTrackPlan.BuildPlan(Streams, Choice([0, 2, 1], Keep(0), Keep(2), Keep(1)));

        Assert.Equal([2, 1], plan.Audio.Select(track => track.InputIndex));
        Assert.Equal([0, 6], plan.Audio.Select(track => track.Channels));
        Assert.Equal([true, false], plan.Audio.Select(track => track.Default));
        Assert.Equal(0, plan.DefaultAudioOutputIndex);
    }

    [Fact]
    public void A_choice_that_keeps_no_audio_has_no_default_audio_position()
    {
        var plan = ManualTrackPlan.BuildPlan(Streams, Choice([0], Keep(0)));

        Assert.Empty(plan.Audio);
        Assert.Equal(0, plan.DefaultAudioOutputIndex);
    }

    [Fact]
    public void Removed_tracks_without_an_index_are_not_described()
    {
        var indexless = Stream("""{"codec_type": "audio", "tags": {"language": "fre"}}""");
        var indexlessSubtitle = Stream("""{"codec_type": "subtitle", "tags": {"language": "fre"}}""");
        var streams = new SplitProbeStreams([Video], [English, indexless], [indexlessSubtitle]);

        var plan = ManualTrackPlan.BuildPlan(streams, Choice([0, 1], Keep(0), Keep(1)));

        Assert.Empty(plan.RemovedAudio);
        Assert.Empty(plan.RemovedSubtitles);
    }
}
