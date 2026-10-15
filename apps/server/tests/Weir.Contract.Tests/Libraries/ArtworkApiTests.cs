using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Posters: a file's title is looked up once through the metadata service and the image is served from Weir. There is no
/// setting for it; the metadata-provider routes only remain for older clients. The metadata service is a fake; no test
/// reaches the real one.
/// </summary>
[ContractArea("libraries")]
public sealed class ArtworkApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Metadata = $"{WeirClient.Api}/processing/metadata-provider";
    private const string Nosferatu = "Nosferatu (1922)/Nosferatu.1922.1080p.mkv";

    // The resolver wakes every five seconds; this is long enough for it to have woken twice.
    private static readonly TimeSpan TwoPasses = TimeSpan.FromSeconds(11);

    [Fact]
    public async Task A_file_shows_the_poster_of_its_title_once_the_title_is_looked_up()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("nosferatu", "nosferatu.jpg", tmdbId: 653);
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        await LibrariesPartBPosters.SeedFileAsync(server, Nosferatu, activity: true);
        using var admin = await server.CreateAdminClientAsync();

        var url = await LibrariesPartBPosters.WaitForPosterAsync(admin, Nosferatu);

        Assert.StartsWith(LibrariesPartBPosters.PosterPrefix, url, StringComparison.Ordinal);
        LibrariesPartBPosters.AssertSearch(gateway.Searches()[0], "movies", "nosferatu", "1922");
        var image = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, image.Status);
        Assert.Equal("image/jpeg", image.Header("Content-Type"));
        var cache = image.Header("Cache-Control")!.Replace(" ", string.Empty, StringComparison.Ordinal).Split(',');
        Assert.Subset(cache.ToHashSet(), new HashSet<string> { "private", "max-age=2592000", "immutable" });
        Assert.Equal(FakeGateway.ImageBytes, image.Bytes);
        Assert.Equal(["/artwork/w342/nosferatu.jpg"], gateway.ImageRequests().Select(request => request.Path));
        var entries = await admin.GetAsync($"{WeirClient.Api}/activity/recent", ("module", "processing"));
        Assert.Equal([url], entries.Fields["items"]!.AsArray().Select(entry => (string?)entry!["poster_url"]));
    }

    [Fact]
    public async Task A_poster_needs_a_signed_in_user_and_an_unknown_id_is_a_404()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("nosferatu", "nosferatu.jpg");
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        await LibrariesPartBPosters.SeedFileAsync(server, Nosferatu);
        using var admin = await server.CreateAdminClientAsync();
        var url = await LibrariesPartBPosters.WaitForPosterAsync(admin, Nosferatu);

        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).Status);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"{LibrariesPartBPosters.PosterPrefix}0123456789abcdef0123456789abcdef")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{LibrariesPartBPosters.PosterPrefix}not-a-poster")).Status);
    }

    [Fact]
    public async Task Two_files_of_one_title_are_looked_up_once()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("metropolis", "metropolis.jpg");
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        string[] paths = ["Metropolis (1927)/Metropolis.1927.1080p.mkv", "Metropolis (1927)/Metropolis.1927.extras.mkv"];
        await LibrariesPartBPosters.SeedFilesAsync(server, paths);

        using var admin = await server.CreateAdminClientAsync();

        var urls = new List<string>();
        foreach (var path in paths)
        {
            urls.Add(await LibrariesPartBPosters.WaitForPosterAsync(admin, path));
        }

        Assert.Equal(urls[0], urls[1]);
        Assert.Single(gateway.SearchesFor("metropolis"));
    }

    [Fact]
    public async Task A_lookup_the_server_is_stopped_in_the_middle_of_is_made_again_when_it_starts()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("metropolis", "metropolis.jpg");
        gateway.HoldSearches();
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        const string path = "Metropolis (1927)/Metropolis.1927.1080p.mkv";
        await LibrariesPartBPosters.SeedFileAsync(server, path);
        await gateway.SearchHeldAsync();

        await server.RestartAsync();
        gateway.ReleaseSearches();
        using var admin = await server.CreateAdminClientAsync();

        Assert.NotEmpty(await LibrariesPartBPosters.WaitForPosterAsync(admin, path));
        // The search the stop cut short is recorded when the fake answers it, which is after the release.
        await Poll.UntilAsync(() => Task.FromResult(gateway.SearchesFor("metropolis").Count >= 2), "both searches to be recorded");
        Assert.Equal(2, gateway.SearchesFor("metropolis").Count);
    }

    [Fact]
    public async Task Every_episode_of_a_series_shares_one_lookup_of_the_series()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("example show", "show.jpg");
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        string[] paths = ["Example Show/Season 1/Example.Show.S01E01.mkv", "Example Show/Season 1/Example.Show.S01E02.mkv"];
        await LibrariesPartBPosters.SeedFilesAsync(server, paths, mediaType: "tv");

        using var admin = await server.CreateAdminClientAsync();

        foreach (var path in paths)
        {
            await LibrariesPartBPosters.WaitForPosterAsync(admin, path);
        }

        var search = Assert.Single(gateway.SearchesFor("example show"));
        Assert.Equal(["tv"], search.Query["mediaType"]);
    }

    [Fact]
    public async Task A_title_the_service_does_not_know_has_no_poster_and_is_not_asked_about_again()
    {
        using var gateway = new FakeGateway();
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        const string path = "Zzzq Unknown (2001)/Zzzq.Unknown.2001.mkv";
        await LibrariesPartBPosters.SeedFileAsync(server, path);
        using var admin = await server.CreateAdminClientAsync();

        await Poll.UntilAsync(
            () => Task.FromResult(gateway.SearchesFor("zzzq unknown") is { Count: > 0 } searches ? searches : null),
            "the title to be searched");

        await LibrariesPartBWaits.NeverWithinAsync(
            () => gateway.SearchesFor("zzzq unknown").Count > 1, TwoPasses, "a second search for a title the service does not know");
        Assert.Null(await LibrariesPartBPosters.PosterUrlOfAsync(admin, path));
    }

    [Fact]
    public async Task A_busy_service_is_left_alone_for_as_long_as_it_asked()
    {
        using var gateway = new FakeGateway();
        gateway.Busy(retryAfterSeconds: 600);
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        await LibrariesPartBPosters.SeedFilesAsync(server, ["First Film (2010)/First.Film.2010.mkv", "Second Film (2011)/Second.Film.2011.mkv"]);

        using var admin = await server.CreateAdminClientAsync();

        await Poll.UntilAsync(
            () => Task.FromResult(gateway.Searches() is { Count: > 0 } searches ? searches : null), "the first search");

        await LibrariesPartBWaits.NeverWithinAsync(
            () => gateway.Searches().Count > 1, TwoPasses, "another search while the service asked Weir to wait");
        Assert.Null(await LibrariesPartBPosters.PosterUrlOfAsync(admin, "First Film (2010)/First.Film.2010.mkv"));
    }

    [Fact]
    public async Task A_save_to_the_old_artwork_setting_changes_nothing_and_lookups_carry_on()
    {
        using var gateway = new FakeGateway();
        gateway.Knows("nosferatu", "nosferatu.jpg");
        gateway.Knows("metropolis", "metropolis.jpg");
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        await LibrariesPartBPosters.SeedFileAsync(server, Nosferatu);
        using var admin = await server.CreateAdminClientAsync();
        await LibrariesPartBPosters.WaitForPosterAsync(admin, Nosferatu);

        var saved = await admin.PutWithCsrfAsync(Metadata, new JsonObject { ["provider"] = string.Empty, ["artwork_enabled"] = false });
        LibrariesPartBChecks.Status(saved, HttpStatusCode.OK);
        Assert.True((bool)saved.Fields["artwork_enabled"]!);
        Assert.NotEmpty(await LibrariesPartBPosters.WaitForPosterAsync(admin, Nosferatu));

        const string second = "Metropolis (1927)/Metropolis.1927.1080p.mkv";
        await LibrariesPartBPosters.SeedFileAsync(server, second);
        using var again = await server.CreateAdminClientAsync();
        Assert.NotEmpty(await LibrariesPartBPosters.WaitForPosterAsync(again, second));
    }

    [Fact]
    public async Task The_old_metadata_provider_test_asks_whether_the_metadata_service_answers()
    {
        using var gateway = new FakeGateway();
        await using var server = await WeirServer.StartNewAsync(gateway.Env);
        using var admin = await server.CreateAdminClientAsync();

        var tested = await admin.PostWithCsrfAsync($"{Metadata}/test", new JsonObject());

        LibrariesPartBChecks.Status(tested, HttpStatusCode.OK);
        Assert.Equal("matched", (string)tested.Fields["status"]!);
        Assert.Equal(["/health"], gateway.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task The_files_list_always_names_the_poster_field()
    {
        // With posters off for the whole server, every file still carries poster_url, as null.
        await LibrariesPartBPosters.SeedFileAsync(fixture.Server, "Plain (2000)/Plain.2000.mkv");
        using var admin = await fixture.Server.CreateAdminClientAsync();

        Assert.Null(await LibrariesPartBPosters.PosterUrlOfAsync(admin, "Plain (2000)/Plain.2000.mkv"));
    }
}
