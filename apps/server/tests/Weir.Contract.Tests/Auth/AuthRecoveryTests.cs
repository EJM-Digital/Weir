using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.MediaManagers;
using static Weir.Contract.Tests.Auth.AuthSupport;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// Recovery codes against a running server: made at first-run setup, the way back in with one, what a recovery ends and what it leaves
/// alone, and the limits on wrong codes.
/// </summary>
[ContractArea("auth")]
public sealed class AuthRecoveryTests
{
    private const string Api = WeirClient.Api;
    private const string NewPassword = "a-brand-new-strong-password";

    private static async Task<(WeirClient Owner, List<string> Codes)> SetUpAsync(WeirServer server)
    {
        var owner = server.CreateClient();
        var created = await owner.BootstrapAsync();
        AssertStatus(HttpStatusCode.OK, created);
        var codes = created.Fields["recovery_codes"]!.AsArray().Select(code => (string)code!).ToList();
        Assert.Equal(10, codes.Distinct().Count());
        return (owner, codes);
    }

    private static Task<WeirResponse> SpendAsync(WeirClient client, string code) =>
        client.PostWithCsrfAsync($"{Api}/auth/recovery/code", new JsonObject { ["code"] = code });

    private static Task<WeirResponse> ChooseAsync(WeirClient client, string token, string password) =>
        client.PostWithCsrfAsync($"{Api}/auth/recovery/password", new JsonObject { ["recovery_token"] = token, ["new_password"] = password });

    [Fact]
    public async Task Setup_shows_ten_codes_and_one_of_them_gets_the_owner_back_in_with_a_new_password()
    {
        await using var server = await WeirServer.StartNewAsync();
        var (owner, codes) = await SetUpAsync(server);
        AssertStatus(HttpStatusCode.NoContent, await owner.LogoutWithHeaderAsync());
        using var forgotten = server.CreateClient();

        var spent = await SpendAsync(forgotten, codes[3]);

        AssertStatus(HttpStatusCode.OK, spent);
        Assert.Equal(WeirClient.AdminUsername, (string)spent.Fields["username"]!);
        Assert.Equal(9, (int)spent.Fields["codes_left"]!);
        var chosen = await ChooseAsync(forgotten, (string)spent.Fields["recovery_token"]!, NewPassword);
        AssertStatus(HttpStatusCode.OK, chosen);
        AssertStatus(HttpStatusCode.Unauthorized, await forgotten.AttemptLoginAsync());
        await forgotten.LoginAsync(WeirClient.AdminUsername, NewPassword);
        var status = await forgotten.GetAsync($"{Api}/auth/recovery-codes");
        Assert.Equal(9, (int)status.Fields["remaining"]!);
        AssertStatus(HttpStatusCode.BadRequest, await SpendAsync(server.CreateClient(), codes[3]));
    }

    [Fact]
    public async Task A_recovery_ends_every_session_and_leaves_the_media_managers_key_working()
    {
        await using var server = await WeirServer.StartNewAsync(Handoffs.SecretEnvironment);
        var (owner, codes) = await SetUpAsync(server);
        using var manager = server.CreateClient();
        var capabilities = $"{Api}/intake/capabilities";
        AssertStatus(HttpStatusCode.OK, await manager.GetAsync(capabilities, Handoffs.Secret));
        AssertStatus(HttpStatusCode.OK, await owner.GetAsync($"{Api}/auth/me"));

        AssertStatus(HttpStatusCode.OK, await SpendAsync(server.CreateClient(), codes[0]));

        AssertStatus(HttpStatusCode.Unauthorized, await owner.GetAsync($"{Api}/auth/me"));
        AssertStatus(HttpStatusCode.OK, await manager.GetAsync(capabilities, Handoffs.Secret));
        await using var database = await server.StopForDatabaseAsync(restart: false);
        Assert.Equal(0L, SeedSql.Scalar(database.Connection, "SELECT count(*) FROM user_sessions"));
    }

    [Fact]
    public async Task A_recovery_is_written_to_activity_with_the_codes_left_and_no_code_is_in_the_log()
    {
        await using var server = await WeirServer.StartNewAsync();
        var (_, codes) = await SetUpAsync(server);
        AssertStatus(HttpStatusCode.OK, await SpendAsync(server.CreateClient(), codes[0]));
        AssertStatus(HttpStatusCode.OK, await SpendAsync(server.CreateClient(), codes[1]));

        List<Dictionary<string, object?>> recovered;
        await using (var database = await server.StopForDatabaseAsync(restart: false))
        {
            recovered = SeedSql.Rows(database.Connection, "SELECT title, detail FROM activity_events WHERE event_type = 'auth.sign_in_recovered' ORDER BY id");
        }

        Assert.Equal(["alice. 9 of 10 recovery codes left.", "alice. 8 of 10 recovery codes left."], recovered.Select(row => (string)row["detail"]!));
        Assert.All(recovered, row => Assert.Equal("Password reset with a recovery code. Every signed-in session was ended.", (string)row["title"]!));
        var log = server.LogText();
        Assert.All(codes, code => Assert.DoesNotContain(code[6..], log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_set_voids_the_old_codes_and_wrong_codes_are_limited_like_sign_in()
    {
        await using var server = await WeirServer.StartNewAsync();
        var (owner, codes) = await SetUpAsync(server);
        var made = await owner.PostWithCsrfAsync($"{Api}/auth/recovery-codes", new JsonObject { ["current_password"] = WeirClient.AdminPassword });
        AssertStatus(HttpStatusCode.OK, made);
        Assert.Equal(10, made.Fields["codes"]!.AsArray().Count);
        using var stranger = server.CreateClient();
        AssertStatus(HttpStatusCode.BadRequest, await SpendAsync(stranger, codes[0]));

        HttpStatusCode last = default;
        for (var i = 0; i < 12 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = (await SpendAsync(stranger, "AAAAA-AAAAA-AAAAA-AAAAA")).Status;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
