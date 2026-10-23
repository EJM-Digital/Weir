namespace Weir.Infrastructure.Activity;

/// <summary>When the server last looked, on its own, at the things its health checks are about.</summary>
/// <param name="Folders">When it last checked every workflow's folders.</param>
/// <param name="Readiness">When it last checked whether Weir is ready, workers included.</param>
public sealed record ServerLookTimes(DateTimeOffset? Folders, DateTimeOffset? Readiness);

/// <summary>
/// Remembers when the watching tasks (<c>folder-checks</c>, <c>readiness-changes</c>) last looked, and tells every open stream
/// each time one does, so a health row can say when the server last checked it rather than when the browser last read it. A
/// look that finds nothing new publishes no data topic, which is why the time travels on its own.
/// </summary>
public sealed class ServerLooks
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Broadcast<long> _changes = new(backlog: 1);
    private ServerLookTimes _times = new(null, null);
    private long _version;

    public ServerLooks(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>The times as they stand.</summary>
    public ServerLookTimes Latest
    {
        get
        {
            lock (_gate)
            {
                return _times;
            }
        }
    }

    /// <summary>Every workflow's folders were just checked.</summary>
    public void FoldersLooked() => Record(times => times with { Folders = _time.GetUtcNow() });

    /// <summary>Whether Weir is ready was just checked.</summary>
    public void ReadinessLooked() => Record(times => times with { Readiness = _time.GetUtcNow() });

    /// <summary>Starts listening for looks. An item says only that a look happened: read <see cref="Latest"/> for when.</summary>
    public BroadcastSubscription<long> Subscribe() => _changes.Subscribe();

    private void Record(Func<ServerLookTimes, ServerLookTimes> update)
    {
        lock (_gate)
        {
            _times = update(_times);
        }

        _changes.Publish(Interlocked.Increment(ref _version));
    }
}
