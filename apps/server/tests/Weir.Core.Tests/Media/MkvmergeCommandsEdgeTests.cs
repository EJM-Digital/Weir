using System.Text.Json;
using Weir.Core.Media;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Media;

/// <summary>The identify and version commands, track names, and the malformed shapes mkvmerge and ffprobe documents can take.</summary>
public sealed class MkvmergeCommandsEdgeTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<ProbeStreamInfo> Streams(string json) =>
        [.. Parse(json).EnumerateArray().Select(element => new ProbeStreamInfo(element))];

    private static PlannedTrack Track(int index, TrackKind kind, string lang = "eng") =>
        new() { InputIndex = index, LangLabel = lang, Kind = kind };

    [Fact]
    public void Identifying_a_file_reads_its_headers_as_json()
    {
        Assert.Equal(["mkvmerge", "-J", "in.mkv"], MkvmergeCommands.BuildIdentifyArgv("mkvmerge", "in.mkv"));
    }

    [Fact]
    public void The_version_check_asks_for_the_version()
    {
        Assert.Equal(["mkvmerge", "--version"], MkvmergeCommands.BuildVersionArgv("mkvmerge"));
    }

    [Fact]
    public void Standardized_names_are_written_for_every_kept_audio_and_subtitle_track()
    {
        var plan = new RemuxPlan
        {
            VideoIndices = [0],
            Audio = [Track(1, TrackKind.Audio)],
            Subtitles = [Track(2, TrackKind.Subtitle, "fre")],
            Metadata = new MetadataRules { StandardizeTrackNames = true },
        };
        var trackIds = new Dictionary<int, int> { [0] = 0, [1] = 1, [2] = 2 };

        var argv = MkvmergeCommands.BuildRemuxArgv("mkvmerge", "in.mkv", "out.mkv", plan, trackIds, []);

        var names = argv.Zip(argv.Skip(1)).Where(pair => pair.First == "--track-name").Select(pair => pair.Second).ToList();
        Assert.Equal(
            [
                $"1:{TrackNaming.RenderTrackName(plan.Metadata, plan.Audio[0])}",
                $"2:{TrackNaming.RenderTrackName(plan.Metadata, plan.Subtitles[0])}",
            ],
            names);
    }

    [Fact]
    public void A_plan_that_keeps_a_stream_mkvmerge_has_no_track_for_is_refused()
    {
        var plan = new RemuxPlan { VideoIndices = [0], Audio = [Track(5, TrackKind.Audio)], Subtitles = [] };
        var trackIds = new Dictionary<int, int> { [0] = 0 };

        var error = Assert.Throws<MkvmergeTrackMappingException>(() => MkvmergeCommands.BuildRemuxArgv("mkvmerge", "in.mkv", "out.mkv", plan, trackIds, []));

        Assert.Contains("ffprobe stream 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_identification_that_is_not_an_object_has_no_tracks()
    {
        var identification = MkvmergeCommands.ParseIdentification(Parse("[]"));

        Assert.Empty(identification.Tracks);
        Assert.Empty(identification.Attachments);
        Assert.Equal(0, identification.ChapterEntries);
    }

    [Fact]
    public void Entries_that_are_not_objects_or_have_no_numeric_id_are_ignored()
    {
        var identification = MkvmergeCommands.ParseIdentification(Parse(
            """
            {"tracks":[1,{"type":"video"},{"id":"x","type":"video"},{"id":2,"type":"AUDIO"}],
             "attachments":["a",{"file_name":"cover.jpg"},{"id":4,"file_name":"font.ttf","content_type":"font/ttf"}],
             "chapters":[3,{"num_entries":"many"},{"num_entries":2},{"num_entries":5}]}
            """));

        var track = Assert.Single(identification.Tracks);
        Assert.Equal((2, "audio"), (track.Id, track.Type));
        Assert.Equal(4, Assert.Single(identification.Attachments).Id);
        Assert.Equal(7, identification.ChapterEntries);
    }

    [Fact]
    public void Tracks_attachments_and_chapters_that_are_not_lists_are_ignored()
    {
        var identification = MkvmergeCommands.ParseIdentification(Parse("""{"tracks":{},"attachments":3,"chapters":"x"}"""));

        Assert.Empty(identification.Tracks);
        Assert.Empty(identification.Attachments);
        Assert.Equal(0, identification.ChapterEntries);
    }

    [Fact]
    public void Streams_mkvmerge_does_not_count_are_left_out_of_the_mapping()
    {
        var streams = Streams(
            """
            [{"index":0,"codec_type":"video"},
             {"index":1,"codec_type":"attachment"},
             {"index":2,"codec_type":"data"},
             {"codec_type":"audio"},
             {"index":4,"codec_type":"audio"}]
            """);
        var identification = new MkvmergeIdentification([new MkvmergeTrack(0, "video"), new MkvmergeTrack(1, "audio")], [], 0);

        var map = MkvmergeCommands.MapStreamIndicesToTrackIds(streams, identification);

        Assert.Equal(new Dictionary<int, int> { [0] = 0, [4] = 1 }, map);
    }
}
