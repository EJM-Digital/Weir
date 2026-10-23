using System.Globalization;
using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>The Library screen can say when "Scheduled scan and clean" next runs, from the overview it already reads.</summary>
public sealed class LibraryModeScheduleApiTests
{
    [Fact]
    public async Task The_overview_says_whether_the_schedule_is_on_and_when_it_next_runs()
    {
        var server = await StartServerAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Scheduled', 'movie', '/in', '/out', '/work', 9) RETURNING id");
        var overview = $"/api/v1/processing/libraries/{libraryId}/library-overview";

        using var off = await client.GetAsync(overview);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var offSchedule = (await Json(off))["schedule"]!;
        Assert.False(offSchedule["enabled"]!.GetValue<bool>());
        Assert.Null(offSchedule["next_run_at"]);

        using var folders = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-settings",
            new { library_folders = new[] { "/library/films" }, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, folders.StatusCode);
        using var turnedOn = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-schedule",
            new { enabled = true, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, turnedOn.StatusCode);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var on = await client.GetAsync(overview);
        var onSchedule = (await Json(on))["schedule"]!;
        Assert.True(onSchedule["enabled"]!.GetValue<bool>());

        // Never run before, it is due straight away (or, if the timer has already started it, a day from now).
        var next = DateTimeOffset.Parse(onSchedule["next_run_at"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(next, before, DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1));
    }

    [Theory]
    [InlineData(-2, false)]
    [InlineData(-72, true)]
    public async Task The_next_run_is_a_day_after_the_last_scheduled_one_and_now_when_that_day_has_passed(int hoursSinceLastRun, bool dueNow)
    {
        var server = await StartServerAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Scheduled', 'movie', '/in', '/out', '/work', 9) RETURNING id");
        using var folders = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-settings",
            new { library_folders = new[] { "/library/films" }, csrf_token = await client.CsrfAsync() });
        using var turnedOn = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-schedule",
            new { enabled = true, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, turnedOn.StatusCode);
        var lastRun = DateTimeOffset.UtcNow.AddHours(hoursSinceLastRun);
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($key, 'processing.library.scan.v1', $payload, 'completed')",
            ("$key", $"processing.library.scan.v1:{libraryId}:{Guid.NewGuid():N}"),
            ("$payload", $"{{\"library_id\": {libraryId}, \"trigger\": \"schedule\", \"scheduled_at\": \"{lastRun:O}\"}}"));
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        using var overview = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}/library-overview");

        var next = DateTimeOffset.Parse((await Json(overview))["schedule"]!["next_run_at"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (dueNow)
        {
            Assert.InRange(next, before, DateTimeOffset.UtcNow.AddMinutes(1));
        }
        else
        {
            Assert.InRange(next, lastRun.AddDays(1).AddMinutes(-1), lastRun.AddDays(1).AddMinutes(1));
        }
    }
}
