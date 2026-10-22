using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>The query string of <c>GET /processing/files</c>: each narrowing it takes is applied when it is valid and refused by name when it is not.</summary>
public sealed class ProcessingFilesQueryApiTests
{
    private static async Task<long> SeedLibraryAsync(WeirTestServer server, string name) =>
        await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type) VALUES ($name, 'movie') RETURNING id",
            ("$name", name));

    private static Task SeedFileAsync(WeirTestServer server, long libraryId, string relativePath, string seen = "CURRENT_TIMESTAMP") =>
        TestDatabase.ExecuteAsync(
            server,
            $"INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'unprocessed', {seen})",
            ("$lib", libraryId), ("$path", relativePath));

    private static async Task<(ApiTestClient Client, WeirTestServer Server)> StartAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (client, server);
    }

    private static async Task<IReadOnlyList<string>> PathsAsync(ApiTestClient client, string query)
    {
        using var response = await client.GetAsync($"/api/v1/processing/files?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        return [.. body!["files"]!.AsArray().Select(file => file!["relative_path"]!.GetValue<string>()).Order()];
    }

    [Fact]
    public async Task A_library_id_lists_only_that_workflow_s_files()
    {
        var (client, server) = await StartAsync();
        await using var _ = server;
        var movies = await SeedLibraryAsync(server, "Films");
        var shows = await SeedLibraryAsync(server, "Series");
        await SeedFileAsync(server, movies, "Heat/heat.mkv");
        await SeedFileAsync(server, shows, "Show/s01e01.mkv");

        Assert.Equal(["Heat/heat.mkv"], await PathsAsync(client, $"library_id={movies}"));
        Assert.Equal(["Heat/heat.mkv", "Show/s01e01.mkv"], await PathsAsync(client, string.Empty));
    }

    [Fact]
    public async Task Within_days_keeps_the_files_seen_in_that_time()
    {
        var (client, server) = await StartAsync();
        await using var _ = server;
        var movies = await SeedLibraryAsync(server, "Films");
        await SeedFileAsync(server, movies, "Heat/heat.mkv");
        await SeedFileAsync(server, movies, "Old/old.mkv", "datetime('now', '-40 days')");

        Assert.Equal(["Heat/heat.mkv"], await PathsAsync(client, "within_days=30"));
        Assert.Equal(["Heat/heat.mkv", "Old/old.mkv"], await PathsAsync(client, "within_days=3650"));
    }

    [Fact]
    public async Task A_limit_caps_the_page()
    {
        var (client, server) = await StartAsync();
        await using var _ = server;
        var movies = await SeedLibraryAsync(server, "Films");
        await SeedFileAsync(server, movies, "a.mkv");
        await SeedFileAsync(server, movies, "b.mkv");

        Assert.Single(await PathsAsync(client, "limit=1"));
        Assert.Equal(2, (await PathsAsync(client, "limit=1000")).Count);
    }

    [Theory]
    [InlineData("library_id=abc", "library_id")]
    [InlineData("library_id=0", "library_id")]
    [InlineData("within_days=0", "within_days")]
    [InlineData("within_days=3651", "within_days")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=1001", "limit")]
    [InlineData("limit=many", "limit")]
    public async Task A_narrowing_that_is_not_valid_is_refused_by_name(string query, string name)
    {
        var (client, server) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"/api/v1/processing/files?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(name, await response.Content.ReadAsStringAsync());
    }
}
