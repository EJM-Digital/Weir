using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using static Weir.Api.Tests.Platform.ApiTestClient;
using static Weir.Api.Tests.Platform.RecoverySupport;

namespace Weir.Api.Tests.Platform;

/// <summary>The signed-in side of recovery codes: how many are left, a new set that needs the password, saying it was saved, and the set setup shows.</summary>
public sealed class AuthRecoveryCodesApiTests
{
    private const string Codes = "/api/v1/auth/recovery-codes";
    private const string Confirm = "/api/v1/auth/recovery-codes/confirm";

    private static async Task<ApiTestClient> SignedInAsync(WeirTestServer server)
    {
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return client;
    }

    private static async Task<HttpResponseMessage> NewSetAsync(ApiTestClient client, string password = AdminPassword) =>
        await client.PostAsync(Codes, new { current_password = password, csrf_token = await client.CsrfAsync() });

    [Fact]
    public async Task Every_recovery_code_route_needs_a_signed_in_user()
    {
        await using var server = await StartAsync();
        var anonymous = new ApiTestClient(server);

        using var status = await anonymous.GetAsync(Codes);
        using var made = await anonymous.PostAsync(Codes, new { current_password = AdminPassword, csrf_token = await anonymous.CsrfAsync() });
        using var confirmed = await anonymous.PostAsync(Confirm, new { csrf_token = await anonymous.CsrfAsync() });

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized], [status.StatusCode, made.StatusCode, confirmed.StatusCode]);
    }

    [Fact]
    public async Task An_account_that_has_no_codes_says_so()
    {
        await using var server = await StartAsync();
        var client = await SignedInAsync(server);

        using var status = await client.GetAsync(Codes);

        Assert.Equal("{\"remaining\":0,\"confirmed\":false}", await status.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_set_needs_the_current_password_and_the_confirmation_token()
    {
        await using var server = await StartAsync();
        var client = await SignedInAsync(server);

        using var wrong = await NewSetAsync(client, "not-the-password");
        using var badToken = await client.PostAsync(Codes, new { current_password = AdminPassword, csrf_token = "not-a-token" });
        using var extra = await client.PostAsync(Codes, new { current_password = AdminPassword, csrf_token = await client.CsrfAsync(), unexpected = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("Current password is not correct.", await Detail(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(badToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes"));
    }

    [Fact]
    public async Task A_wrong_password_for_a_new_set_counts_against_the_account_and_is_limited()
    {
        await using var server = await StartAsync(variables: [("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "100")]);
        var client = await SignedInAsync(server);
        for (var i = 0; i < Weir.Core.Auth.UsernameLoginBackoff.FreeAttempts + 1; i++)
        {
            using var wrong = await NewSetAsync(client, "not-the-password");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var waiting = await NewSetAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        Assert.Equal("Too many wrong passwords for this account. Wait a few minutes, then try again.", await Detail(waiting));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes"));
    }

    [Fact]
    public async Task A_new_set_is_shown_once_kept_only_as_hashes_and_voids_the_set_before_it()
    {
        var logs = new CapturingLoggerFactory();
        await using var server = await StartAsync(logs);
        var client = await SignedInAsync(server);

        using var first = await NewSetAsync(client);
        var firstCodes = (await Json(first))["codes"]!.AsArray().Select(code => code!.GetValue<string>()).ToList();
        using var second = await NewSetAsync(client);
        var made = await Json(second);
        var secondCodes = made["codes"]!.AsArray().Select(code => code!.GetValue<string>()).ToList();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("no-store, private", Header(second, "Cache-Control"));
        Assert.Equal(RecoveryCodeRules.SetSize, secondCodes.Distinct().Count());
        Assert.Equal(RecoveryCodeRules.SetSize, made["remaining"]!.GetValue<int>());
        Assert.False(made["confirmed"]!.GetValue<bool>());
        Assert.Equal(RecoveryCodeRules.SetSize, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE code_hash LIKE '$argon2id$%'"));
        foreach (var code in firstCodes.Concat(secondCodes))
        {
            Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE instr(code_hash, $c) > 0 OR instr(lookup, $c) > 0", ("$c", code[6..])));
            Assert.DoesNotContain(code[6..], Everything(logs), StringComparison.Ordinal);
        }

        var browser = new ApiTestClient(server);
        using var oldCode = await SpendAsync(browser, firstCodes[0]);
        using var newCode = await SpendAsync(browser, secondCodes[0]);
        Assert.Equal(HttpStatusCode.BadRequest, oldCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newCode.StatusCode);
    }

    [Fact]
    public async Task Saying_the_set_was_saved_is_remembered_until_a_newer_set_is_made()
    {
        await using var server = await StartAsync();
        var client = await SignedInAsync(server);
        using var made = await NewSetAsync(client);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);

        using var before = await client.GetAsync(Codes);
        using var confirmed = await client.PostAsync(Confirm, new { csrf_token = await client.CsrfAsync() });
        using var after = await client.GetAsync(Codes);
        using var badToken = await client.PostAsync(Confirm, new { csrf_token = "not-a-token" });
        using var newer = await NewSetAsync(client);
        using var afterNewer = await client.GetAsync(Codes);

        Assert.Equal("{\"remaining\":10,\"confirmed\":false}", await before.Content.ReadAsStringAsync());
        var saved = await Json(confirmed);
        Assert.True(saved["confirmed"]!.GetValue<bool>());
        Assert.Equal("Thanks. Your recovery codes are marked as saved.", saved["message"]!.GetValue<string>());
        Assert.Equal("{\"remaining\":10,\"confirmed\":true}", await after.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal("{\"remaining\":10,\"confirmed\":false}", await afterNewer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task First_run_setup_shows_ten_codes_once_signs_the_owner_in_and_logs_none_of_them()
    {
        var logs = new CapturingLoggerFactory();
        await using var server = await StartAsync(logs);
        var client = new ApiTestClient(server);

        using var created = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "first-owner-pass-min8", csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await Json(created);
        var codes = body["recovery_codes"]!.AsArray().Select(code => code!.GetValue<string>()).ToList();
        Assert.Equal(RecoveryCodeRules.SetSize, codes.Distinct().Count());
        Assert.All(codes, code => Assert.True(RecoveryCodeRules.TryParse(code, out _, out _)));
        Assert.Equal(RecoveryCodeRules.SetSize, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes WHERE confirmed_at IS NULL AND used_at IS NULL"));
        using var status = await client.GetAsync(Codes);
        Assert.Equal("{\"remaining\":10,\"confirmed\":false}", await status.Content.ReadAsStringAsync());
        Assert.DoesNotContain(codes[0][6..], Everything(logs), StringComparison.Ordinal);
        Assert.False(LoggedAtOrAbove(logs, LogLevel.Warning, "recovery"));
    }

    [Fact]
    public async Task A_setup_that_is_refused_makes_no_codes()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        await TestDatabase.ExecuteAsync(server, "UPDATE users SET is_active = 0");
        var client = new ApiTestClient(server);

        using var refused = await client.PostAsync("/api/v1/auth/bootstrap", new { username = "owner1", password = "first-owner-pass-min8", csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM recovery_codes"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users"));
    }
}
