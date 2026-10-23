using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Time;
using Weir.Infrastructure.Processing;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The last edges of the Processing API: files carrying every detail, workflows that are gone, settings left blank, and cleanups that have run.</summary>
public sealed class ProcessingEdgesApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static Task<long> SeedLibraryAsync(WeirTestServer server, string name = "Films") =>
        TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type) VALUES ($name, 'movie') RETURNING id", ("$name", name));

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string path, string status = "processed") =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, $status, CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", path), ("$status", status));

    [Fact]
    public async Task A_file_is_listed_with_every_detail_it_has_and_with_none_it_has_not()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = await SeedLibraryAsync(server);
        var at = Timestamp.FromUtc(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)).ToSqlite();
        await SeedFileAsync(server, libraryId, "Film/full.mkv", "processing");
        await SeedFileAsync(server, libraryId, "Film/bare.mkv", "unprocessed");
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE files SET duration_seconds = 5400.5, size_changed_at = $at, hold_until = $at, next_retry_at = $at, last_attempt_at = $at WHERE relative_path = 'Film/full.mkv'",
            ("$at", at));
        server.Services.GetRequiredService<LiveProgressStore>().Update(
            "Film/full.mkv",
            new LiveProgress(42.0, "Weir is writing the cleaned-up file.", 30.0, "processing", "120x", 5.0, ["commentary"], ["forced"], "writing"));

        using var response = await client.GetAsync("/api/v1/processing/files");

        var files = (await Json(response))["files"]!.AsArray().ToDictionary(file => file!["relative_path"]!.GetValue<string>());
        var full = files["Film/full.mkv"]!;
        var bare = files["Film/bare.mkv"]!;
        Assert.Equal(5400.5, full["duration_seconds"]!.GetValue<double>());
        Assert.Equal(42.0, full["progress_percent"]!.GetValue<double>());
        Assert.Equal(30.0, full["progress_eta_seconds"]!.GetValue<double>());
        Assert.Equal(5.0, full["progress_elapsed_seconds"]!.GetValue<double>());
        Assert.Equal(["commentary"], full["progress_removed_audio"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.All(["hold_until", "next_retry_at", "size_changed_at", "last_attempt_at"], name => Assert.NotNull(full[name]));
        Assert.All(["duration_seconds", "progress_percent", "progress_eta_seconds", "progress_elapsed_seconds", "progress_removed_audio", "hold_until", "next_retry_at"], name => Assert.Null(bare[name]));
    }

    [Fact]
    public async Task A_list_of_connections_to_check_skips_the_ones_that_are_not_numbers()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync("/api/v1/processing/reject-support?connection_ids=abc&connection_ids=7&connection_ids=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_file_whose_workflow_is_gone_cannot_be_asked_why_it_is_held()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "PRAGMA foreign_keys = OFF; INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES (9999, 'Film/orphan.mkv', 'unprocessed', CURRENT_TIMESTAMP) RETURNING id");

        using var response = await client.GetAsync($"/api/v1/processing/files/{fileId}/why-held");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The workflow this file belonged to no longer exists.", await Detail(response));
    }

    [Fact]
    public async Task A_kept_file_whose_workflow_is_gone_is_cleared_and_told_it_will_be_scanned()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var markerId = await TestDatabase.ScalarAsync(
            server,
            "PRAGMA foreign_keys = OFF; INSERT INTO file_skip_markers (library_id, relative_path, size_bytes, mtime_ns) VALUES (9999, 'Film/orphan.mkv', 1, 1) RETURNING id");

        using var response = await client.PostAsync($"/api/v1/processing/kept-files/{markerId}/process-again", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Weir will look at this file the next time it scans its workflow.", (await Json(response))["detail"]!.GetValue<string>());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task A_cleanup_that_has_run_and_one_that_has_failed_say_when()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, status) VALUES ('sweep-done', 'processing.work_temp_stale_sweep.v1', 'completed'), " +
            "('sweep-failed', 'processing.work_temp_stale_sweep.v1', 'failed')");

        using var response = await client.GetAsync("/api/v1/processing/maintenance");

        var families = (await Json(response))["families"]!.AsArray();
        var sweep = families.Single(family => family!["family"]!.GetValue<string>() == "work_temp_stale_sweep")!;
        var unclaimed = families.Single(family => family!["family"]!.GetValue<string>() == "unclaimed_handbacks")!;
        Assert.NotNull(sweep["last_completed_at"]);
        Assert.NotNull(sweep["last_failed_at"]);
        Assert.Null(unclaimed["last_completed_at"]);
        Assert.Null(unclaimed["last_failed_at"]);
    }

    [Theory]
    [InlineData("""{"forced": "Forced"}""")]
    [InlineData("""{"hearing_impaired": "SDH", "commentary": "Notes"}""")]
    [InlineData("""{"audio_description": "AD"}""")]
    [InlineData("""{}""")]
    public async Task Track_name_overrides_may_name_some_of_the_four_kinds_and_leave_the_rest_as_they_were(string overridesJson)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var overrides = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(overridesJson)!;

        using var response = await client.PostAsync(
            "/api/v1/processing/rule-sets",
            new { csrf_token = await client.CsrfAsync(), name = "Overrides " + Guid.NewGuid().ToString("N"), track_name_overrides = overrides });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var shown = (await Json(response))["track_name_overrides"]!;
        foreach (var (kind, text) in overrides)
        {
            Assert.Equal(text, shown[kind]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_file_log_of_a_workflow_a_manager_hands_files_to_is_read_and_a_long_path_is_shortened_in_its_download()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = await SeedLibraryAsync(server);
        var longPath = string.Join('/', Enumerable.Range(0, 12).Select(index => $"Folder number {index}")) + "/film.mkv";
        var fileId = await SeedFileAsync(server, libraryId, longPath);
        var connectionId = await TestDatabase.ScalarAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', 'http://192.0.2.30:5000') RETURNING id");
        await TestDatabase.ExecuteAsync(server, "INSERT INTO library_manager_links (library_id, connection_id) VALUES ($lib, $conn)", ("$lib", libraryId), ("$conn", connectionId));

        using var log = await client.GetAsync($"/api/v1/processing/files/{fileId}/log");
        using var download = await client.GetAsync($"/api/v1/processing/files/{fileId}/log/download");

        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var name = download.Content.Headers.ContentDisposition!.FileName!.Trim('"');
        Assert.True(name.Length < longPath.Length);
        Assert.EndsWith("film.mkv.log.txt", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settings_row_with_a_blank_timezone_is_read_and_saved_as_utc()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        const string Settings = "/api/v1/processing/operator-settings";
        using (await client.GetAsync(Settings))
        {
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE suite_settings SET app_timezone = '   '");
        await SeedLibraryAsync(server);

        using var read = await client.GetAsync(Settings);
        using var saved = await client.PutAsync(Settings, new { csrf_token = await client.CsrfAsync(), keep_failed_work_files = true });
        using var libraries = await client.GetAsync("/api/v1/processing/libraries");

        Assert.All([read, saved, libraries], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
    }

    [Fact]
    public async Task A_workflow_row_with_blank_choices_is_listed_with_the_defaults()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = await SeedLibraryAsync(server);
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET rejected_file_action = '', output_collision_policy = '', ffmpeg_strictness = '' WHERE id = $id", ("$id", libraryId));

        using var response = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}");

        var body = await Json(response);
        Assert.Equal(("leave", "replace", "normal"), (body["rejected_file_action"]!.GetValue<string>(), body["output_collision_policy"]!.GetValue<string>(), body["ffmpeg_strictness"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Track_name_overrides_given_as_null_leave_every_kind_as_it_was()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(
            "/api/v1/processing/rule-sets",
            new
            {
                csrf_token = await client.CsrfAsync(),
                name = "Null overrides " + Guid.NewGuid().ToString("N"),
                track_name_overrides = new Dictionary<string, string?> { ["forced"] = null, ["hearing_impaired"] = null, ["commentary"] = null, ["audio_description"] = null },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_workflow_shows_when_weir_will_next_look_at_its_folder_and_next_scan_it()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = await SeedLibraryAsync(server);
        var wakeups = server.Services.GetRequiredService<Weir.Infrastructure.Jobs.ScanWakeups>();
        var look = DateTimeOffset.UtcNow.AddMinutes(5);
        var scan = DateTimeOffset.UtcNow.AddMinutes(30);
        wakeups.Request(libraryId, look);
        wakeups.RecordNextPeriodic(libraryId, scan);

        using var response = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}");

        var body = await Json(response);
        Assert.NotNull(body["next_look_at"]);
        Assert.NotNull(body["next_scan_at"]);
    }
}
