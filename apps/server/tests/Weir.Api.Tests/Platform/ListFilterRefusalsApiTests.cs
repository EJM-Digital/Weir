using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Every list that can be narrowed turns away a filter value it cannot read and names the filter, and takes a filter left empty
/// as no filter at all.
/// </summary>
public sealed class ListFilterRefusalsApiTests
{
    private const string SystemLog = "/api/v1/system/log";
    private const string Files = "/api/v1/processing/files";
    private const string Cleans = "/api/v1/processing/library-cleans";
    private const string FileLog = "/api/v1/processing/files/1/log";
    private const string OverviewStats = "/api/v1/processing/overview-stats";

    public static TheoryData<string, string, string> Unreadable => new()
    {
        { SystemLog, "workflow", "abc" },
        { SystemLog, "job", "abc" },
        { SystemLog, "q", new string('x', 201) },
        { SystemLog, "event_type", new string('x', 65) },
        { SystemLog, "result", "bogus" },
        { SystemLog, "trigger", "bogus" },
        { SystemLog, "has_exception", "maybe" },
        { SystemLog, "from", "yesterday" },
        { SystemLog, "to", "tomorrow" },
        { SystemLog, "limit", "abc" },
        { SystemLog, "sort", "bogus" },
        { SystemLog, "direction", "sideways" },
        { SystemLog, "cursor", "not-a-position" },
        { SystemLog, "source", "bogus" },
        { SystemLog, "level", "bogus" },
        { SystemLog, "category", "bogus" },
        { SystemLog, "status", "bogus" },
        { Files, "library_id", "abc" },
        { Files, "within_days", "abc" },
        { Files, "limit", "abc" },
        { Cleans, "library_id", "abc" },
        { Cleans, "within_days", "abc" },
        { Cleans, "limit", "abc" },
        { FileLog, "limit", "abc" },
        { OverviewStats, "window_days", "abc" },
    };

    public static TheoryData<string, string> Left => new()
    {
        { SystemLog, "workflow" },
        { SystemLog, "job" },
        { SystemLog, "q" },
        { SystemLog, "event_type" },
        { SystemLog, "result" },
        { SystemLog, "trigger" },
        { SystemLog, "has_exception" },
        { SystemLog, "from" },
        { SystemLog, "to" },
        { SystemLog, "limit" },
        { SystemLog, "cursor" },
    };

    public static TheoryData<string, string, string> Readable => new()
    {
        { SystemLog, "workflow", "1" },
        { SystemLog, "job", "1" },
        { SystemLog, "q", "scan" },
        { SystemLog, "event_type", "job.completed" },
        { SystemLog, "has_exception", "true" },
        { SystemLog, "from", "2026-01-01T00:00:00Z" },
        { SystemLog, "to", "2026-12-31T00:00:00Z" },
        { SystemLog, "limit", "5" },
        { Files, "library_id", "1" },
        { Files, "within_days", "7" },
        { Files, "limit", "5" },
        { Cleans, "library_id", "1" },
        { Cleans, "within_days", "7" },
        { Cleans, "limit", "5" },
        { FileLog, "limit", "5" },
        { OverviewStats, "window_days", "7" },
    };

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task A_filter_value_that_cannot_be_read_is_refused_and_names_the_filter(string path, string filter, string value)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"{path}?{filter}={Uri.EscapeDataString(value)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains($"\"{filter}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Left))]
    public async Task A_filter_left_empty_is_no_filter(string path, string filter)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"{path}?{filter}=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Readable))]
    public async Task A_filter_value_that_can_be_read_is_not_refused(string path, string filter, string value)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"{path}?{filter}={Uri.EscapeDataString(value)}");

        Assert.NotEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, $"{path}?{filter}={value} answered {(int)response.StatusCode}");
    }
}
