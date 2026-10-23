using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Core.Json;
using Weir.Infrastructure.Runtime;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

public sealed class NetworkAccessEndpointTests
{
    private const string Path = "/api/v1/suite/network-access";

    private static NetworkAccessStatus Status(
        NetworkAccessState state,
        NetworkScope? scope,
        NetworkScope? pending = null,
        FirewallVerdict firewall = FirewallVerdict.NotChecked) =>
        new(state, scope, pending, firewall, 9347, ["http://10.0.0.196:9347"]);

    private static string Summary(NetworkAccessStatus status) =>
        Assert.IsType<WireString>(NetworkAccessStatusWire.From(status, "MEDIA-PC", notChangeableReason: null)["summary"]).Value;

    [Fact]
    public void A_server_for_this_pc_only_says_so()
    {
        Assert.Equal("Only this PC can reach Weir.", Summary(Status(NetworkAccessState.ThisPcOnly, NetworkScope.ThisPcOnly)));
    }

    [Fact]
    public void A_server_the_firewall_lets_through_says_other_devices_can_reach_it()
    {
        var status = Status(NetworkAccessState.Allowed, NetworkScope.Network, firewall: FirewallVerdict.Allows);

        Assert.Equal("Other devices on your network can reach Weir.", Summary(status));
    }

    [Fact]
    public void A_server_the_firewall_blocks_says_what_to_do_about_it()
    {
        var status = Status(NetworkAccessState.Blocked, NetworkScope.Network, firewall: FirewallVerdict.Blocks);

        Assert.Equal(
            "Windows Firewall is blocking other devices from reaching Weir. Try again to ask Windows to allow it, or limit Weir to this PC.",
            Summary(status));
    }

    [Fact]
    public void A_change_to_the_network_that_needs_the_firewall_waits_for_approval_on_this_pc_by_name()
    {
        var status = Status(NetworkAccessState.ThisPcOnly, NetworkScope.ThisPcOnly, NetworkScope.Network, FirewallVerdict.Blocks);

        Assert.Equal(
            "Waiting for you to approve Windows Firewall on MEDIA-PC. Weir restarts for your network once you do.",
            Summary(status));
    }

    [Fact]
    public void A_change_to_the_network_the_firewall_already_allows_just_restarts()
    {
        var status = Status(NetworkAccessState.ThisPcOnly, NetworkScope.ThisPcOnly, NetworkScope.Network, FirewallVerdict.Allows);

        Assert.Equal("Restarting Weir so other devices on your network can reach it.", Summary(status));
    }

    [Fact]
    public void A_change_back_to_this_pc_only_just_restarts()
    {
        var status = Status(NetworkAccessState.Allowed, NetworkScope.Network, NetworkScope.ThisPcOnly, FirewallVerdict.Allows);

        Assert.Equal("Restarting Weir so only this PC can reach it.", Summary(status));
    }

    [Fact]
    public void A_copy_of_weir_that_does_not_manage_its_exposure_says_what_does()
    {
        var status = Status(NetworkAccessState.NotApplicable, scope: null);

        var wire = NetworkAccessStatusWire.From(status, "MEDIA-PC", "Set by Docker's port mapping.");

        Assert.Equal("Set by Docker's port mapping.", Assert.IsType<WireString>(wire["summary"]).Value);
    }

    private sealed class FakeNetworkAccess(NetworkAccessStatus status, string? notChangeableReason = null) : INetworkAccess
    {
        public List<NetworkScope> Chosen { get; } = [];

        public string? NotChangeableReason => notChangeableReason;

        public NetworkAccessStatus Read() => status;

        public void Choose(NetworkScope scope) => Chosen.Add(scope);
    }

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync(
        INetworkAccess? access = null, string signInAs = "alice", (string Name, string Value)[]? variables = null)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), .. variables ?? []],
            configureServices: services =>
            {
                if (access is not null)
                {
                    services.AddSingleton(access);
                }
            });
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.SeedViewerAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync(signInAs, signInAs == "alice" ? AdminPassword : ViewerPassword);
        return (server, client);
    }

    private static readonly NetworkAccessStatus ThisPcOnly = Status(NetworkAccessState.ThisPcOnly, NetworkScope.ThisPcOnly);

    [Fact]
    public async Task Reading_the_state_needs_a_signed_in_user()
    {
        await using var server = await WeirTestServer.StartAsync();

        using var response = await server.Client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_state_reads_as_the_documented_json()
    {
        var (server, client) = await StartAsync(new FakeNetworkAccess(
            Status(NetworkAccessState.ThisPcOnly, NetworkScope.ThisPcOnly, NetworkScope.Network, FirewallVerdict.Blocks)));
        await using var _server = server;

        using var response = await client.GetAsync(Path);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("this_pc_only", json["state"]!.GetValue<string>());
        Assert.Equal("this_pc_only", json["scope"]!.GetValue<string>());
        Assert.Equal("network", json["pending_scope"]!.GetValue<string>());
        Assert.Equal("blocked", json["firewall"]!.GetValue<string>());
        Assert.Equal(9347, json["port"]!.GetValue<int>());
        Assert.Equal(["http://10.0.0.196:9347"], json["addresses"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(json["machine_name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_bare_install_reads_as_not_applicable_and_says_to_use_the_bind_option()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        using var response = await client.GetAsync(Path);
        var json = await Json(response);

        Assert.Equal("not_applicable", json["state"]!.GetValue<string>());
        Assert.Null(json["scope"]);
        Assert.Contains("--host", json["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_choosing_the_network_saves_the_choice_and_gets_the_state_back()
    {
        var access = new FakeNetworkAccess(ThisPcOnly);
        var (server, client) = await StartAsync(access);
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([NetworkScope.Network], access.Chosen);
        Assert.Equal("this_pc_only", (await Json(response))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_change_is_written_to_the_activity_log_with_who_made_it()
    {
        var (server, client) = await StartAsync(new FakeNetworkAccess(ThisPcOnly));
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });
        var detail = await TestDatabase.ScalarStringAsync(
            server, "SELECT detail FROM activity_events WHERE event_type = 'system.network_access.changed'");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Other devices on your network can reach Weir. Changed by alice.", detail);
    }

    [Fact]
    public async Task A_viewer_cannot_change_who_can_reach_weir()
    {
        var access = new FakeNetworkAccess(ThisPcOnly);
        var (server, client) = await StartAsync(access, signInAs: "bob");
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(access.Chosen);
    }

    [Fact]
    public async Task A_change_without_a_valid_confirmation_token_is_refused()
    {
        var access = new FakeNetworkAccess(ThisPcOnly);
        var (server, client) = await StartAsync(access);
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = "not-a-token", scope = "network" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(access.Chosen);
    }

    [Fact]
    public async Task A_scope_that_is_not_one_of_the_two_choices_is_refused()
    {
        var access = new FakeNetworkAccess(ThisPcOnly);
        var (server, client) = await StartAsync(access);
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "everyone" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(access.Chosen);
    }

    [Fact]
    public async Task A_copy_that_cannot_change_it_answers_with_the_reason()
    {
        var access = new FakeNetworkAccess(ThisPcOnly, "Set by something else.");
        var (server, client) = await StartAsync(access);
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Set by something else.", await Detail(response));
        Assert.Empty(access.Chosen);
    }

    [Fact]
    public async Task A_bare_install_points_to_the_bind_option()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("--host", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_says_its_port_mapping_decides()
    {
        var (server, client) = await StartAsync(variables: [("WEIR_RUNTIME", "docker")]);
        await using var _server = server;

        using var response = await client.PutAsync(Path, new { csrf_token = await client.CsrfAsync(), scope = "network" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.StartsWith("Set by Docker's port mapping", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_of_weir_that_does_not_manage_its_exposure_and_gives_no_reason_has_an_empty_summary()
    {
        var status = Status(NetworkAccessState.NotApplicable, scope: null);

        var wire = NetworkAccessStatusWire.From(status, "MEDIA-PC", notChangeableReason: null);

        Assert.Equal(string.Empty, Assert.IsType<WireString>(wire["summary"]).Value);
    }
}
