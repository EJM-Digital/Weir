using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>Which facts about a track a sorter's match can look at, and how an unreadable match is treated.</summary>
public sealed class TrackSorterFactsTests
{
    private static long Rank(TrackSorter sorter, SortableTrack track) => Assert.Single(TrackSorters.SorterKeyComponent(sorter, track));

    [Theory]
    [InlineData("index", "5", 0)]
    [InlineData("index", "6", 1)]
    [InlineData("codec_rank", "3", 0)]
    [InlineData("codec_rank", "4", 1)]
    public void A_number_about_a_track_is_matched_by_its_digits(string field, string expected, long rank)
    {
        var track = new SortableTrack { Index = 5, CodecRank = 3 };

        Assert.Equal(rank, Rank(new TrackSorter(field, expected), track));
    }

    [Theory]
    [InlineData("dub", true, "true", 0)]
    [InlineData("dub", false, "true", 1)]
    [InlineData("audio_description", true, "true", 0)]
    [InlineData("audio_description", false, "true", 1)]
    public void A_flag_the_planner_detects_is_matched_as_true_or_nothing(string field, bool on, string expected, long rank)
    {
        var track = new SortableTrack { Dub = on, AudioDescription = on };

        Assert.Equal(rank, Rank(new TrackSorter(field, expected), track));
    }

    [Fact]
    public void A_fact_the_track_does_not_have_matches_nothing_and_sorts_as_empty()
    {
        var track = new SortableTrack();

        Assert.Equal(1, Rank(new TrackSorter("nonsense", "x"), track));
        Assert.Equal(0, Rank(new TrackSorter("nonsense", "!=x"), track));
        Assert.Equal(1, TrackSorters.SorterKeyComponent(new TrackSorter("nonsense"), track)[0]);
    }

    [Theory]
    [InlineData("5.x")]
    [InlineData("x.1")]
    [InlineData("loud")]
    public void A_channel_count_that_is_not_a_number_matches_no_track(string expected)
    {
        Assert.Equal(1, Rank(new TrackSorter("channels", expected), new SortableTrack { Channels = 6 }));
    }

    [Fact]
    public void An_empty_match_is_taken_as_equal_to_nothing()
    {
        Assert.Equal(0, Rank(new TrackSorter("title", string.Empty), new SortableTrack { Title = "Anything" }));
        Assert.Equal(0, Rank(new TrackSorter("language", string.Empty), new SortableTrack { Language = string.Empty }));
        Assert.Equal(1, Rank(new TrackSorter("language", string.Empty), new SortableTrack { Language = "eng" }));
    }
}
