using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Api.Endpoints;
using Weir.Core.Activity;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Tests;
using static Weir.Api.Tests.Platform.LiveStreamApiTests;

namespace Weir.Api.Tests.Platform;

/// <summary>The last edges of the API: numbers larger than a database holds, a log nobody may read, and filters written in every shape.</summary>
public sealed class ApiRemainingEdgesApiTests
{
    [Theory]
    [InlineData("/api/v1/activity/recent?before_id=99999999999999999999&library_id=99999999999999999999")]
    [InlineData("/api/v1/media-managers/connections/99999999999999999999/folder-chain")]
    [InlineData("/api/v1/suite/logs?limit=99999999999999999999")]
    [InlineData("/api/v1/suite/logs?limit=-99999999999999999999")]
    public async Task A_number_larger_than_the_database_holds_is_read_as_the_largest_it_does(string path)
    {
        var (server, client) = await StartSignedInAsync();
        await using var _ = server;

        using var response = await client.GetAsync(path);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("level=")]
    [InlineData("level=info,,error")]
    [InlineData("level=info&level=error")]
    [InlineData("result=&trigger=")]
    [InlineData("result=success&trigger=manual")]
    [InlineData("from=&to=")]
    public async Task A_log_filter_may_be_empty_repeated_or_written_with_commas(string query)
    {
        var (server, client) = await StartSignedInAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"/api/v1/system/log?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_newest_event_stream_ends_quietly_when_it_is_cancelled_between_frames()
    {
        using var cancel = new CancellationTokenSource();
        await using var frames = ActivityEndpoints.LatestFramesAsync(
            _ => Task.FromResult<long?>(3), new ActivityLatestNotifier(), new FakeTimeProvider(), TimeSpan.FromMinutes(1), NullLogger.Instance, cancel.Token).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.True(await frames.MoveNextAsync());

        await cancel.CancelAsync();

        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task A_log_nobody_may_read_is_not_pruned_listed_or_downloaded_and_the_server_says_so()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _ = server;
        var logFile = server.Services.GetRequiredService<WeirLogFile>();
        logFile.WriteLine("{\"timestamp\":\"2020-01-01T00:00:00Z\",\"level\":\"ERROR\",\"logger\":\"weir\",\"message\":\"old\"}");
        using (var unreadable = UnreadablePath.Create(logFile.Path))
        {
            if (unreadable is null)
            {
                return; // a privileged user cannot be refused, so there is nothing to show
            }

            using var saved = await client.PutAsync("/api/v1/suite/settings", new { csrf_token = await client.CsrfAsync(), app_timezone = "UTC", log_retention_days = 3 });
            using var listed = await client.GetAsync("/api/v1/suite/logs");
            using var download = await client.GetAsync("/api/v1/suite/logs/download");

            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
        }
    }

    /// <summary>Opens a stream, reads its opening frames and one change, cancels it, and says whether the stream then went on.</summary>
    private static async Task<bool> GoesOnAfterCancelAsync(Func<CancellationToken, IAsyncEnumerable<string>> open, int openingFrames, Func<Task> change)
    {
        using var cancel = new CancellationTokenSource();
        await using var stream = open(cancel.Token).GetAsyncEnumerator(cancel.Token);
        for (var opened = 0; opened < openingFrames; opened++)
        {
            Assert.True(await stream.MoveNextAsync());
        }

        var pending = stream.MoveNextAsync().AsTask();
        await change();
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await cancel.CancelAsync();
        return await stream.MoveNextAsync();
    }

    private static Func<Task> Now(Action change) => () =>
    {
        change();
        return Task.CompletedTask;
    };

    [Fact]
    public async Task The_live_streams_end_quietly_when_they_are_cancelled_between_frames()
    {
        var (server, _) = await StartSignedInAsync();
        await using var _server = server;
        var services = server.Services;

        Assert.False(await GoesOnAfterCancelAsync(
            services.GetRequiredService<LiveStreamFrames>().ForAsync, 1, Now(() => services.GetRequiredService<DataChangePublisher>().Publish("files"))));
        Assert.False(await GoesOnAfterCancelAsync(
            services.GetRequiredService<ConnectionActivityFrames>().ForAsync,
            0,
            Now(() => services.GetRequiredService<ConnectionActivityHub>().Publish(new ConnectionRef(ConnectionKind.MediaManager, 1), ConnectionPhase.Asked, ConnectionDirection.Outbound, null))));
        Assert.False(await GoesOnAfterCancelAsync(
            services.GetRequiredService<SystemLogFrames>().ForAsync, 0, Now(() => services.GetRequiredService<LogAlerts>().Publish(new LogAlert(DateTimeOffset.UtcNow, "ERROR", "broke")))));
        Assert.False(await GoesOnAfterCancelAsync(
            services.GetRequiredService<SystemChecksFrames>().ForAsync, 0, Now(() => services.GetRequiredService<ServerLooks>().ReadinessLooked())));
        Assert.False(await GoesOnAfterCancelAsync(
            services.GetRequiredService<SystemTasksFrames>().ForAsync,
            0,
            Now(() => services.GetRequiredService<PeriodicTaskRegistry>().Plan("edge-test-task", "Edge test", DateTimeOffset.UtcNow.AddHours(1), null))));
    }

    [Fact]
    public async Task The_overview_stream_ends_quietly_when_it_is_cancelled_between_frames()
    {
        await using var server = await WeirTestServer.StartAsync();
        var frames = server.Services.GetRequiredService<SystemOverviewFrames>();

        Assert.False(await GoesOnAfterCancelAsync(
            frames.ForAsync,
            1,
            async () =>
            {
                await TestDatabase.ExecuteAsync(server, "INSERT INTO server_starts (started_at) VALUES (datetime('now'))");
                await frames.RefreshAsync(CancellationToken.None);
            }));
    }

    [Fact]
    public void The_note_on_a_title_whose_search_did_not_go_through_names_the_manager_when_it_is_known()
    {
        var answer = new RedownloadAnswer(1, "Film/film.mkv", "owner", RedownloadAnswer.DeletedButSearchFailed, "No search.");
        var named = answer with { Connection = new ManagerConnection("radarr", "Main", "http://192.0.2.20:7878", "key") };

        Assert.StartsWith("The media manager deleted the file, but no search", answer.WaitingNote(), StringComparison.Ordinal);
        Assert.StartsWith("Radarr", named.WaitingNote(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("weir-config.json", "attachment; filename=\"weir-config.json\"")]
    [InlineData("weir config é.json", "attachment; filename*=utf-8''weir%20config%20%C3%A9.json")]
    public async Task A_download_names_its_file_plainly_or_escaped_when_the_name_needs_it(string fileName, string expected)
    {
        var path = Path.Join(Path.GetTempPath(), "weir-download-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{}");
        try
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await SuiteConfigurationEndpoints.WriteFileAsync(context, path, fileName, "application/json");

            Assert.Equal(expected, context.Response.Headers.ContentDisposition.ToString());
            Assert.Equal(2, context.Response.Body.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
