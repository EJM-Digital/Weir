using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The kept-for-older-clients "test the metadata service" route, against the service's recorded health reply, one that is changed to say it is unwell and one nothing answers for.</summary>
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

    private static ScriptedManager GatewayAnswering(string healthBody) => new ScriptedManager().Json(HttpMethod.Get, "/health", healthBody);

    [Fact]
    public async Task The_test_says_the_metadata_service_was_reached_when_it_answers_its_recorded_health_reply()
    {
        var (server, client) = await StartAsync(GatewayAnswering(ArrRecording.Load("metadata-gateway", "health").Only.Reply.Text));
        await using var _ = server;

        using var response = await client.PostAsync("/api/v1/processing/metadata-provider/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(("matched", "Weir reached Deluno's metadata service."), (body["status"]!.GetValue<string>(), body["detail"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_test_says_the_metadata_service_could_not_be_reached_when_its_health_reply_is_not_ok()
    {
        // The service cannot be made unwell on demand, so the one member that says it is well, the status in the recorded health reply, is changed.
        var recorded = ArrRecording.Load("metadata-gateway", "health").Only.Reply.Body!.DeepClone();
        recorded["status"] = "starting";
        var (server, client) = await StartAsync(GatewayAnswering(recorded.ToJsonString()));
        await using var _ = server;

        using var response = await client.PostAsync("/api/v1/processing/metadata-provider/test", new { csrf_token = await client.CsrfAsync() });

        var body = await Json(response);
        Assert.Equal(("unreachable", "Weir could not reach Deluno's metadata service just now."), (body["status"]!.GetValue<string>(), body["detail"]!.GetValue<string>()));
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
