using System.Threading.Channels;

namespace Weir.Infrastructure.Activity;

/// <summary>
/// One process-wide feed that every open stream listens to. Each subscriber holds its own bounded backlog and a slow one
/// loses its oldest items, so publishing never waits on a reader.
/// </summary>
/// <typeparam name="T">What is published.</typeparam>
public sealed class Broadcast<T>
{
    private readonly int _backlog;
    private readonly Lock _gate = new();
    private readonly List<BroadcastSubscription<T>> _subscribers = [];

    /// <param name="backlog">How many items one slow subscriber may hold before its oldest are dropped.</param>
    public Broadcast(int backlog)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(backlog, 1);
        _backlog = backlog;
    }

    /// <summary>Whether anyone is listening right now.</summary>
    public bool HasSubscribers
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count > 0;
            }
        }
    }

    public void Publish(T item)
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Offer(item);
            }
        }
    }

    /// <summary>Starts listening to everything published from now on. Dispose the subscription when the stream ends.</summary>
    public BroadcastSubscription<T> Subscribe()
    {
        var subscription = new BroadcastSubscription<T>(this, _backlog);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    internal void Unsubscribe(BroadcastSubscription<T> subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }
}

/// <summary>One open stream's view of a <see cref="Broadcast{T}"/>: what was published since it began, oldest first.</summary>
public sealed class BroadcastSubscription<T> : IDisposable
{
    private readonly Broadcast<T> _feed;
    private readonly Channel<T> _items;

    internal BroadcastSubscription(Broadcast<T> feed, int backlog)
    {
        _feed = feed;
        _items = Channel.CreateBounded<T>(new BoundedChannelOptions(backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken) => _items.Reader.ReadAllAsync(cancellationToken);

    /// <summary>The next item, waiting for one. A stream reads in a loop that only cancellation ends, since a subscription is closed only by its own owner.</summary>
    public ValueTask<T> ReadAsync(CancellationToken cancellationToken) => _items.Reader.ReadAsync(cancellationToken);

    internal void Offer(T item) => _items.Writer.TryWrite(item);

    public void Dispose()
    {
        _feed.Unsubscribe(this);
        _items.Writer.TryComplete();
    }
}
