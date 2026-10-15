using System.Net;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Checks that a real server reaches the fakes the way the scenarios rely on: a manager on 127.0.0.1, the fake tools by folder.</summary>
[ContractArea("harness")]
public sealed class FakesWithServerTests
{
    [Fact]
    public async Task A_server_connects_to_a_fake_manager_and_sends_its_api_key()
    {
        using var deluno = FakeManager.StartDeluno();
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        var created = await admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections", new JsonObject
        {
            ["kind"] = "deluno",
            ["name"] = "Contract Deluno",
            ["base_url"] = deluno.BaseUrl,
            ["api_key"] = deluno.ApiKey,
        });

        Assert.True(created.Status is HttpStatusCode.OK or HttpStatusCode.Created, created.ToString());
        Assert.Equal(deluno.BaseUrl, (string)created.Fields["base_url"]!);
        // The server also checks every manager once a minute; asking for the check now keeps the test from waiting for the next one.
        var tested = await admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections/{(long)created.Fields["id"]!}/test");
        Assert.True(tested.Status == HttpStatusCode.OK, tested.ToString());
        var probe = await deluno.WaitForRequestAsync("GET", "/api/integrations/external/health");
        Assert.Equal(FakeManager.DefaultApiKey, probe[0].Header("X-Api-Key"));
    }

    [Fact]
    public async Task A_server_started_with_the_fake_tools_folder_asks_the_fake_ffmpeg_what_hardware_it_offers()
    {
        using var tools = FakeFfmpeg.Install();
        await using var server = await WeirServer.StartNewAsync(tools.Env);
        using var admin = await server.CreateAdminClientAsync();

        var hardware = await HardwareReport.OnceFfmpegAnswersAsync(admin);

        Assert.True((bool)hardware.Fields["detected"]!, hardware.ToString());
        Assert.Contains(tools.Calls(tool: "ffmpeg"), call => call.Arguments.Contains("-hwaccels"));
    }
}
