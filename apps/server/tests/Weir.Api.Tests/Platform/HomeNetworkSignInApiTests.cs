using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Skip sign-in for devices on the home network, over real HTTP: who is let in without a password and who never is, what the
/// skipped visitor still has to type the password for, and what the setting writes down. A visitor who is let in is the account for
/// that one request, with no cookie and no session left behind.
/// </summary>
public sealed class HomeNetworkSignInApiTests
{
    private const string Path = "/api/v1/suite/home-network-sign-in";
    private const string Me = "/api/v1/auth/me";
    private const string Elsewhere = "203.0.113.5";
    private const string AtHome = "192.168.1.20";

    private sealed record Reply(int Status, string Body, string SetCookie)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;

        public string Detail => Json["detail"]!.GetValue<string>();
    }

    private static async Task<(WeirTestServer Server, ApiTestClient Owner)> StartAsync(params (string Name, string Value)[] variables)
    {
        var server = await StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var owner = new ApiTestClient(server);
        await owner.SignInAsync();
        return (server, owner);
    }

    private static async Task<HttpResponseMessage> SaveAsync(
        ApiTestClient client, bool enabled, string[]? extra = null, string[]? allow = null, string? password = AdminPassword)
    {
        var body = new JsonObject
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["enabled"] = enabled,
            ["extra_networks"] = new JsonArray((extra ?? []).Select(entry => (JsonNode)JsonValue.Create(entry)!).ToArray()),
            ["allow_list"] = new JsonArray((allow ?? []).Select(entry => (JsonNode)JsonValue.Create(entry)!).ToArray()),
        };
        if (password is not null)
        {
            body["current_password"] = password;
        }

        return await client.PutAsync(Path, body);
    }

    private static async Task TurnOnAsync(ApiTestClient owner, string[]? extra = null, string[]? allow = null)
    {
        using var saved = await SaveAsync(owner, enabled: true, extra, allow);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private static async Task<Reply> FromAsync(
        WeirTestServer server, string peer, string method, string path, string? forwardedFor = null, object? json = null, string? cookie = null)
    {
        var bytes = json is null ? [] : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        var context = await server.TestServer.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            request.Request.Method = method;
            request.Request.Path = path;
            if (forwardedFor is not null)
            {
                request.Request.Headers["X-Forwarded-For"] = forwardedFor;
            }

            if (cookie is not null)
            {
                request.Request.Headers.Cookie = cookie;
            }

            if (json is not null)
            {
                request.Request.Headers.ContentType = "application/json";
                request.Request.Body = new MemoryStream(bytes);
            }
        });
        using var reader = new StreamReader(context.Response.Body);
        return new Reply(context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.Headers.SetCookie.ToString());
    }

    private static async Task<string> AnonymousCsrfAsync(WeirTestServer server, string peer) =>
        (await FromAsync(server, peer, "GET", "/api/v1/auth/csrf")).Json["csrf_token"]!.GetValue<string>();

    private static Task<long> SessionRowsAsync(WeirTestServer server) => TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions");

    [Fact]
    public async Task It_is_off_until_the_owner_turns_it_on_and_a_local_device_signs_in_like_any_other()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;

        var state = await Json(await owner.GetAsync(Path));
        var home = await FromAsync(server, AtHome, "GET", Me);
        var loopback = await FromAsync(server, "127.0.0.1", "GET", Me);

        Assert.False(state["enabled"]!.GetValue<bool>());
        Assert.Empty(state["extra_networks"]!.AsArray());
        Assert.Empty(state["allow_list"]!.AsArray());
        Assert.Null(state["warning"]);
        Assert.Equal((401, 401), (home.Status, loopback.Status));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.4.5.6")]
    [InlineData("172.20.0.9")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.7.7")]
    [InlineData("::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.20")]
    public async Task A_local_device_is_the_accounts_own_identity_for_that_request_with_no_cookie_and_no_session(string peer)
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        var sessionsBefore = await SessionRowsAsync(server);

        var me = await FromAsync(server, peer, "GET", Me);
        var session = await FromAsync(server, peer, "GET", "/api/v1/auth/session");

        Assert.Equal(200, me.Status);
        Assert.Equal("alice", me.Json["user"]!["username"]!.GetValue<string>());
        Assert.Equal("admin", me.Json["user"]!["role"]!.GetValue<string>());
        Assert.Equal(200, session.Status);
        Assert.Equal("home-network", session.Json["session_id"]!.GetValue<string>());
        Assert.Empty(me.SetCookie);
        Assert.Empty(session.SetCookie);
        Assert.Equal(sessionsBefore, await SessionRowsAsync(server));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.5")]
    [InlineData("172.32.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public async Task A_public_address_is_never_skipped(string peer)
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);

        var me = await FromAsync(server, peer, "GET", Me);
        var settings = await FromAsync(server, peer, "GET", Path);

        Assert.Equal((401, 401), (me.Status, settings.Status));
        Assert.Empty(me.SetCookie);
    }

    [Fact]
    public async Task Turning_it_off_again_makes_every_device_sign_in_again()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        Assert.Equal(200, (await FromAsync(server, AtHome, "GET", Me)).Status);

        using var off = await SaveAsync(owner, enabled: false);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(401, (await FromAsync(server, AtHome, "GET", Me)).Status);
    }

    [Fact]
    public async Task A_header_from_a_peer_that_is_not_a_trusted_proxy_is_ignored_whatever_it_claims()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);

        var claimsHome = await FromAsync(server, Elsewhere, "GET", Me, forwardedFor: AtHome);
        var claimsLoopback = await FromAsync(server, Elsewhere, "GET", Me, forwardedFor: "127.0.0.1");
        var homeClaimsAbroad = await FromAsync(server, AtHome, "GET", Me, forwardedFor: Elsewhere);

        Assert.Equal((401, 401, 200), (claimsHome.Status, claimsLoopback.Status, homeClaimsAbroad.Status));
    }

    [Fact]
    public async Task A_header_from_a_peer_that_is_not_a_trusted_proxy_is_ignored_while_another_peer_is_trusted()
    {
        var (server, owner) = await StartAsync(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/24"));
        await using var _ = server;
        await TurnOnAsync(owner);

        var spoofed = await FromAsync(server, Elsewhere, "GET", Me, forwardedFor: $"{AtHome}, 10.0.0.1");
        var neighbourAbroad = await FromAsync(server, "10.0.1.1", "GET", Me, forwardedFor: Elsewhere);

        Assert.Equal((401, 200), (spoofed.Status, neighbourAbroad.Status));
    }

    [Fact]
    public async Task A_trusted_proxy_names_the_client_by_the_header_walked_from_the_right()
    {
        var (server, owner) = await StartAsync(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/24"));
        await using var _ = server;
        await TurnOnAsync(owner);

        var fromHome = await FromAsync(server, "10.0.0.1", "GET", Me, forwardedFor: AtHome);
        var fromAbroad = await FromAsync(server, "10.0.0.1", "GET", Me, forwardedFor: Elsewhere);
        var forgedLeft = await FromAsync(server, "10.0.0.1", "GET", Me, forwardedFor: $"127.0.0.1, {Elsewhere}");
        var chained = await FromAsync(server, "10.0.0.1", "GET", Me, forwardedFor: $"{Elsewhere}, {AtHome}, 10.0.0.9");
        var noHeader = await FromAsync(server, "10.0.0.1", "GET", Me);
        var notAnAddress = await FromAsync(server, "10.0.0.1", "GET", Me, forwardedFor: "banana");

        Assert.Equal(
            (200, 401, 401, 200, 401, 401),
            (fromHome.Status, fromAbroad.Status, forgedLeft.Status, chained.Status, noHeader.Status, notAnAddress.Status));
    }

    [Fact]
    public async Task A_real_session_is_used_before_the_home_network_is_asked()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        var cookie = string.Join("; ", owner.Cookies.Select(pair => $"{pair.Key}={pair.Value}"));

        var me = await FromAsync(server, AtHome, "GET", "/api/v1/auth/session", cookie: cookie);

        Assert.NotEqual("home-network", me.Json["session_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_allow_list_narrows_which_local_devices_skip_and_never_widens_to_a_public_one()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner, allow: ["192.168.1.0/28", Elsewhere]);

        var listed = await FromAsync(server, "192.168.1.9", "GET", Me);
        var local = await FromAsync(server, "192.168.1.40", "GET", Me);
        var loopback = await FromAsync(server, "127.0.0.1", "GET", Me);
        var publicOnTheList = await FromAsync(server, Elsewhere, "GET", Me);

        Assert.Equal((200, 401, 401, 401), (listed.Status, local.Status, loopback.Status, publicOnTheList.Status));
    }

    [Fact]
    public async Task A_network_the_owner_adds_counts_as_local()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner, extra: ["100.64.0.0/10"]);

        var added = await FromAsync(server, "100.64.9.9", "GET", Me);
        var other = await FromAsync(server, "100.128.0.1", "GET", Me);

        Assert.Equal((200, 401), (added.Status, other.Status));
    }

    [Fact]
    public async Task It_signs_nobody_in_when_the_account_is_missing_or_switched_off()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);

        await TestDatabase.ExecuteAsync(server, "UPDATE users SET is_active = 0");
        var inactive = await FromAsync(server, AtHome, "GET", Me);
        await TestDatabase.ExecuteAsync(server, "DELETE FROM user_sessions");
        await TestDatabase.ExecuteAsync(server, "DELETE FROM users");
        var none = await FromAsync(server, AtHome, "GET", Me);

        Assert.Equal((401, 401), (inactive.Status, none.Status));
    }

    [Fact]
    public async Task A_bad_entry_is_refused_in_the_exact_words_and_nothing_is_saved()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;

        using var badNetwork = await SaveAsync(owner, enabled: true, extra: ["192.168.1.0/99"]);
        using var badAllow = await SaveAsync(owner, enabled: true, allow: ["10.0.0.1", "banana"]);

        const string Tail = " is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28).";
        Assert.Equal((HttpStatusCode.BadRequest, "'192.168.1.0/99'" + Tail), (badNetwork.StatusCode, await Detail(badNetwork)));
        Assert.Equal((HttpStatusCode.BadRequest, "'banana'" + Tail), (badAllow.StatusCode, await Detail(badAllow)));
        Assert.False((await Json(await owner.GetAsync(Path)))["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Changing_the_setting_needs_the_current_password_even_from_a_skipped_visitor()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        var csrf = await AnonymousCsrfAsync(server, AtHome);
        object Body(object? password) => new Dictionary<string, object?>
        {
            ["csrf_token"] = csrf,
            ["enabled"] = false,
            ["extra_networks"] = Array.Empty<string>(),
            ["allow_list"] = Array.Empty<string>(),
            ["current_password"] = password,
        };

        var wrong = await FromAsync(server, AtHome, "PUT", Path, json: Body("not-the-password"));
        var missing = await FromAsync(server, AtHome, "PUT", Path, json: Body(null));
        var right = await FromAsync(server, AtHome, "PUT", Path, json: Body(AdminPassword));

        Assert.Equal((400, "Current password is incorrect."), (wrong.Status, wrong.Detail));
        Assert.Equal(422, missing.Status);
        Assert.Equal(200, right.Status);
        Assert.False(right.Json["enabled"]!.GetValue<bool>());
        Assert.Empty(right.SetCookie);
    }

    [Fact]
    public async Task Too_many_wrong_passwords_lock_the_account_out_of_the_change_and_it_stays_as_it_was()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner, allow: ["192.168.1.0/24"]);
        var csrf = await AnonymousCsrfAsync(server, AtHome);
        var backoff = server.Services.GetRequiredService<AuthRateLimiters>().LoginUsernameBackoff;
        for (var failure = 0; failure < 12; failure++)
        {
            backoff.RecordFailure("alice");
        }

        var locked = await FromAsync(server, AtHome, "PUT", Path, json: new { csrf_token = csrf, enabled = false, current_password = AdminPassword });

        backoff.RecordSuccess("alice");
        var stillOn = await FromAsync(server, AtHome, "GET", Path);
        Assert.Equal((429, "Too many wrong passwords for this account. Wait a few minutes, then try again."), (locked.Status, locked.Detail));
        Assert.True(stillOn.Json["enabled"]!.GetValue<bool>());
        Assert.Equal("192.168.1.0/24", stillOn.Json["allow_list"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Changing_the_password_or_the_username_needs_the_current_password_even_from_a_skipped_visitor()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        var csrf = await AnonymousCsrfAsync(server, AtHome);

        var password = await FromAsync(server, AtHome, "POST", "/api/v1/auth/change-password", json: new { csrf_token = csrf, current_password = "not-the-password", new_password = "a-brand-new-password-1" });
        var username = await FromAsync(server, AtHome, "POST", "/api/v1/auth/change-username", json: new { csrf_token = csrf, current_password = "not-the-password", new_username = "mallory" });

        Assert.Equal((400, "Current password is incorrect.", 400, "Current password is incorrect."), (password.Status, password.Detail, username.Status, username.Detail));
        using var signedIn = await owner.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task The_account_forms_are_locked_out_with_the_setting_after_too_many_wrong_passwords_even_for_the_right_one()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TurnOnAsync(owner);
        var csrf = await AnonymousCsrfAsync(server, AtHome);
        var backoff = server.Services.GetRequiredService<AuthRateLimiters>().LoginUsernameBackoff;
        for (var failure = 0; failure < 12; failure++)
        {
            backoff.RecordFailure("alice");
        }

        var username = await FromAsync(server, AtHome, "POST", "/api/v1/auth/change-username", json: new { csrf_token = csrf, current_password = AdminPassword, new_username = "mallory" });
        var password = await FromAsync(server, AtHome, "POST", "/api/v1/auth/change-password", json: new { csrf_token = csrf, current_password = AdminPassword, new_password = "a-brand-new-password-1" });

        Assert.Equal((429, 429), (username.Status, password.Status));
        backoff.RecordSuccess("alice");
        using var signedIn = await new ApiTestClient(server).LoginAsync();
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_can_change_the_setting()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        await TestDatabase.SeedUserAsync(server, "opal", "a-long-enough-password", "operator");
        var operatorClient = new ApiTestClient(server);
        await operatorClient.SignInAsync("opal", "a-long-enough-password");

        using var refused = await SaveAsync(operatorClient, enabled: true, password: "a-long-enough-password");
        using var read = await operatorClient.GetAsync(Path);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.OK), (refused.StatusCode, read.StatusCode));
        Assert.False((await Json(read))["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_save_is_refused_without_a_confirmation_token_and_to_an_anonymous_caller()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;

        using var noToken = await owner.PutAsync(Path, new { csrf_token = "stale", enabled = true, current_password = AdminPassword });
        var anonymous = await FromAsync(server, Elsewhere, "PUT", Path, json: new { csrf_token = await AnonymousCsrfAsync(server, Elsewhere), enabled = true, current_password = AdminPassword });

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(401, anonymous.Status);
    }

    [Fact]
    public async Task Activity_is_written_only_when_the_value_changes()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        const string Type = "system.home_network_sign_in.changed";
        Task<long> Events() => TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = $type", ("$type", Type));

        using var offAgain = await SaveAsync(owner, enabled: false);
        Assert.Equal(0, await Events());

        await TurnOnAsync(owner);
        Assert.Equal(1, await Events());
        Assert.Equal("Local network sign-in setting changed", await TestDatabase.ScalarStringAsync(server, "SELECT title FROM activity_events WHERE event_type = $type", ("$type", Type)));
        Assert.Equal("Skip sign-in for devices on the local network turned on.", await TestDatabase.ScalarStringAsync(server, "SELECT detail FROM activity_events WHERE event_type = $type", ("$type", Type)));

        await TurnOnAsync(owner, allow: ["192.168.1.0/24"]);
        using var onWithNetworks = await SaveAsync(owner, enabled: true, extra: ["100.64.0.0/10"], allow: ["192.168.1.0/24"]);
        Assert.Equal(1, await Events());

        using var off = await SaveAsync(owner, enabled: false);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(2, await Events());
        Assert.Equal(
            "Skip sign-in for devices on the local network turned off.",
            await TestDatabase.ScalarStringAsync(server, "SELECT detail FROM activity_events WHERE event_type = $type ORDER BY id DESC LIMIT 1", ("$type", Type)));
    }

    [Fact]
    public async Task A_failed_save_writes_no_activity()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;

        using var wrong = await SaveAsync(owner, enabled: true, password: "not-the-password");
        using var bad = await SaveAsync(owner, enabled: true, extra: ["banana"]);

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.BadRequest),
            (wrong.StatusCode, bad.StatusCode));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'system.home_network_sign_in.changed'"));
    }

    [Fact]
    public async Task The_saved_choice_is_read_back_with_trimmed_unique_entries()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;

        await TurnOnAsync(owner, extra: [" 100.64.0.0/10 ", "100.64.0.0/10", ""], allow: ["fd00::/8"]);
        var state = await Json(await owner.GetAsync(Path));

        Assert.True(state["enabled"]!.GetValue<bool>());
        Assert.Equal(["100.64.0.0/10"], state["extra_networks"]!.AsArray().Select(entry => entry!.GetValue<string>()));
        Assert.Equal(["fd00::/8"], state["allow_list"]!.AsArray().Select(entry => entry!.GetValue<string>()));
        Assert.Null(state["warning"]);
    }

    [Fact]
    public async Task The_current_password_is_asked_for_only_after_someone_is_signed_in()
    {
        await using var server = await WeirTestServer.StartAsync();
        var context = new DefaultHttpContext { RequestServices = server.Services };
        await using var request = new ApiRequest(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => request.RequireCurrentPasswordAsync("anything"));
    }

    [Fact]
    public async Task The_system_checks_count_it_as_passing()
    {
        var (server, owner) = await StartAsync();
        await using var _ = server;
        var before = (await Json(await owner.GetAsync("/api/v1/system/overview")))["checks"]!;

        await TurnOnAsync(owner);
        var after = (await Json(await owner.GetAsync("/api/v1/system/overview")))["checks"]!;

        Assert.Equal(before["total"]!.GetValue<int>(), after["total"]!.GetValue<int>());
        Assert.Equal(before["passing"]!.GetValue<int>(), after["passing"]!.GetValue<int>());
    }
}
