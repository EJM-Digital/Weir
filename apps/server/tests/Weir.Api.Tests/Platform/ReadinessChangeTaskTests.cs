using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weir.Api.Endpoints;
using Weir.Core.Readiness;
using Weir.Core.Workers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The server looks at its own readiness while a browser watches, and says so when the answer is not the one it gave last time: a
/// worker that stops, or a start that finishes, shows on every open screen without anyone asking.
/// </summary>
public sealed class ReadinessChangeTaskTests
{
    private const string Sentinel = "sentinel";

    private static ReadinessReport Report(string workerStatus = "healthy", double startupSeconds = 1.5, int stale = 0) => new(
        Ready: true,
        Version: "1.0.0",
        Status: "ready",
        StartupSeconds: startupSeconds,
        Steps: [new ReadinessStep("database", "ready", "Local database is connected and migrations are complete.")],
        WorkerHealth: [new WorkerLaneHealth("processing", 4, 4 - stale, stale, 0, workerStatus, "Weir worker heartbeats are current.")]);

    [Fact]
    public void The_answer_is_the_same_whatever_the_seconds_the_start_took()
    {
        Assert.Equal(ReadinessChangeTask.AnswerOf(Report(startupSeconds: 1.5)), ReadinessChangeTask.AnswerOf(Report(startupSeconds: 9.25)));
    }

    [Fact]
    public void The_answer_changes_when_a_worker_lane_does()
    {
        Assert.NotEqual(ReadinessChangeTask.AnswerOf(Report()), ReadinessChangeTask.AnswerOf(Report(workerStatus: "degraded", stale: 1)));
    }

    private static async Task<string> NextAsync(BroadcastSubscription<string> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var topic in subscription.ReadAllAsync(timeout.Token))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    /// <summary>Whether a topic was published since the last call: a marker published after it tells where the topics end.</summary>
    private static async Task<bool> PublishedAsync(DataChangePublisher changes, BroadcastSubscription<string> subscription)
    {
        changes.Publish(Sentinel);
        return await NextAsync(subscription) != Sentinel;
    }

    private static void RemoveWorkerSlots(IServiceCollection services)
    {
        var slots = services
            .Where(service => service.ServiceType == typeof(IHostedService) &&
                service.ImplementationType is { } type && (type == typeof(ProcessingWorkerService) || type == typeof(UpkeepWorkerService)))
            .ToList();
        foreach (var service in slots)
        {
            services.Remove(service);
        }
    }

    /// <summary>
    /// A server whose worker slots report only what the test says. The real slots beat on their own clock, so one beat landing
    /// between a stop and the look would take the stop back and leave the answer as it was.
    /// </summary>
    private static async Task<WeirTestServer> StartWithSlotsTheTestDrivesAsync()
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "1")],
            configureServices: RemoveWorkerSlots);
        Assert.DoesNotContain(server.Services.GetServices<IHostedService>(), service => service is ProcessingWorkerService or UpkeepWorkerService);
        var heartbeats = server.Services.GetRequiredService<WorkerHeartbeats>();
        heartbeats.Started(ProcessingWorkerService.HeartbeatModule, 0);
        for (var slot = 0; slot < WorkLanes.UpkeepSlots; slot++)
        {
            heartbeats.Started(WorkLanes.UpkeepHeartbeatModule, slot);
        }

        await Eventually.ThatAsync(async () => (await SystemEndpoints.BuildReadinessAsync(server.Services)).Ready);
        return server;
    }

    [Fact]
    public async Task A_worker_that_stops_and_one_that_comes_back_are_published_while_a_browser_watches()
    {
        await using var server = await StartWithSlotsTheTestDrivesAsync();
        var changes = new DataChangePublisher();
        var clients = new ActivityStreamClients();
        var looks = new ServerLooks(TimeProvider.System);
        var task = new ReadinessChangeTask(server.Services, clients, changes, looks);
        var heartbeats = server.Services.GetRequiredService<WorkerHeartbeats>();
        using var watcher = clients.Open();
        using var subscription = changes.Subscribe();

        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DataTopics.Readiness, await NextAsync(subscription));
        Assert.NotNull(looks.Latest.Readiness);

        await task.RunOnceAsync(CancellationToken.None);
        Assert.False(await PublishedAsync(changes, subscription), "An answer that has not changed is not published again.");

        heartbeats.Stopped(ProcessingWorkerService.HeartbeatModule, 0);
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DataTopics.Readiness, await NextAsync(subscription));

        heartbeats.Beat(ProcessingWorkerService.HeartbeatModule, 0);
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DataTopics.Readiness, await NextAsync(subscription));
    }

    [Fact]
    public async Task A_worker_that_stops_and_reports_again_before_the_look_is_not_published()
    {
        await using var server = await StartWithSlotsTheTestDrivesAsync();
        var changes = new DataChangePublisher();
        var clients = new ActivityStreamClients();
        var task = new ReadinessChangeTask(server.Services, clients, changes, new ServerLooks(TimeProvider.System));
        var heartbeats = server.Services.GetRequiredService<WorkerHeartbeats>();
        using var watcher = clients.Open();
        using var subscription = changes.Subscribe();
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DataTopics.Readiness, await NextAsync(subscription));

        heartbeats.Stopped(ProcessingWorkerService.HeartbeatModule, 0);
        heartbeats.Beat(ProcessingWorkerService.HeartbeatModule, 0);
        await task.RunOnceAsync(CancellationToken.None);

        Assert.False(await PublishedAsync(changes, subscription), "A worker that is running at the look is the answer already given.");
    }

    [Fact]
    public async Task With_nobody_watching_nothing_is_looked_at_or_published()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")]);
        var changes = new DataChangePublisher();
        var task = new ReadinessChangeTask(server.Services, new ActivityStreamClients(), changes, new ServerLooks(TimeProvider.System));
        using var subscription = changes.Subscribe();

        await task.RunOnceAsync(CancellationToken.None);

        Assert.False(await PublishedAsync(changes, subscription));
    }
}
