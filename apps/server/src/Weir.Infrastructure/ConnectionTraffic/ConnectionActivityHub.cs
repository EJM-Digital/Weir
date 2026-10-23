using System.Threading.Channels;
using Weir.Core.MediaManagers;
using Weir.Core.Time;

namespace Weir.Infrastructure.ConnectionTraffic;

/// <summary>
/// The process-wide, in-memory stream of what is happening on each connection, shared by every call that reports it and
/// every open Activity stream. Nothing here touches the database or waits on a reader: the Connections views are lit by
/// what is published, never by asking.
/// </summary>
/// <remarks>
/// Each connection sends at most one frame per <see cref="Throttle"/> for each phase, so a burst of calls (a queue poll, a
/// folder-chain check) cannot flood a stream. A frame held back is replaced by any later one for the same connection, and
/// is sent once its window is over, so the phase a connection ends on always reaches the stream.
/// </remarks>
public sealed class ConnectionActivityHub
{
    /// <summary>The shortest time between two frames of one phase for one connection.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);

    /// <summary>How many frames one slow stream may hold before its oldest are dropped.</summary>
    private const int SubscriberBacklog = 256;

    private readonly TimeProvider _time;
    private readonly ConnectionUsageLedger? _usage;
    private readonly Lock _gate = new();
    private readonly Dictionary<ConnectionRef, ConnectionLane> _lanes = [];
    private readonly List<ConnectionActivitySubscription> _subscribers = [];

    public ConnectionActivityHub(TimeProvider time, ConnectionUsageLedger? usage = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _usage = usage;
    }

    /// <summary>
    /// Says what happened on <paramref name="connection"/> just now. Every call is noted in the usage ledger; the
    /// streams get it subject to the throttle.
    /// </summary>
    public void Publish(ConnectionRef connection, ConnectionPhase phase, ConnectionDirection direction, long? milliseconds)
    {
        var activity = new ConnectionActivity(connection, phase, direction, Timestamp.UtcNow(_time), milliseconds);
        _usage?.Record(activity);
        lock (_gate)
        {
            var lane = LaneFor(connection);
            var now = _time.GetUtcNow();
            if (lane.IsOpenFor(phase, now))
            {
                Send(lane, activity, now);
                return;
            }

            lane.Held = activity;
            ScheduleRelease(connection, lane, lane.OpensAt(phase) - now);
        }
    }

    /// <summary>Starts a stream of everything published from now on. Dispose it when the stream ends.</summary>
    public ConnectionActivitySubscription Subscribe()
    {
        var subscription = new ConnectionActivitySubscription(this, SubscriberBacklog);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    internal void Unsubscribe(ConnectionActivitySubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    private ConnectionLane LaneFor(ConnectionRef connection)
    {
        if (!_lanes.TryGetValue(connection, out var lane))
        {
            lane = new ConnectionLane();
            _lanes.Add(connection, lane);
        }

        return lane;
    }

    private void Send(ConnectionLane lane, ConnectionActivity activity, DateTimeOffset now)
    {
        lane.Held = null;
        lane.Sent(activity.Phase, now);
        foreach (var subscriber in _subscribers)
        {
            subscriber.Offer(activity);
        }
    }

    private void ScheduleRelease(ConnectionRef connection, ConnectionLane lane, TimeSpan wait)
    {
        if (lane.Release is not null)
        {
            return;
        }

        lane.Release = _time.CreateTimer(_ => ReleaseHeld(connection), null, wait, Timeout.InfiniteTimeSpan);
    }

    private void ReleaseHeld(ConnectionRef connection)
    {
        lock (_gate)
        {
            var lane = _lanes[connection];
            lane.Release?.Dispose();
            lane.Release = null;
            if (lane.Held is not { } held)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (lane.IsOpenFor(held.Phase, now))
            {
                Send(lane, held, now);
            }
            else
            {
                ScheduleRelease(connection, lane, lane.OpensAt(held.Phase) - now);
            }
        }
    }

    /// <summary>When each phase last went out for one connection, and the newest frame still waiting its turn.</summary>
    private sealed class ConnectionLane
    {
        private readonly Dictionary<ConnectionPhase, DateTimeOffset> _lastSent = [];

        public ConnectionActivity? Held { get; set; }

        public ITimer? Release { get; set; }

        public bool IsOpenFor(ConnectionPhase phase, DateTimeOffset now) => OpensAt(phase) <= now;

        public DateTimeOffset OpensAt(ConnectionPhase phase) =>
            _lastSent.TryGetValue(phase, out var sent) ? sent + Throttle : DateTimeOffset.MinValue;

        public void Sent(ConnectionPhase phase, DateTimeOffset now) => _lastSent[phase] = now;
    }
}

/// <summary>One open stream's view of <see cref="ConnectionActivityHub"/>: what was published since it began, oldest first.</summary>
public sealed class ConnectionActivitySubscription : IDisposable
{
    private readonly ConnectionActivityHub _hub;
    private readonly Channel<ConnectionActivity> _frames;

    internal ConnectionActivitySubscription(ConnectionActivityHub hub, int backlog)
    {
        _hub = hub;
        _frames = Channel.CreateBounded<ConnectionActivity>(new BoundedChannelOptions(backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public IAsyncEnumerable<ConnectionActivity> ReadAllAsync(CancellationToken cancellationToken) => _frames.Reader.ReadAllAsync(cancellationToken);

    /// <summary>The next thing that happened, waiting for one. A stream reads in a loop that only cancellation ends, since a subscription is closed only by its own owner.</summary>
    public ValueTask<ConnectionActivity> ReadAsync(CancellationToken cancellationToken) => _frames.Reader.ReadAsync(cancellationToken);

    internal void Offer(ConnectionActivity activity) => _frames.Writer.TryWrite(activity);

    public void Dispose()
    {
        _hub.Unsubscribe(this);
        _frames.Writer.TryComplete();
    }
}
