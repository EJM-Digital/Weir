using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// <c>GET /processing/libraries</c> reports whether periodic watched-folder scanning currently runs for a library —
/// <c>periodic_scan</c>, one of <see cref="PeriodicScanStates"/> — instead of leaving a client to infer it from a
/// 25-second silence (#747).
/// </summary>
public sealed class LibraryPeriodicScanStatusApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Client, long LibraryId, string Name, string MediaType)> SeededMovieLibraryAsync(
        params (string Name, string Value)[] variables)
    {
        var server = await ApiTestClient.StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var listed = await client.GetAsync("/api/v1/processing/libraries");
        var library = (await ApiTestClient.Json(listed)).AsArray().First(item => item!["media_type"]!.GetValue<string>() == "movie")!;
        return (server, client, library["id"]!.GetValue<long>(), library["name"]!.GetValue<string>(), "movie");
    }

    private static Task<HttpResponseMessage> PutLibraryAsync(ApiTestClient client, long id, object fields) =>
        client.PutAsync($"/api/v1/processing/libraries/{id}", fields);

    [Fact]
    public async Task Periodic_scan_is_off_when_the_global_switch_is_disabled()
    {
        var (server, client, id, _, _) = await SeededMovieLibraryAsync(
            ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", "0"));
        await using var disposeServer = server;

        using var one = await client.GetAsync($"/api/v1/processing/libraries/{id}");
        var body = await ApiTestClient.Json(one);

        Assert.Equal(PeriodicScanStates.Off, body["periodic_scan"]!.GetValue<string>());
        Assert.Null(body["next_scan_at"]);
    }

    [Fact]
    public async Task Periodic_scan_is_off_when_the_librarys_own_switch_is_disabled()
    {
        var (server, client, id, name, mediaType) = await SeededMovieLibraryAsync();
        await using var disposeServer = server;

        using var saved = await PutLibraryAsync(client, id, new
        {
            csrf_token = await client.CsrfAsync(),
            name,
            media_type = mediaType,
            enabled = false,
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);

        using var one = await client.GetAsync($"/api/v1/processing/libraries/{id}");
        var body = await ApiTestClient.Json(one);

        Assert.Equal(PeriodicScanStates.Off, body["periodic_scan"]!.GetValue<string>());
        Assert.Null(body["next_scan_at"]);
    }

    [Fact]
    public async Task Periodic_scan_is_off_when_the_scopes_switch_is_disabled()
    {
        var (server, client, id, _, _) = await SeededMovieLibraryAsync();
        await using var disposeServer = server;

        using var saved = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new
            {
                csrf_token = await client.CsrfAsync(),
                movie_schedule_enabled = false,
            });
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);

        using var one = await client.GetAsync($"/api/v1/processing/libraries/{id}");
        var body = await ApiTestClient.Json(one);

        Assert.Equal(PeriodicScanStates.Off, body["periodic_scan"]!.GetValue<string>());
        Assert.Null(body["next_scan_at"]);
    }

    [Fact]
    public async Task Periodic_scan_is_outside_hours_when_the_librarys_schedule_grid_closes_every_slot()
    {
        var (server, client, id, name, mediaType) = await SeededMovieLibraryAsync();
        await using var disposeServer = server;
        var neverOpen = new string('0', ScheduleGrid.SlotsPerWeek);

        using var saved = await PutLibraryAsync(client, id, new
        {
            csrf_token = await client.CsrfAsync(),
            name,
            media_type = mediaType,
            schedule_enabled = true,
            schedule_grid = neverOpen,
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);

        using var one = await client.GetAsync($"/api/v1/processing/libraries/{id}");
        var body = await ApiTestClient.Json(one);

        Assert.Equal(PeriodicScanStates.OutsideHours, body["periodic_scan"]!.GetValue<string>());
        // A grid with no open slot at all never reopens, so there is nothing to report a time for.
        Assert.Null(body["next_scan_at"]);
    }

    [Fact]
    public async Task Periodic_scan_is_scheduled_and_reports_the_schedulers_recorded_next_look()
    {
        var (server, client, id, _, _) = await SeededMovieLibraryAsync();
        await using var disposeServer = server;

        var wakeups = server.Services.GetRequiredService<ScanWakeups>();
        var next = DateTimeOffset.UtcNow.AddMinutes(5);
        wakeups.RecordNextPeriodic(id, next);

        using var one = await client.GetAsync($"/api/v1/processing/libraries/{id}");
        var body = await ApiTestClient.Json(one);

        Assert.Equal(PeriodicScanStates.Scheduled, body["periodic_scan"]!.GetValue<string>());
        var nextScanAt = DateTimeOffset.Parse(body["next_scan_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(nextScanAt.ToUniversalTime(), next.AddSeconds(-1), next.AddSeconds(1));
    }
}
