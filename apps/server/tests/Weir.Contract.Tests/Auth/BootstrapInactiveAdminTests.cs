using System.Net;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Auth.AuthSupport;

namespace Weir.Contract.Tests.Auth;

/// <summary>Setup is open only while no account exists at all: a deactivated admin does not reopen it.</summary>
[ContractArea("auth")]
public sealed class BootstrapInactiveAdminTests
{
    private const string Api = WeirClient.Api;

    [Fact]
    public async Task Active_admin_still_closes_bootstrap()
    {
        await using var server = await SeededServerAsync(active: true);
        using var session = server.CreateClient();

        Assert.False((bool)(await session.GetAsync($"{Api}/auth/bootstrap/status")).Fields["bootstrap_allowed"]!);
        Assert.Equal(HttpStatusCode.OK, (await session.AttemptLoginAsync()).Status);
    }

    [Fact]
    public async Task Inactive_sole_admin_keeps_bootstrap_closed()
    {
        await using var server = await SeededServerAsync(active: false);
        using var session = server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.AttemptLoginAsync()).Status);
        Assert.False((bool)(await session.GetAsync($"{Api}/auth/bootstrap/status")).Fields["bootstrap_allowed"]!);
    }

    /// <summary>Setup cannot be used to replace an account: it is refused and the account is untouched.</summary>
    [Fact]
    public async Task Setup_is_refused_when_an_inactive_account_exists_and_leaves_it_alone()
    {
        await using var server = await SeededServerAsync(active: false);
        using var session = server.CreateClient();

        var refused = await session.BootstrapAsync("alice-again", "recovered-password-strong");

        AssertStatus(HttpStatusCode.Forbidden, refused);
        List<Dictionary<string, object?>> users;
        await using (var database = await server.StopForDatabaseAsync(restart: false))
        {
            users = SeedSql.Rows(database.Connection, "SELECT username, is_active FROM users");
        }

        var user = Assert.Single(users);
        Assert.Equal(WeirClient.AdminUsername, user["username"]);
        Assert.Equal(0L, Convert.ToInt64(user["is_active"], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<WeirServer> SeededServerAsync(bool active)
    {
        var server = await WeirServer.StartNewAsync();
        try
        {
            await using var database = await server.StopForDatabaseAsync();
            SeedSql.InsertUser(database.Connection, WeirClient.AdminUsername, SeededAccounts.AdminPasswordHash, "admin", active);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        return server;
    }
}
