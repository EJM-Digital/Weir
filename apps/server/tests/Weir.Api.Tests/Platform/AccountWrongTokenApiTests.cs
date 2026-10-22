using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>Changing a user name and signing the other browsers out, with a confirmation token that is not the session's: refused in plain words, nothing changed.</summary>
public sealed class AccountWrongTokenApiTests
{
    private const string ConfirmationExpired = "Your confirmation token expired. Refresh the page and try again.";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task A_new_user_name_with_the_wrong_token_is_refused_and_the_name_stays()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(
            "/api/v1/auth/change-username", new { current_password = AdminPassword, new_username = "someone-else", csrf_token = "not-a-token" });

        Assert.Equal((HttpStatusCode.BadRequest, ConfirmationExpired), (response.StatusCode, await Detail(response)));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM users WHERE username = 'alice'"));
    }

    [Fact]
    public async Task Signing_the_other_browsers_out_with_the_wrong_token_leaves_them_signed_in()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var other = new ApiTestClient(server);
        await other.SignInAsync();

        using var response = await client.PostAsync("/api/v1/auth/sessions/revoke-others", headers: new Dictionary<string, string> { ["X-CSRF-Token"] = "not-a-token" });
        using var withNoToken = await client.PostAsync("/api/v1/auth/sessions/revoke-others");

        Assert.Equal((HttpStatusCode.BadRequest, ConfirmationExpired), (response.StatusCode, await Detail(response)));
        Assert.Equal((HttpStatusCode.BadRequest, ConfirmationExpired), (withNoToken.StatusCode, await Detail(withNoToken)));
        using var stillIn = await other.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
    }
}
