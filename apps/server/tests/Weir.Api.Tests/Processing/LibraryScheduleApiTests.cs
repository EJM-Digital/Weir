using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Scheduling;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// The Library screen's "Scheduled scan and clean" switch over HTTP: turning it on asks for the final-removal confirmation when the
/// last scan found tracks to remove, turning it off stops the scans, and the request is refused without an operator's session.
/// </summary>
public sealed class LibraryScheduleApiTests
{
    private const string Folder = "/library/films";

    private static string SchedulePath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-schedule";

    private static string SettingsPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-settings";

    private static async Task<(WeirTestServer Server, ApiTestClient Client, long LibraryId)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Scheduled', 'movie', '/in', '/out', '/work', 9) RETURNING id");
        using var folders = await client.PutAsync(SettingsPath(libraryId), new { library_folders = new[] { Folder }, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, folders.StatusCode);
        return (server, client, libraryId);
    }

    private static async Task<HttpResponseMessage> SetScheduleAsync(ApiTestClient client, long libraryId, bool enabled, bool confirmFinalRemoval = false) =>
        await client.PostAsync(SchedulePath(libraryId), new { enabled, confirm_final_removal = confirmFinalRemoval, csrf_token = await client.CsrfAsync() });

    private static async Task<bool> ScheduleIsOnAsync(WeirTestServer server, long libraryId) =>
        await TestDatabase.ScalarAsync(server, "SELECT library_schedule_enabled FROM libraries WHERE id = $id", ("$id", libraryId)) == 1;

    /// <summary>One look by the timer that queues the scheduled scan, run now instead of waiting for its half-minute turn.</summary>
    private static Task RunScheduleTimerAsync(WeirTestServer server) =>
        server.Services.GetServices<IPeriodicTask>().OfType<LibraryModeScheduleTask>().Single().RunOnceAsync(CancellationToken.None);

    private static Task<long> ScheduledScanCountAsync(WeirTestServer server, long libraryId) =>
        TestDatabase.ScalarAsync(
            server,
            "SELECT count(*) FROM jobs WHERE job_kind = $kind AND dedupe_key LIKE $prefix AND payload_json LIKE $trigger",
            ("$kind", LibraryModeJobKinds.ScanKind),
            ("$prefix", LibraryModeJobKinds.ScanDedupeKeyPrefix(libraryId) + "%"),
            ("$trigger", $"%\"trigger\":\"{LibraryModeSchedule.Trigger}\"%"));

    private static Task SeedFileToCleanAsync(WeirTestServer server, long libraryId) =>
        TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO library_files (library_id, path, size_bytes, mtime, classification, removed_audio_tracks, " +
            "removed_subtitle_tracks, estimated_bytes_saved) VALUES ($library, $path, 1000, 1700000000, 'would_change', 2, 1, 5000)",
            ("$library", libraryId),
            ("$path", Folder + "/film.mkv"));

    [Fact]
    public async Task Turning_the_schedule_on_queues_one_first_scan_however_often_the_timer_looks()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;

        using var turnedOn = await SetScheduleAsync(client, libraryId, enabled: true);

        Assert.Equal(HttpStatusCode.OK, turnedOn.StatusCode);
        var settings = await Json(turnedOn);
        Assert.True(settings["library_schedule_enabled"]!.GetValue<bool>());
        Assert.Equal(Folder, settings["library_folders"]!.AsArray().Single()!.GetValue<string>());
        Assert.True(await ScheduleIsOnAsync(server, libraryId));
        await RunScheduleTimerAsync(server);
        await RunScheduleTimerAsync(server);
        Assert.Equal(1, await ScheduledScanCountAsync(server, libraryId));
    }

    [Fact]
    public async Task Turning_the_schedule_off_stops_the_scans_and_clears_the_next_run()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        using (await SetScheduleAsync(client, libraryId, enabled: true))
        {
        }

        using var turnedOff = await SetScheduleAsync(client, libraryId, enabled: false);

        Assert.Equal(HttpStatusCode.OK, turnedOff.StatusCode);
        Assert.False((await Json(turnedOff))["library_schedule_enabled"]!.GetValue<bool>());
        Assert.False(await ScheduleIsOnAsync(server, libraryId));
        using var overview = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}/library-overview");
        var schedule = (await Json(overview))["schedule"]!;
        Assert.False(schedule["enabled"]!.GetValue<bool>());
        Assert.Null(schedule["next_run_at"]);
        await RunScheduleTimerAsync(server);
        Assert.Equal(0, await ScheduledScanCountAsync(server, libraryId));
    }

    [Fact]
    public async Task Turning_it_off_when_it_is_already_off_changes_nothing_and_asks_for_nothing()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileToCleanAsync(server, libraryId);

        using var response = await SetScheduleAsync(client, libraryId, enabled: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await Json(response))["library_schedule_enabled"]!.GetValue<bool>());
        Assert.False(await ScheduleIsOnAsync(server, libraryId));
    }

    [Fact]
    public async Task Turning_it_on_when_the_last_scan_found_tracks_to_remove_asks_for_the_confirmation_and_stays_off()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileToCleanAsync(server, libraryId);

        using var refused = await SetScheduleAsync(client, libraryId, enabled: true);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await Json(refused);
        Assert.Equal("confirm_final_removal_required", body["error"]!.GetValue<string>());
        Assert.Equal(1, body["files_count"]!.GetValue<int>());
        Assert.Equal(3, body["tracks_count"]!.GetValue<int>());
        Assert.Equal(5000, body["estimated_bytes_saved"]!.GetValue<long>());
        Assert.StartsWith("1 file, 3 tracks will be removed.", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(await ScheduleIsOnAsync(server, libraryId));
        await RunScheduleTimerAsync(server);
        Assert.Equal(0, await ScheduledScanCountAsync(server, libraryId));
    }

    [Fact]
    public async Task Confirming_the_removal_turns_the_schedule_on()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileToCleanAsync(server, libraryId);

        using var confirmed = await SetScheduleAsync(client, libraryId, enabled: true, confirmFinalRemoval: true);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True((await Json(confirmed))["library_schedule_enabled"]!.GetValue<bool>());
        Assert.True(await ScheduleIsOnAsync(server, libraryId));
    }

    [Fact]
    public async Task A_workflow_that_does_not_exist_is_refused_with_not_found()
    {
        var (server, client, _) = await StartAsync();
        await using var __ = server;

        using var response = await SetScheduleAsync(client, 99999, enabled: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No library with that id.", await Detail(response));
    }

    [Fact]
    public async Task A_request_the_screen_would_never_send_is_refused_and_changes_nothing()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var csrf = await client.CsrfAsync();

        using var withoutSwitch = await client.PostAsync(SchedulePath(libraryId), new { csrf_token = csrf });
        using var extraField = await client.PostAsync(SchedulePath(libraryId), new { enabled = true, csrf_token = csrf, run_at = "03:00" });
        using var notABoolean = await client.PostAsync(SchedulePath(libraryId), new { enabled = "maybe", csrf_token = csrf });

        Assert.All([withoutSwitch, extraField, notABoolean], response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.False(await ScheduleIsOnAsync(server, libraryId));
    }

    [Fact]
    public async Task Without_an_operators_session_and_token_the_schedule_cannot_be_changed()
    {
        var (server, _, libraryId) = await StartAsync();
        await using var __ = server;

        await OperatorPostRefusals.AssertAsync(
            server, SchedulePath(libraryId), csrf => new { enabled = true, confirm_final_removal = false, csrf_token = csrf });

        Assert.False(await ScheduleIsOnAsync(server, libraryId));
    }
}
