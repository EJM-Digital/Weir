using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core;
using Weir.Core.Workers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests;

public sealed class SystemEndpointsTests
{
    [Fact]
    public async Task Health_is_ok_with_the_documented_body_and_headers()
    {
        await using var server = await WeirTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"dependencies\":{\"database\":\"ok\"}}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'", Header(response, "Content-Security-Policy"));
        Assert.Matches("^[0-9a-f]{32}$", Header(response, "X-Request-ID"));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Health_answers_head_and_refuses_other_methods_with_json_detail_bodies()
    {
        await using var server = await WeirTestServer.StartAsync();

        using var head = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        using var post = await server.Client.PostAsync("/health", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal("{\"detail\":\"Method Not Allowed\"}", await post.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_id_is_echoed_and_hsts_is_opt_in()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SECURITY_ENABLE_HSTS", "true")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Request-ID", "trace-me");

        using var response = await server.Client.SendAsync(request);

        Assert.Equal("trace-me", Header(response, "X-Request-ID"));
        Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Health_is_unhealthy_when_the_database_cannot_be_reached()
    {
        // No workers, so only the periodic tasks hold the file open, and only briefly.
        await using var server = await WeirTestServer.StartAsync([("WEIR_PROCESSING_WORKER_COUNT", "0")]);
        await server.BreakDatabaseAsync();

        using var response = await server.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"unhealthy\",\"dependencies\":{\"database\":\"failed\"}}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_requires_a_signed_in_operator()
    {
        await using var server = await WeirTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/api/v1/system/readiness");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("{\"detail\":\"Not authenticated.\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Readiness_reports_a_pre_release_version_exactly()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_VERSION", "1.0.0-rc.1")], signedIn: true);

        using var response = await server.Client.GetAsync("/api/v1/system/readiness");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1.0.0-rc.1", body.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Readiness_reports_running_workers()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_VERSION", "7.8.9")], signedIn: true);

        // The worker slots report their first heartbeat as soon as they start (#521).
        var responseText = await WaitForReadyAsync(server, "/api/v1/system/readiness");

        using var body = JsonDocument.Parse(responseText);
        var root = body.RootElement;
        Assert.Equal(
            ["ready", "version", "machine_name", "machine_name_looks_generated", "status", "startup_seconds", "steps", "worker_health"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.Equal("7.8.9", root.GetProperty("version").GetString());
        Assert.Equal(Environment.MachineName, root.GetProperty("machine_name").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("startup_seconds").GetDouble() >= 0);
        Assert.Equal(
            "[{\"name\":\"database\",\"status\":\"ready\",\"detail\":\"Local database is connected and migrations are complete.\"}," +
            "{\"name\":\"workers\",\"status\":\"ready\",\"detail\":\"Background workers and schedules are ready.\"}," +
            "{\"name\":\"filesystem_watcher\",\"status\":\"ready\",\"detail\":\"No workflows are being watched for filesystem events.\"}]",
            root.GetProperty("steps").GetRawText());
        Assert.Equal(
            "[{\"module\":\"processing\",\"expected_workers\":10,\"active_workers\":10,\"stale_workers\":0,\"stopped_workers\":0,\"status\":\"healthy\"," +
            "\"detail\":\"Weir worker heartbeats are current.\"}," +
            "{\"module\":\"upkeep\",\"expected_workers\":2,\"active_workers\":2,\"stale_workers\":0,\"stopped_workers\":0,\"status\":\"healthy\"," +
            "\"detail\":\"Upkeep worker heartbeats are current.\"}]",
            root.GetProperty("worker_health").GetRawText());
    }

    [Fact]
    public async Task Readiness_reports_worker_slots_as_stopped_after_shutdown()
    {
        var server = await WeirTestServer.StartAsync(signedIn: true);
        WorkerHeartbeats heartbeats;
        await using (server)
        {
            await WaitForReadyAsync(server, "/api/v1/system/readiness");
            heartbeats = server.Services.GetRequiredService<WorkerHeartbeats>();
        }

        var lane = Assert.Single(heartbeats.Snapshot([new KeyValuePair<string, int>("processing", 8)]));
        Assert.Equal(("degraded", 0, 8), (lane.Status, lane.ActiveWorkers, lane.StoppedWorkers));
    }

    [Fact]
    public async Task Readiness_is_ready_when_workers_are_turned_off()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_PROCESSING_WORKER_COUNT", "0")], signedIn: true);
        await server.WaitForStartupRecoveryAsync();

        using var detailed = await server.Client.GetAsync("/api/v1/system/readiness");
        using var brief = await server.Client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, detailed.StatusCode);
        using var body = JsonDocument.Parse(await detailed.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(WeirVersion.BuildVersion, body.RootElement.GetProperty("version").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("worker_health")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, brief.StatusCode);
        Assert.Equal("{\"ready\":true,\"status\":\"ready\"}", await brief.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Public_ready_is_200_once_workers_are_running()
    {
        await using var server = await WeirTestServer.StartAsync();

        Assert.Equal("{\"ready\":true,\"status\":\"ready\"}", await WaitForReadyAsync(server, "/ready"));
    }

    /// <summary>Poll until the endpoint answers 200, and return its body.</summary>
    private static async Task<string> WaitForReadyAsync(WeirTestServer server, string path)
    {
        var (status, text) = await Eventually.PollAsync(
            async () =>
            {
                using var response = await server.Client.GetAsync(path);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            },
            result => result.StatusCode == HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, status);
        return text;
    }

    [Fact]
    public async Task Startup_creates_the_database_and_the_log_file()
    {
        await using var server = await WeirTestServer.StartAsync(freshDatabase: true);
        using (await server.Client.GetAsync("/health"))
        {
        }

        var dbPath = Path.Join(server.Home, "data", "weir.sqlite3");
        Assert.True(File.Exists(dbPath));
        Assert.True(Directory.Exists(Path.Join(server.Home, "backups")));
        Assert.True(Directory.Exists(Path.Join(server.Home, "temp")));
        using var stream = new FileStream(Path.Join(server.Home, "logs", "weir.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var log = await reader.ReadToEndAsync();
        Assert.Contains($"\"message\": \"Created database schema revision={Weir.Infrastructure.Sqlite.SchemaMigrator.HeadRevision}", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_refuses_a_database_from_an_unknown_release()
    {
        var error = await Assert.ThrowsAsync<DatabaseSchemaMismatchException>(() => WeirTestServer.StartAsync(prepareHome: home =>
        {
            var dbPath = Path.Join(home, "data", "weir.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE alembic_version (version_num VARCHAR(32) NOT NULL); INSERT INTO alembic_version VALUES ('0100_future');";
            command.ExecuteNonQuery();
        }));

        Assert.Equal(SchemaMismatchKind.UnknownRevision, error.Kind);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;
}
