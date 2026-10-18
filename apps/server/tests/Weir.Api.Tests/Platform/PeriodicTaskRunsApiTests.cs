using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Core.Jobs;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The timers the server runs on the real server, called by hand so no test waits for an hour to pass: what each one does to the real
/// database and files, and what it leaves alone.
/// </summary>
public sealed class PeriodicTaskRunsApiTests
{
    private static T TaskOf<T>(WeirTestServer server)
        where T : IPeriodicTask => server.Services.GetServices<IPeriodicTask>().OfType<T>().Single();

    [Fact]
    public async Task The_sign_in_clean_up_removes_signed_out_and_expired_sign_ins_and_keeps_the_live_one()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var signedOut = new ApiTestClient(server);
        var expired = new ApiTestClient(server);
        var live = new ApiTestClient(server);
        await signedOut.SignInAsync();
        await expired.SignInAsync();
        await live.SignInAsync();
        using (await signedOut.PostAsync("/api/v1/auth/logout", new { csrf_token = await signedOut.CsrfAsync() }))
        {
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE user_sessions SET absolute_expires_at = '2000-01-01 00:00:00.000000' WHERE rowid = 2");

        await TaskOf<SessionCleanupTask>(server).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM user_sessions"));
        using var me = await live.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    private static string OldLogLine(string text) =>
        $$"""{"timestamp": "{{DateTimeOffset.UtcNow.AddDays(-3).ToString("O", CultureInfo.InvariantCulture)}}", "level": "INFO", "logger": "Weir.Tests", "message": "{{text}}"}""";

    private static string LogText(WeirTestServer server)
    {
        using var stream = new FileStream(Path.Join(server.Home, "logs", "weir.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task The_log_trim_prunes_to_the_days_saved_and_does_it_at_most_once_a_day()
    {
        await using var server = await WeirTestServer.StartAsync(
            prepareHome: home =>
            {
                Directory.CreateDirectory(Path.Join(home, "logs"));
                File.WriteAllText(Path.Join(home, "logs", "weir.log"), OldLogLine("three days old") + "\n");
            });
        var trim = TaskOf<LogRetentionTask>(server);
        var database = server.Services.GetRequiredService<SqliteDatabase>();
        Assert.Equal(30, await trim.ReadKeepDaysAsync(database));
        Assert.Contains("three days old", LogText(server), StringComparison.Ordinal);
        await TestDatabase.ExecuteAsync(server, "UPDATE suite_settings SET log_retention_days = 1");
        Assert.Equal(1, await trim.ReadKeepDaysAsync(database));
        var tomorrowAndMore = DateTimeOffset.UtcNow.AddDays(30);

        Assert.Equal(1, await trim.TickAsync(tomorrowAndMore, CancellationToken.None));
        Assert.DoesNotContain("three days old", LogText(server), StringComparison.Ordinal);

        Assert.Equal(0, await trim.TickAsync(tomorrowAndMore.AddHours(23), CancellationToken.None));
        Assert.Equal(1, await trim.TickAsync(tomorrowAndMore.AddHours(25), CancellationToken.None));
    }

    [Fact]
    public async Task The_System_overview_timer_reads_nothing_while_no_browser_is_watching_and_reads_once_one_is()
    {
        await using var server = await StartServerAsync();
        var timer = server.Services.GetServices<IPeriodicTask>().Single(task => task.Name == "system-overview");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var browsers = server.Services.GetRequiredService<ActivityStreamClients>();

        // A reading begins by waiting for its turn on the token, so one that was asked for ends with the token's cancellation.
        await timer.RunOnceAsync(cancelled.Token);
        using (browsers.Open())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timer.RunOnceAsync(cancelled.Token));
        }

        Assert.Equal(0, browsers.Count);
    }

    [Fact]
    public async Task The_System_overview_timer_sends_a_change_to_an_open_stream_when_a_browser_is_watching()
    {
        await using var server = await StartServerAsync();
        var timer = server.Services.GetServices<IPeriodicTask>().Single(task => task.Name == "system-overview");
        var frames = server.Services.GetRequiredService<SystemOverviewFrames>();
        using var stop = new CancellationTokenSource();
        await using var stream = frames.ForAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await stream.MoveNextAsync());
        Assert.Contains("\"browsers_live\":0", stream.Current, StringComparison.Ordinal);
        var next = stream.MoveNextAsync().AsTask();

        using (server.Services.GetRequiredService<ActivityStreamClients>().Open())
        {
            await timer.RunOnceAsync(stop.Token);

            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("\"browsers_live\":1", stream.Current, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_job_kind_the_server_runs_belongs_to_the_lane_that_is_meant_to_take_it()
    {
        await using var server = await StartServerAsync();
        var kinds = server.Services.GetRequiredService<JobHandlerRegistry>().JobKinds;

        var upkeep = kinds.Where(kind => Weir.Infrastructure.Jobs.WorkLanes.LaneOf(kind) == WorkLane.Upkeep).Order(StringComparer.Ordinal).ToArray();
        var files = kinds.Where(kind => Weir.Infrastructure.Jobs.WorkLanes.LaneOf(kind) == WorkLane.Files).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "processing.downloaded_scan_retry.v1",
                "processing.library.scan.v1",
                "processing.unclaimed_handback_cleanup.v1",
                "processing.watched_folder.remux_scan_dispatch.v1",
                "processing.work_temp_stale_sweep.v1",
            ],
            upkeep);
        Assert.Equal(
            [
                "processing.file.pass_through.v1",
                "processing.file.reject.v1",
                "processing.file.remux_pass.v1",
                "processing.library.clean.v1",
            ],
            files);
    }
}
