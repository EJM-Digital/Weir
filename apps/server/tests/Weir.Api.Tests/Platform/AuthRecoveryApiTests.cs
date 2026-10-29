using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Weir.Api.Tests.Platform.ApiTestClient;
using static Weir.Api.Tests.Platform.RecoverySupport;

namespace Weir.Api.Tests.Platform;

/// <summary>Getting back in with a recovery code: the code, the username it shows, the new password, and what a recovery leaves behind.</summary>
public sealed class AuthRecoveryApiTests
{
    private const string NewPassword = "a-brand-new-strong-password";

    private static async Task<HttpResponseMessage> MetricsAsync(ApiTestClient client) =>
        await client.GetAsync(Metrics, new Dictionary<string, string> { ["Authorization"] = "Bearer " + MetricsToken });

    [Fact]
    public async Task A_code_shows_the_username_ends_every_session_and_the_new_password_signs_in()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var signedIn = new ApiTestClient(server);
        await signedIn.SignInAsync();
        var another = new ApiTestClient(server);
        await another.SignInAsync();
        using var keyBefore = await MetricsAsync(new ApiTestClient(server));
        Assert.Equal(HttpStatusCode.OK, keyBefore.StatusCode);
        var forgotten = new ApiTestClient(server);

        using var spent = await SpendAsync(forgotten, codes[4]);

        Assert.Equal(HttpStatusCode.OK, spent.StatusCode);
        Assert.Equal("no-store, private", Header(spent, "Cache-Control"));
        var body = await Json(spent);
        Assert.Equal("alice", body["username"]!.GetValue<string>());
        Assert.Equal(9, body["codes_left"]!.GetValue<int>());
        Assert.Empty(forgotten.Cookies);
        foreach (var browser in new[] { signedIn, another })
        {
            using var me = await browser.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions"));
        using var keyAfter = await MetricsAsync(new ApiTestClient(server));
        Assert.Equal(HttpStatusCode.OK, keyAfter.StatusCode);

        using var chosen = await ChoosePasswordAsync(forgotten, body["recovery_token"]!.GetValue<string>(), NewPassword);

        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal("Password changed. Sign in with your new password.", (await Json(chosen))["message"]!.GetValue<string>());
        using var old = await forgotten.LoginAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await forgotten.LoginAsync(password: NewPassword);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(
            "alice. 9 recovery codes left.",
            await TestDatabase.ScalarStringAsync(server, "SELECT detail FROM activity_events WHERE event_type = 'auth.sign_in_recovered'"));
        Assert.Equal(
            "Sign-in recovered with a recovery code",
            await TestDatabase.ScalarStringAsync(server, "SELECT title FROM activity_events WHERE event_type = 'auth.sign_in_recovered'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'auth.password_changed'"));
    }

    [Fact]
    public async Task The_code_is_spent_even_when_the_new_password_is_never_chosen()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);

        using var first = await SpendAsync(browser, codes[0]);
        using var again = await SpendAsync(browser, codes[0]);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("That recovery code isn't right, or it has been used already. Check it and try again.", await Detail(again));
        Assert.Equal(9, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE used_at IS NULL"));
        using var oldPassword = await browser.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, oldPassword.StatusCode);
    }

    [Fact]
    public async Task A_code_is_read_whatever_its_case_and_spacing()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);

        using var spent = await SpendAsync(browser, codes[2].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, spent.StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_neither_it_nor_a_good_one_is_ever_logged()
    {
        var logs = new CapturingLoggerFactory();
        await using var server = await StartAsync(logs);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);
        var wrong = codes[0][..6] + "AAAAA-AAAAA-AAAAA";

        using var refused = await SpendAsync(browser, wrong);
        using var spent = await SpendAsync(browser, codes[1]);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, spent.StatusCode);
        Assert.True(LoggedAtOrAbove(logs, LogLevel.Warning, "auth event: recovery code refused"));
        Assert.True(LoggedAtOrAbove(logs, LogLevel.Information, "auth event: sign-in recovered with a recovery code"));
        var everything = Everything(logs);
        foreach (var secret in new[] { wrong, codes[1] }.Concat(codes))
        {
            Assert.DoesNotContain(secret[6..], everything, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "AAAAA-AAAAA", await TestDatabase.ScalarStringAsync(server, "SELECT group_concat(title || ' ' || coalesce(detail, ''), ' ') FROM activity_events") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_spent_without_a_confirmation_token_that_is_weirs_and_extra_fields_are_refused()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);

        using var badToken = await browser.PostAsync("/api/v1/auth/recovery/code", new { code = codes[0], csrf_token = "not-a-token" });
        using var extra = await browser.PostAsync("/api/v1/auth/recovery/code", new { code = codes[0], csrf_token = await browser.CsrfAsync(), unexpected = 1 });
        using var empty = await browser.PostAsync("/api/v1/auth/recovery/code", new { code = string.Empty, csrf_token = await browser.CsrfAsync() });

        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(badToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(10, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE used_at IS NULL"));
    }

    [Fact]
    public async Task Attempts_are_limited_per_address_like_sign_in_and_the_refusal_says_when_to_come_back()
    {
        await using var server = await StartAsync(variables: [("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "2"), ("WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS", "120")]);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);

        using var first = await SpendAsync(browser, "AAAAA-AAAAA-AAAAA-AAAAA");
        using var second = await SpendAsync(browser, "AAAAA-AAAAA-AAAAA-AAAAA");
        using var third = await SpendAsync(browser, codes[0]);

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], [first.StatusCode, second.StatusCode, third.StatusCode]);
        Assert.Equal("Too many recovery attempts from this address. Try again later.", await Detail(third));
        Assert.Equal("120", Header(third, "Retry-After"));
        Assert.Equal(10, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE used_at IS NULL"));
    }

    [Fact]
    public async Task Wrong_codes_from_anywhere_back_off_the_whole_install_and_even_a_good_code_waits()
    {
        await using var server = await StartAsync(variables: [("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "100")]);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);
        for (var i = 0; i < Weir.Core.Auth.UsernameLoginBackoff.FreeAttempts + 1; i++)
        {
            using var wrong = await SpendAsync(browser, "AAAAA-AAAAA-AAAAA-AAAAA");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var waiting = await SpendAsync(new ApiTestClient(server), codes[0]);

        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        Assert.Equal("Too many wrong recovery codes. Wait a few minutes, then try again.", await Detail(waiting));
        Assert.True(int.Parse(Header(waiting, "Retry-After"), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal(10, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE used_at IS NULL"));
    }

    [Fact]
    public async Task A_password_that_is_too_weak_is_refused_and_the_same_token_takes_a_better_one()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);
        using var spent = await SpendAsync(browser, codes[0]);
        var token = (await Json(spent))["recovery_token"]!.GetValue<string>();

        using var common = await ChoosePasswordAsync(browser, token, "password1234");
        using var contains = await ChoosePasswordAsync(browser, token, "my-alice-password");
        using var tooShort = await ChoosePasswordAsync(browser, token, "short");
        using var good = await ChoosePasswordAsync(browser, token, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, common.StatusCode);
        Assert.Equal("Password is too common. Choose a stronger password.", await Detail(common));
        Assert.Equal("Password must not contain the username.", await Detail(contains));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_not_ours_or_was_used_already_is_refused()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);
        using var spent = await SpendAsync(browser, codes[0]);
        var token = (await Json(spent))["recovery_token"]!.GetValue<string>();
        using var chosen = await ChoosePasswordAsync(browser, token, NewPassword);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);

        using var reused = await ChoosePasswordAsync(browser, token, "yet-another-strong-password");
        using var invented = await ChoosePasswordAsync(browser, "made-up-token", "yet-another-strong-password");
        using var badCsrf = await browser.PostAsync("/api/v1/auth/recovery/password", new { recovery_token = token, new_password = NewPassword, csrf_token = "not-a-token" });

        Assert.Equal("This password reset has timed out. Use another recovery code to start again.", await Detail(reused));
        Assert.Equal("This password reset has timed out. Use another recovery code to start again.", await Detail(invented));
        Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(badCsrf));
    }

    [Fact]
    public async Task A_recovery_clears_the_wait_a_forgotten_password_had_built_up()
    {
        await using var server = await StartAsync(variables: [("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "100")]);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var browser = new ApiTestClient(server);
        for (var i = 0; i < 7; i++)
        {
            using var wrong = await browser.LoginAsync(password: "wrong-password-here");
            Assert.True(wrong.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests);
        }

        using var waiting = await browser.LoginAsync(password: NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        using var spent = await SpendAsync(browser, codes[0]);
        using var chosen = await ChoosePasswordAsync(browser, (await Json(spent))["recovery_token"]!.GetValue<string>(), NewPassword);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);

        using var signedIn = await browser.LoginAsync(password: NewPassword);

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task Every_alert_that_is_set_up_hears_of_the_recovery_and_is_not_told_the_code()
    {
        var alerts = new RecordingAlerts();
        await using var server = await StartAsync(alerts: alerts);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        using var created = await admin.PostAsync(
            "/api/v1/suite/notification-channels",
            new { csrf_token = await admin.CsrfAsync(), label = "Chat", provider = "webhook", url = "https://hooks.example.com/chat", events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var spent = await SpendAsync(new ApiTestClient(server), codes[0]);
        Assert.Equal(HttpStatusCode.OK, spent.StatusCode);
        await server.Services.GetRequiredService<Weir.Infrastructure.Http.NotificationDispatcher>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var post = Assert.Single(alerts.Posts);
        Assert.Equal("https://hooks.example.com/chat", post.Url);
        Assert.Contains("\"event\": \"auth_sign_in_recovered\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("Sign-in recovered with a recovery code", post.Body, StringComparison.Ordinal);
        Assert.Contains("9 recovery codes left", post.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(codes[0][6..], post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_alert_set_up_a_recovery_sends_nothing()
    {
        var alerts = new RecordingAlerts();
        await using var server = await StartAsync(alerts: alerts);
        await TestDatabase.SeedAdminAsync(server);
        var codes = await SeedCodesAsync(server);

        using var spent = await SpendAsync(new ApiTestClient(server), codes[0]);
        await server.Services.GetRequiredService<Weir.Infrastructure.Http.NotificationDispatcher>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, spent.StatusCode);
        Assert.Empty(alerts.Posts);
    }
}
