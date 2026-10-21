using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The edges of starting and ending a session that the sign-in walk-through does not reach: what the first-run status says when the
/// database is not in a usable state, the limit on first-run attempts, a first-run request with a token that is not Weir's, and
/// signing out with no session, with a session Weir no longer has, and with the token sent either way.
/// </summary>
public sealed class AuthSessionLifecycleApiTests
{
    private const string BootstrapStatus = "/api/v1/auth/bootstrap/status";
    private const string Bootstrap = "/api/v1/auth/bootstrap";
    private const string Logout = "/api/v1/auth/logout";
    private const string ConfirmationExpired = "Your confirmation token expired. Refresh the page and try again.";

    [Theory]
    [InlineData("DROP TABLE users", "Local SQLite schema is not ready (Weir migrates its database at startup; check the server log and restart it).")]
    [InlineData(
        "DROP TABLE users; CREATE VIEW users AS SELECT 1 AS id WHERE no_such_function() = 1",
        "SQLite database unavailable or cannot be opened (check WEIR_HOME and WEIR_DB_PATH).")]
    [InlineData(
        "DROP TABLE users; CREATE VIEW users AS SELECT 1 AS id, 'admin' AS role, 1 AS is_active WHERE length(zeroblob(2000000000)) > 0",
        "Database query failed while checking bootstrap status. See the server log, restart Weir so it can migrate the database, and verify WEIR_HOME / WEIR_DB_PATH.")]
    public async Task The_first_run_status_says_what_is_wrong_when_the_database_cannot_answer(string breakUsers, string expected)
    {
        await using var server = await StartServerAsync();
        await TestDatabase.ExecuteTogetherAsync(server, breakUsers);

        using var response = await server.Client.GetAsync(BootstrapStatus);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(expected, await Detail(response));
    }

    [Fact]
    public async Task First_run_attempts_are_limited_per_address_and_the_refusal_says_when_to_come_back()
    {
        await using var server = await StartServerAsync(("WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS", "2"), ("WEIR_BOOTSTRAP_RATE_WINDOW_SECONDS", "120"));
        var browser = new ApiTestClient(server);
        var attempt = async () => await browser.PostAsync(Bootstrap, new { username = "owner", password = "password1234", csrf_token = await browser.CsrfAsync() });

        using var first = await attempt();
        using var second = await attempt();
        using var third = await attempt();

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], [first.StatusCode, second.StatusCode, third.StatusCode]);
        Assert.Equal("Too many bootstrap attempts from this address. Try again later.", await Detail(third));
        Assert.Equal("120", Header(third, "Retry-After"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users"));
    }

    [Fact]
    public async Task First_run_with_a_token_that_is_not_weirs_creates_no_account()
    {
        await using var server = await StartServerAsync();
        var browser = new ApiTestClient(server);

        using var response = await browser.PostAsync(Bootstrap, new { username = "owner", password = "first-owner-pass-min8", csrf_token = "not-a-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ConfirmationExpired, await Detail(response));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users"));
    }

    [Fact]
    public async Task Sign_in_with_a_token_that_is_not_weirs_is_refused_with_the_plain_confirmation_sentence()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var browser = new ApiTestClient(server);

        using var response = await browser.PostAsync("/api/v1/auth/login", new { username = "alice", password = AdminPassword, csrf_token = "not-a-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ConfirmationExpired, await Detail(response));
        Assert.Empty(browser.Cookies);
    }

    [Fact]
    public async Task Signing_out_with_a_token_that_is_not_the_sessions_is_refused_and_the_session_stays()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var browser = new ApiTestClient(server);
        await browser.SignInAsync();

        using var response = await browser.PostAsync(Logout, headers: new Dictionary<string, string> { ["X-CSRF-Token"] = "not-a-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ConfirmationExpired, await Detail(response));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions WHERE revoked_at IS NULL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Signing_out_ends_the_session_and_is_written_to_activity_whichever_way_the_token_is_sent(bool inHeader)
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var browser = new ApiTestClient(server);
        await browser.SignInAsync();
        var token = await browser.CsrfAsync();

        using var response = inHeader
            ? await browser.PostAsync(Logout, headers: new Dictionary<string, string> { ["X-CSRF-Token"] = token })
            : await browser.PostAsync(Logout, new { csrf_token = token });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions WHERE revoked_at IS NULL"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.logout'"));
        using var me = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Signing_out_with_a_session_cookie_Weir_no_longer_has_still_clears_the_cookie_and_writes_nothing()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var browser = new ApiTestClient(server);
        await browser.SignInAsync();
        var token = await browser.CsrfAsync();
        await TestDatabase.ExecuteAsync(server, "DELETE FROM user_sessions");

        using var response = await browser.PostAsync(Logout, headers: new Dictionary<string, string> { ["X-CSRF-Token"] = token });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("Max-Age=0", Header(response, "Set-Cookie"), StringComparison.Ordinal);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.logout'"));
    }

    [Fact]
    public async Task Signing_out_with_no_session_at_all_answers_with_an_expired_cookie()
    {
        await using var server = await StartServerAsync();
        var browser = new ApiTestClient(server);

        using var response = await browser.PostAsync(Logout, new { csrf_token = await browser.CsrfAsync() });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("Max-Age=0", Header(response, "Set-Cookie"), StringComparison.Ordinal);
        Assert.Equal("no-store, private", Header(response, "Cache-Control"));
    }
}
