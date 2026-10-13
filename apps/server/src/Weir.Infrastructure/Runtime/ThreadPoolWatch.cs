using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Says in the log when the server is slow to begin work that is waiting for a thread. The pool has as many threads ready as the
/// machine has processors and adds one only every half second or so after that, so a few requests or timers that hold their threads
/// (a wait on a file, a call that blocks) leave every other request waiting, on a machine with few processors for seconds at a time,
/// and nothing else in the log shows it. Once a second this queues a small piece of work and measures how long it waits to begin; a
/// wait of <see cref="ThreadPoolStallTracker.LateAfter"/> or more is a warning with the pool's own counts.
/// </summary>
/// <remarks>
/// The watching is done by a thread of its own (<see cref="BlockingWork"/>), because a watcher that needed a pool thread would be
/// the first thing a starved pool stopped. The probe that has not begun is measured against the clock at every look, so a stall is
/// said while it goes on, not after it ends.
/// </remarks>
public sealed class ThreadPoolWatch : BackgroundService
{
    /// <summary>How often the watch looks and queues its probe.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly ILogger<ThreadPoolWatch> _logger;
    private readonly Action<Action> _queue;
    private readonly Func<ThreadPoolCounts> _counts;
    private readonly ThreadPoolStallTracker _tracker = new();
    private readonly long _startedAt;
    private Probe? _probe;

    public ThreadPoolWatch(TimeProvider time, ILogger<ThreadPoolWatch> logger)
        : this(time, logger, work => ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null), CurrentCounts)
    {
    }

    // A test queues the probe where it can run it by hand, and says what the pool looks like.
    internal ThreadPoolWatch(TimeProvider time, ILogger<ThreadPoolWatch> logger, Action<Action> queue, Func<ThreadPoolCounts> counts)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _counts = counts ?? throw new ArgumentNullException(nameof(counts));
        _startedAt = time.GetTimestamp();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BlockingWork.RunAsync(() =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Tick();
                stoppingToken.WaitHandle.WaitOne(Every);
            }
        });

    /// <summary>One look: how long the last probe waited, and a new probe when the last has begun.</summary>
    internal void Tick()
    {
        var now = _time.GetTimestamp();
        var delay = TimeSpan.Zero;
        var begun = false;
        if (_probe is { } probe)
        {
            begun = probe.Begun;
            delay = _time.GetElapsedTime(probe.QueuedAt, begun ? probe.BegunAt : now);
        }

        if (_probe is null || begun)
        {
            var next = new Probe(now, _time);
            _probe = next;
            _queue(next.Begin);
        }

        Say(_tracker.Observe(_time.GetElapsedTime(_startedAt, now), delay, begun, _counts()));
    }

    private void Say(Stall stall)
    {
        var counts = stall.Counts;
        switch (stall.Event)
        {
            case StallEvent.Began:
                _logger.LogWarning(
                    "Weir's server has been slow to get to work for {Seconds} s, so pages may answer slowly. It has {Threads} threads (it starts {Minimum} at once) and {Pending} pieces of work waiting. Processors available: {Processors}.",
                    Whole(stall.Lasted),
                    counts.Threads,
                    counts.MinThreads,
                    counts.Pending,
                    counts.Processors);
                break;
            case StallEvent.Continues:
                _logger.LogWarning(
                    "Weir's server is still slow to get to work after {Seconds} s. It has {Threads} threads (it starts {Minimum} at once) and {Pending} pieces of work waiting. Processors available: {Processors}.",
                    Whole(stall.Lasted),
                    counts.Threads,
                    counts.MinThreads,
                    counts.Pending,
                    counts.Processors);
                break;
            case StallEvent.Ended when stall.Told:
                _logger.LogInformation(
                    "Weir's server is getting to work promptly again after {Seconds} s; the longest anything waited was {Longest} s.",
                    Whole(stall.Lasted),
                    Whole(stall.Longest));
                break;
            case StallEvent.Ended:
                _logger.LogWarning(
                    "Weir's server was slow to get to work for {Seconds} s, so pages may have answered slowly. It had {Threads} threads (it starts {Minimum} at once). Processors available: {Processors}.",
                    Whole(stall.Lasted),
                    counts.Threads,
                    counts.MinThreads,
                    counts.Processors);
                break;
            default:
                break;
        }
    }

    private static int Whole(TimeSpan span) => (int)Math.Round(span.TotalSeconds);

    private static ThreadPoolCounts CurrentCounts()
    {
        ThreadPool.GetMinThreads(out var minimum, out _);
        ThreadPool.GetMaxThreads(out var maximum, out _);
        return new ThreadPoolCounts(ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, minimum, maximum, Environment.ProcessorCount);
    }

    /// <summary>A piece of work that does nothing but note when it began.</summary>
    private sealed class Probe(long queuedAt, TimeProvider time)
    {
        private long _begunAt;
        private int _begun;

        public long QueuedAt { get; } = queuedAt;

        public bool Begun => Volatile.Read(ref _begun) == 1;

        public long BegunAt => Volatile.Read(ref _begunAt);

        public void Begin()
        {
            Volatile.Write(ref _begunAt, time.GetTimestamp());
            Volatile.Write(ref _begun, 1);
        }
    }
}
