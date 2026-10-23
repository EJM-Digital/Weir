using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.Processing.RemuxPass;
using static Weir.Api.Tests.Platform.ApiTestClient;
using static Weir.Api.Tests.Processing.LibraryViewApiTests;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// The Library Files routes at their edges: who may use each, filters that mean nothing, a clean that has nothing to act on, files
/// the rules do not want changed, a file still shared with a download, and a track choice that cannot be applied.
/// </summary>
public sealed class LibraryFilesEndpointsApiTests
{
    private static Task<long> CleanJobsAsync(WeirTestServer server) =>
        TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.library.clean.v1'");

    private static async Task<(HttpStatusCode Status, JsonNode Body)> CleanAsync(ApiTestClient client, long libraryId, object body)
    {
        var json = JsonSerializer.SerializeToNode(body)!.AsObject();
        json["csrf_token"] = await client.CsrfAsync();
        using var response = await client.PostAsync(CleanPath(libraryId), json);
        return (response.StatusCode, await Json(response));
    }

    [Fact]
    public async Task The_table_is_open_to_anyone_signed_in_and_cleaning_and_setting_a_file_aside_to_operators()
    {
        var (server, _, libraryId) = await StartAsync();
        await using var _ = server;

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, FilesPath(libraryId), Callers.Viewer);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, CleanPath(libraryId), Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, LeaveAlonePath(libraryId), Callers.Operator);
    }

    [Fact]
    public async Task A_wrong_confirmation_token_queues_nothing_and_sets_nothing_aside()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        using var clean = await client.PostAsync(CleanPath(libraryId), new { csrf_token = "not-a-token", paths = new[] { "/lib/film.mkv" }, confirm_final_removal = true });
        using var aside = await client.PostAsync(LeaveAlonePath(libraryId), new { csrf_token = "not-a-token", path = "/lib/film.mkv" });

        Assert.All([clean, aside], response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal(0, await CleanJobsAsync(server));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM library_file_marks"));
    }

    [Fact]
    public async Task A_filter_value_the_table_does_not_know_narrows_nothing()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe);

        using var response = await client.GetAsync(FilesPath(libraryId, "status=no-such-status&state=sideways&q=%20%20&classification=%20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await Json(response))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Cleaning_with_nothing_selected_or_nothing_in_the_latest_scan_is_refused()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        var empty = await CleanAsync(client, libraryId, new { paths = Array.Empty<string>() });
        var unknown = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/not-scanned.mkv" }, confirm_final_removal = true });

        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal("Select at least one file to clean.", empty.Body["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal("None of the selected files are in the latest scan. Scan the library again first.", unknown.Body["detail"]!.GetValue<string>());
        Assert.Equal(0, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_file_the_rules_would_not_change_is_not_queued_but_the_one_next_to_it_is()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe);

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/film.mkv", "/lib/show.mkv" }, confirm_final_removal = true });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
        Assert.Empty(body["skipped_paths"]!.AsArray());
        Assert.Equal(1, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_file_still_shared_with_a_download_is_never_queued_and_the_answer_says_why()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var film = Path.Join(server.Home, "library", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        await File.WriteAllTextAsync(film, "the film");
        Assert.True(FileLifecycle.CreateHardLink(Path.Join(server.Home, "downloads-film.mkv"), film), "The test folder cannot make a hard link.");
        await SeedFileAsync(server, libraryId, film, "would_change", FilmProbe);

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { film }, confirm_final_removal = true });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body["queued"]!.GetValue<int>());
        Assert.Equal([film], body["skipped_paths"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.StartsWith("film.mkv: ", body["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_track_choice_that_is_not_a_choice_is_refused_before_anything_is_read_from_the_file()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/film.mkv" }, manual_plan = new { surprise = 1 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.StartsWith("'manual_plan' must be an object with 'keep'", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_track_choice_for_a_file_whose_tracks_were_never_read_says_to_scan_again()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);
        await TestDatabase.ExecuteAsync(server, "DELETE FROM library_file_probes");

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/film.mkv" }, manual_plan = JapaneseOnlyChoice() });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Weir has not read this file's tracks yet. Scan the library again, then choose.", body["detail"]!.GetValue<string>());
        Assert.Equal(0, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_track_choice_for_a_file_whose_stored_tracks_cannot_be_used_says_so()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);
        await TestDatabase.ExecuteAsync(server, "UPDATE library_file_probes SET probe_json = '{\"streams\": [{\"index\": 1, \"codec_type\": 5}]}'");

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/film.mkv" }, manual_plan = JapaneseOnlyChoice() });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(Weir.Core.Media.ToolFailureText.UnusableTrackData, body["detail"]!.GetValue<string>());
        Assert.Equal(0, await CleanJobsAsync(server));
    }

    [Theory]
    [InlineData("classification=would_change&manager=radarr&q=film")]
    [InlineData("classification=&manager=&q=")]
    [InlineData("manager=%20")]
    public async Task The_table_narrows_by_a_classification_manager_or_search_only_when_it_is_given_one(string query)
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        using var response = await client.GetAsync(FilesPath(libraryId, query));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_path_named_twice_is_cleaned_once()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        var (status, body) = await CleanAsync(client, libraryId, new { paths = new[] { "/lib/film.mkv", "/lib/film.mkv" }, confirm_final_removal = true });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
        Assert.Equal(1, await CleanJobsAsync(server));
    }

    [Fact]
    public async Task A_track_choice_made_against_a_file_of_a_known_size_is_queued_with_that_size()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe, sizeBytes: 2_000_000);

        var (status, body) = await CleanAsync(
            client, libraryId, new { paths = new[] { "/lib/film.mkv" }, confirm_final_removal = true, manual_plan = JapaneseOnlyChoice(), expected_size_bytes = 2_000_000 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
    }
}
