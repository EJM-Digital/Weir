using Weir.Core.Artwork;
using Weir.Core.Json;

namespace Weir.Core.Tests.Artwork;

/// <summary>Artwork keys, poster addresses and titles at their edges: nothing to read, nothing recognisable, or a hint outside its range.</summary>
public sealed class ArtworkEdgeTests
{
    [Fact]
    public void A_poster_the_manager_named_without_a_title_is_keyed_by_its_reference()
    {
        Assert.Equal("poster:abcd1234.jpg", ArtworkKeys.ForPosterRef("abcd1234.jpg"));
    }

    [Fact]
    public void Only_text_in_the_shape_of_a_poster_id_is_one()
    {
        Assert.False(ArtworkKeys.IsPosterId(null));
        Assert.True(ArtworkKeys.IsPosterId(ArtworkKeys.PosterId("abcd1234.jpg")));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("99999")]
    public void A_year_outside_the_years_films_were_made_in_is_left_out(string year)
    {
        var hints = ArtworkHints.FromHandoff((WireObject)WireJsonParser.Parse($$"""{"title":"Film","year":{{year}}}"""));

        Assert.Equal(new ArtworkHints("Film", null, null, null, null, null, null, null), hints);
    }

    [Fact]
    public void A_poster_address_on_the_gateway_with_a_scheme_that_is_not_http_is_not_taken()
    {
        Assert.Null(ArtworkPosterSource.RefFromAnswerUrl("ftp://gateway.test/abcd1234.jpg", "gateway.test"));
    }

    [Theory]
    [InlineData("movies", "The.Film.1999.1080p")]
    [InlineData("tv", "The.Show.S01E01.1080p")]
    public void A_path_with_no_name_in_it_gives_the_title_from_the_release_name_alone(string scope, string releaseName)
    {
        Assert.NotNull(ArtworkTitleReader.Read(scope, string.Empty, releaseName));
    }

    [Fact]
    public void A_series_path_made_only_of_episode_markers_and_symbols_names_no_series()
    {
        Assert.Null(ArtworkTitleReader.Read("tv", "S01E01/S01E02.mkv", null));
        Assert.Null(ArtworkTitleReader.Read("tv", "!!!/S01E02.mkv", null));
    }
}
