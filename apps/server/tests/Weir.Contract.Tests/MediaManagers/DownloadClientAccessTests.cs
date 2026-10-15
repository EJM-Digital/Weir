using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Changing or removing a download client connection needs an admin who proves the request is theirs: nobody signed in, a viewer, a
/// missing or wrong CSRF token, an unknown connection, a field Weir does not have and a value that is too long are each refused,
/// and the connection, with the secret it holds, is left exactly as it was.
/// </summary>
[ContractArea("media_managers")]
public sealed class DownloadClientAccessTests(DownloadClientServerFixture fixture) : IClassFixture<DownloadClientServerFixture>
{
    private const string Secret = "stored-secret-4d1e";
    private const string Missing = $"{DownloadClients.Route}/999999";

    private static readonly DownloadClientKind Kind = DownloadClients.Kind("qbittorrent");

    /// <summary>A connection to a fake qBittorrent that wants <see cref="Secret"/>, with the admin who made it.</summary>
    private async Task<(WeirClient Admin, FakeDownloadClient Client, JsonObject Connection)> ConnectionAsync()
    {
        var admin = await fixture.Server.CreateAdminClientAsync();
        await DownloadClients.ClearAsync(admin);
        var client = DownloadClients.StartFake(Kind, Secret);
        return (admin, client, await DownloadClients.CreateAsync(admin, Kind, client, Secret));
    }

    private static async Task AssertUntouchedAsync(WeirClient admin, FakeDownloadClient client, JsonObject connection)
    {
        Assert.Equal(connection.ToJsonString(), Assert.Single((await admin.GetAsync(DownloadClients.Route)).Elements)!.ToJsonString());
        var tested = await DownloadClients.TestAsync(admin, connection);
        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal(new SignInAttempt(Kind.Username, Secret, Accepted: true), client.Attempts[^1]);
    }

    private static JsonObject Attempt() => new() { ["password"] = string.Empty, ["enabled"] = false, ["nickname"] = "Changed" };

    [Fact]
    public async Task Nobody_signed_in_can_neither_change_nor_remove_a_connection()
    {
        var (admin, client, connection) = await ConnectionAsync();
        using var _ = admin;
        using var __ = client;
        using var stranger = fixture.Server.CreateClient();
        var route = $"{DownloadClients.Route}/{JsonFields.Id(connection)}";

        var changed = await stranger.RequestAsync(HttpMethod.Put, route, Attempt());
        var removed = await stranger.RequestAsync(HttpMethod.Delete, route, new JsonObject());

        Assert.Equal(HttpStatusCode.Unauthorized, changed.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.Status);
        await AssertUntouchedAsync(admin, client, connection);
    }

    [Fact]
    public async Task A_viewer_can_neither_change_nor_remove_a_connection()
    {
        var (admin, client, connection) = await ConnectionAsync();
        using var _ = admin;
        using var __ = client;
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);

        var changed = await DownloadClients.UpdateAsync(viewer, connection, Attempt());
        var removed = await DownloadClients.DeleteAsync(viewer, connection);

        Assert.Equal(HttpStatusCode.Forbidden, changed.Status);
        Assert.Equal(HttpStatusCode.Forbidden, removed.Status);
        await AssertUntouchedAsync(admin, client, connection);
    }

    [Fact]
    public async Task A_request_without_a_csrf_token_or_with_a_wrong_one_is_refused()
    {
        var (admin, client, connection) = await ConnectionAsync();
        using var _ = admin;
        using var __ = client;
        var route = $"{DownloadClients.Route}/{JsonFields.Id(connection)}";
        var wrong = new JsonObject { ["csrf_token"] = "not-the-token" };

        var updateWithout = await admin.RequestAsync(HttpMethod.Put, route, Attempt());
        var updateWrong = await DownloadClients.UpdateAsync(admin, connection, JsonFields.Merge(Attempt(), wrong));
        var removeWithout = await admin.RequestAsync(HttpMethod.Delete, route, new JsonObject());
        var removeWrong = await admin.RequestAsync(HttpMethod.Delete, route, wrong);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, updateWithout.Status);
        Assert.Equal(HttpStatusCode.BadRequest, updateWrong.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removeWithout.Status);
        Assert.Equal(HttpStatusCode.BadRequest, removeWrong.Status);
        await AssertUntouchedAsync(admin, client, connection);
    }

    [Fact]
    public async Task A_connection_that_does_not_exist_is_not_found_to_change_or_remove()
    {
        var (admin, client, _) = await ConnectionAsync();
        using var _ = admin;
        using var __ = client;

        var changed = await admin.PutWithCsrfAsync(Missing, Attempt());
        var removed = await admin.DeleteWithCsrfBodyAsync(Missing);
        var notANumber = await admin.PutWithCsrfAsync($"{DownloadClients.Route}/first", Attempt());

        Assert.Equal(HttpStatusCode.NotFound, changed.Status);
        Assert.Equal("That download client connection does not exist.", (string)changed.Fields["detail"]!);
        Assert.Equal(HttpStatusCode.NotFound, removed.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notANumber.Status);
    }

    [Fact]
    public async Task A_field_Weir_does_not_have_or_a_value_that_is_too_long_is_refused_and_nothing_changes()
    {
        var (admin, client, connection) = await ConnectionAsync();
        using var _ = admin;
        using var __ = client;

        var unknown = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["nickname"] = "Changed", ["lanes"] = new JsonObject() });
        var longSecret = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["password"] = new string('x', 2001) });
        var longUser = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["username"] = new string('x', 201) });
        var longAddress = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["base_url"] = "http://192.0.2.50/" + new string('x', 2000) });
        var notABoolean = await DownloadClients.UpdateAsync(admin, connection, new JsonObject { ["enabled"] = "sometimes" });

        foreach (var refused in new[] { unknown, longSecret, longUser, longAddress, notABoolean })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        }

        await AssertUntouchedAsync(admin, client, connection);
    }
}
