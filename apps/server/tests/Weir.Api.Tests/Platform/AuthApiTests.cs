using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>Sign-in, sessions, usernames and the secure-cookie mode over real HTTP.</summary>
public sealed class AuthApiTests
{
    [Fact]
    public async Task Login_me_logout_flow()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);

        using var login = await client.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("{\"user\":{\"id\":1,\"username\":\"alice\",\"role\":\"admin\",\"app_theme\":null}}", await login.Content.ReadAsStringAsync());
        Assert.True(client.Cookies["weir_session"].Length > 20);

        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal("alice", (await Json(me))["user"]!["username"]!.GetValue<string>());
        using var session = await client.GetAsync("/api/v1/auth/session");
        Assert.False((await Json(session))["trusted_device"]!.GetValue<bool>());

        using var logout = await client.PostAsync("/api/v1/auth/logout", headers: new Dictionary<string, string> { ["X-CSRF-Token"] = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Matches("^weir_session=\"\"; expires=[A-Z][a-z]{2}, \\d{2} [A-Z][a-z]{2} \\d{4} \\d{2}:\\d{2}:\\d{2} GMT; HttpOnly; Max-Age=0; Path=/; SameSite=lax$", Header(logout, "Set-Cookie"));
        Assert.Equal("no-store, private", Header(logout, "Cache-Control"));
        using var after = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Login_failures_invalid_csrf_extra_fields_and_missing_logout_csrf()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);

        using var wrong = await client.LoginAsync(password: "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Invalid username or password.", await Detail(wrong));

        using var extra = await client.PostAsync("/api/v1/auth/login", new { username = "alice", password = AdminPassword, csrf_token = await client.CsrfAsync(), unexpected = "value" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Equal(
            "{\"detail\":[{\"type\":\"extra_forbidden\",\"loc\":[\"body\",\"unexpected\"],\"msg\":\"Extra inputs are not permitted\",\"input\":\"value\"}]}",
            await extra.Content.ReadAsStringAsync());

        using var badCsrf = await client.PostAsync("/api/v1/auth/login", new { username = "alice", password = AdminPassword, csrf_token = "invalid-token" });
        Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(badCsrf));

        await client.SignInAsync();
        using var logout = await client.PostAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal("Missing CSRF token (X-CSRF-Token header or body csrf_token).", await Detail(logout));
    }

    [Fact]
    public async Task Failed_logins_are_recorded_once_per_username_per_window()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.LoginAsync(password: "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.login_failed' AND detail = 'alice' AND \"trigger\" = 'manual' AND result = 'failed'"));
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_signs_everyone_out()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var wrongCurrent = await client.PostAsync("/api/v1/auth/change-password", new { csrf_token = await client.CsrfAsync(), current_password = "wrong-current-password", new_password = "new-password-stronger-123" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Contains("current password", (await Detail(wrongCurrent)).ToLowerInvariant(), StringComparison.Ordinal);

        using var change = await client.PostAsync("/api/v1/auth/change-password", new { csrf_token = await client.CsrfAsync(), current_password = AdminPassword, new_password = "new-password-stronger-123" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Contains("sign in again", ((await Json(change))["message"]!.GetValue<string>()).ToLowerInvariant(), StringComparison.Ordinal);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var old = await client.LoginAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await client.LoginAsync(password: "new-password-stronger-123");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.password_changed'"));
    }

    [Fact]
    public async Task Sessions_rotate_have_an_explicit_lifetime_and_trusted_devices_last_longer()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        using var first = await client.LoginAsync();
        Assert.Matches("^weir_session=[A-Za-z0-9_-]{43}; HttpOnly; Max-Age=7776000; Path=/; SameSite=lax$", Header(first, "Set-Cookie"));
        Assert.Equal("no-store, private", Header(first, "Cache-Control"));
        var oldCookie = client.Cookies["weir_session"];
        using var second = await client.LoginAsync(trustedDevice: true);
        Assert.NotEqual(oldCookie, client.Cookies["weir_session"]);
        Assert.Contains("Max-Age=31536000", Header(second, "Set-Cookie"), StringComparison.Ordinal);

        using var session = await client.GetAsync("/api/v1/auth/session");
        var body = await Json(session);
        Assert.True(body["trusted_device"]!.GetValue<bool>());
        Assert.Equal(60 * 1440, body["idle_timeout_minutes"]!.GetValue<int>());
        Assert.Equal(365, body["absolute_timeout_days"]!.GetValue<int>());
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions WHERE is_trusted_device = 1"));
    }

    [Fact]
    public async Task A_session_survives_a_server_restart_and_a_second_browser_does_not_revoke_the_first()
    {
        string home;
        string cookie;
        await using (var first = await StartServerAsync())
        {
            first.KeepHome = true;
            home = first.Home;
            await TestDatabase.SeedAdminAsync(first);
            var remote = new ApiTestClient(first);
            await remote.SignInAsync();
            var local = new ApiTestClient(first);
            await local.SignInAsync();
            Assert.Equal(HttpStatusCode.OK, (await remote.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/api/v1/auth/me")).StatusCode);

            using var crossSession = await local.PostAsync("/api/v1/auth/change-password", new { csrf_token = await remote.CsrfAsync(), current_password = AdminPassword, new_password = "new-password-stronger-123" });
            Assert.Equal(HttpStatusCode.BadRequest, crossSession.StatusCode);
            cookie = remote.Cookies["weir_session"];
        }

        await using var second = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")], home: home);
        var client = new ApiTestClient(second);
        client.SetCookie("weir_session", cookie);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("alice", (await Json(me))["user"]!["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admin_ping_needs_an_admin_and_an_unknown_role_is_refused()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.SeedViewerAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        using var ping = await admin.GetAsync("/api/v1/auth/admin/ping");
        Assert.Equal("{\"ok\":true}", await ping.Content.ReadAsStringAsync());

        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        using var forbidden = await viewer.GetAsync("/api/v1/auth/admin/ping");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("Forbidden.", await Detail(forbidden));

        await TestDatabase.ExecuteAsync(server, "UPDATE users SET role = 'superuser' WHERE username = 'bob'");
        using var invalid = await viewer.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        Assert.Equal("Invalid account role.", await Detail(invalid));
    }

    /// <summary>The other tests seed accounts at the smallest hash cost; this one keeps the cost the product really uses on the sign-in path.</summary>
    [Fact]
    public async Task A_password_hashed_at_the_production_cost_signs_in_and_a_wrong_one_does_not()
    {
        await using var server = await StartServerAsync();
        var hash = Core.Security.PasswordHasher.Hash(AdminPassword);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash, StringComparison.Ordinal);
        await TestDatabase.ExecuteAsync(server, "INSERT INTO users (username, password_hash, role, is_active) VALUES ('alice', $h, 'admin', 1)", ("$h", hash));
        var client = new ApiTestClient(server);

        using var wrong = await client.LoginAsync(password: "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        await client.SignInAsync();
    }

    [Fact]
    public async Task Bootstrap_creates_the_first_admin_once()
    {
        await using var server = await StartServerAsync(("WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS", "100"));
        var client = new ApiTestClient(server);
        using var status = await client.GetAsync("/api/v1/auth/bootstrap/status");
        Assert.Equal(
            "{\"bootstrap_allowed\":true,\"reason\":\"no_admin_user\",\"requires_setup_code\":false}",
            await status.Content.ReadAsStringAsync());

        using var shortPassword = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "short", csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortPassword.StatusCode);
        Assert.Equal(
            "{\"detail\":[{\"type\":\"string_too_short\",\"loc\":[\"body\",\"password\"],\"msg\":\"String should have at least 8 characters\",\"input\":\"short\",\"ctx\":{\"min_length\":8}}]}",
            await shortPassword.Content.ReadAsStringAsync());

        using var common = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "password1234", csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.BadRequest, common.StatusCode);
        Assert.Contains("common", (await Detail(common)).ToLowerInvariant(), StringComparison.Ordinal);

        using var created = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "first-owner-pass-min8", csrf_token = await client.CsrfAsync() });
        Assert.Equal("owner1", (await Json(created))["username"]!.GetValue<string>());
        using var closed = await client.GetAsync("/api/v1/auth/bootstrap/status");
        Assert.Equal(
            "{\"bootstrap_allowed\":false,\"reason\":\"admin_already_exists\",\"requires_setup_code\":false}",
            await closed.Content.ReadAsStringAsync());
        await client.SignInAsync("owner1", "first-owner-pass-min8");
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.bootstrap_succeeded'"));
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.login_succeeded'"));
        using var settings = await client.GetAsync("/api/v1/suite/settings");
        Assert.Equal("pending", (await Json(settings))["setup_wizard_state"]!.GetValue<string>());

        // Bootstrap takes anonymous CSRF tokens only, so the attempts come from a signed-out browser.
        var anonymous = new ApiTestClient(server);
        for (var i = 0; i < 2; i++)
        {
            using var again = await anonymous.PostAsync("/api/v1/auth/bootstrap", new { username = "intruder" + i, password = "some-long-password-here", csrf_token = await anonymous.CsrfAsync() });
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
            Assert.Equal("Bootstrap is not available: an admin user already exists.", await Detail(again));
        }

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.bootstrap_denied'"));
    }

    [Fact]
    public async Task Creating_the_first_admin_signs_that_browser_in()
    {
        await using var server = await StartServerAsync();
        var client = new ApiTestClient(server);

        using var created = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "first-owner-pass-min8", csrf_token = await client.CsrfAsync() });

        Assert.Matches("^weir_session=[A-Za-z0-9_-]{43}; HttpOnly; Max-Age=\\d+; Path=/; SameSite=lax$", Header(created, "Set-Cookie"));
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("owner1", (await Json(me))["user"]!["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_is_rate_limited_with_retry_after()
    {
        await using var server = await StartServerAsync(("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "3"), ("WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS", "120"));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.LoginAsync(password: "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await client.LoginAsync(password: "wrong");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("120", Header(limited, "Retry-After"));
        Assert.Equal("Too many login attempts from this address. Try again later.", await Detail(limited));
    }

    [Fact]
    public async Task Repeated_failures_for_one_account_are_backed_off_even_under_a_generous_per_ip_limit()
    {
        await using var server = await StartServerAsync(("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "100"));
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.SeedViewerAsync(server);
        var client = new ApiTestClient(server);

        // The sixth failure is the one that first exceeds UsernameLoginBackoff.FreeAttempts (5); the
        // exact growth of the wait beyond this point is covered without wall-clock risk in
        // UsernameLoginBackoffTests, which drives the limiter with a fake clock.
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.LoginAsync(password: "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var backedOff = await client.LoginAsync(password: "wrong");
        Assert.Equal(HttpStatusCode.TooManyRequests, backedOff.StatusCode);
        Assert.Equal("2", Header(backedOff, "Retry-After"));
        Assert.Equal("Too many sign-in attempts for this account. Wait a few minutes, then try again.", await Detail(backedOff));

        // A different account is not caught by alice's backoff.
        using var otherAccount = await client.LoginAsync("bob", "wrong-password-for-bob");
        Assert.Equal(HttpStatusCode.Unauthorized, otherAccount.StatusCode);
    }

    [Fact]
    public async Task A_failed_login_records_the_typed_username_only_when_it_matches_an_account()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);

        using var knownAccount = await client.LoginAsync(password: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, knownAccount.StatusCode);
        using var guessedUsername = await client.LoginAsync(username: "not-a-real-account", password: "guess");
        Assert.Equal(HttpStatusCode.Unauthorized, guessedUsername.StatusCode);

        Assert.Equal("alice", await TestDatabase.ScalarStringAsync(
            server, "SELECT detail FROM activity_events WHERE event_type = 'auth.login_failed' AND detail = 'alice'"));
        Assert.Equal("an unknown username", await TestDatabase.ScalarStringAsync(
            server, "SELECT detail FROM activity_events WHERE event_type = 'auth.login_failed' AND detail = 'an unknown username'"));
        Assert.Null(await TestDatabase.ScalarStringAsync(
            server, "SELECT detail FROM activity_events WHERE event_type = 'auth.login_failed' AND detail = 'not-a-real-account'"));
    }

    [Fact]
    public async Task Security_headers_are_set_on_health_and_api_responses()
    {
        await using var server = await StartServerAsync(("WEIR_SECURITY_ENABLE_HSTS", "1"));
        var client = new ApiTestClient(server);
        foreach (var path in new[] { "/health", "/api/v1/auth/csrf", "/api/v1/system/directories", "/api/v1/suite/security-overview", "/api/v1/suite/update-status", "/metrics" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
            Assert.Equal("default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'", Header(response, "Content-Security-Policy"));
            Assert.Equal("no-store, private", Header(response, "Cache-Control"));
            Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
            Assert.Equal("camera=(), microphone=(), geolocation=(), payment=(), usb=()", Header(response, "Permissions-Policy"));
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
        }
    }

    [Fact]
    public async Task The_cookie_is_secure_only_when_forced_on_plain_http()
    {
        await using var plain = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(plain);
        var client = new ApiTestClient(plain);
        using var login = await client.LoginAsync();
        Assert.DoesNotContain("secure", Header(login, "Set-Cookie").ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        using var logout = await client.PostAsync("/api/v1/auth/logout", new { csrf_token = await client.CsrfAsync() });
        Assert.DoesNotContain("secure", Header(logout, "Set-Cookie").ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        await using var forced = await StartServerAsync(("WEIR_SESSION_COOKIE_SECURE", "always"));
        await TestDatabase.SeedAdminAsync(forced);
        using var forcedLogin = await new ApiTestClient(forced).LoginAsync();
        Assert.EndsWith("; Secure", Header(forcedLogin, "Set-Cookie"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_warns_once_when_an_https_origin_cannot_secure_the_cookie()
    {
        var logger = new CapturingLoggerFactory();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<ILoggerFactory>(logger));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        var httpsOrigin = new Dictionary<string, string> { ["Origin"] = "https://weir.example", ["X-Requested-With"] = "XMLHttpRequest" };

        using var first = await client.PostAsync(
            "/api/v1/auth/login",
            new { username = "alice", password = AdminPassword, csrf_token = await client.CsrfAsync() },
            httpsOrigin);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var logout = await client.PostAsync("/api/v1/auth/logout", new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var second = await client.PostAsync(
            "/api/v1/auth/login",
            new { username = "alice", password = AdminPassword, csrf_token = await client.CsrfAsync() },
            httpsOrigin);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Single(logger.Entries, entry => entry.Message.Contains("could not be marked Secure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sign_in_over_plain_lan_http_never_warns()
    {
        var logger = new CapturingLoggerFactory();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<ILoggerFactory>(logger));
        await TestDatabase.SeedAdminAsync(server);
        using var login = await new ApiTestClient(server).LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("could not be marked Secure", StringComparison.Ordinal));
    }

    /// <summary>A logger that keeps every message, so a test can assert on exactly what a console/file sink would receive.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        // Background timers (the manager heartbeat, library mode's schedule) log through this factory too,
        // from their own threads, alongside the request thread the test asserts from.
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Usernames_ignore_case_and_can_be_changed_with_the_current_password()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        foreach (var spelling in new[] { "alice", "Alice", "ALICE", "  AlIcE  " })
        {
            using var response = await client.LoginAsync(spelling);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var signedOut = await new ApiTestClient(server).PostAsync("/api/v1/auth/change-username", new { current_password = AdminPassword, new_username = "user1", csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);

        using var wrong = await client.PostAsync("/api/v1/auth/change-username", new { current_password = "wrong-password", new_username = "user1", csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var unchanged = await client.PostAsync("/api/v1/auth/change-username", new { current_password = AdminPassword, new_username = "alice", csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
        using var renamed = await client.PostAsync("/api/v1/auth/change-username", new { current_password = AdminPassword, new_username = "user1", csrf_token = await client.CsrfAsync() });
        Assert.Equal("{\"message\":\"Username changed. Use it the next time you sign in.\",\"username\":\"user1\"}", await renamed.Content.ReadAsStringAsync());
        Assert.Equal("user1", (await Json(await client.GetAsync("/api/v1/auth/me")))["user"]!["username"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("USER1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("alice")).StatusCode);
    }

    [Fact]
    public async Task Saving_the_theme_needs_csrf_and_a_valid_value_and_each_account_keeps_its_own()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.SeedViewerAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();

        using var badCsrf = await admin.PostAsync("/api/v1/auth/theme", new { theme = "light", csrf_token = "invalid-token" });
        Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(badCsrf));

        using var invalid = await admin.PostAsync("/api/v1/auth/theme", new { theme = "purple", csrf_token = await admin.CsrfAsync() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        using var saved = await admin.PostAsync("/api/v1/auth/theme", new { theme = "light", csrf_token = await admin.CsrfAsync() });
        Assert.Equal("{\"message\":\"Theme saved.\",\"app_theme\":\"light\"}", await saved.Content.ReadAsStringAsync());
        Assert.Equal("light", (await Json(await admin.GetAsync("/api/v1/auth/me")))["user"]!["app_theme"]!.GetValue<string>());

        // A viewer may set their own theme too, and it never touches another account's preference.
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        using var viewerSaved = await viewer.PostAsync("/api/v1/auth/theme", new { theme = "dark", csrf_token = await viewer.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, viewerSaved.StatusCode);
        Assert.Equal("dark", (await Json(await viewer.GetAsync("/api/v1/auth/me")))["user"]!["app_theme"]!.GetValue<string>());
        Assert.Equal("light", (await Json(await admin.GetAsync("/api/v1/auth/me")))["user"]!["app_theme"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_sessions_can_be_listed_and_signed_out()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var a = new ApiTestClient(server);
        var b = new ApiTestClient(server);
        await a.SignInAsync();
        await b.SignInAsync();
        using var sessions = await a.GetAsync("/api/v1/auth/sessions");
        var items = (await Json(sessions))["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Single(items, item => item!["current"]!.GetValue<bool>());
        var otherId = items.Single(item => !item!["current"]!.GetValue<bool>())!["session_id"]!.GetValue<string>();
        var currentId = items.Single(item => item!["current"]!.GetValue<bool>())!["session_id"]!.GetValue<string>();
        var token = await a.CsrfAsync();
        var csrf = new Dictionary<string, string> { ["X-CSRF-Token"] = token };

        using var noToken = await a.PostAsync($"/api/v1/auth/sessions/{otherId}/revoke");
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(noToken));
        using var badId = await a.PostAsync("/api/v1/auth/sessions/not-a-uuid/revoke", headers: csrf);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badId.StatusCode);
        using var current = await a.PostAsync($"/api/v1/auth/sessions/{currentId}/revoke", headers: csrf);
        Assert.Equal("The current session cannot be revoked here.", await Detail(current));
        using var revoked = await a.PostAsync($"/api/v1/auth/sessions/{otherId}/revoke", headers: csrf);
        Assert.Equal("{\"message\":\"Session signed out.\",\"revoked_count\":1}", await revoked.Content.ReadAsStringAsync());
        using var gone = await a.PostAsync($"/api/v1/auth/sessions/{otherId}/revoke", headers: csrf);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/v1/auth/me")).StatusCode);

        await b.SignInAsync();
        using var others = await a.PostAsync("/api/v1/auth/sessions/revoke-others", headers: csrf);
        Assert.Equal("{\"message\":\"Signed out 1 other session.\",\"revoked_count\":1}", await others.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/auth/me")).StatusCode);

        // Two at once reads in the plural.
        var c = new ApiTestClient(server);
        await b.SignInAsync();
        await c.SignInAsync();
        using var twoOthers = await a.PostAsync("/api/v1/auth/sessions/revoke-others", headers: csrf);
        Assert.Equal("{\"message\":\"Signed out 2 other sessions.\",\"revoked_count\":2}", await twoOthers.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_endpoints_answer_503_without_a_session_secret()
    {
        await using var server = await WeirTestServer.StartAsync();
        using var response = await server.Client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("WEIR_SESSION_SECRET must be set for auth endpoints.", await Detail(response));
    }

    [Fact]
    public async Task Bootstrap_status_is_503_when_the_database_cannot_be_opened()
    {
        await using var server = await StartServerAsync();
        await server.BreakDatabaseAsync();
        using var response = await server.Client.GetAsync("/api/v1/auth/bootstrap/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", (await Detail(response)).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_reports_for_a_real_signed_in_session()
    {
        await using var server = await StartServerAsync(("WEIR_VERSION", "7.8.9"));
        await server.WaitForStartupRecoveryAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system/readiness")).StatusCode);
        await client.SignInAsync();
        using var response = await client.GetAsync("/api/v1/system/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.True(body["ready"]!.GetValue<bool>());
        Assert.Equal("7.8.9", body["version"]!.GetValue<string>());
        Assert.Equal("disabled", body["worker_health"]![0]!["status"]!.GetValue<string>());
    }
}
