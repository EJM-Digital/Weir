using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.SystemArea.SystemPartBJson;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// Basic contract coverage (auth, status codes, shapes) for the system endpoints: logs, metrics, notification
/// channels, configuration backup, readiness, pause, security overview and update status.
/// </summary>
[ContractArea("system")]
public sealed partial class SuiteEndpointsContractTests(NoInternetUsersFixture fixture) : IClassFixture<NoInternetUsersFixture>
{
    private const string Logs = SystemPartBHelpers.Api + "/suite/logs";
    private const string SuiteMetrics = SystemPartBHelpers.Api + "/suite/metrics";
    private const string Backups = SystemPartBHelpers.Api + "/suite/configuration-backups";
    private const string Readiness = SystemPartBHelpers.Api + "/system/readiness";
    private const string Pause = SystemPartBHelpers.Api + "/pause";
    private const string Security = SystemPartBHelpers.Api + "/suite/security-overview";
    private const string UpdateStatus = SystemPartBHelpers.Api + "/suite/update-status";

    private WeirServer Server => fixture.Server;

    // --- /suite/logs ---------------------------------------------------------------------------

    [Fact]
    public async Task Suite_logs_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Logs)).Status);
    }

    [Fact]
    public async Task Suite_logs_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        foreach (var client in new[] { admin, viewer })
        {
            var response = await client.GetAsync(Logs);
            Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
            var body = response.Fields;
            AssertKeys(["items", "total", "counts"], body);
            Assert.IsType<JsonArray>(body["items"]);
            Assert.True(IsInteger(body["total"]));
            AssertKeys(["error", "warning", "information"], body["counts"]);
            foreach (var item in body["items"]!.AsArray())
            {
                AssertHasKeys(["timestamp", "level", "component", "message", "logger"], item);
            }
        }
    }

    [Fact]
    public async Task Suite_logs_level_filter_only_returns_that_level()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Logs, ("level", "ERROR"), ("limit", 50));

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.All(response.Fields["items"]!.AsArray(), item => Assert.Equal("ERROR", (string?)item!["level"]));
    }

    // --- /suite/metrics ------------------------------------------------------------------------

    [Fact]
    public async Task Suite_metrics_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(SuiteMetrics)).Status);
    }

    [Fact]
    public async Task Suite_metrics_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        foreach (var client in new[] { admin, viewer })
        {
            var response = await client.GetAsync(SuiteMetrics);
            Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
            var body = response.Fields;
            AssertKeys(
                ["uptime_seconds", "total_requests", "average_response_ms", "error_log_count", "status_counts", "busiest_routes"],
                body);
            Assert.True((double)body["uptime_seconds"]! >= 0);
            Assert.True((double)body["total_requests"]! >= 1);
            Assert.IsType<JsonObject>(body["status_counts"]);
            foreach (var route in body["busiest_routes"]!.AsArray())
            {
                AssertKeys(["route", "request_count", "average_response_ms"], route);
            }
        }
    }

    // --- /suite/configuration-backups ----------------------------------------------------------

    [Fact]
    public async Task Configuration_backups_require_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Backups)).Status);
    }

    [Fact]
    public async Task Configuration_backups_forbidden_for_viewer()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Backups)).Status);
    }

    [Fact]
    public async Task Configuration_backups_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Backups);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        AssertKeys(["directory", "password_set", "items"], body);
        Assert.True(IsString(body["directory"]));
        foreach (var item in body["items"]!.AsArray())
        {
            AssertKeys(["id", "created_at", "file_name", "size_bytes", "made_by", "secrets_sealed"], item);
        }
    }

    [Fact]
    public async Task Configuration_backup_download_missing_is_404()
    {
        using var admin = await Server.CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Backups}/999999/download")).Status);
    }

    // --- /ready and /system/readiness ----------------------------------------------------------

    [Fact]
    public async Task Public_ready_needs_no_sign_in()
    {
        using var client = Server.CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        AssertSameJson(Parse("""{"ready": true, "status": "ready"}"""), response.Json);
    }

    [Fact]
    public async Task System_readiness_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Readiness)).Status);
    }

    [Fact]
    public async Task System_readiness_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        foreach (var client in new[] { admin, viewer })
        {
            var response = await client.GetAsync(Readiness);
            Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
            var body = response.Fields;
            AssertKeys(
                [
                    "ready", "version", "machine_name", "machine_name_looks_generated", "status", "startup_seconds",
                    "steps", "worker_health",
                ],
                body);
            Assert.True((bool)body["ready"]!);
            AssertTruthy(body["machine_name"], "machine_name");
            Assert.True(IsBool(body["machine_name_looks_generated"]));
            Assert.Equal("ready", (string?)body["status"]);
            AssertTruthy(body["version"], "version");
            Assert.True((double)body["startup_seconds"]! >= 0);
            var names = new HashSet<string>();
            foreach (var step in body["steps"]!.AsArray())
            {
                AssertKeys(["name", "status", "detail"], step);
                Assert.Equal("ready", (string?)step!["status"]);
                names.Add((string)step["name"]!);
            }

            Assert.True(names.IsSupersetOf(["database", "workers"]));
            foreach (var worker in body["worker_health"]!.AsArray())
            {
                AssertHasKeys(["module", "expected_workers", "active_workers", "status", "detail"], worker);
            }
        }
    }

    // --- /pause --------------------------------------------------------------------------------

    [Fact]
    public async Task Pause_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Pause)).Status);
    }

    [Fact]
    public async Task Pause_readable_by_viewer_but_not_changeable()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        var response = await viewer.GetAsync(Pause);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        AssertKeys(["paused", "paused_until", "scan_while_paused", "reason", "in_flight_policy"], response.Fields);
        var changed = await viewer.PutWithCsrfAsync(Pause, new JsonObject { ["paused"] = true });
        Assert.Equal(HttpStatusCode.Forbidden, changed.Status);
    }

    [Fact]
    public async Task Pause_put_rejects_unknown_fields()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PutWithCsrfAsync(Pause, new JsonObject { ["paused"] = false, ["not_a_field"] = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
    }

    [Fact]
    public async Task Pause_get_reflects_put()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var put = await admin.PutWithCsrfAsync(Pause, new JsonObject { ["paused"] = false, ["scan_while_paused"] = true });

        Assert.True(put.Status == HttpStatusCode.OK, put.ToString());
        AssertSameJson(put.Json, (await admin.GetAsync(Pause)).Json);
    }

    // --- /suite/security-overview --------------------------------------------------------------

    [Fact]
    public async Task Security_overview_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Security)).Status);
    }

    [Fact]
    public async Task Security_overview_shape()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        var response = await viewer.GetAsync(Security);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        AssertKeys(
            [
                "session_signing_configured", "sign_in_cookie_https_mode", "sign_in_cookie_https_plain",
                "sign_in_cookie_same_site", "standard_session_idle_timeout_plain",
                "standard_session_absolute_timeout_plain", "trusted_session_idle_timeout_plain",
                "trusted_session_absolute_timeout_plain", "extra_https_hardening_enabled", "sign_in_attempt_limit",
                "sign_in_attempt_window_plain", "first_time_setup_attempt_limit", "first_time_setup_attempt_window_plain",
                "allowed_browser_origins_count", "restart_required_note",
            ],
            body);
        Assert.True((bool)body["session_signing_configured"]!);
        Assert.True((double)body["sign_in_attempt_limit"]! >= 1);
        Assert.Equal(0, (int)body["allowed_browser_origins_count"]!);
    }

    // --- /suite/update-status ------------------------------------------------------------------

    [Fact]
    public async Task Update_status_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(UpdateStatus)).Status);
    }

    /// <summary>One address per handler: the update check is served at /suite/update-status only.</summary>
    [Fact]
    public async Task The_retired_update_status_alias_is_not_served()
    {
        using var admin = await Server.CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{SystemPartBHelpers.Api}/suite/settings/update-status")).Status);
    }

    /// <summary>With no route to the release feed the check says so instead of failing or guessing.</summary>
    [Fact]
    public async Task Update_status_when_the_release_feed_is_unreachable()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(UpdateStatus);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        AssertHasKeys(["current_version", "install_type", "status", "summary", "in_app_upgrade_supported"], body);
        AssertTruthy(body["current_version"], "current_version");
        Assert.Contains((string?)body["install_type"], new[] { "windows", "docker", "source" });
        Assert.Equal("unavailable", (string?)body["status"]);
        AssertTruthy(body["summary"], "summary");
        Assert.True(!body.ContainsKey("latest_version") || body["latest_version"] is null);
    }
}
