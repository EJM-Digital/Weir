using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The Performance and Cleanup settings at their edges: a save that names no setting, the two settings no other test saves, and
/// the "run now" buttons for each cleanup job.
/// </summary>
public sealed class OperatorSettingsEdgesApiTests
{
    private const string Settings = "/api/v1/processing/operator-settings";
    private const string RunNow = "/api/v1/processing/maintenance/run";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static Task<long> ManualRunsAsync(WeirTestServer server, string jobKind, string scope) =>
        TestDatabase.ScalarAsync(
            server,
            "SELECT count(*) FROM jobs WHERE job_kind = $kind AND payload_json LIKE $payload",
            ("$kind", jobKind), ("$payload", $"%\"media_scope\":\"{scope}\"%\"trigger\":\"manual\"%"));

    [Fact]
    public async Task Who_may_save_settings_and_who_may_run_a_cleanup_now()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Put, Settings, Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, RunNow, Callers.Operator);
    }

    [Fact]
    public async Task A_save_that_names_no_setting_is_refused_and_changes_nothing()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PutAsync(Settings, new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("No operator settings fields to update.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeping_failed_work_files_and_the_days_a_file_log_is_kept_save_and_read_back()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var keep = await client.PutAsync(Settings, new { csrf_token = await client.CsrfAsync(), keep_failed_work_files = true });
        using var days = await client.PutAsync(Settings, new { csrf_token = await client.CsrfAsync(), file_log_retention_days = 45 });
        using var read = await client.GetAsync(Settings);

        Assert.All([keep, days, read], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var saved = await Json(read);
        Assert.True(saved["keep_failed_work_files"]!.GetValue<bool>());
        Assert.Equal(45, saved["file_log_retention_days"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("work_temp_stale_sweep", "movie", "processing.work_temp_stale_sweep.v1", "Queued a work file sweep for Movies. It runs as soon as a worker is free.")]
    [InlineData("work_temp_stale_sweep", "tv", "processing.work_temp_stale_sweep.v1", "Queued a work file sweep for TV. It runs as soon as a worker is free.")]
    [InlineData("unclaimed_handbacks", "movie", "processing.unclaimed_handback_cleanup.v1", "Queued the unclaimed hand-back cleanup for Movies. It runs as soon as a worker is free.")]
    [InlineData("unclaimed_handbacks", "tv", "processing.unclaimed_handback_cleanup.v1", "Queued the unclaimed hand-back cleanup for TV. It runs as soon as a worker is free.")]
    public async Task Running_a_cleanup_now_queues_that_job_for_that_kind_of_library(string family, string scope, string jobKind, string detail)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = $kind", ("$kind", jobKind));

        using var response = await client.PostAsync(RunNow, new { csrf_token = await client.CsrfAsync(), family, media_scope = scope });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.True(body["queued"]!.GetValue<bool>());
        Assert.Equal(detail, body["detail"]!.GetValue<string>());
        Assert.Equal(1, await ManualRunsAsync(server, jobKind, scope));
    }

    [Fact]
    public async Task Running_a_cleanup_now_with_a_token_that_is_not_the_sessions_queues_nothing()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(RunNow, new { csrf_token = "not-a-token", family = "work_temp_stale_sweep" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE payload_json LIKE '%\"trigger\":\"manual\"%'"));
    }
}
