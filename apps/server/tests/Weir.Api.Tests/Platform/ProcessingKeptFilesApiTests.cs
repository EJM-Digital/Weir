using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>"Kept files" (#786 review of #785): the list a "keep" choice leaves behind, and its one way back.</summary>
public sealed class ProcessingKeptFilesApiTests
{
    private static async Task<long> SeedLibraryAsync(WeirTestServer server, string watched) =>
        await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, display_order) VALUES ($name, 'movie', $w, 1) RETURNING id",
            ("$name", "Movies " + Guid.NewGuid().ToString("N")[..8]), ("$w", watched));

    private static Task<long> SeedMarkerAsync(WeirTestServer server, long libraryId, string relativePath) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO file_skip_markers (library_id, relative_path, size_bytes, mtime_ns) VALUES ($lib, $path, 10, 20) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath));

    [Fact]
    public async Task The_list_names_the_library_and_the_file()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Path.Combine(server.Home, "watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watched);
        var libraryId = await SeedLibraryAsync(server, watched);
        await SeedMarkerAsync(server, libraryId, "Film/film.mkv");

        using var response = await client.GetAsync("/api/v1/processing/kept-files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var files = (await ApiTestClient.Json(response))!["files"]!.AsArray();
        var file = Assert.Single(files);
        Assert.Equal("Film/film.mkv", file!["relative_path"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(file["library_name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Process_again_clears_the_marker_and_queues_a_scan()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Path.Combine(server.Home, "watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watched);
        var libraryId = await SeedLibraryAsync(server, watched);
        var markerId = await SeedMarkerAsync(server, libraryId, "Film/film.mkv");

        using var response = await client.SendAsync(
            HttpMethod.Post, $"/api/v1/processing/kept-files/{markerId}/process-again", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM file_skip_markers"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.watched_folder.remux_scan_dispatch.v1'"));
    }

    [Fact]
    public async Task Process_again_on_an_unknown_marker_answers_404()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/processing/kept-files/999/process-again", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_process_a_kept_file_again()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedViewerAsync(server);
        var watched = Path.Combine(server.Home, "watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watched);
        var libraryId = await SeedLibraryAsync(server, watched);
        var markerId = await SeedMarkerAsync(server, libraryId, "Film/film.mkv");
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ApiTestClient.ViewerPassword);

        using var response = await viewer.SendAsync(
            HttpMethod.Post, $"/api/v1/processing/kept-files/{markerId}/process-again", new { csrf_token = await viewer.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task Process_again_for_a_workflow_Deluno_hands_downloads_to_clears_the_marker_and_scans_nothing()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Path.Combine(server.Home, "watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watched);
        var libraryId = await SeedLibraryAsync(server, watched);
        var markerId = await SeedMarkerAsync(server, libraryId, "Film/film.mkv");
        var connection = await TestDatabase.ScalarAsync(
            server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', 'http://192.0.2.10:5099') RETURNING id");
        await TestDatabase.ExecuteAsync(server, "INSERT INTO library_manager_links (library_id, connection_id) VALUES ($l, $c)", ("$l", libraryId), ("$c", connection));

        using var response = await client.SendAsync(
            HttpMethod.Post, $"/api/v1/processing/kept-files/{markerId}/process-again", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Deluno hands this workflow its downloads, so Weir does not scan its watched folder. Weir processes this file when Deluno hands it over.",
            (await ApiTestClient.Json(response))["detail"]!.GetValue<string>());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM file_skip_markers"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.watched_folder.remux_scan_dispatch.v1'"));
    }
}
