using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// "Verbose file-detection records", "Wait after a file last changes", "Skip files smaller than" and the hours window for Movies
/// and for TV are retired: nothing acts on the first, the next two now live on each workflow, and each workflow's weekly
/// schedule decides when it works. The settings do not show them, and an older client that still sends them is answered as
/// though it had not, rather than refused.
/// </summary>
public sealed class RetiredOperatorSettingsApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task Verbose_detection_logging_is_not_a_setting()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var response = await client.GetAsync("/api/v1/processing/operator-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestClient.Json(response)).AsObject().ContainsKey("verbose_detection_logging"));
    }

    [Fact]
    public async Task An_older_client_that_still_sends_it_is_answered_and_nothing_is_saved()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var alone = await client.PutAsync(
            "/api/v1/processing/operator-settings", new { csrf_token = await client.CsrfAsync(), verbose_detection_logging = true });
        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
        Assert.False((await ApiTestClient.Json(alone)).AsObject().ContainsKey("verbose_detection_logging"));

        using var alongside = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new { csrf_token = await client.CsrfAsync(), verbose_detection_logging = true, runner_capacity = 8 });
        Assert.Equal(HttpStatusCode.OK, alongside.StatusCode);
        Assert.Equal(8, (await ApiTestClient.Json(alongside))["runner_capacity"]!.GetValue<int>());

        // The column is still there for configuration backups that carry it, and keeps its default.
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT verbose_detection_logging FROM operator_settings WHERE id = 1"));
    }

    [Fact]
    public async Task The_wait_and_minimum_size_are_not_Performance_settings()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var response = await client.GetAsync("/api/v1/processing/operator-settings");

        var body = (await ApiTestClient.Json(response)).AsObject();
        Assert.False(body.ContainsKey("min_file_age_seconds"));
        Assert.False(body.ContainsKey("min_input_file_size_mb"));
    }

    [Fact]
    public async Task An_older_client_that_still_sends_the_wait_and_minimum_size_is_answered_and_they_are_ignored()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var saved = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new { csrf_token = await client.CsrfAsync(), min_file_age_seconds = 120, min_input_file_size_mb = 200 });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = (await ApiTestClient.Json(saved)).AsObject();
        Assert.False(body.ContainsKey("min_file_age_seconds"));
        Assert.False(body.ContainsKey("min_input_file_size_mb"));
    }

    private static readonly string[] HoursWindowFields =
    [
        "movie_schedule_hours_limited", "movie_schedule_days", "movie_schedule_start", "movie_schedule_end",
        "tv_schedule_hours_limited", "tv_schedule_days", "tv_schedule_start", "tv_schedule_end",
    ];

    [Fact]
    public async Task Movies_and_TV_have_no_hours_window_of_their_own()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var response = await client.GetAsync("/api/v1/processing/operator-settings");

        var body = (await ApiTestClient.Json(response)).AsObject();
        Assert.All(HoursWindowFields, field => Assert.False(body.ContainsKey(field), field));
        Assert.True(body["movie_schedule_enabled"]!.GetValue<bool>());
        Assert.True(body["tv_schedule_enabled"]!.GetValue<bool>());
        Assert.Equal(0, await TestDatabase.ScalarAsync(
            server, "SELECT count(*) FROM pragma_table_info('operator_settings') WHERE name LIKE 'movie_schedule_%' AND name <> 'movie_schedule_enabled'"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(
            server, "SELECT count(*) FROM pragma_table_info('operator_settings') WHERE name LIKE 'tv_schedule_%' AND name <> 'tv_schedule_enabled'"));
    }

    [Fact]
    public async Task An_older_client_sending_the_whole_schedule_group_is_answered_and_only_the_switch_is_kept()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var saved = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new
            {
                csrf_token = await client.CsrfAsync(),
                movie_schedule_enabled = false,
                movie_schedule_hours_limited = true,
                movie_schedule_days = "Mon,Tue",
                movie_schedule_start = "09:00",
                movie_schedule_end = "17:30",
                tv_schedule_enabled = true,
                tv_schedule_hours_limited = true,
                tv_schedule_days = "Wed",
                tv_schedule_start = "08:00",
                tv_schedule_end = "09:30",
            });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = (await ApiTestClient.Json(saved)).AsObject();
        Assert.False(body["movie_schedule_enabled"]!.GetValue<bool>());
        Assert.True(body["tv_schedule_enabled"]!.GetValue<bool>());
        Assert.All(HoursWindowFields, field => Assert.False(body.ContainsKey(field), field));
    }

    [Fact]
    public async Task An_older_client_sending_only_hours_window_fields_is_answered_and_nothing_is_saved()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var before = await ApiTestClient.Json(await client.GetAsync("/api/v1/processing/operator-settings"));

        // Not a whole group, and values that were once refused (a day that is not a day, an hour past 23): none is read.
        using var saved = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new { csrf_token = await client.CsrfAsync(), movie_schedule_hours_limited = true, movie_schedule_days = "Caturday", tv_schedule_start = "25:00" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(before.ToJsonString(), (await ApiTestClient.Json(saved)).ToJsonString());
    }
}
