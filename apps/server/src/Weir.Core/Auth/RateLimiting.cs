using Weir.Core.Net;

namespace Weir.Core.Auth;

/// <summary>
/// In-process sliding-window limiter: at most
/// <see cref="MaxEvents"/> events per key within the window, with the least recently used keys
/// evicted beyond <see cref="MaxKeys"/>.
/// </summary>
public sealed class SlidingWindowLimiter
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly LinkedList<(string Key, Queue<double> Events)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Queue<double> Events)>> _buckets = new(StringComparer.Ordinal);

    public SlidingWindowLimiter(long maxEvents, double windowSeconds, TimeProvider time, int maxKeys = 10_000)
    {
        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "max_events must be >= 1");
        }

        if (windowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "window_seconds must be > 0");
        }

        if (maxKeys < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "max_keys must be >= 1");
        }

        ArgumentNullException.ThrowIfNull(time);
        MaxEvents = maxEvents;
        WindowSeconds = windowSeconds;
        MaxKeys = maxKeys;
        _time = time;
        _startTimestamp = time.GetTimestamp();
    }

    public long MaxEvents { get; }

    public double WindowSeconds { get; }

    public int MaxKeys { get; }

    internal int KeyCount
    {
        get
        {
            lock (_lock)
            {
                return _buckets.Count;
            }
        }
    }

    internal bool HasKey(string key)
    {
        lock (_lock)
        {
            return _buckets.ContainsKey(key);
        }
    }

    /// <summary>Record one event for <paramref name="key"/>; <see langword="false"/> when limited.</summary>
    public bool Allow(string? key)
    {
        var now = _time.GetElapsedTime(_startTimestamp).TotalSeconds;
        var cutoff = now - WindowSeconds;
        var k = string.IsNullOrEmpty(key) ? "unknown" : key;
        lock (_lock)
        {
            EvictExpired(cutoff);
            if (!_buckets.TryGetValue(k, out var node))
            {
                node = _order.AddLast((k, new Queue<double>()));
                _buckets[k] = node;
            }

            var events = node.Value.Events;
            if (events.Count >= MaxEvents)
            {
                return false;
            }

            events.Enqueue(now);
            _order.Remove(node);
            _order.AddLast(node);
            while (_buckets.Count > MaxKeys)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _buckets.Remove(oldest.Value.Key);
            }

            return true;
        }
    }

    private void EvictExpired(double cutoff)
    {
        var node = _order.First;
        while (node is not null)
        {
            var next = node.Next;
            var events = node.Value.Events;
            while (events.Count > 0 && events.Peek() < cutoff)
            {
                events.Dequeue();
            }

            if (events.Count == 0)
            {
                _order.Remove(node);
                _buckets.Remove(node.Value.Key);
            }

            node = next;
        }
    }
}

/// <summary>
/// The client address a rate limit counts against. <c>X-Forwarded-For</c> is honoured only when the direct
/// peer is a trusted proxy; then the right-most address that is not itself a trusted proxy is used.
/// </summary>
public static class ClientRateLimitKey
{
    /// <param name="peer">The request's client host (after the server's own proxy handling), or <see langword="null"/>.</param>
    /// <param name="forwardedFor">The <c>X-Forwarded-For</c> header.</param>
    /// <param name="trustedProxyIps"><c>WEIR_TRUSTED_PROXY_IPS</c>.</param>
    /// <param name="warnForwardedIgnored">Called when the header is ignored because no proxy is trusted.</param>
    public static string Resolve(string? peer, string? forwardedFor, IReadOnlyList<string> trustedProxyIps, Action? warnForwardedIgnored = null)
    {
        ArgumentNullException.ThrowIfNull(trustedProxyIps);
        var host = string.IsNullOrEmpty(peer) ? "unknown" : peer;
        var xff = (forwardedFor ?? string.Empty).Trim();
        if (xff.Length == 0)
        {
            return host;
        }

        if (trustedProxyIps.Count == 0)
        {
            warnForwardedIgnored?.Invoke();
            return host;
        }

        var trusted = trustedProxyIps
            .Select(raw => NetRange.TryParse(raw, strict: false, out var network) ? network : null)
            .OfType<NetRange>()
            .ToList();
        if (!InNetworks(host, trusted))
        {
            return host;
        }

        var chain = xff.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (!InNetworks(chain[i], trusted))
            {
                return chain[i];
            }
        }

        return chain.Count > 0 ? chain[0] : host;
    }

    public static bool InNetworks(string raw, IReadOnlyList<NetRange> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        return NetAddress.TryParse(raw, out var address) && networks.Any(network => network.Contains(address));
    }
}
