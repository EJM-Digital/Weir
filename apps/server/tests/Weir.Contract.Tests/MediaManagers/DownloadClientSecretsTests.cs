using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// A download client's password or API key is write-only: an update that leaves it out keeps what is saved, an empty one clears it
/// (except where the client cannot work without it), and a new one replaces it. Each is proved against a fake client of the kind
/// that refuses any other secret, so "kept" means the next sign-in still gets in.
/// </summary>
[ContractArea("media_managers")]
public sealed class DownloadClientSecretsTests(DownloadClientServerFixture fixture) : IClassFixture<DownloadClientServerFixture>
{
    private const string Secret = "stored-secret-1f9c";
    private const string NewSecret = "replacement-secret-77b2";

    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await fixture.Server.CreateAdminClientAsync();
        await DownloadClients.ClearAsync(admin);
        return admin;
    }

    private static async Task<JsonObject> ReadAsync(WeirClient admin, JsonObject connection) =>
        (await admin.GetAsync($"{DownloadClients.Route}/{JsonFields.Id(connection)}")).Fields;

    [Theory]
    [MemberData(nameof(DownloadClients.KindNames), MemberType = typeof(DownloadClients))]
    public async Task An_update_of_the_address_alone_keeps_the_secret_so_the_next_test_still_signs_in(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();
        using var first = DownloadClients.StartFake(kind, Secret);
        using var moved = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, first, Secret);
        Assert.True((bool)(await DownloadClients.TestAsync(admin, connection))["ok"]!);

        var updated = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["base_url"] = moved.BaseUrl });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        DownloadClients.AssertNoSecret(updated, Secret);
        Assert.Equal(moved.BaseUrl, (string)updated.Fields["base_url"]!);
        Assert.True((bool)updated.Fields[kind.SavedFlag]!);
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, Secret, Accepted: true), moved.Attempts[^1]);
        DownloadClients.AssertNoSecret(await admin.GetAsync($"{DownloadClients.Route}/{JsonFields.Id(connection)}"), Secret);
        DownloadClients.AssertNoSecret(await admin.GetAsync(DownloadClients.Route), Secret);
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNamesWithoutRequiredSecret), MemberType = typeof(DownloadClients))]
    public async Task An_empty_secret_clears_it_so_the_next_test_is_refused(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, Secret);
        Assert.True((bool)(await DownloadClients.TestAsync(admin, connection))["ok"]!);

        var updated = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { [kind.SecretField] = string.Empty });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        Assert.False((bool)updated.Fields[kind.SavedFlag]!);
        Assert.Equal(kind.Username, (string?)updated.Fields["username"]);
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.False((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, string.Empty, Accepted: false), client.Attempts[^1]);
        Assert.False((bool)(await ReadAsync(admin, connection))[kind.SavedFlag]!);
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNamesWithRequiredSecret), MemberType = typeof(DownloadClients))]
    public async Task An_empty_secret_is_refused_where_the_client_cannot_work_without_one_and_the_saved_one_stays(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, Secret);

        var refused = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { [kind.SecretField] = string.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal(kind.RequiredMessage, (string)refused.Fields["detail"]!);
        Assert.True((bool)(await ReadAsync(admin, connection))[kind.SavedFlag]!);
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, Secret, Accepted: true), client.Attempts[^1]);
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNames), MemberType = typeof(DownloadClients))]
    public async Task A_new_secret_replaces_the_old_one_and_the_next_test_signs_in_with_it(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, Secret);
        client.Require(kind.Username, NewSecret);
        var beforeChange = await DownloadClients.TestAsync(admin, connection);
        Assert.False((bool)beforeChange["ok"]!, beforeChange.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, Secret, Accepted: false), client.Attempts[^1]);

        var updated = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { [kind.SecretField] = NewSecret });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        DownloadClients.AssertNoSecret(updated, Secret, NewSecret);
        Assert.True((bool)updated.Fields[kind.SavedFlag]!);
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, NewSecret, Accepted: true), client.Attempts[^1]);
        DownloadClients.AssertNoSecret(await admin.GetAsync(DownloadClients.Route), Secret, NewSecret);
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNames), MemberType = typeof(DownloadClients))]
    public async Task A_secret_sent_as_null_is_left_alone(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, Secret);

        var updated = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { [kind.SecretField] = null, ["nickname"] = "Garage" });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        Assert.True((bool)updated.Fields[kind.SavedFlag]!);
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(kind.Username, Secret, Accepted: true), client.Attempts[^1]);
    }
}
