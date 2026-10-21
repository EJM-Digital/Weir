using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>Planning edges: a tag-less commentary track, tracks dropped from a language slot, attachments with no list, and subtitle ordering by language.</summary>
public sealed class RemuxRulesPlanningEdgeTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    private static readonly ProbeStreamInfo Video = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");

    private static ProbeStreamInfo Audio(int index, string? language, int channels = 2, string title = "") =>
        Stream(
            $$"""{"index": {{index}}, "codec_type": "audio", "codec_name": "aac", "channels": {{channels}}, "bit_rate": "{{channels * 64000}}", "tags": {{Tags(language, title)}}}""");

    private static ProbeStreamInfo Subtitle(int index, string language, string title = "") =>
        Stream($$"""{"index": {{index}}, "codec_type": "subtitle", "codec_name": "subrip", "tags": {{Tags(language, title)}}}""");

    private static string Tags(string? language, string title)
    {
        var parts = new List<string>();
        if (language is not null)
        {
            parts.Add($"\"language\": \"{language}\"");
        }

        if (title.Length > 0)
        {
            parts.Add($"\"title\": \"{title}\"");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    [Fact]
    public void A_commentary_track_with_no_language_is_recorded_as_undetermined()
    {
        var config = RemuxRules.DefaultConfig() with { RemoveCommentary = true };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng"), Audio(2, null, title: "Director's Commentary")], [], config);

        Assert.NotNull(plan);
        var record = Assert.Single(plan!.RemovedTrackRecords);
        Assert.Equal("und", record.Language);
        Assert.StartsWith("und (commentary excluded", Assert.Single(plan.RemovedAudio), StringComparison.Ordinal);
    }

    [Fact]
    public void Per_language_mode_reports_each_track_that_lost_its_slot()
    {
        var config = RemuxRules.DefaultConfig() with
        {
            AudioKeepMode = RemuxRuleValues.AudioKeepModePerLanguage,
            PrimaryAudioLang = "eng",
        };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng", channels: 6), Audio(2, "eng"), Audio(3, null)], [], config);

        Assert.NotNull(plan);
        Assert.Equal([1], plan!.Audio.Select(track => track.InputIndex));
        Assert.Equal(2, plan.RemovedAudio.Count);
        Assert.All(plan.RemovedAudio, entry => Assert.EndsWith("removed (not selected for its language slot)", entry, StringComparison.Ordinal));
        Assert.Equal(["eng", "und"], plan.RemovedTrackRecords.Select(record => record.Language));
        Assert.All(plan.RemovedTrackRecords, record => Assert.Equal("not selected for its language slot", record.Reason));
        Assert.Contains(plan.AudioSelectionNotes, note => note.StartsWith("Removed non-selected", StringComparison.Ordinal));
    }

    [Fact]
    public void Per_language_mode_with_only_commentary_left_plans_nothing()
    {
        var config = RemuxRules.DefaultConfig() with
        {
            AudioKeepMode = RemuxRuleValues.AudioKeepModePerLanguage,
            RemoveCommentary = true,
        };

        Assert.Null(RemuxRules.PlanRemux([Video], [Audio(1, "eng", title: "Commentary")], [], config));
    }

    [Fact]
    public void Removing_attachments_with_no_attachment_list_removes_nothing()
    {
        var config = RemuxRules.DefaultConfig() with { Metadata = new MetadataRules { RemoveAttachments = true } };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng")], [], config);

        Assert.NotNull(plan);
        Assert.Empty(plan!.RemovedAttachments);
    }

    private static ProcessingRulesConfig KeepSubtitles(string mode, string[] languages, string order) => RemuxRules.DefaultConfig() with
    {
        SubtitleMode = mode,
        SubtitleLangs = languages,
        SubtitleSortersJson = order,
    };

    private const string ByLanguage = """[{"field":"language","value":"","reversed":false}]""";

    [Theory]
    // A variant that is not itself listed ranks as its base language.
    [InlineData(new[] { "eng", "por" }, new[] { 3, 2 })]
    // A listed variant ranks as itself.
    [InlineData(new[] { "eng", "por-BR" }, new[] { 3, 2 })]
    [InlineData(new[] { "por-BR", "eng" }, new[] { 2, 3 })]
    public void Subtitles_are_ordered_by_where_their_language_sits_in_the_list(string[] languages, int[] expected)
    {
        var subtitles = new[] { Subtitle(2, "por", "Brazilian"), Subtitle(3, "eng") };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng")], subtitles, KeepSubtitles(RemuxRuleValues.SubtitleModeKeepSelected, languages, ByLanguage));

        Assert.NotNull(plan);
        Assert.Equal(expected, plan!.Subtitles.Select(track => track.InputIndex));
    }

    [Fact]
    public void Keeping_every_language_leaves_a_language_order_to_the_position_the_tracks_already_have()
    {
        var subtitles = new[] { Subtitle(2, "por"), Subtitle(3, "eng") };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng")], subtitles, KeepSubtitles(RemuxRuleValues.SubtitleModeKeepAll, [], ByLanguage));

        Assert.NotNull(plan);
        Assert.Equal([2, 3], plan!.Subtitles.Select(track => track.InputIndex));
    }
}
