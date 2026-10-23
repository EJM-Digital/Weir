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

    [Fact]
    public async Task A_series_answer_with_no_series_in_it_is_no_match()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Json(HttpMethod.Get, "/metadata/tvdb/tv/78804", """{"provider":"tvdb"}""");
        var series = new ArtworkLookup("k", "tv", string.Empty, null, null, 78804, null, 0, ArtworkOutcomes.Pending, null, true);

        var answer = await fixture.Gateway.FindAsync(series, CancellationToken.None);

        Assert.Equal(GatewayStatus.NotFound, answer.Status);
    }

    [Theory]
    [InlineData("tv", 5L, 78804L)]
    [InlineData("movie", null, 78804L)]
    [InlineData("tv", null, null)]
    public async Task A_title_is_searched_for_unless_it_is_a_series_known_only_by_its_tvdb_id(string scope, long? tmdbId, long? tvdbId)
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"results":[]}""");
        var lookup = new ArtworkLookup("k", scope, "dark", 2017, tmdbId, tvdbId, null, 0, ArtworkOutcomes.Pending, null, true);

        await fixture.Gateway.FindAsync(lookup, CancellationToken.None);

        Assert.Empty(fixture.Http.RequestsTo(HttpMethod.Get, "/metadata/tvdb/tv/78804"));
        Assert.NotEmpty(fixture.Http.RequestsTo(HttpMethod.Get, "/metadata/search"));
    }

    [Fact]
    public async Task The_shipped_gateway_address_is_one_the_health_check_reaches()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Json(HttpMethod.Get, "/health", """{"service":"deluno-metadata-gateway","status":"ok"}""");
        var shipped = new ArtworkGatewayClient(
            fixture.Store.Options with { ArtworkGatewayUrl = ArtworkPosterSource.DefaultGatewayUrl },
            fixture.Http,
            fixture.Store.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkGatewayClient>.Instance);

        Assert.True(shipped.IsConfigured);
        Assert.Equal(GatewayStatus.Ok, await shipped.CheckHealthAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void A_busy_service_that_names_no_wait_is_left_alone_for_the_usual_pause(int? seconds)
    {
        using var fixture = new ArtworkFixture();

        fixture.Limiter.HonourBusy(seconds is { } asked ? TimeSpan.FromSeconds(asked) : null);

        Assert.True(fixture.Limiter.IsPaused);
        fixture.Store.Clock.Advance(ArtworkSchedule.DefaultBusyPause);
        Assert.False(fixture.Limiter.IsPaused);
    }

    private static async Task<(string? Key, ArtworkLookup? Lookup)> LinkedTitleAsync(ArtworkFixture fixture, long library, string path) =>
        await fixture.Store.WithUnitOfWork(
            async uow =>
            {
                var key = await fixture.Files.LookupKeyAsync(uow, library, path);
                return (key, key is null ? null : await fixture.Lookups.FindAsync(uow, key, fixture.Store.Clock.GetUtcNow()));
            },
            commit: false);

    [Fact]
    public async Task A_manager_that_names_only_a_poster_for_a_file_with_no_readable_name_has_the_title_keyed_by_that_poster()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("tv");
        const string path = "!!!/S01E02.mkv";
        Assert.Null(ArtworkTitleReader.Read("tv", path, null));

        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "tv", [path], releaseName: null, new ArtworkHints(null, null, null, null, null, 1, 2, PosterFile)));

        var (key, lookup) = await LinkedTitleAsync(fixture, library, path);
        Assert.Equal(ArtworkKeys.ForPosterRef(PosterFile), key);
        Assert.NotNull(lookup);
    }

    [Fact]
    public async Task A_file_nothing_can_be_made_of_is_linked_to_no_title()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("tv");
        const string path = "!!!/S01E02.mkv";

        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "tv", [path], releaseName: null, new ArtworkHints(null, null, null, null, null, 1, 2, null)));

        Assert.Null((await LinkedTitleAsync(fixture, library, path)).Key);
    }

    [Theory]
    [InlineData(1923, 1923)]
    [InlineData(null, 1922)]
    public async Task A_year_a_manager_gives_replaces_the_one_in_the_name_and_none_leaves_it(int? hintedYear, int expectedYear)
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("movie");
        const string path = "Nosferatu (1922)/Nosferatu.1922.mkv";

        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", [path], releaseName: null, new ArtworkHints(null, hintedYear, null, null, null, null, null, null)));

        Assert.Equal(expectedYear, (await LinkedTitleAsync(fixture, library, path)).Lookup!.Year);
    }

    [Fact]
    public void A_poster_id_that_is_not_one_names_no_file()
    {
        using var fixture = new ArtworkFixture();

        Assert.Throws<ArgumentException>(() => fixture.PosterFiles.Exists("../outside"));
        Assert.Throws<ArgumentException>(() => fixture.PosterFiles.Delete("../outside"));
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
