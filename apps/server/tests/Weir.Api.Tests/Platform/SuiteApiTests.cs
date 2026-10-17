using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Time;
using Weir.Core.Updates;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Suite settings, pause, metrics auth, local browse, configuration bundles, the CORS policy, HEAD mirroring
/// GET, and operational history over real HTTP.
/// </summary>
public sealed class SuiteApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAdminAsync(params (string Name, string Value)[] variables)
    {
        var server = await StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task Settings_need_a_session_viewers_can_read_and_only_operators_can_save()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.SeedViewerAsync(server);
        var anonymous = new ApiTestClient(server);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/suite/settings")).StatusCode);

        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        using var read = await viewer.GetAsync("/api/v1/suite/settings");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await viewer.PutAsync("/api/v1/suite/settings", new { csrf_token = await viewer.CsrfAsync(), app_timezone = "UTC", log_retention_days = 30 });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Settings_have_the_default_shape_and_a_put_persists()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        using var initial = await client.GetAsync("/api/v1/suite/settings");
        var body = await Json(initial);
        Assert.False(body.AsObject().ContainsKey("signed_in_home_notice"));
        Assert.False(body.AsObject().ContainsKey("product_display_name"));
        Assert.Equal("pending", body["setup_wizard_state"]!.GetValue<string>());
        Assert.Equal(30, body["log_retention_days"]!.GetValue<int>());
        Assert.False(body["configuration_backup_enabled"]!.GetValue<bool>());
        Assert.Equal(24, body["configuration_backup_interval_hours"]!.GetValue<int>());
        Assert.Equal("02:00", body["configuration_backup_preferred_time"]!.GetValue<string>());
        Assert.Null(body["configuration_backup_last_run_at"]);
        Assert.True(body.AsObject().ContainsKey("updated_at"));

        using var put = await client.PutAsync("/api/v1/suite/settings", new
        {
            csrf_token = await client.CsrfAsync(),
            product_display_name = "House Library",
            setup_wizard_state = "skipped",
            app_timezone = "UTC",
            log_retention_days = 45,
            configuration_backup_enabled = true,
            configuration_backup_interval_hours = 12,
            configuration_backup_preferred_time = "03:30",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await Json(put);
        Assert.False(saved.AsObject().ContainsKey("product_display_name"));
        Assert.Equal("03:30", saved["configuration_backup_preferred_time"]!.GetValue<string>());
        Assert.Equal("Weir", await TestDatabase.ScalarStringAsync(server, "SELECT product_display_name FROM suite_settings WHERE id = 1"));
        Assert.Equal(45, await TestDatabase.ScalarAsync(server, "SELECT log_retention_days FROM suite_settings WHERE id = 1"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT configuration_backup_enabled FROM suite_settings WHERE id = 1"));

        using var badZone = await client.PutAsync("/api/v1/suite/settings", new { csrf_token = await client.CsrfAsync(), app_timezone = "Not/A_Real_Zone", log_retention_days = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
        Assert.Contains("timezone", (await Detail(badZone)).ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>No page shows a note for the signed-in home page, so there is no setting for one. An older client that still sends it is answered as though it had not.</summary>
    [Fact]
    public async Task An_older_client_sending_the_home_notice_is_answered_and_nothing_is_kept()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;

        using var put = await client.PutAsync("/api/v1/suite/settings", new
        {
            csrf_token = await client.CsrfAsync(),
            signed_in_home_notice = "Welcome back.",
            app_timezone = "UTC",
            log_retention_days = 45,
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await Json(put);
        Assert.False(saved.AsObject().ContainsKey("signed_in_home_notice"));
        Assert.Equal(45, saved["log_retention_days"]!.GetValue<int>());
        Assert.False((await Json(await client.GetAsync("/api/v1/suite/settings"))).AsObject().ContainsKey("signed_in_home_notice"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM pragma_table_info('suite_settings') WHERE name = 'signed_in_home_notice'"));

        using var notText = await client.PutAsync("/api/v1/suite/settings", new
        {
            csrf_token = await client.CsrfAsync(),
            signed_in_home_notice = 7,
            app_timezone = "UTC",
            log_retention_days = 45,
        });
        Assert.Equal(HttpStatusCode.OK, notText.StatusCode);
    }

    [Fact]
    public async Task Security_overview_lists_its_fields()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var body = (await Json(await client.GetAsync("/api/v1/suite/security-overview"))).AsObject();
        foreach (var key in new[] { "restart_required_note", "session_signing_configured", "allowed_browser_origins_count", "standard_session_idle_timeout_plain", "trusted_session_absolute_timeout_plain" })
        {
            Assert.True(body.ContainsKey(key), key);
        }
    }

    [Fact]
    public async Task Update_status_compares_with_the_latest_release_and_says_when_none_is_published()
    {
        var catalog = new FakeReleaseCatalog();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_VERSION", "1.0.0")],
            configureServices: services => services.AddSingleton<IReleaseCatalogClient>(catalog));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        var body = await Json(await client.GetAsync("/api/v1/suite/update-status"));
        Assert.Equal("1.0.0", body["current_version"]!.GetValue<string>());
        Assert.Equal("1.2.3", body["latest_version"]!.GetValue<string>());
        Assert.Equal("update_available", body["status"]!.GetValue<string>());

        catalog.NotFound = true;
        var missing = await Json(await client.GetAsync("/api/v1/suite/update-status"));
        Assert.Equal("not_published", missing["status"]!.GetValue<string>());
        Assert.Contains("no public weir release is published yet", missing["summary"]!.GetValue<string>().ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_status_reports_a_pre_release_version_exactly()
    {
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_VERSION", "1.0.0-rc.1")],
            configureServices: services => services.AddSingleton<IReleaseCatalogClient>(new FakeReleaseCatalog()));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        var body = await Json(await client.GetAsync("/api/v1/suite/update-status"));

        Assert.Equal("1.0.0-rc.1", body["current_version"]!.GetValue<string>());
    }

    [Fact]
    public async Task Logs_skip_entries_whose_timestamp_does_not_parse()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var logFile = server.Services.GetRequiredService<Infrastructure.Logging.WeirLogFile>();
        logFile.WriteLine("{\"timestamp\":\"not-a-time\",\"level\":\"INFO\",\"logger\":\"weir.platform.suite_settings\",\"message\":\"broken timestamp\"}");
        logFile.WriteLine("{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"INFO\",\"logger\":\"weir.platform.suite_settings\",\"message\":\"valid timestamp\"}");

        using var response = await client.GetAsync("/api/v1/suite/logs?search=timestamp");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await Json(response))["items"]!.AsArray();
        Assert.Single(items);
        Assert.Equal("valid timestamp", items[0]!["message"]!.GetValue<string>());
        Assert.Equal("2026-05-09T10:00:00Z", items[0]!["timestamp"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pause_starts_off_and_can_be_set_with_or_without_an_expiry()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var initial = await Json(await client.GetAsync("/api/v1/pause"));
        Assert.False(initial["paused"]!.GetValue<bool>());
        Assert.Contains("already running finishes", initial["in_flight_policy"]!.GetValue<string>(), StringComparison.Ordinal);

        using var timed = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = 120 });
        Assert.Equal(HttpStatusCode.OK, timed.StatusCode);
        var timedBody = await Json(timed);
        Assert.True(timedBody["paused"]!.GetValue<bool>());
        Assert.NotNull(timedBody["paused_until"]);
        Assert.Contains("automatically at", timedBody["reason"]!.GetValue<string>(), StringComparison.Ordinal);

        var open = await Json(await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = (int?)null, scan_while_paused = false }));
        Assert.Null(open["paused_until"]);
        Assert.Contains("when you resume it", open["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(open["scan_while_paused"]!.GetValue<bool>());

        var resumed = await Json(await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = false }));
        Assert.False(resumed["paused"]!.GetValue<bool>());
        Assert.Null(resumed["paused_until"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsync("/api/v1/pause", new { paused = true, scan_while_paused = true })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = 0 })).StatusCode);
    }

    [Fact]
    public async Task Metrics_need_an_operator_session_or_the_bearer_token()
    {
        var (server, client) = await SignedInAdminAsync(("WEIR_METRICS_BEARER_TOKEN", "metrics-secret-token"));
        await using var _ = server;
        var anonymous = new ApiTestClient(server);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/metrics", new Dictionary<string, string> { ["Authorization"] = "Bearer wrong-token" })).StatusCode);

        using var bearer = await anonymous.GetAsync("/metrics", new Dictionary<string, string> { ["Authorization"] = "Bearer metrics-secret-token" });
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        Assert.Contains("weir_http_requests_total", await bearer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", bearer.Content.Headers.ContentType?.ToString());

        using var session = await client.GetAsync("/metrics");
        Assert.Contains("weir_http_requests_total", await session.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_browse_is_admin_only_lists_roots_and_reports_missing_folders()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/system/directories")).StatusCode);

        var roots = await Json(await client.GetAsync("/api/v1/system/directories"));
        Assert.Null(roots["current_path"]);
        Assert.Null(roots["parent_path"]);
        var first = roots["entries"]!.AsArray()[0]!.AsObject();
        Assert.True(first.ContainsKey("name") && first.ContainsKey("path") && first.ContainsKey("kind") && first.ContainsKey("description"));

        var missing = Path.Join(Path.GetTempPath(), "weir-missing-" + Guid.NewGuid());
        using var notFound = await client.GetAsync("/api/v1/system/directories?path=" + Uri.EscapeDataString(missing));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("The requested directory does not exist.", await Detail(notFound));
    }

    [Fact]
    public async Task The_configuration_bundle_is_admin_only_and_round_trips()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/suite/configuration-bundle")).StatusCode);

        Assert.Equal(4, (await Json(await client.GetAsync("/api/v1/suite/configuration-bundle")))["format_version"]!.GetValue<int>());
        var bundle = (await Json(await client.GetAsync("/api/v1/suite/configuration-bundle"))).AsObject();
        Assert.True(bundle.ContainsKey("arr_library_operator_settings"));
        Assert.DoesNotContain(bundle, pair => pair.Key.StartsWith("pruner_", StringComparison.Ordinal));

        var older = bundle.DeepClone().AsObject();
        older["suite_settings"]!["product_display_name"] = "Restored From Older Backup";
        older["pruner_server_instances"] = new JsonArray(new JsonObject { ["id"] = 1, ["provider"] = "plex" });
        using var put = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle = older });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Weir", await TestDatabase.ScalarStringAsync(server, "SELECT product_display_name FROM suite_settings WHERE id = 1"));

        using var restore = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var bad = bundle.DeepClone().AsObject();
        bad["format_version"] = 999;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle = bad })).StatusCode);

        var olderFormat = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Fixtures", "bundle-v4.json")));
        using var fromOlder = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle = olderFormat });
        Assert.Equal(HttpStatusCode.OK, fromOlder.StatusCode);
        Assert.Equal("Weir", await TestDatabase.ScalarStringAsync(server, "SELECT product_display_name FROM suite_settings WHERE id = 1"));
    }

    /// <summary>The fixture is a backup an older Weir wrote: it carries the home note and the hours window for Movies and TV, which are retired.</summary>
    [Fact]
    public async Task A_backup_from_an_older_Weir_restores_without_the_retired_settings_it_carries()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var olderFormat = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Fixtures", "bundle-v4.json")))!.AsObject();
        Assert.Equal("Hello", olderFormat["suite_settings"]!["signed_in_home_notice"]!.GetValue<string>());
        olderFormat["operator_settings"]!["movie_schedule_hours_limited"] = 1;
        olderFormat["operator_settings"]!["movie_schedule_days"] = "Mon,Tue";
        olderFormat["operator_settings"]!["tv_schedule_start"] = "08:00";

        using var restored = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle = olderFormat });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var answer = await Json(restored);
        Assert.False(answer["suite_settings"]!.AsObject().ContainsKey("signed_in_home_notice"));
        foreach (var field in new[] { "movie_schedule_hours_limited", "movie_schedule_days", "tv_schedule_start" })
        {
            Assert.False(answer["operator_settings"]!.AsObject().ContainsKey(field), field);
        }

        Assert.False((await Json(await client.GetAsync("/api/v1/suite/settings"))).AsObject().ContainsKey("signed_in_home_notice"));
        Assert.False((await Json(await client.GetAsync("/api/v1/processing/operator-settings"))).AsObject().ContainsKey("movie_schedule_days"));
        Assert.Equal(14, await TestDatabase.ScalarAsync(server, "SELECT log_retention_days FROM suite_settings WHERE id = 1"));
    }

    [Fact]
    public async Task A_backup_made_when_performance_kept_the_free_space_gives_it_to_every_workflow()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var olderFormat = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Fixtures", "bundle-v4.json")))!.AsObject();
        olderFormat["operator_settings"]!["minimum_free_disk_space_mb"] = 8192;

        using var restored = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle = olderFormat });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries WHERE minimum_free_disk_space_mb = 8192"));
    }

    [Fact]
    public async Task A_backup_keeps_the_space_each_workflow_keeps_free()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        using var created = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Anime", media_type = "tv", watched_folder = @"c:\anime-in", output_folder = @"c:\anime-out", minimum_free_disk_space_mb = 2048 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bundle = (await Json(await client.GetAsync("/api/v1/suite/configuration-bundle"))).AsObject();
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET minimum_free_disk_space_mb = 5120");

        using var restored = await client.PutAsync("/api/v1/suite/configuration-bundle", new { csrf_token = await client.CsrfAsync(), bundle });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(2048, await TestDatabase.ScalarAsync(server, "SELECT minimum_free_disk_space_mb FROM libraries WHERE name = 'Anime'"));
    }

    /// <summary>
    /// Each of these is a retired second address for a handler that already has one. The server serves one
    /// address per handler; this is here so an alias cannot quietly reappear.
    /// </summary>
    [Fact]
    public async Task The_retired_url_aliases_for_bundles_snapshots_and_update_status_are_not_served()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        foreach (var path in new[]
        {
            "/api/v1/system/suite-configuration-bundle",
            "/api/v1/system/suite-configuration-backups",
            "/api/v1/suite/settings/configuration-bundle",
            "/api/v1/suite/settings/update-status",
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Configuration_snapshots_are_listed_and_downloaded()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var backups = server.Services.GetRequiredService<ConfigurationBackups>();
        var uow = await UnitOfWork.OpenAsync(server.Services.GetRequiredService<SqliteDatabase>());
        long id;
        await using (uow)
        {
            id = (await backups.CreateAsync(uow, BackupSource.Schedule)).Id;
            await uow.CommitAsync();
        }

        var list = await Json(await client.GetAsync("/api/v1/suite/configuration-backups"));
        Assert.Equal(backups.Directory, list["directory"]!.GetValue<string>());
        Assert.Single(list["items"]!.AsArray());

        using var download = await client.GetAsync($"/api/v1/suite/configuration-backups/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(4, JsonNode.Parse(await download.Content.ReadAsStringAsync())!["format_version"]!.GetValue<int>());
        Assert.Contains("attachment", Header(download, "Content-Disposition"), StringComparison.Ordinal);

        using var missing = await client.GetAsync("/api/v1/suite/configuration-backups/999/download");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Configuration snapshot not found.", await Detail(missing));
    }

    [Fact]
    public async Task Notification_channels_can_be_created_updated_and_deleted()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        var empty = await Json(await client.GetAsync("/api/v1/suite/notification-channels"));
        Assert.Empty(empty["items"]!.AsArray());
        Assert.Contains(empty["supported_events"]!.AsArray(), item => item!.GetValue<string>() == "job_failed");

        using var created = await client.PostAsync("/api/v1/suite/notification-channels", new { csrf_token = await client.CsrfAsync(), label = "Ops", provider = "webhook", url = "https://hooks.example.com/weir", events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var channelId = (await Json(created))["id"]!.GetValue<int>();

        using var updated = await client.PutAsync($"/api/v1/suite/notification-channels/{channelId}", new { csrf_token = await client.CsrfAsync(), label = "Renamed", provider = "webhook", url = "https://hooks.example.com/weir", enabled = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Renamed", (await Json(updated))["label"]!.GetValue<string>());

        using var missing = await client.PutAsync("/api/v1/suite/notification-channels/999", new { csrf_token = await client.CsrfAsync(), label = "x", provider = "webhook", url = "https://hooks.example.com/weir" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/suite/notification-channels/999/test", new { csrf_token = await client.CsrfAsync() })).StatusCode);

        var csrf = new Dictionary<string, string> { ["X-CSRF-Token"] = await client.CsrfAsync() };
        using var deleted = await client.SendAsync(HttpMethod.Delete, $"/api/v1/suite/notification-channels/{channelId}", headers: csrf);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels"));
    }

    [Fact]
    public async Task Operational_history_reset_needs_confirmation_and_keeps_active_work()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        using var wrong = await client.PostAsync("/api/v1/suite/operational-history/reset", new { csrf_token = await client.CsrfAsync(), confirm = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("RESET", await Detail(wrong), StringComparison.Ordinal);

        await TestDatabase.ExecuteAsync(server, "INSERT INTO jobs (dedupe_key, job_kind, status) VALUES ('processing-done', 'processing.file.remux_pass.v1', 'completed'), ('processing-pending', 'processing.file.remux_pass.v1', 'pending')");
        using var reset = await client.PostAsync("/api/v1/suite/operational-history/reset", new { csrf_token = await client.CsrfAsync(), confirm = "RESET" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var body = await Json(reset);
        Assert.Equal("reset", body["status"]!.GetValue<string>());
        Assert.True(body["activity_events_deleted"]!.GetValue<int>() >= 1);
        Assert.Equal(1, body["jobs_deleted"]!.GetValue<int>());
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE dedupe_key = 'processing-pending'"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE dedupe_key = 'processing-done'"));
    }

    [Fact]
    public async Task Cors_preflight_allows_weir_methods_and_headers_only()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", "http://localhost:5173"));
        var client = new ApiTestClient(server);
        Task<HttpResponseMessage> Preflight(string method, string? headers)
        {
            var values = new Dictionary<string, string> { ["Origin"] = "http://localhost:5173", ["Access-Control-Request-Method"] = method };
            if (headers is not null)
            {
                values["Access-Control-Request-Headers"] = headers;
            }

            return client.SendAsync(HttpMethod.Options, "/api/v1/auth/csrf", headers: values);
        }

        using var ok = await Preflight("POST", "Content-Type, X-CSRF-Token");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("OPTIONS", Header(ok, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.Contains("X-CSRF-Token", Header(ok, "Access-Control-Allow-Headers"), StringComparison.Ordinal);
        using var trace = await Preflight("TRACE", null);
        Assert.Equal(HttpStatusCode.BadRequest, trace.StatusCode);
        Assert.DoesNotContain("TRACE", Header(trace, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preflight("POST", "X-Injected-Header")).StatusCode);
    }

    [Fact]
    public async Task Head_mirrors_get_and_post_only_routes_answer_405_with_allow()
    {
        await using var server = await StartServerAsync();
        var client = new ApiTestClient(server);
        using var get = await client.GetAsync("/health");
        using var head = await client.SendAsync(HttpMethod.Head, "/health");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Content.Headers.ContentType?.ToString(), head.Content.Headers.ContentType?.ToString());

        using var headLogin = await client.SendAsync(HttpMethod.Head, "/api/v1/auth/login");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, headLogin.StatusCode);
        Assert.Equal("POST", Header(headLogin, "Allow"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Head, "/api/v1/nothing-here")).StatusCode);
    }

    /// <summary>
    /// #548. The versions themselves depend on what is installed on the machine running the tests (CI runners
    /// have ffmpeg; mkvmerge is only there on the packaging job), so this asserts the contract the web app and
    /// an operator rely on rather than any particular string: operator-only, always 200, and both tools always
    /// present as a non-empty string — "not installed" for an absent tool, never a missing key and never a 500.
    /// <c>Weir.Infrastructure.Tests.Media.MediaToolVersionReportTests</c> pins the actual wording against a
    /// scripted runner, where it can be deterministic.
    /// </summary>
    [Fact]
    public async Task Media_tools_are_operator_only_and_always_report_both_tools()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/system/media-tools")).StatusCode);

        using var response = await client.GetAsync("/api/v1/system/media-tools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await Json(response)).AsObject();
        Assert.False(string.IsNullOrWhiteSpace(body["ffmpeg"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(body["mkvmerge"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Install_level_routes_refuse_an_operator_and_allow_only_admin()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        await TestDatabase.SeedUserAsync(server, "opal", "operator-password-here", "operator");
        var operatorClient = new ApiTestClient(server);
        await operatorClient.SignInAsync("opal", "operator-password-here");

        foreach (var path in new[] { "/api/v1/system/directories", "/api/v1/suite/configuration-bundle" })
        {
            using var response = await operatorClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var path in new[] { "/api/v1/suite/check-update", "/api/v1/suite/download-update", "/api/v1/suite/apply-update" })
        {
            using var updateStep = await operatorClient.PostAsync(path, new { csrf_token = await operatorClient.CsrfAsync() });
            Assert.Equal(HttpStatusCode.Forbidden, updateStep.StatusCode);
        }

        using var updateSettings = await operatorClient.PutAsync(
            "/api/v1/suite/update-settings",
            new { csrf_token = await operatorClient.CsrfAsync(), mode = "notify", check_on_startup = true });
        Assert.Equal(HttpStatusCode.Forbidden, updateSettings.StatusCode);

        using var historyReset = await operatorClient.PostAsync(
            "/api/v1/suite/operational-history/reset", new { csrf_token = await operatorClient.CsrfAsync(), confirm = "RESET" });
        Assert.Equal(HttpStatusCode.Forbidden, historyReset.StatusCode);

        using var createChannel = await operatorClient.PostAsync(
            "/api/v1/suite/notification-channels",
            new { csrf_token = await operatorClient.CsrfAsync(), label = "Ops", provider = "webhook", url = "https://hooks.example.com/weir", events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Forbidden, createChannel.StatusCode);

        using var createConnection = await operatorClient.PostAsync(
            "/api/v1/media-managers/connections",
            new { csrf_token = await operatorClient.CsrfAsync(), kind = "sonarr", base_url = "https://sonarr.example", api_key = "key" });
        Assert.Equal(HttpStatusCode.Forbidden, createConnection.StatusCode);

        using var adminStillWorks = await admin.GetAsync("/api/v1/system/directories");
        Assert.Equal(HttpStatusCode.OK, adminStillWorks.StatusCode);
    }

    private sealed class FakeReleaseCatalog : IReleaseCatalogClient
    {
        public bool NotFound { get; set; }

        public Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken)
        {
            if (NotFound)
            {
                throw new ReleaseFetchException(404);
            }

            return Task.FromResult<GitHubReleaseRecord?>(new GitHubReleaseRecord(
                "v1.2.3",
                "1.2.3",
                "Weir 1.2.3",
                "https://example.com/release",
                Timestamp.FromUtc(new DateTime(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc)),
                false,
                false,
                [new GitHubReleaseAsset("Weir-win-Setup.exe", $"{ProjectLocation.ApiRepositoryUrl}/releases/assets/123", $"{ProjectLocation.RepositoryUrl}/releases/download/v1.2.3/Weir-win-Setup.exe", 123456789, "application/octet-stream")]));
        }
    }
}
