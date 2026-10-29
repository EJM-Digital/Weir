using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Auth.AuthSupport;

namespace Weir.Contract.Tests.Auth;

/// <summary>Bootstrap: creating the first admin, and every way a bootstrap is refused.</summary>
[ContractArea("auth")]
public sealed class AuthBootstrapTests(ServerFixture fixture, AuthBootstrapTests.NoAccountServerFixture noAccount)
    : AliceTestBase(fixture), IClassFixture<ServerFixture>, IClassFixture<AuthBootstrapTests.NoAccountServerFixture>
{
    private const string Api = WeirClient.Api;

    [Fact]
    public async Task Bootstrap_returns_a_working_session()
    {
        await using var server = await WeirServer.StartNewAsync();
        var session = NewSession(server);
        AssertStatus(HttpStatusCode.OK, await session.BootstrapAsync());

        var me = await session.GetAsync($"{Api}/auth/me");
        AssertStatus(HttpStatusCode.OK, me);
        Assert.Equal(WeirClient.AdminUsername, (string)me.Fields["user"]!["username"]!);

        var current = await session.GetAsync($"{Api}/auth/session");
        AssertStatus(HttpStatusCode.OK, current);
        Assert.True((bool)current.Fields["current"]!);
    }

    [Fact]
    public async Task Bootstrap_allowed_when_no_admin()
    {
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string>
        {
            ["WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS"] = "100",
            ["WEIR_BOOTSTRAP_RATE_WINDOW_SECONDS"] = "60",
        });
        var session = NewSession(server);
        var status = await session.GetAsync($"{Api}/auth/bootstrap/status");
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.True((bool)status.Fields["bootstrap_allowed"]!);
        var created = await session.BootstrapAsync("owner1", "first-owner-pass-min8");
        AssertStatus(HttpStatusCode.OK, created);
        Assert.Equal("owner1", (string)created.Fields["username"]!);
        Assert.False((bool)(await session.GetAsync($"{Api}/auth/bootstrap/status")).Fields["bootstrap_allowed"]!);

        // Bootstrap signs the new admin in directly: the cookie it just set already works, with no separate login required.
        var me = await session.GetAsync($"{Api}/auth/me");
        AssertStatus(HttpStatusCode.OK, me);
        Assert.Equal("owner1", (string)me.Fields["user"]!["username"]!);

        var activity = await session.GetAsync($"{Api}/activity/recent");
        AssertStatus(HttpStatusCode.OK, activity);
        var eventTypes = activity.Fields["items"]!.AsArray().Select(item => (string)item!["event_type"]!).ToHashSet();
        Assert.Contains("auth.bootstrap_succeeded", eventTypes);
        Assert.Contains("auth.login_succeeded", eventTypes);
    }

    [Fact]
    public async Task Bootstrap_rejects_short_password()
    {
        var session = NewSession(noAccount.Server);
        Assert.True((bool)(await session.GetAsync($"{Api}/auth/bootstrap/status")).Fields["bootstrap_allowed"]!);

        var response = await session.BootstrapAsync("owner1", "short");

        AssertStatus(HttpStatusCode.UnprocessableEntity, response);
        var detail = response.Fields["detail"] as JsonArray ?? [];
        Assert.Contains(
            detail,
            item => item is JsonObject entry
                && entry["loc"] is JsonArray location
                && location.Select(part => (string?)part).SequenceEqual(["body", "password"])
                && ((string?)entry["msg"] ?? string.Empty).Contains("at least 8 characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_rejects_common_password()
    {
        var session = NewSession(noAccount.Server);

        var response = await session.BootstrapAsync("owner1", "password1234");

        AssertStatus(HttpStatusCode.BadRequest, response);
        Assert.Contains("common", ((string)response.Fields["detail"]!).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_blocked_after_admin_exists()
    {
        Assert.False((bool)(await Alice.GetAsync($"{Api}/auth/bootstrap/status")).Fields["bootstrap_allowed"]!);

        var response = await Alice.BootstrapAsync("intruder", "some-long-password-here");

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    /// <summary>No account at all. Bootstrap tests here must fail, so nothing changes and setup stays open.</summary>
    public sealed class NoAccountServerFixture : IAsyncLifetime
    {
        public WeirServer Server { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Server = await WeirServer.StartNewAsync();
        }

        public async Task DisposeAsync() => await Server.DisposeAsync();
    }
}
