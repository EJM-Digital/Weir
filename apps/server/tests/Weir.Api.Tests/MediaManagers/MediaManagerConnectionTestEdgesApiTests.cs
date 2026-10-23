using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// Testing a media manager connection at its edges: a connection with no address is not tried, and the answer says what to do.
/// </summary>
public sealed class MediaManagerConnectionTestEdgesApiTests
{
    [Fact]
    public async Task A_connection_with_no_address_is_not_tried_and_the_result_says_to_add_one()
    {
        var manager = new ScriptedManager();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var id = await TestDatabase.ScalarAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', '') RETURNING id");

        using var response = await client.PostAsync($"/api/v1/media-managers/connections/{id}/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Equal("Add the address where this app can be reached, then test again.", body["detail"]!.GetValue<string>());
        Assert.Empty(manager.Calls);
    }

    [Fact]
    public async Task A_connection_with_an_address_but_no_key_is_tried_without_one()
    {
        var manager = new ScriptedManager();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var id = await TestDatabase.ScalarAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', 'http://192.0.2.30:5000') RETURNING id");

        using var response = await client.PostAsync($"/api/v1/media-managers/connections/{id}/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(manager.Calls);
        Assert.All(manager.Calls, call => Assert.True(string.IsNullOrEmpty(call.ApiKey)));
    }
}
