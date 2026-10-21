using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The kept-for-older-clients "test the metadata service" route, against a service that answers, one that is unwell and one nothing answers for.</summary>
public sealed class MetadataProviderTestApiTests
{
    private const string Gateway = "http://gateway.test";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync(ScriptedManager gateway)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_ARTWORK_GATEWAY_URL", Gateway)],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(gateway));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Theory]
    [InlineData("""{"status":"ok"}""", "matched", "Weir reached Deluno's metadata service.")]
    [InlineData("""{"status":"starting"}""", "unreachable", "Weir could not reach Deluno's metadata service just now.")]
    public async Task The_test_says_whether_the_metadata_service_is_well(string health, string status, string detail)
    {
        var (server, client) = await StartAsync(new ScriptedManager().Json(HttpMethod.Get, "/health", health));
        await using var _ = server;

        using var response = await client.PostAsync("/api/v1/processing/metadata-provider/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal((status, detail), (body["status"]!.GetValue<string>(), body["detail"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_test_says_so_when_nothing_answers_for_the_metadata_service()
    {
        var (server, client) = await StartAsync(new ScriptedManager());
        await using var _ = server;

        using var response = await client.PostAsync("/api/v1/processing/metadata-provider/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal("unreachable", (await Json(response))["status"]!.GetValue<string>());
    }
}
