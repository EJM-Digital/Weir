using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.SystemArea.SystemPartBJson;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// Suite settings (/suite/settings): access, defaults, validation, the setup wizard state, log retention and the
/// configuration backup tick.
/// </summary>
[ContractArea("system")]
public sealed class SuiteSettingsApiTests(UsersFixture fixture) : IClassFixture<UsersFixture>
{
    private const string Settings = SystemPartBHelpers.Api + "/suite/settings";
    private const string Backups = SystemPartBHelpers.Api + "/suite/configuration-backups";

    private WeirServer Server => fixture.Server;

    private static JsonObject RetentionBody(string timezone = "UTC") => new()
    {
        ["app_timezone"] = timezone,
        ["log_retention_days"] = 30,
    };

    [Fact]
    public async Task Suite_settings_get_requires_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Settings)).Status);
    }

    [Fact]
    public async Task Suite_settings_get_ok_for_viewer()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        var response = await viewer.GetAsync(Settings);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.False(response.Fields.ContainsKey("product_display_name"));
    }

    [Fact]
    public async Task Suite_settings_get_default_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Settings);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        // Weir is named after the machine it runs on, so there is no name setting to read.
        Assert.False(body.ContainsKey("product_display_name"));
        Assert.False(body.ContainsKey("signed_in_home_notice"));
        // This module's install gained its users by seeding after first start, so the wizard stayed at its first-run value.
        Assert.Equal("pending", (string?)body["setup_wizard_state"]);
        Assert.Contains((string?)body["app_timezone"], new[] { "UTC", "America/New_York" });
        Assert.Equal(30, (int)body["log_retention_days"]!);
        Assert.False((bool)body["configuration_backup_enabled"]!);
        Assert.Equal(24, (int)body["configuration_backup_interval_hours"]!);
        Assert.Equal("02:00", (string?)body["configuration_backup_preferred_time"]);
        AssertNull(body, "configuration_backup_last_run_at");
        Assert.True(body.ContainsKey("updated_at"));
    }

    /// <summary>An install that already has users and loses its settings row does not reopen first-run setup.</summary>
    [Fact]
    public async Task Ensure_suite_settings_row_defaults_to_skipped_for_existing_install()
    {
        await using var server = await WeirServer.StartNewAsync();
        await using (var database = await server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, "DELETE FROM suite_settings");
            SeedSql.InsertUser(
                database.Connection, SystemPartBHelpers.ExistingAdminUsername, SystemPartBHelpers.ExistingAdminPasswordHash, "admin");
        }

        using var client = server.CreateClient();
        await client.LoginAsync(SystemPartBHelpers.ExistingAdminUsername, SystemPartBHelpers.ExistingAdminPassword);
        var response = await client.GetAsync(Settings);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("skipped", (string?)response.Fields["setup_wizard_state"]);
    }

    [Fact]
    public async Task Bootstrap_explicitly_keeps_setup_wizard_pending_for_true_first_run()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var client = server.CreateClient();

        var response = await client.BootstrapAsync("fresh-admin", "bootstrap-password-strong");
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        await client.LoginAsync("fresh-admin", "bootstrap-password-strong");

        var settings = await client.GetAsync(Settings);
        Assert.True(settings.Status == HttpStatusCode.OK, settings.ToString());
        Assert.Equal("pending", (string?)settings.Fields["setup_wizard_state"]);
    }

    [Fact]
    public async Task Suite_security_overview_get_ok()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync($"{SystemPartBHelpers.Api}/suite/security-overview");

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        Assert.True(body.ContainsKey("restart_required_note"));
        Assert.True(body.ContainsKey("session_signing_configured"));
        Assert.True(body.ContainsKey("allowed_browser_origins_count"));
        Assert.True(body.ContainsKey("standard_session_idle_timeout_plain"));
        Assert.True(body.ContainsKey("trusted_session_absolute_timeout_plain"));
    }

    [Fact]
    public async Task Suite_settings_put_persists()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        var response = await admin.PutWithCsrfAsync(
            Settings,
            new JsonObject
            {
                ["product_display_name"] = "House Library", // sent by an older client: accepted, not stored
                ["setup_wizard_state"] = "skipped",
                ["app_timezone"] = "UTC",
                ["log_retention_days"] = 45,
                ["configuration_backup_enabled"] = true,
                ["configuration_backup_interval_hours"] = 12,
                ["configuration_backup_preferred_time"] = "03:30",
            });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var saved = response.Fields;
        Assert.False(saved.ContainsKey("product_display_name"));
        Assert.Equal("skipped", (string?)saved["setup_wizard_state"]);
        Assert.Equal(45, (int)saved["log_retention_days"]!);
        Assert.True((bool)saved["configuration_backup_enabled"]!);
        Assert.Equal(12, (int)saved["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string?)saved["configuration_backup_preferred_time"]);

        var read = await admin.GetAsync(Settings);
        Assert.Equal(HttpStatusCode.OK, read.Status);

        await using var database = await server.StopForDatabaseAsync();
        var rows = SeedSql.Rows(database.Connection, "SELECT * FROM suite_settings WHERE id = 1");
        var row = Assert.Single(rows);
        Assert.Equal("Weir", row["product_display_name"]);
        Assert.Equal("skipped", row["setup_wizard_state"]);
        Assert.Equal(45L, Convert.ToInt64(row["log_retention_days"], CultureInfo.InvariantCulture));
        Assert.True(Convert.ToInt64(row["configuration_backup_enabled"], CultureInfo.InvariantCulture) != 0);
        Assert.Equal(12L, Convert.ToInt64(row["configuration_backup_interval_hours"], CultureInfo.InvariantCulture));
        Assert.Equal("03:30", row["configuration_backup_preferred_time"]);
    }

    [Fact]
    public async Task Suite_settings_put_viewer_forbidden()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        var response = await viewer.PutWithCsrfAsync(Settings, RetentionBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    [Fact]
    public async Task Apply_suite_settings_put_rejects_invalid_timezone()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PutWithCsrfAsync(Settings, RetentionBody("Not/A_Real_Zone"));

        Assert.True(response.Status == HttpStatusCode.BadRequest, response.ToString());
        Assert.Contains("timezone", ((string)response.Fields["detail"]!).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_retention_does_not_prune_activity_history()
    {
        await using var server = await WeirServer.StartNewAsync();
        using (var first = await server.CreateAdminClientAsync())
        {
            var saved = await first.PutWithCsrfAsync(Settings, RetentionBody());
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        }

        await using (var database = await server.StopForDatabaseAsync())
        {
            SeedSql.Execute(
                database.Connection,
                "INSERT INTO activity_events (event_type, module, title, detail, created_at) VALUES ($type, $module, $title, $detail, $at)",
                ("$type", "auth.login_succeeded"),
                ("$module", "auth"),
                ("$title", "old"),
                ("$detail", "alice"),
                ("$at", SeedSql.UtcText(DateTime.UtcNow.AddDays(-40))));
        }

        using (var admin = server.CreateClient())
        {
            await admin.LoginAsync(WeirClient.AdminUsername, WeirClient.AdminPassword);
            var saved = await admin.PutWithCsrfAsync(Settings, RetentionBody());
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        }

        await using var after = await server.StopForDatabaseAsync();
        var old = SeedSql.Scalar(after.Connection, "SELECT COUNT(*) FROM activity_events WHERE title = 'old'");
        Assert.Equal(1, Convert.ToInt64(old ?? 0L, CultureInfo.InvariantCulture));
    }

    /// <summary>No page shows a note for the signed-in home page, so there is no setting for one. An older client that still sends it is answered as though it had not.</summary>
    [Fact]
    public async Task An_older_client_sending_the_home_notice_is_answered_and_nothing_is_kept()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        foreach (JsonNode? notice in new JsonNode?[] { "Welcome back.", null, 7 })
        {
            var body = RetentionBody();
            body["signed_in_home_notice"] = notice;
            var response = await admin.PutWithCsrfAsync(Settings, body);
            Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
            Assert.False(response.Fields.ContainsKey("signed_in_home_notice"));
        }

        var read = await admin.GetAsync(Settings);
        Assert.False(read.Fields.ContainsKey("signed_in_home_notice"));
        await using var database = await server.StopForDatabaseAsync();
        Assert.DoesNotContain("signed_in_home_notice", SeedSql.Rows(database.Connection, "SELECT * FROM suite_settings WHERE id = 1").Single().Keys);
    }

    /// <summary>With automatic backups on and no backup yet, the running server writes one.</summary>
    [Fact]
    public async Task Suite_configuration_backup_tick_creates_snapshot()
    {
        await using var server = await WeirServer.StartNewAsync();
        int rowsBefore;
        using (var admin = await server.CreateAdminClientAsync())
        {
            var saved = await admin.PutWithCsrfAsync(
                Settings,
                new JsonObject
                {
                    ["setup_wizard_state"] = "completed",
                    ["app_timezone"] = "UTC",
                    ["log_retention_days"] = 30,
                    ["configuration_backup_enabled"] = true,
                    ["configuration_backup_interval_hours"] = 6,
                    ["configuration_backup_preferred_time"] = "04:15",
                });
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
            var before = await admin.GetAsync(Backups);
            Assert.True(before.Status == HttpStatusCode.OK, before.ToString());
            rowsBefore = before.Fields["items"]!.AsArray().Count;
        }

        // The backup schedule checks once a minute; a restart makes it check straight away.
        await server.RestartAsync();
        using var signedIn = server.CreateClient();
        await signedIn.LoginAsync(WeirClient.AdminUsername, WeirClient.AdminPassword);

        var listing = await Poll.UntilAsync(
            async () =>
            {
                var response = await signedIn.GetAsync(Backups);
                Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
                return response.Fields["items"]!.AsArray().Count >= rowsBefore + 1 ? response.Fields : null;
            },
            "an automatic configuration backup",
            TimeSpan.FromSeconds(90));

        Assert.True((double)listing["items"]![0]!["size_bytes"]! > 0);
        AssertTruthy(listing["directory"], "directory");
        var suite = (await signedIn.GetAsync(Settings)).Fields;
        Assert.True(suite["configuration_backup_last_run_at"] is not null);
        Assert.Equal("04:15", (string?)suite["configuration_backup_preferred_time"]);
    }
}
