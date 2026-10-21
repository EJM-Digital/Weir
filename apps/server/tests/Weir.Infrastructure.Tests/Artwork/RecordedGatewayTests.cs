using System.Net;
using System.Text;
using System.Web;
using Weir.Core.Artwork;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.Artwork;

/// <summary>
/// The poster and original-language lookups against what Deluno's metadata service answered when it was recorded
/// (<c>fixtures/recorded/metadata-gateway</c>, read-only requests to the public service, as Weir makes them). Every reply is a
/// recorded one, and a request nothing was recorded for fails the test. The poster image itself and the service's busy answer
/// (503 with Retry-After) were not recorded: the service has no way to be made busy on demand, and an image is a file, not an answer.
/// </summary>
public sealed class RecordedGatewayTests
{
    private const string BuckBunnyPoster = "i9jJzvoXET4D9pOkoEwncSdNNER.jpg";
    private const string BuckBunnyPath = "Big Buck Bunny (2008)/Big.Buck.Bunny.2008.1080p.mkv";
    private const string UnknownPath = "Zzqxv Nonexistent Title Qq (1901)/Zzqxv.Nonexistent.Title.Qq.1901.1080p.mkv";

    private static ArrRecording Recorded(string state) => ArrRecording.Load("metadata-gateway", "2026-10-20", state);

    private static ArtworkLookup Film(string title, int? year, long? tmdbId = null) =>
        new("k", "movie", title, year, tmdbId, null, null, 0, ArtworkOutcomes.Pending, null, true);

    /// <summary>Makes the fixture's metadata service answer each of <paramref name="states"/> to the request recorded for it, and nothing else.</summary>
    private static void Serve(ArtworkFixture fixture, params string[] states)
    {
        var recordings = states.Select(Recorded).ToList();
        foreach (var path in recordings.Select(recording => recording.Only.Request.Path).Distinct())
        {
            fixture.Http.Route(HttpMethod.Get, path, request => RecordedAnswer(request, recordings));
        }
    }

    private static HttpResponseMessage RecordedAnswer(RecordedRequest request, IReadOnlyList<ArrRecording> recordings)
    {
        var asked = QueryOf(request.Uri.Query);
        var recording = recordings.FirstOrDefault(candidate => candidate.Only.Request.Path == request.Uri.AbsolutePath && candidate.Only.Request.Query == asked)
            ?? throw new Xunit.Sdk.XunitException(
                $"Nothing was recorded for GET {request.Uri.PathAndQuery}. Recordings in play: {string.Join(", ", recordings.Select(candidate => candidate.File))}.");
        var reply = recording.Only.Reply;
        return new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new StringContent(reply.Text, Encoding.UTF8, "application/json") };
    }

    private static string QueryOf(string query)
    {
        var pairs = HttpUtility.ParseQueryString(query);
        return string.Join('&', pairs.AllKeys.OfType<string>().Order(StringComparer.Ordinal).Select(key => $"{key}={pairs[key]}"));
    }

    [Fact]
    public async Task A_film_is_found_by_title_and_year_and_the_poster_id_and_language_are_read_from_the_answer()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-film-by-title");

        var answer = await fixture.Gateway.FindAsync(Film("big buck bunny", 2008), CancellationToken.None);

        Assert.Equal(GatewayStatus.Ok, answer.Status);
        Assert.Equal((BuckBunnyPoster, 10378L, "en"), (answer.Value!.PosterRef, answer.Value.TmdbId, answer.Value.OriginalLanguage));
    }

    [Fact]
    public async Task A_film_a_manager_named_by_its_TMDb_id_is_looked_up_with_that_id_whether_or_not_it_gave_a_title()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-film-by-tmdb-id", "search-film-by-tmdb-id-only");

        var withTitle = await fixture.Gateway.FindAsync(Film("big buck bunny", 2008, tmdbId: 10378), CancellationToken.None);
        var idOnly = await fixture.Gateway.FindAsync(Film(string.Empty, null, tmdbId: 10378), CancellationToken.None);

        foreach (var answer in new[] { withTitle, idOnly })
        {
            Assert.Equal(GatewayStatus.Ok, answer.Status);
            Assert.Equal((BuckBunnyPoster, 10378L), (answer.Value!.PosterRef, answer.Value.TmdbId));
        }
    }

    [Fact]
    public async Task The_original_language_is_the_one_of_the_first_result_with_a_poster_when_the_service_lists_several()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-film-original-language");

        var answer = await fixture.Gateway.FindAsync(Film("parasite", 2019), CancellationToken.None);

        Assert.Equal(GatewayStatus.Ok, answer.Status);
        Assert.Equal(("7IiTTgloJzvGI1TAYymCfbfl3vT.jpg", 496243L, "ko"), (answer.Value!.PosterRef, answer.Value.TmdbId, answer.Value.OriginalLanguage));
    }

    [Fact]
    public async Task A_series_is_searched_in_the_tv_catalogue_by_its_name_alone()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-series-by-title");
        var series = new ArtworkLookup("k", "tv", "breaking bad", null, null, null, null, 0, ArtworkOutcomes.Pending, null, true);

        var answer = await fixture.Gateway.FindAsync(series, CancellationToken.None);

        Assert.Equal(GatewayStatus.Ok, answer.Status);
        Assert.Equal(("anFx9aTOOYqgS3v7x3R84Kz67ly.jpg", 1396L, "en"), (answer.Value!.PosterRef, answer.Value.TmdbId, answer.Value.OriginalLanguage));
    }

    [Fact]
    public async Task A_series_known_only_by_its_TheTVDB_id_takes_the_poster_address_and_language_TheTVDB_gives()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "series-by-tvdb-id");
        var series = new ArtworkLookup("k", "tv", string.Empty, null, null, 81189, null, 0, ArtworkOutcomes.Pending, null, true);

        var answer = await fixture.Gateway.FindAsync(series, CancellationToken.None);

        Assert.Equal(GatewayStatus.Ok, answer.Status);
        Assert.Equal(("https://artworks.thetvdb.com/banners/posters/81189-10.jpg", "eng"), (answer.Value!.PosterRef, answer.Value.OriginalLanguage));
    }

    [Fact]
    public async Task A_title_the_service_has_no_result_for_is_not_found_not_an_error()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-unknown-title");

        var answer = await fixture.Gateway.FindAsync(Film("zzqxv nonexistent title qq", 1901), CancellationToken.None);

        Assert.Equal(GatewayStatus.NotFound, answer.Status);
        Assert.Null(answer.Value);
    }

    [Fact]
    public async Task The_health_check_reads_the_service_status_the_service_gave()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "health");

        Assert.Equal(GatewayStatus.Ok, await fixture.Gateway.CheckHealthAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_film_resolved_from_the_recorded_answer_gets_its_poster_stored_and_is_not_asked_about_again()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-film-by-title");
        fixture.ServeImage(BuckBunnyPoster);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, BuckBunnyPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var key = ArtworkKeys.ForTitle("movie", new ArtworkTitle("big buck bunny", 2008));
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(key));
        Assert.Equal("en", await fixture.OriginalLanguageAsync(key));
        Assert.Equal(ArtworkPosterUrls.UrlFor(ArtworkKeys.PosterId(BuckBunnyPoster)), Assert.Single(await fixture.PosterUrlsForAsync(library, BuckBunnyPath)).Value);
        Assert.Single(fixture.Searches());
    }

    [Fact]
    public async Task A_title_the_recorded_service_has_no_result_for_is_remembered_as_missing_and_asked_again_only_after_a_week()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-unknown-title");
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, UnknownPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        fixture.Store.Clock.Advance(TimeSpan.FromDays(6));
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var key = ArtworkKeys.ForTitle("movie", new ArtworkTitle("zzqxv nonexistent title qq", 1901));
        Assert.Equal(ArtworkOutcomes.Missing, await fixture.OutcomeAsync(key));
        Assert.Single(fixture.Searches());
        Assert.Empty(await fixture.PosterUrlsForAsync(library, UnknownPath));

        fixture.Store.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(2, fixture.Searches().Count);
    }

    [Fact]
    public async Task A_profile_that_keeps_the_original_language_is_told_the_one_the_recorded_service_gave()
    {
        using var fixture = new ArtworkFixture();
        Serve(fixture, "search-film-by-title");
        fixture.ServeImage(BuckBunnyPoster);

        var result = await fixture.OriginalLanguages.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), BuckBunnyPath, origin: null, CancellationToken.None);

        Assert.Equal(Weir.Core.Rules.LookupResult.StatusMatched, result.Status);
        Assert.Equal("en", result.Metadata!.OriginalLanguage);
    }
}
