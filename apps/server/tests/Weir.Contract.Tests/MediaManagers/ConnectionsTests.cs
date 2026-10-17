using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>Media manager connections: create, update, delete, test, retired search lanes, webhook secrets and access.</summary>
[ContractArea("media_managers")]
public sealed class ConnectionsTests(NoWebhookSecretFixture fixture) : IClassFixture<NoWebhookSecretFixture>
{
    private const string Route = ManagerConnections.Route;
    private const string HealthPath = "/api/integrations/external/health";

    private WeirServer Server => fixture.Server;

    /// <summary>A signed-in operator with no media manager connections left over.</summary>
    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await Server.CreateAdminClientAsync();
        await ManagerConnections.ClearAsync(admin);
        return admin;
    }

    [Fact]
    public async Task A_manager_with_no_columns_of_its_own_can_be_added()
    {
        using var admin = await OperatorAsync();

        var created = await ManagerConnections.CreateAsync(admin);

        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var row = created.Fields;
        Assert.Equal("deluno", (string)row["kind"]!);
        Assert.Equal("http://192.0.2.10:5099", (string)row["base_url"]!);
        Assert.Equal("/api/v1/intake/webhook/deluno", (string)row["webhook_url_path"]!);
        Assert.False(row.ContainsKey("lanes"));
    }

    [Fact]
    public async Task Deluno_can_create_its_connection_with_the_body_it_sends()
    {
        using var admin = await OperatorAsync();

        // Deluno sends a name when it wires itself up. Weir accepts it and names the connection itself.
        var response = await admin.PostWithCsrfAsync(Route, new JsonObject
        {
            ["kind"] = "deluno",
            ["name"] = "Deluno",
            ["base_url"] = "http://192.0.2.10:5099",
            ["api_key"] = "deluno_secret_key",
            ["enabled"] = true,
        });

        Assert.True(response.Status == HttpStatusCode.Created, response.ToString());
        Assert.Equal("Deluno on 192.0.2.10", (string)response.Fields["name"]!);
    }

    [Fact]
    public async Task A_connection_is_named_after_its_kind_and_the_host_in_its_address()
    {
        using var admin = await OperatorAsync();

        var onAName = await ManagerConnections.CreateAsync(admin, Body("radarr", "http://nas:7878", name: "Anything"));
        var onAnAddress = await ManagerConnections.CreateAsync(admin, Body("sonarr", "http://10.0.0.51:8989", name: "Anything"));

        Assert.Equal("Radarr on nas", (string)onAName.Fields["name"]!);
        Assert.Equal("Sonarr on 10.0.0.51", (string)onAnAddress.Fields["name"]!);
    }

    [Fact]
    public async Task Connections_of_one_kind_on_one_host_are_told_apart_by_port()
    {
        using var admin = await OperatorAsync();
        await ManagerConnections.CreateAsync(admin, Body("radarr", "http://nas:7878"));
        await ManagerConnections.CreateAsync(admin, Body("radarr", "http://nas:7879"));

        var listed = await admin.GetAsync(Route);

        Assert.Equal(["Radarr on nas (7878)", "Radarr on nas (7879)"], listed.Elements.Select(row => (string)row!["name"]!));
    }

    [Fact]
    public async Task The_api_key_is_never_returned_only_whether_it_is_saved()
    {
        using var admin = await OperatorAsync();

        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        Assert.True((bool)row["api_key_is_saved"]!);
        Assert.False(row.ContainsKey("api_key"));
        Assert.DoesNotContain("deluno_secret_key", row.ToJsonString());
    }

    [Fact]
    public async Task Listing_returns_every_configured_manager()
    {
        using var admin = await OperatorAsync();
        await ManagerConnections.CreateAsync(admin, new JsonObject { ["name"] = "Deluno", ["kind"] = "deluno" });
        await ManagerConnections.CreateAsync(admin, Body("radarr", "http://192.0.2.20:7878", name: "Radarr"));

        var listed = await admin.GetAsync(Route);

        Assert.Equal(new HashSet<string> { "deluno", "radarr" }, listed.Elements.Select(row => (string)row!["kind"]!).ToHashSet());
    }

    [Fact]
    public async Task An_unknown_kind_is_refused()
    {
        using var admin = await OperatorAsync();

        var refused = await ManagerConnections.CreateAsync(admin, new JsonObject { ["kind"] = "plex" });

        Assert.Contains((int)refused.Status, new[] { 400, 422 });
    }

    [Fact]
    public async Task An_address_that_cannot_work_is_refused()
    {
        using var admin = await OperatorAsync();

        var refused = await ManagerConnections.CreateAsync(admin, new JsonObject { ["base_url"] = "not-a-url" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("will not work", (string)refused.Fields["detail"]!);
    }

    [Fact]
    public async Task Omitting_the_api_key_on_update_leaves_it_alone()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var updated = (await admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(row)}", new JsonObject { ["enabled"] = false })).Fields;

        Assert.False((bool)updated["enabled"]!);
        Assert.True((bool)updated["api_key_is_saved"]!);
    }

    [Fact]
    public async Task A_name_sent_on_update_is_ignored()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var updated = await admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(row)}", new JsonObject { ["name"] = "Deluno renamed" });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        Assert.Equal("Deluno on 192.0.2.10", (string)updated.Fields["name"]!);
    }

    [Fact]
    public async Task The_name_deluno_sends_is_never_taken_as_the_nickname()
    {
        using var admin = await OperatorAsync();

        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        JsonFields.AssertNull(row, "nickname");
    }

    [Fact]
    public async Task A_connection_can_carry_a_trimmed_nickname_beside_its_derived_name()
    {
        using var admin = await OperatorAsync();

        var created = await ManagerConnections.CreateAsync(admin, new JsonObject { ["nickname"] = "  4K  " });

        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        Assert.Equal("Deluno on 192.0.2.10", (string)created.Fields["name"]!);
        Assert.Equal("4K", (string)created.Fields["nickname"]!);
    }

    [Fact]
    public async Task A_nickname_can_be_changed_kept_and_cleared_on_update()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin, new JsonObject { ["nickname"] = "4K" })).Fields;
        var url = $"{Route}/{JsonFields.Id(row)}";

        var changed = (await admin.PutWithCsrfAsync(url, new JsonObject { ["nickname"] = "Kids" })).Fields;
        var kept = (await admin.PutWithCsrfAsync(url, new JsonObject { ["enabled"] = false })).Fields;
        var cleared = (await admin.PutWithCsrfAsync(url, new JsonObject { ["nickname"] = string.Empty })).Fields;

        Assert.Equal("Kids", (string)changed["nickname"]!);
        Assert.Equal("Kids", (string)kept["nickname"]!);
        JsonFields.AssertNull(cleared, "nickname");
    }

    [Fact]
    public async Task A_nickname_over_thirty_characters_is_refused()
    {
        using var admin = await OperatorAsync();

        var refused = await ManagerConnections.CreateAsync(admin, new JsonObject { ["nickname"] = new string('x', 31) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
    }

    [Fact]
    public async Task Moving_a_connection_to_another_host_renames_it()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var updated = (await admin.PutWithCsrfAsync(
            $"{Route}/{JsonFields.Id(row)}", new JsonObject { ["base_url"] = "http://192.0.2.11:5099" })).Fields;

        Assert.Equal("Deluno on 192.0.2.11", (string)updated["name"]!);
    }

    [Fact]
    public async Task An_empty_api_key_on_update_clears_it()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var updated = (await admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(row)}", new JsonObject { ["api_key"] = string.Empty })).Fields;

        Assert.False((bool)updated["api_key_is_saved"]!);
    }

    [Fact]
    public async Task A_connection_can_be_deleted()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var deleted = await ManagerConnections.DeleteAsync(admin, JsonFields.Id(row));

        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        Assert.Empty((await admin.GetAsync(Route)).Elements);
    }

    [Fact]
    public async Task An_unreachable_connection_is_a_normal_test_result()
    {
        using var admin = await OperatorAsync();
        // The address really has nothing listening.
        var row = (await ManagerConnections.CreateAsync(admin, new JsonObject { ["base_url"] = ManagerConnections.ClosedPortUrl() })).Fields;

        var response = await admin.PostWithCsrfAsync($"{Route}/{JsonFields.Id(row)}/test");

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.False((bool)response.Fields["ok"]!);
        Assert.Contains("could not reach", (string)response.Fields["detail"]!);
    }

    [Fact]
    public async Task Removing_a_connection_during_its_test_returns_a_clean_not_found()
    {
        using var admin = await OperatorAsync();
        using var fake = FakeManager.StartDeluno();
        var row = (await ManagerConnections.CreateAsync(admin, new JsonObject { ["base_url"] = fake.BaseUrl, ["api_key"] = fake.ApiKey })).Fields;

        // A second operator session removes the connection while Weir is waiting for the manager.
        using var other = Server.CreateClient();
        await other.LoginAsync();
        HttpStatusCode? removedDuringProbe = null;
        fake.Route("GET", HealthPath, _ =>
        {
            removedDuringProbe = ManagerConnections.DeleteAsync(other, JsonFields.Id(row)).GetAwaiter().GetResult().Status;
            return new Reply(200, new JsonObject { ["status"] = "ok" });
        });
        var response = await admin.PostWithCsrfAsync($"{Route}/{JsonFields.Id(row)}/test");

        Assert.NotEmpty(fake.RequestsTo("GET", HealthPath));
        Assert.Equal(HttpStatusCode.NoContent, removedDuringProbe);
        Assert.True(response.Status == HttpStatusCode.NotFound, response.ToString());
        Assert.Contains("removed while its connection test was running", (string)response.Fields["detail"]!);
    }

    /// <summary>
    /// Searching for missing files and upgrades is the media manager's own, so a connection has no search lanes. An older client
    /// that still saves one is answered with the connection as it is, and nothing it sends is kept or checked.
    /// </summary>
    [Fact]
    public async Task An_older_client_saving_a_lane_is_answered_with_the_connection_and_nothing_is_kept()
    {
        using var admin = await OperatorAsync();
        // Switched off, so the minute heartbeat leaves it alone: its answer would change the row the test compares.
        var row = (await ManagerConnections.CreateAsync(admin, new JsonObject { ["enabled"] = false })).Fields;
        var lane = $"{Route}/{JsonFields.Id(row)}/lanes/missing";

        var saved = await admin.PutWithCsrfAsync(lane, OldLaneBody(days: "Mon,Tue", start: "01:00", end: "05:00"));

        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        Assert.Equal(JsonFields.Id(row), JsonFields.Id(saved.Fields));
        Assert.False(saved.Fields.ContainsKey("lanes"));
        Assert.False(saved.Fields.ContainsKey("lane"));
        Assert.False(saved.Fields.ContainsKey("schedule_days"));

        // A day that is not a day and an hour past 23 were once refused. The values are not read now.
        var unreadable = await admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(row)}/lanes/upgrade", OldLaneBody(days: "monday", start: "25:00", end: "9"));
        Assert.True(unreadable.Status == HttpStatusCode.OK, unreadable.ToString());

        var read = await admin.GetAsync($"{Route}/{JsonFields.Id(row)}");
        Assert.Equal(row.ToJsonString(), read.Fields.ToJsonString());
    }

    [Fact]
    public async Task The_retired_lane_route_still_checks_who_is_asking_which_connection_and_which_lane()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;

        var noConnection = await admin.PutWithCsrfAsync($"{Route}/99999/lanes/missing", OldLaneBody());
        var noLane = await admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(row)}/lanes/sideways", OldLaneBody());
        using var probe = Server.CreateClient();
        var anonymous = await probe.RequestAsync(HttpMethod.Put, $"{Route}/{JsonFields.Id(row)}/lanes/missing", OldLaneBody());

        Assert.Equal(HttpStatusCode.NotFound, noConnection.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noLane.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    private static JsonObject OldLaneBody(string days = "Mon", string start = "01:00", string end = "05:00") => new()
    {
        ["enabled"] = true,
        ["max_items_per_run"] = 25,
        ["retry_delay_minutes"] = 60,
        ["schedule_enabled"] = true,
        ["schedule_days"] = days,
        ["schedule_start"] = start,
        ["schedule_end"] = end,
        ["schedule_interval_seconds"] = 900,
    };

    [Fact]
    public async Task Routes_require_an_operator_session()
    {
        using (var probe = Server.CreateClient())
        {
            var attempt = await probe.PostAsync($"{WeirClient.Api}/auth/login", new JsonObject
            {
                ["username"] = SeededAccounts.ViewerUsername,
                ["password"] = SeededAccounts.ViewerPassword,
                ["csrf_token"] = await probe.CsrfTokenAsync(),
            });
            if (attempt.Status != HttpStatusCode.OK)
            {
                await using var database = await Server.StopForDatabaseAsync();
                SeededAccounts.EnsureViewer(database.Connection);
            }
        }

        using var viewer = Server.CreateClient();
        await viewer.LoginAsync(SeededAccounts.ViewerUsername, SeededAccounts.ViewerPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Route)).Status);
    }

    // --- the per-connection inbound secret ---------------------------------------

    [Fact]
    public async Task A_generated_secret_is_shown_once_and_then_only_reported_as_set()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;
        Assert.False((bool)row["webhook_secret_is_set"]!);

        var generated = await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(row));
        var secret = (string)generated["webhook_secret"]!;

        Assert.NotEmpty(secret);
        Assert.Equal("/api/v1/intake/webhook/deluno", (string)generated["webhook_url_path"]!);
        Assert.Equal("X-Webhook-Secret", (string)generated["header_name"]!);
        var fetched = (await admin.GetAsync($"{Route}/{JsonFields.Id(row)}")).Fields;
        Assert.True((bool)fetched["webhook_secret_is_set"]!);
        Assert.DoesNotContain(secret, fetched.ToJsonString());
    }

    [Fact]
    public async Task The_intake_webhook_enforces_that_managers_own_secret()
    {
        using var admin = await OperatorAsync();
        var row = (await ManagerConnections.CreateAsync(admin)).Fields;
        var secret = (string)(await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(row)))["webhook_secret"]!;
        var webhook = $"{WeirClient.Api}/intake/webhook/deluno";
        var body = new JsonObject
        {
            ["eventType"] = "deluno.processor-handoff",
            ["mediaType"] = "movies",
            ["sourcePath"] = "/x/y.mkv",
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsync(webhook, body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsync(webhook, body, Secret("wrong"))).Status);
        // The right secret gets past authorisation; the hand-off then fails on its own merits,
        // because no Processing watched folder is configured on this server.
        var accepted = await admin.PostAsync(webhook, body, Secret(secret));
        Assert.Equal(HttpStatusCode.BadRequest, accepted.Status);
        Assert.Contains("watched folder", (string)accepted.Fields["detail"]!);
    }

    [Fact]
    public async Task One_managers_secret_does_not_gate_another()
    {
        // The point of per-connection secrets: revoking one must not lock out the rest.
        using var admin = await OperatorAsync();
        var deluno = (await ManagerConnections.CreateAsync(admin, new JsonObject { ["name"] = "Deluno", ["kind"] = "deluno" })).Fields;
        await ManagerConnections.CreateAsync(admin, Body("radarr", "http://192.0.2.20:7878", name: "Radarr"));
        await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(deluno));

        var grab = new JsonObject { ["eventType"] = "Grab" };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{WeirClient.Api}/intake/webhook/radarr", grab)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsync($"{WeirClient.Api}/intake/webhook/deluno", grab)).Status);
    }

    private static JsonObject Body(string kind, string baseUrl, string? name = null)
    {
        var body = new JsonObject { ["kind"] = kind, ["base_url"] = baseUrl };
        if (name is not null)
        {
            body["name"] = name;
        }

        return body;
    }

    private static Dictionary<string, string> Secret(string value) => new() { ["X-Webhook-Secret"] = value };
}
