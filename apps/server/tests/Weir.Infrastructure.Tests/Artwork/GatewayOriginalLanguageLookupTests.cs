using System.Net;
using Weir.Core.Artwork;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Artwork;

/// <summary>A title's original language comes from the same lookup as its poster, once per title, within the service's limits.</summary>
public sealed class GatewayOriginalLanguageLookupTests
{
    private const string PosterFile = "zv7J85D8CC9qYagAEhPM63CIG6j.jpg";
    private const string FilmPath = "Nosferatu (1922)/Nosferatu.1922.1080p.mkv";
    private const string EpisodeOne = "Doctor Who (2005)/Season 1/Doctor.Who.2005.S01E01.1080p.mkv";
    private const string EpisodeTwo = "Doctor Who (2005)/Season 1/Doctor.Who.2005.S01E02.1080p.mkv";

    private static string FilmKey() => ArtworkKeys.ForTitle("movie", new ArtworkTitle("nosferatu", 1922));

    [Fact]
    public async Task A_title_the_background_pass_has_not_reached_is_asked_about_now_and_its_poster_comes_from_the_same_answer()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de")).ServeImage(PosterFile);

        var result = await fixture.OriginalLanguages.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), FilmPath, origin: null, CancellationToken.None);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal((LookupResult.StatusMatched, "de"), (result.Status, result.Metadata!.OriginalLanguage));
        Assert.Single(fixture.Searches());
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(FilmKey()));
        Assert.Single(fixture.Http.Requests, request => request.Uri.AbsolutePath.StartsWith("/artwork/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_language_the_background_pass_already_found_is_read_without_asking_again()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de")).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var result = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal("de", result.Metadata!.OriginalLanguage);
        Assert.Single(fixture.Searches());
    }

    [Fact]
    public async Task Every_episode_of_a_series_shares_one_lookup_by_the_series_name()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "en"));
        var library = await fixture.LibraryIdAsync("tv");

        await fixture.OriginalLanguages.LookupAsync("tv", library, EpisodeOne, origin: null, CancellationToken.None);
        var second = await fixture.OriginalLanguages.LookupAsync("tv", library, EpisodeTwo, origin: null, CancellationToken.None);

        var search = Assert.Single(fixture.Searches());
        Assert.Equal(("tv", "doctor who", "2005"), (ArtworkFixture.QueryValue(search, "mediaType"), ArtworkFixture.QueryValue(search, "query"), ArtworkFixture.QueryValue(search, "year")));
        Assert.Equal("en", second.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_a_manager_named_by_tmdb_id_is_looked_up_by_that_id()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, tmdbId: 653, originalLanguage: "de"));
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", [FilmPath], releaseName: null, new ArtworkHints("Nosferatu", 1922, 653, null, null, null, null, null)));

        var result = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal("653", ArtworkFixture.QueryValue(Assert.Single(fixture.Searches()), "providerId"));
        Assert.Equal("de", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_the_service_does_not_know_is_declined_and_not_asked_about_again_for_a_week()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"provider":"deluno-broker","results":[]}""");
        var library = await fixture.LibraryIdAsync("movie");

        var first = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);
        fixture.Store.Clock.Advance(TimeSpan.FromDays(6));
        var second = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal((LookupResult.StatusNoMatch, LookupResult.StatusNoMatch), (first.Status, second.Status));
        Assert.Single(fixture.Searches());

        fixture.Store.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal(2, fixture.Searches().Count);
    }

    [Fact]
    public async Task A_match_that_names_no_language_is_declined_and_remembered()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: string.Empty));
        var library = await fixture.LibraryIdAsync("movie");

        var first = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);
        var second = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal((LookupResult.StatusNoMatch, LookupResult.StatusNoMatch), (first.Status, second.Status));
        Assert.Single(fixture.Searches());
    }

    [Fact]
    public async Task A_busy_service_is_declined_as_unreachable_and_left_alone_until_it_said_to_come_back()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Route(HttpMethod.Get, "/metadata/search", _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable, """{"error":"provider_busy"}""", ("Retry-After", "120")));
        var library = await fixture.LibraryIdAsync("movie");

        var busy = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);
        var other = await fixture.OriginalLanguages.LookupAsync("movie", library, "Metropolis (1927)/Metropolis.1927.mkv", origin: null, CancellationToken.None);

        Assert.Equal((LookupResult.StatusUnreachable, LookupResult.StatusUnreachable), (busy.Status, other.Status));
        Assert.Single(fixture.Searches());
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_is_declined_and_the_title_is_retried_later()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("movie");

        var down = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);
        fixture.Store.Clock.Advance(ArtworkSchedule.FirstFailureBackoff + TimeSpan.FromSeconds(1));
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de"));
        var back = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusUnreachable, down.Status);
        Assert.Equal("de", back.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_file_with_no_readable_name_and_nothing_linked_to_it_is_declined_for_want_of_a_title()
    {
        using var fixture = new ArtworkFixture();

        var result = await fixture.OriginalLanguages.LookupAsync("tv", await fixture.LibraryIdAsync("tv"), "!!!/S01E02.mkv", origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusNoMatch, result.Status);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task A_file_with_no_library_is_looked_up_by_its_name()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de"));

        var result = await fixture.OriginalLanguages.LookupAsync("movie", libraryId: null, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal("de", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_a_manager_named_only_by_its_tmdb_id_is_looked_up_by_that_id()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, tmdbId: 653, originalLanguage: "de"));
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", ["!!!/x.mkv"], releaseName: null, new ArtworkHints(null, null, 653, null, null, null, null, null)));

        var result = await fixture.OriginalLanguages.LookupAsync("movie", library, "!!!/x.mkv", origin: null, CancellationToken.None);

        Assert.Equal("653", ArtworkFixture.QueryValue(Assert.Single(fixture.Searches()), "providerId"));
        Assert.Equal("de", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_series_a_manager_named_only_by_its_tvdb_id_is_looked_up_by_that_id()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Json(HttpMethod.Get, "/metadata/tvdb/tv/78804", """{"provider":"tvdb","series":{"tvdbId":"78804","originalLanguage":"eng","posterUrl":null}}""");
        var library = await fixture.LibraryIdAsync("tv");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "tv", [EpisodeOne], releaseName: null, new ArtworkHints(null, null, null, 78804, null, 1, 1, null)));

        var result = await fixture.OriginalLanguages.LookupAsync("tv", library, EpisodeOne, origin: null, CancellationToken.None);

        Assert.Equal("eng", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_a_manager_gave_only_a_poster_for_cannot_be_searched_and_is_read_from_the_file_name_instead()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de"));
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", [FilmPath], releaseName: null, new ArtworkHints(null, null, null, null, null, null, null, PosterFile)));

        var result = await fixture.OriginalLanguages.LookupAsync("movie", library, FilmPath, origin: null, CancellationToken.None);

        Assert.Equal("nosferatu", ArtworkFixture.QueryValue(Assert.Single(fixture.Searches()), "query"));
        Assert.Equal("de", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_whose_file_name_says_nothing_is_read_from_the_release_name_the_manager_gave()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de"));
        var origin = new HandoffOrigin("deluno", HandoffId: null, CallbackPath: null, ReleaseName: "Nosferatu.1922.1080p.BluRay-GRP");

        var result = await fixture.OriginalLanguages.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), "!!!/x.mkv", origin, CancellationToken.None);

        Assert.Equal("nosferatu", ArtworkFixture.QueryValue(Assert.Single(fixture.Searches()), "query"));
        Assert.Equal("de", result.Metadata!.OriginalLanguage);
    }

    [Fact]
    public async Task A_title_known_only_by_a_poster_with_a_file_name_that_says_nothing_is_declined_for_want_of_a_title()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", ["!!!/!!!.mkv"], releaseName: null, new ArtworkHints(null, null, null, null, null, null, null, PosterFile)));

        var result = await fixture.OriginalLanguages.LookupAsync("movie", library, "!!!/!!!.mkv", origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusNoMatch, result.Status);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task A_title_removed_while_it_was_being_asked_about_is_answered_as_it_was_read()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Route(HttpMethod.Get, "/metadata/search", _ =>
        {
            using var connection = fixture.Store.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM artwork_lookups";
            command.ExecuteNonQuery();
            return FakeManagerHttp.Response(HttpStatusCode.OK, ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de"));
        });

        var result = await fixture.OriginalLanguages.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), FilmPath, origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusUnreachable, result.Status);
    }

    [Fact]
    public async Task With_the_metadata_service_switched_off_the_lookup_declines_without_asking()
    {
        using var fixture = new ArtworkFixture();
        var gateway = fixture.SwitchedOffGateway();
        var lookup = new GatewayOriginalLanguageLookup(fixture.Store.Database, fixture.Lookups, fixture.Files, fixture.Subjects, fixture.Resolver, gateway, fixture.Limiter, fixture.Store.Clock);

        var result = await lookup.LookupAsync("movie", await fixture.LibraryIdAsync("movie"), FilmPath, origin: null, CancellationToken.None);

        Assert.Equal(LookupResult.StatusNotConfigured, result.Status);
        Assert.Empty(fixture.Http.Requests);
    }
}
