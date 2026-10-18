using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Media manager HTTP security and the HTTP side of each manager dialect.</summary>
public sealed class ManagerHttpAndPortTests
{
    private static ManagerConnection Connection(string kind = "radarr", string name = "Main") => new(kind, name, "http://manager.local", "k", 1);

    [Theory]
    [InlineData("ftp://127.0.0.1:8989")]
    [InlineData("http://user:pass@127.0.0.1:8989")]
    [InlineData("http://127.0.0.1:8989/?x=1")]
    [InlineData("http://127.0.0.1:8989/#fragment")]
    public void The_client_rejects_unsafe_base_urls(string baseUrl) =>
        Assert.Throws<MediaManagerHttpException>(() => new MediaManagerHttpClient(baseUrl, "api-key", new FakeManagerHttp()));

    [Fact]
    public async Task The_client_rejects_absolute_api_paths()
    {
        var http = new FakeManagerHttp();
        var client = new MediaManagerHttpClient("http://127.0.0.1:8989", "api-key", http);
        await Assert.ThrowsAsync<MediaManagerHttpException>(() => client.GetJsonAsync("http://169.254.169.254/latest/meta-data/"));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("42", 42.0)]
    [InlineData("Wed, 21 Oct 2026 07:28:00 GMT", null)]
    [InlineData("-3", null)]
    public async Task A_rate_limit_is_distinguishable_and_carries_the_backoff(string retryAfter, double? expected)
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Get, "/api/v3/queue", _ => FakeManagerHttp.Response((HttpStatusCode)429, "slow down", ("Retry-After", retryAfter)));
        var client = new MediaManagerHttpClient("http://127.0.0.1:8989", "api-key", http);
        var error = await Assert.ThrowsAsync<MediaManagerRateLimitedException>(() => client.GetJsonAsync("/api/v3/queue"));
        Assert.Equal(expected, error.RetryAfterSeconds);
        // The response body ("slow down") is never echoed back: a manager's answer must not leak through a status
        // report an unauthenticated or lower-privilege screen can read.
        Assert.Equal("HTTP 429", error.Message);
    }

    [Fact]
    public async Task A_redirect_is_refused_instead_of_followed()
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Get, "/api/v3/queue", _ => FakeManagerHttp.Response(HttpStatusCode.Found, headers: [("Location", "http://elsewhere.example/")]));
        var client = new MediaManagerHttpClient("http://127.0.0.1:8989", "api-key", http);

        var redirected = await Assert.ThrowsAsync<MediaManagerRedirectedException>(() => client.GetJsonAsync("/api/v3/queue"));

        Assert.Equal("answered with a redirect to another address. Use the address it redirects to.", redirected.Message);
        Assert.False(Assert.Single(http.Requests).FollowRedirects);
    }

    [Fact]
    public async Task A_refusal_never_echoes_the_response_body()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/v3/queue", "internal admin page: token=SECRET-FROM-INTERNAL-SERVICE", HttpStatusCode.InternalServerError);
        var client = new MediaManagerHttpClient("http://127.0.0.1:8989", "api-key", http);

        var error = await Assert.ThrowsAsync<MediaManagerHttpException>(() => client.GetJsonAsync("/api/v3/queue"));

        Assert.Equal("HTTP 500", error.Message);
        Assert.DoesNotContain("SECRET", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_queue_delete_sends_booleans_the_way_arr_binds_them_and_a_refusal_raises()
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Delete, "/api/v3/queue/17", _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var client = new MediaManagerHttpClient("http://127.0.0.1:8989/", "api-key", http);
        await client.DeleteAsync("/api/v3/queue/17", [new("removeFromClient", true), new("blocklist", true)]);
        var request = Assert.Single(http.Requests);
        Assert.Equal("http://127.0.0.1:8989/api/v3/queue/17?removeFromClient=true&blocklist=true", request.Uri.ToString());
        Assert.Equal("api-key", request.Headers["X-Api-Key"]);

        http.Json(HttpMethod.Delete, "/api/v3/queue/18", string.Empty, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<MediaManagerHttpException>(() => client.DeleteAsync("/api/v3/queue/18"));
    }

    [Fact]
    public async Task The_queue_dialect_removes_and_blocklists_one_item_and_a_handoff_manager_does_not()
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Delete, "/api/v3/queue/99", _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var ports = new HttpMediaManagerPorts(http);
        var sonarr = ports.PortForKind("sonarr")!;
        Assert.True(sonarr.Capabilities().RemovesQueueItems);
        await sonarr.RemoveQueueItemAsync(new ManagerConnection("sonarr", "Sonarr", "http://127.0.0.1:8989", "k"), new WireObject().Set("id", 99).Set("downloadId", "x"));
        Assert.Equal("/api/v3/queue/99?removeFromClient=true&blocklist=true", Assert.Single(http.Requests).PathAndQuery);
        await Assert.ThrowsAsync<MediaManagerHttpException>(() => sonarr.RemoveQueueItemAsync(Connection("sonarr"), new WireObject().Set("downloadId", "no id")));

        var deluno = ports.PortForKind("deluno")!;
        Assert.False(deluno.Capabilities().RemovesQueueItems);
        await Assert.ThrowsAsync<MediaManagerHttpException>(() => deluno.RemoveQueueItemAsync(Connection("deluno"), new WireObject().Set("id", 1)));
    }

    [Fact]
    public async Task Arr_queue_and_library_answers_are_read_and_asked_with_their_page_sizes()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Get, "/api/v3/queue", """{"records":[{"status":"downloading"},"junk"]}""")
            .Json(HttpMethod.Get, "/api/v3/movie", """[{"movieFile":{"path":"/media/Solaris/f.mkv"}},{"movieFile":null},{}]""");
        var radarr = new HttpMediaManagerPorts(http).PortForKind("radarr")!;
        var signal = await radarr.QueueRowsAsync(Connection());
        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.Equal(["movie"], signal.Rows.Select(row => row.Scope));
        var truth = await radarr.LibraryTruthAsync(Connection(), "movie");
        Assert.Equal(["/media/Solaris/f.mkv"], truth.LibraryFilePaths);
        Assert.Equal(["/api/v3/queue?pageSize=1000", "/api/v3/movie?pageSize=200000"], http.Requests.Select(r => r.PathAndQuery));

        var wrongScope = await radarr.LibraryTruthAsync(Connection(), "tv");
        Assert.Equal((SignalStatus.NoSignal, 0), (wrongScope.Status, wrongScope.LibraryFilePaths.Count));
    }

    [Fact]
    public async Task Sonarrs_library_truth_walks_the_series_because_sonarr_refuses_an_episode_file_list_with_no_series()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Get, "/api/v3/series", """[{"id":1,"title":"One"},{"id":2,"title":"Two"}]""")
            .Route(HttpMethod.Get, "/api/v3/episodefile", request => request.Uri.Query.Contains("seriesId=1", StringComparison.Ordinal)
                ? FakeManagerHttp.Response(HttpStatusCode.OK, """[{"id":10,"path":"/tv/One/S01E01.mkv"}]""")
                : request.Uri.Query.Contains("seriesId=2", StringComparison.Ordinal)
                    ? FakeManagerHttp.Response(HttpStatusCode.OK, """[{"id":20,"path":"/tv/Two/S01E01.mkv"},{"id":21,"path":"/tv/Two/S01E02.mkv"}]""")
                    : FakeManagerHttp.Response(HttpStatusCode.BadRequest, """{"message":"BadRequest: seriesId or episodeFileIds must be provided"}"""));
        var sonarr = new HttpMediaManagerPorts(http).PortForKind("sonarr")!;

        var truth = await sonarr.LibraryTruthAsync(Connection("sonarr"), "tv");

        Assert.Equal(SignalStatus.Reported, truth.Status);
        Assert.Equal(["/tv/One/S01E01.mkv", "/tv/Two/S01E01.mkv", "/tv/Two/S01E02.mkv"], truth.LibraryFilePaths);
    }

    [Fact]
    public async Task An_unreachable_or_refusing_manager_is_reported_with_the_connection_named()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/v3/rootfolder", "nope", HttpStatusCode.Unauthorized);
        var radarr = new HttpMediaManagerPorts(http).PortForKind("radarr")!;
        var down = await radarr.QueueRowsAsync(Connection(name: "4K"));
        Assert.Equal(SignalStatus.Unreachable, down.Status);
        Assert.Equal("Weir could not reach Radarr (4K) at http://manager.local. Check the address is right, and that the app is running and reachable from this machine.", down.Detail);

        var refused = await radarr.DescribeAsync(Connection(name: "4K"));
        Assert.Equal(SignalStatus.Unreachable, refused.Status);
        Assert.Contains("refused Weir's API key", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rate_limited_deluno_backs_off_and_a_down_deluno_degrades_to_the_static_profile()
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Get, "/api/integrations/external/queue", _ => FakeManagerHttp.Response((HttpStatusCode)429, string.Empty, ("Retry-After", "30")));
        var deluno = new HttpMediaManagerPorts(http).PortForKind("deluno")!;
        var signal = await deluno.QueueRowsAsync(Connection("deluno"));
        Assert.Equal(SignalStatus.Unreachable, signal.Status);
        Assert.Contains("about 30s", signal.Detail, StringComparison.Ordinal);

        var described = await deluno.DescribeAsync(Connection("deluno"));
        Assert.Equal(SignalStatus.Unreachable, described.Status);
        Assert.Equal(["movie", "tv"], described.Capabilities.Scopes);
        Assert.StartsWith("Weir could not reach Deluno (Main)", described.Detail, StringComparison.Ordinal);

        var truth = await deluno.LibraryTruthAsync(Connection("deluno"), "movie");
        Assert.Equal(SignalStatus.NoSignal, truth.Status);
        Assert.Contains("Deluno (Main)", truth.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deluno_describe_reads_the_manifest_with_the_api_key()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/integrations/external/manifest", """{"libraries":[{"id":"lib-movies","name":"Movies","mediaType":"movies","path":"/media/movies"},{"id":"lib-tv","name":"TV","mediaType":"tv","path":"/media/tv"}]}""");
        var described = await new HttpMediaManagerPorts(http).PortForKind("deluno")!.DescribeAsync(Connection("deluno"));
        Assert.Equal(SignalStatus.Reported, described.Status);
        Assert.Equal(["movie", "tv"], described.Capabilities.Scopes);
        Assert.Equal(["/media/movies", "/media/tv"], described.LibraryRoots);
        Assert.Equal("k", Assert.Single(http.Requests).Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task Native_speaks_weirs_own_payload_keys()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/integrations/external/queue", """{"items":[{"media_scope":"tv","state":"importing","file_path":"/tv/Show/S01E01.mkv","release_name":"Show S01E01","entity_id":4}]}""");
        var row = Assert.Single((await new HttpMediaManagerPorts(http).PortForKind("native")!.QueueRowsAsync(Connection("native", "Home"))).Rows);
        Assert.Equal("tv", row.Scope);
        Assert.Equal(
            """{"status":"importpending","outputPath":"/tv/Show/S01E01.mkv","title":"Show S01E01","media":{"title":"Show S01E01","year":null},"entityId":4}""",
            WireJsonWriter.Dumps(row.Payload, WireJsonFormat.Compact));
    }

    // --- list_library_files / file_changed (#507) -----------------------------------------------

    [Fact]
    public async Task Radarr_lists_movie_library_files_from_the_movie_endpoint_and_rescans_by_id()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/v3/movie", """[{"id":7,"title":"Solaris","movieFile":{"path":"/media/Solaris/f.mkv"}},{"id":8,"title":"No File"}]""");
        var radarr = new HttpMediaManagerPorts(http).PortForKind("radarr")!;
        var signal = await radarr.ListLibraryFilesAsync(Connection(), "movie");
        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.Equal([new ManagerLibraryFile("7", "Solaris", "/media/Solaris/f.mkv")], signal.Files);
        Assert.Equal("/api/v3/movie?pageSize=200000", Assert.Single(http.Requests).PathAndQuery);

        // Wrong scope: no network, just the "does not look after this" no-signal.
        var wrongScope = await radarr.ListLibraryFilesAsync(Connection(), "tv");
        Assert.Equal(SignalStatus.NoSignal, wrongScope.Status);
        Assert.Empty(wrongScope.Files);

        http.Json(HttpMethod.Post, "/api/v3/command", """{"id":1}""", HttpStatusCode.Created);
        var outcome = await radarr.FileChangedAsync(Connection(), new SortedSet<string>(), "7", "/media/Solaris/f.mkv", "removed 2 audio tracks", CancellationToken.None);
        Assert.Equal(ManagerNotifyOutcome.Notified, outcome);
        var command = http.RequestsTo(HttpMethod.Post, "/api/v3/command").Single();
        Assert.Equal("""{"name":"RescanMovie","movieId":7}""", WireJsonWriter.Dumps(command.Json!, WireJsonFormat.Compact));
    }

    [Fact]
    public async Task Sonarr_lists_episode_files_per_series_and_rescans_by_series_id()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Get, "/api/v3/series", """[{"id":12,"title":"Show"},{"id":13,"title":"Other"}]""")
            .Route(HttpMethod.Get, "/api/v3/episodefile", request => request.Uri.Query.Contains("seriesId=12", StringComparison.Ordinal)
                ? FakeManagerHttp.Response(HttpStatusCode.OK, """[{"path":"/tv/Show/S01/e01.mkv"},{"path":"/tv/Show/S01/e02.mkv"}]""")
                : FakeManagerHttp.Response(HttpStatusCode.OK, "[]"));
        var sonarr = new HttpMediaManagerPorts(http).PortForKind("sonarr")!;
        var signal = await sonarr.ListLibraryFilesAsync(Connection("sonarr"), "tv");
        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.Equal(
            [new ManagerLibraryFile("12", "Show", "/tv/Show/S01/e01.mkv"), new ManagerLibraryFile("12", "Show", "/tv/Show/S01/e02.mkv")],
            signal.Files);
        Assert.Equal(
            ["/api/v3/series?pageSize=200000", "/api/v3/episodefile?seriesId=12", "/api/v3/episodefile?seriesId=13"],
            http.Requests.Select(r => r.PathAndQuery));

        http.Json(HttpMethod.Post, "/api/v3/command", """{"id":2}""", HttpStatusCode.Created);
        await sonarr.FileChangedAsync(Connection("sonarr"), new SortedSet<string>(), "12", "/tv/Show/S01/e01.mkv", null, CancellationToken.None);
        var command = http.RequestsTo(HttpMethod.Post, "/api/v3/command").Single();
        Assert.Equal("""{"name":"RescanSeries","seriesId":12}""", WireJsonWriter.Dumps(command.Json!, WireJsonFormat.Compact));
    }

    [Fact]
    public async Task Deluno_has_no_listing_but_accepts_file_changed_when_it_advertises_the_capability()
    {
        var http = new FakeManagerHttp();
        var deluno = new HttpMediaManagerPorts(http).PortForKind("deluno")!;
        var signal = await deluno.ListLibraryFilesAsync(Connection("deluno"), "movie");
        Assert.Equal(SignalStatus.NoSignal, signal.Status);
        Assert.Empty(http.Requests);

        var withoutCapability = await deluno.FileChangedAsync(Connection("deluno"), new SortedSet<string>(), null, "/media/f.mkv", "removed 2 audio tracks", CancellationToken.None);
        Assert.Equal(ManagerNotifyOutcome.NotSupported, withoutCapability);
        Assert.Empty(http.Requests);

        http.Json(HttpMethod.Post, "/api/integrations/external/file-changed", """{"path":"/media/f.mkv","coalesced":false,"titles":[]}""", HttpStatusCode.Accepted);
        var capabilities = new SortedSet<string>(StringComparer.Ordinal) { "external-file-changed" };
        var notified = await deluno.FileChangedAsync(Connection("deluno"), capabilities, null, "/media/f.mkv", "removed 2 audio tracks", CancellationToken.None);
        Assert.Equal(ManagerNotifyOutcome.Notified, notified);
        var request = http.RequestsTo(HttpMethod.Post, "/api/integrations/external/file-changed").Single();
        Assert.Equal("""{"path":"/media/f.mkv","tool":"Weir","reason":"removed 2 audio tracks"}""", WireJsonWriter.Dumps(request.Json!, WireJsonFormat.Compact));
    }
}
