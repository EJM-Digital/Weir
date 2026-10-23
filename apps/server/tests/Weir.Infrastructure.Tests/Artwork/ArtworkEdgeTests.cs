using System.Net;
using System.Net.Http.Headers;
using Weir.Core.Artwork;
using Weir.Core.Rules;
using Weir.Infrastructure.Artwork;

namespace Weir.Infrastructure.Tests.Artwork;

/// <summary>What the poster services do with answers and files that are not the ordinary kind.</summary>
public sealed class ArtworkEdgeTests
{
    private const string PosterFile = "zv7J85D8CC9qYagAEhPM63CIG6j.jpg";

    private static ArtworkLookup Lookup(string title, long? tmdbId = null) => new("k", "movie", title, null, tmdbId, null, null, 0, ArtworkOutcomes.Pending, null, true);

    [Fact]
    public async Task A_pass_over_more_files_than_one_batch_titles_every_one_of_them()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.Store.Execute(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 201) " +
            $"INSERT INTO files (library_id, relative_path, status) SELECT {library}, 'Charade (1963)/Charade.1963.part' || i || '.mkv', 'processed' FROM n");

        await fixture.Discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(201, await fixture.Store.Scalar("SELECT count(*) FROM artwork_files WHERE lookup_key IS NOT NULL"));
    }

    [Fact]
    public async Task A_poster_image_that_cannot_be_removed_is_left_and_the_pass_carries_on()
    {
        using var fixture = new ArtworkFixture();
        var posterId = ArtworkKeys.PosterId(PosterFile);
        await fixture.Store.Execute($"INSERT INTO artwork_posters (poster_id, source_ref, content_type, size_bytes) VALUES ('{posterId}', '{PosterFile}', 'image/jpeg', 7)");
        var blocker = Path.Join(fixture.Store.Options.WeirHome, "artwork", "posters", posterId);
        Directory.CreateDirectory(blocker);

        var removed = await fixture.Pruner.PruneAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM artwork_posters"));
        Assert.True(Directory.Exists(blocker));
    }

    [Fact]
    public async Task A_search_waits_out_a_pause_before_it_goes_ahead()
    {
        using var fixture = new ArtworkFixture();
        fixture.Limiter.HonourUnreachable();

        var waiting = fixture.Limiter.WaitForSearchAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        fixture.Store.Clock.Advance(ArtworkSchedule.UnreachablePause);
        await waiting;
        Assert.False(fixture.Limiter.IsPaused);
    }

    [Fact]
    public async Task An_image_that_turns_out_larger_than_allowed_while_it_is_read_is_refused()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Route(HttpMethod.Get, $"/artwork/w342/{PosterFile}", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedImage(6 * 1024 * 1024) });

        var answer = await fixture.Gateway.FetchPosterAsync(PosterFile, CancellationToken.None);

        Assert.Equal(GatewayStatus.NotFound, answer.Status);
    }

    [Fact]
    public async Task A_search_answer_with_no_list_of_results_is_no_match()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"provider":"deluno-broker"}""");

        var answer = await fixture.Gateway.FindAsync(Lookup("nosferatu"), CancellationToken.None);

        Assert.Equal(GatewayStatus.NotFound, answer.Status);
    }

    [Fact]
    public async Task A_title_with_no_name_and_no_id_is_searched_for_with_an_empty_query()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"results":[]}""");

        await fixture.Gateway.FindAsync(Lookup(string.Empty), CancellationToken.None);

        Assert.Equal(string.Empty, ArtworkFixture.QueryValue(fixture.Searches().Single(), "query"));
    }

    [Fact]
    public async Task A_busy_answer_that_names_a_time_rather_than_a_delay_is_waited_for_until_then()
    {
        using var fixture = new ArtworkFixture();
        var until = fixture.Store.Clock.GetUtcNow().AddSeconds(90);
        fixture.Http.Route(HttpMethod.Get, "/metadata/search", _ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(until);
            return busy;
        });

        var answer = await fixture.Gateway.FindAsync(Lookup("nosferatu"), CancellationToken.None);

        Assert.Equal((GatewayStatus.Busy, TimeSpan.FromSeconds(90)), (answer.Status, answer.RetryAfter));
    }

    [Fact]
    public async Task A_file_whose_name_gives_no_title_has_no_original_language_to_look_up()
    {
        using var fixture = new ArtworkFixture();

        var result = await fixture.OriginalLanguages.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), "1080p.x265.mkv", origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusNoMatch, result.Status);
        Assert.Empty(fixture.Searches());
    }

    private sealed class UnsizedImage(int size) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[size]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
