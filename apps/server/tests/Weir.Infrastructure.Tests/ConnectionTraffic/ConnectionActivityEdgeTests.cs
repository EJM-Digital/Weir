using Microsoft.Extensions.Time.Testing;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.ConnectionTraffic;

/// <summary>The corners of the activity stream and the usage ledger: a held frame overtaken, a held frame whose window is later, a failed save.</summary>
public sealed class ConnectionActivityEdgeTests
{
    private static readonly ConnectionRef Radarr = new(ConnectionKind.MediaManager, 1);
    private static readonly Timestamp Noon = Timestamp.FromUtc(new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

    private static async Task<List<ConnectionPhase>> PhasesSentAsync(ConnectionActivitySubscription subscription)
    {
        subscription.Dispose();
        var phases = new List<ConnectionPhase>();
        await foreach (var activity in subscription.ReadAllAsync(CancellationToken.None))
        {
            phases.Add(activity.Phase);
        }

        return phases;
    }

    private static void Say(ConnectionActivityHub hub, ConnectionPhase phase) =>
        hub.Publish(Radarr, phase, ConnectionDirection.Outbound, milliseconds: null);

    [Fact]
    public async Task A_held_frame_that_another_phase_has_already_overtaken_is_not_sent_twice()
    {
        var hub = new ConnectionActivityHub(_time);
        var subscription = hub.Subscribe();
        Say(hub, ConnectionPhase.Asked);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Say(hub, ConnectionPhase.Asked);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Say(hub, ConnectionPhase.Answered);

        _time.Advance(ConnectionActivityHub.Throttle);

        Assert.Equal([ConnectionPhase.Asked, ConnectionPhase.Answered], await PhasesSentAsync(subscription));
    }

    [Fact]
    public async Task A_held_frame_whose_own_window_ends_later_than_the_first_wake_waits_for_it()
    {
        var hub = new ConnectionActivityHub(_time);
        var subscription = hub.Subscribe();
        Say(hub, ConnectionPhase.Asked);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Say(hub, ConnectionPhase.Answered);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Say(hub, ConnectionPhase.Asked);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Say(hub, ConnectionPhase.Answered);

        _time.Advance(TimeSpan.FromMilliseconds(30));
        var atFirstWake = await PhasesSentAsync(subscription);
        var later = hub.Subscribe();
        _time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal([ConnectionPhase.Asked, ConnectionPhase.Answered], atFirstWake);
        Assert.Equal([ConnectionPhase.Answered], await PhasesSentAsync(later));
    }

    [Fact]
    public async Task A_save_that_is_cancelled_leaves_the_usage_for_the_next_run()
    {
        using var fixture = new MediaManagerFixture();
        fixture.Usage.Record(new ConnectionActivity(Radarr, ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));
        var flush = new ConnectionUsageFlushTask(fixture.Store.Database, fixture.Usage, fixture.ConnectionStore, new DownloadClientConnectionStore());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.RunOnceAsync(cancelled.Token));

        Assert.Single(fixture.Usage.TakeUnsaved());
    }
}
