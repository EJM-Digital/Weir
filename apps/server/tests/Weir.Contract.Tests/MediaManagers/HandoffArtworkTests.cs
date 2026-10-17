using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Deluno's hand-off carries optional title fields for posters. Weir uses the ids and the poster address when they are there,
/// ignores a malformed field or a poster address on another host, and still accepts a hand-off with none of them. The metadata
/// service is a fake; no test reaches the real one.
/// </summary>
[ContractArea("media_managers")]
public sealed class HandoffArtworkTests
{
    /// <summary>A server pointed at the fake gateway, with a Movies library that watches a folder of this test's.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly TemporaryFolder _folder;

        private Setup(TemporaryFolder folder, FakeGateway gateway, WeirServer server, WeirClient admin, LibraryFolders folders)
        {
            _folder = folder;
            Gateway = gateway;
            Server = server;
            Admin = admin;
            Folders = folders;
        }

        public FakeGateway Gateway { get; }

        public WeirServer Server { get; }

        public WeirClient Admin { get; }

        public LibraryFolders Folders { get; }

        public static async Task<Setup> StartAsync()
        {
            var folder = new TemporaryFolder();
            var gateway = new FakeGateway();
            WeirServer? server = null;
            WeirClient? admin = null;
            try
            {
                server = await WeirServer.StartNewAsync(Handoffs.SecretEnvironment.With(gateway.Env));
                admin = await server.CreateAdminClientAsync();
                var folders = LibraryFolders.Make(Path.Combine(folder.Path, "movies"));
                await ProcessingLibraries.EnsureAsync(admin, "Movies", "movie", folders);
                return new Setup(folder, gateway, server, admin, folders);
            }
            catch
            {
                admin?.Dispose();
                if (server is not null)
                {
                    await server.DisposeAsync();
                }

                gateway.Dispose();
                folder.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await Server.DisposeAsync();
            Gateway.Dispose();
            _folder.Dispose();
        }
    }

    private static async Task<WeirResponse> HandOffAsync(Setup setup, JsonObject? extra = null)
    {
        var source = Path.Combine(setup.Folders.Watched, "Film", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, "x"u8.ToArray());
        var body = JsonFields.Merge(
            new JsonObject
            {
                ["eventType"] = "deluno.processor-handoff",
                ["handoffId"] = "h-poster",
                ["libraryId"] = "lib-1",
                ["mediaType"] = "movies",
                ["sourcePath"] = source,
                ["releaseName"] = "Metropolis.1927.1080p.BluRay-GRP",
                ["callbackPath"] = RemuxJobs.CallbackPath,
            },
            extra);
        using var manager = setup.Server.CreateClient();
        return await manager.PostAsync(Handoffs.WebhookPath, body, Handoffs.Secret);
    }

    private static async Task<string> WaitForPosterAsync(WeirClient admin) =>
        await Poll.UntilAsync(
            async () =>
            {
                var listed = await admin.GetAsync($"{WeirClient.Api}/processing/files");
                Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
                return listed.Fields["files"]!.AsArray()
                    .Select(entry => (string?)entry!["poster_url"])
                    .FirstOrDefault(poster => !string.IsNullOrEmpty(poster));
            },
            "the hand-off's file to show a poster");

    [Fact]
    public async Task A_hand_off_with_none_of_the_poster_fields_is_looked_up_by_its_release_name()
    {
        await using var setup = await Setup.StartAsync();
        setup.Gateway.Knows("metropolis", "metropolis.jpg");

        var response = await HandOffAsync(setup);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await WaitForPosterAsync(setup.Admin);
        var query = setup.Gateway.Searches()[0].Query;
        Assert.Equal(["mediaType", "query", "year"], query.Keys.Order());
        Assert.Equal(["movies"], query["mediaType"]);
        Assert.Equal(["metropolis"], query["query"]);
        Assert.Equal(["1927"], query["year"]);
    }

    [Fact]
    public async Task A_hand_off_with_a_tmdb_id_is_looked_up_exactly()
    {
        await using var setup = await Setup.StartAsync();
        setup.Gateway.Knows("a different title", "metropolis.jpg", tmdbId: 19);

        var response = await HandOffAsync(
            setup, new JsonObject { ["title"] = "Metropolis", ["year"] = 1927, ["tmdbId"] = 19, ["imdbId"] = "tt0017136" });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await WaitForPosterAsync(setup.Admin);
        var search = setup.Gateway.Searches()[0];
        Assert.Equal(["19"], search.Query["providerId"]);
        Assert.Equal(["Metropolis"], search.Query["query"]);
    }

    [Fact]
    public async Task A_poster_address_in_a_hand_off_is_used_without_a_search()
    {
        await using var setup = await Setup.StartAsync();
        setup.Gateway.ServesImage("fromdeluno.jpg");

        var response = await HandOffAsync(
            setup, new JsonObject { ["tmdbId"] = 19, ["posterUrl"] = "https://image.tmdb.org/t/p/w500/fromdeluno.jpg" });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await WaitForPosterAsync(setup.Admin);
        Assert.Empty(setup.Gateway.Searches());
        Assert.Equal(["/artwork/w342/fromdeluno.jpg"], setup.Gateway.ImageRequests().Select(request => request.Path));
    }

    [Fact]
    public async Task A_poster_address_on_another_host_is_ignored_and_the_title_decides()
    {
        await using var setup = await Setup.StartAsync();
        setup.Gateway.Knows("metropolis", "metropolis.jpg");

        var response = await HandOffAsync(
            setup, new JsonObject { ["posterUrl"] = "https://evil.example/t/p/w500/fromdeluno.jpg" });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await WaitForPosterAsync(setup.Admin);
        Assert.Single(setup.Gateway.SearchesFor("metropolis"));
    }

    [Fact]
    public async Task A_hand_off_with_malformed_poster_fields_is_still_accepted()
    {
        await using var setup = await Setup.StartAsync();
        setup.Gateway.Knows("metropolis", "metropolis.jpg");

        var response = await HandOffAsync(
            setup,
            new JsonObject { ["year"] = "soon", ["tmdbId"] = "abc", ["imdbId"] = 12, ["season"] = -3, ["posterUrl"] = 7 });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await WaitForPosterAsync(setup.Admin);
        Assert.Single(setup.Gateway.SearchesFor("metropolis"));
    }
}
