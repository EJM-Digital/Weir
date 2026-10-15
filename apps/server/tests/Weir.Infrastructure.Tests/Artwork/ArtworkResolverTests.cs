using System.Net;
using Weir.Core.Artwork;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Artwork;

/// <summary>Finding each title's poster once, within the limits the metadata service sets.</summary>
public sealed class ArtworkResolverTests
{
    private const string PosterFile = "zv7J85D8CC9qYagAEhPM63CIG6j.jpg";
    private const string FilmPath = "Nosferatu (1922)/Nosferatu.1922.1080p.mkv";

    private static string FilmKey(string title = "nosferatu", int year = 1922) => ArtworkKeys.ForTitle("movie", new ArtworkTitle(title, year));

    [Fact]
    public async Task A_title_is_searched_once_by_name_and_year_and_its_poster_is_stored()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);

        var settled = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var search = Assert.Single(fixture.Searches());
        Assert.Equal(1, settled);
        Assert.Equal(("movies", "nosferatu", "1922"), (ArtworkFixture.QueryValue(search, "mediaType"), ArtworkFixture.QueryValue(search, "query"), ArtworkFixture.QueryValue(search, "year")));
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(FilmKey()));
        var urls = await fixture.PosterUrlsForAsync(library, FilmPath);
        var url = Assert.Single(urls).Value;
        Assert.Equal(ArtworkPosterUrls.UrlFor(ArtworkKeys.PosterId(PosterFile)), url);
        await using var stored = fixture.PosterFiles.Open(ArtworkKeys.PosterId(PosterFile));
        Assert.NotNull(stored);
        Assert.Equal(ArtworkFixture.ImageBytes.Length, stored!.Length);
    }

    [Fact]
    public async Task A_poster_that_cannot_be_replaced_because_it_is_being_sent_keeps_its_title_due_and_is_replaced_on_the_next_pass()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        fixture.PosterFiles.Pause = (_, _) => Task.CompletedTask;
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        var posterId = ArtworkKeys.PosterId(PosterFile);
        await fixture.PosterFiles.WriteAsync(posterId, [9, 9], CancellationToken.None);
        var beingSent = fixture.PosterFiles.Open(posterId)!;

        var whileSent = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(0, whileSent);
        Assert.Equal(2, beingSent.Length);
        Assert.Empty(await fixture.PosterUrlsForAsync(library, FilmPath));

        await beingSent.DisposeAsync();
        var afterwards = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(1, afterwards);
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(FilmKey()));
        await using var stored = fixture.PosterFiles.Open(posterId);
        Assert.Equal(ArtworkFixture.ImageBytes.Length, stored!.Length);
    }

    [Fact]
    public async Task The_poster_is_fetched_from_the_gateway_at_the_size_weir_shows()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var image = Assert.Single(fixture.Http.Requests, request => request.Uri.AbsolutePath.StartsWith("/artwork/", StringComparison.Ordinal));
        Assert.Equal($"/artwork/w342/{PosterFile}", image.Uri.AbsolutePath);
        Assert.False(image.FollowRedirects);
    }

    [Fact]
    public async Task Two_files_of_one_title_share_one_lookup()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.QueueFileAsync(library, "Nosferatu (1922)/Nosferatu.1922.extras.mkv");

        var settled = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.Single(fixture.Searches());
        Assert.Equal(2, (await fixture.PosterUrlsForAsync(library, FilmPath, "Nosferatu (1922)/Nosferatu.1922.extras.mkv")).Count);
    }

    [Fact]
    public async Task A_title_answered_while_a_caller_waited_to_ask_is_not_asked_about_again()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "de")).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        var readBeforeTheAnswer = await fixture.LookupAsync(FilmKey());
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        fixture.Store.Clock.Advance(ArtworkRateLimiter.MinimumBetweenSearches);

        await fixture.Resolver.SearchNowAsync(readBeforeTheAnswer!, CancellationToken.None);

        Assert.Single(fixture.Searches());
        Assert.Equal("de", await fixture.OriginalLanguageAsync(FilmKey()));
    }

    [Fact]
    public async Task A_title_with_a_poster_is_not_asked_about_again()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        fixture.Store.Clock.Advance(TimeSpan.FromDays(60));

        var settled = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(0, settled);
        Assert.Single(fixture.Searches());
    }

    [Fact]
    public async Task Two_titles_with_the_same_poster_store_the_image_once()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.QueueFileAsync(library, "Nosferatu.1979.mkv");

        await fixture.Resolver.ResolveDueAsync(1, CancellationToken.None);
        fixture.Store.Clock.Advance(ArtworkRateLimiter.MinimumBetweenSearches);
        await fixture.Resolver.ResolveDueAsync(1, CancellationToken.None);

        Assert.Equal(2, fixture.Searches().Count);
        Assert.Single(fixture.Http.Requests, request => request.Uri.AbsolutePath.StartsWith("/artwork/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_title_the_service_does_not_know_is_remembered_and_asked_again_only_after_a_week()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Json(HttpMethod.Get, "/metadata/search", """{"error":"provider_record_missing"}""", HttpStatusCode.NotFound);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        fixture.Store.Clock.Advance(TimeSpan.FromDays(6));
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(ArtworkOutcomes.Missing, await fixture.OutcomeAsync(FilmKey()));
        Assert.Single(fixture.Searches());
        Assert.Empty(await fixture.PosterUrlsForAsync(library, FilmPath));

        fixture.Store.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(2, fixture.Searches().Count);
    }

    [Fact]
    public async Task A_search_with_no_poster_in_its_results_counts_as_not_found()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"results":[{"provider":"tmdb","providerId":"9","posterUrl":null}]}""");
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(ArtworkOutcomes.Missing, await fixture.OutcomeAsync(FilmKey()));
    }

    [Fact]
    public async Task A_busy_answer_pauses_every_lookup_for_as_long_as_the_service_asked()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Route(HttpMethod.Get, "/metadata/search", _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable, """{"error":"provider_busy"}""", ("Retry-After", "120")));
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        var settled = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        Assert.Equal(0, settled);
        Assert.Equal(ArtworkOutcomes.Pending, await fixture.OutcomeAsync(FilmKey()));
        Assert.True(fixture.Limiter.IsPaused);

        fixture.Store.Clock.Advance(TimeSpan.FromSeconds(119));
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        Assert.Single(fixture.Searches());

        fixture.Store.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(FilmKey()));
    }

    [Fact]
    public async Task A_busy_answer_that_names_no_wait_pauses_for_a_minute()
    {
        using var fixture = new ArtworkFixture();
        fixture.Http.Json(HttpMethod.Get, "/metadata/search", "{}", HttpStatusCode.ServiceUnavailable);
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        fixture.Store.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(fixture.Limiter.IsPaused);
        fixture.Store.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(fixture.Limiter.IsPaused);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_costs_one_attempt_and_the_title_is_retried_later()
    {
        using var fixture = new ArtworkFixture();
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.QueueFileAsync(library, "Metropolis.1927.mkv");

        var settled = await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(0, settled);
        Assert.Single(fixture.Searches());
        Assert.Equal(1, await fixture.Store.Scalar("SELECT max(attempts) FROM artwork_lookups"));
        Assert.True(fixture.Limiter.IsPaused);

        fixture.Store.Clock.Advance(ArtworkSchedule.UnreachablePause + TimeSpan.FromSeconds(1));
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(FilmKey("metropolis", 1927)));
    }

    [Fact]
    public async Task Searches_are_at_least_two_seconds_apart()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.QueueFileAsync(library, "Metropolis.1927.mkv");

        var resolving = fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        await Eventually.ThatAsync(() => fixture.Searches().Count == 1);
        Assert.False(resolving.IsCompleted);

        // The second search may not have started waiting for its turn yet, so time keeps moving until it has had it.
        await Eventually.ThatAsync(() =>
        {
            fixture.Store.Clock.Advance(ArtworkRateLimiter.MinimumBetweenSearches);
            return resolving.IsCompleted;
        });

        Assert.Equal(2, await resolving);
        Assert.Equal(2, fixture.Searches().Count);
    }

    [Fact]
    public async Task A_title_a_manager_named_by_tmdb_id_is_looked_up_exactly()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, tmdbId: 653)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", [FilmPath], releaseName: "Nosferatu.1922.1080p", new ArtworkHints("Nosferatu", 1922, 653, null, null, null, null, null)));

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        var search = Assert.Single(fixture.Searches());
        Assert.Equal("653", ArtworkFixture.QueryValue(search, "providerId"));
        Assert.Equal(ArtworkOutcomes.Found, await fixture.OutcomeAsync(ArtworkKeys.ForTmdbId("movie", 653)));
    }

    [Fact]
    public async Task A_poster_address_a_manager_gave_is_fetched_without_a_search()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "movie", [FilmPath], releaseName: null, new ArtworkHints(null, null, 653, null, null, null, null, PosterFile)));

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Empty(fixture.Searches());
        Assert.Single(await fixture.PosterUrlsForAsync(library, FilmPath));
    }

    [Fact]
    public async Task A_series_a_manager_named_only_by_tvdb_id_is_looked_up_on_thetvdb_and_its_image_fetched_from_there()
    {
        using var fixture = new ArtworkFixture();
        const string TvdbImage = "https://artworks.thetvdb.com/banners/posters/78804-52.jpg";
        fixture.Http.Json(HttpMethod.Get, "/metadata/tvdb/tv/78804", $$$"""{"provider":"tvdb","series":{"tvdbId":"78804","tmdbId":"57243","posterUrl":"{{{TvdbImage}}}"}}""");
        fixture.Http.Route(HttpMethod.Get, "/banners/posters/78804-52.jpg", _ => ArtworkFixture.Image());
        var library = await fixture.LibraryIdAsync("tv");
        await fixture.InTransactionAsync(uow => fixture.Subjects.LinkHandoffAsync(
            uow, library, "tv", ["Doctor Who/Doctor.Who.S01E01.mkv"], releaseName: null, new ArtworkHints(null, null, null, 78804, null, 1, 1, null)));

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Empty(fixture.Searches());
        var image = Assert.Single(fixture.Http.Requests, request => request.Uri.Host == "artworks.thetvdb.com");
        Assert.Equal("/banners/posters/78804-52.jpg", image.Uri.AbsolutePath);
        Assert.Single(await fixture.PosterUrlsForAsync(library, "Doctor Who/Doctor.Who.S01E01.mkv"));
    }

    [Fact]
    public async Task Files_from_a_library_scan_wait_behind_the_files_weir_is_processing()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, "Metropolis.1927.mkv", priority: ArtworkPriority.Library);
        await fixture.QueueFileAsync(library, FilmPath, priority: ArtworkPriority.Processing);

        await fixture.Resolver.ResolveDueAsync(1, CancellationToken.None);

        Assert.Equal("nosferatu", ArtworkFixture.QueryValue(Assert.Single(fixture.Searches()), "query"));
    }

    [Fact]
    public async Task A_server_with_the_metadata_service_switched_off_shows_no_poster_but_keeps_the_images_on_disk()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile)).ServeImage(PosterFile);
        var library = await fixture.LibraryIdAsync("movie");
        await fixture.QueueFileAsync(library, FilmPath);
        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);
        var switchedOff = new ArtworkPosterUrls(fixture.Files, fixture.SwitchedOffGateway());

        var urls = await fixture.Store.WithUnitOfWork(uow => switchedOff.ForFilesAsync(uow, [(library, FilmPath)]), commit: false);

        Assert.Empty(urls);
        Assert.True(File.Exists(Path.Join(fixture.Store.Options.WeirHome, "artwork", "posters", ArtworkKeys.PosterId(PosterFile))));
    }

    [Fact]
    public async Task The_original_language_in_the_answer_is_kept_with_the_title_it_belongs_to()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: "DE ")).ServeImage(PosterFile);
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal("de", await fixture.OriginalLanguageAsync(FilmKey()));
    }

    [Fact]
    public async Task An_answer_that_names_no_language_is_remembered_as_having_none()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch(ArtworkFixture.SearchAnswer(PosterFile, originalLanguage: string.Empty)).ServeImage(PosterFile);
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(string.Empty, await fixture.OriginalLanguageAsync(FilmKey()));
    }

    [Fact]
    public async Task A_search_with_no_poster_still_keeps_the_original_language_it_reported()
    {
        using var fixture = new ArtworkFixture();
        fixture.ServeSearch("""{"results":[{"provider":"tmdb","providerId":"9","originalLanguage":"ja","posterUrl":null}]}""");
        await fixture.QueueFileAsync(await fixture.LibraryIdAsync("movie"), FilmPath);

        await fixture.Resolver.ResolveDueAsync(10, CancellationToken.None);

        Assert.Equal(("ja", ArtworkOutcomes.Missing), (await fixture.OriginalLanguageAsync(FilmKey()), await fixture.OutcomeAsync(FilmKey())));
    }
}
