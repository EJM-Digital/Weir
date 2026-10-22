using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>The query string of <c>GET /system/log</c>: every filter is accepted when it is valid and refused by name when it is not.</summary>
public sealed class SystemLogQueryApiTests
{
    private const string Log = "/api/v1/system/log";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Theory]
    [InlineData("workflow=1&job=2")]
    [InlineData("q=scanned&event_type=library.scan_completed")]
    [InlineData("has_exception=true&limit=5")]
    [InlineData("from=2026-05-09T00:00:00Z&to=2026-05-10T00:00:00Z")]
    [InlineData("sort=time&direction=asc")]
    public async Task A_valid_filter_is_accepted(string query)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"{Log}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("workflow=0", "workflow")]
    [InlineData("job=abc", "job")]
    [InlineData("has_exception=maybe", "has_exception")]
    [InlineData("from=yesterday", "from")]
    [InlineData("to=never", "to")]
    [InlineData("limit=0", "limit")]
    [InlineData("sort=sideways", "sort")]
    [InlineData("direction=sideways", "direction")]
    [InlineData("level=loud", "level")]
    [InlineData("category=nothing", "category")]
    [InlineData("status=nothing", "status")]
    [InlineData("result=nothing", "result")]
    [InlineData("trigger=nothing", "trigger")]
    public async Task A_filter_that_is_not_valid_is_refused_by_name(string query, string name)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"{Log}?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(name, await response.Content.ReadAsStringAsync());
    }
}
