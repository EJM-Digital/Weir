namespace Weir.Infrastructure.Runtime;

/// <summary>What the thread pool looked like when a stall was noticed.</summary>
/// <param name="Threads">How many threads the pool had.</param>
/// <param name="Pending">How many pieces of work were waiting for one.</param>
/// <param name="MinThreads">How many threads the pool starts at once, before it adds one every half second or so.</param>
/// <param name="MaxThreads">The most threads the pool will make.</param>
/// <param name="Processors">How many processors the machine gives the server.</param>
internal readonly record struct ThreadPoolCounts(int Threads, long Pending, int MinThreads, int MaxThreads, int Processors);

/// <summary>What a look at the wait told.</summary>
internal enum StallEvent
{
    /// <summary>Nothing worth saying.</summary>
    None,

    /// <summary>Work has been waiting for a thread for <see cref="ThreadPoolStallTracker.LateAfter"/> and has not begun.</summary>
    Began,

    /// <summary>The wait goes on, and it is time to say so again (<see cref="ThreadPoolStallTracker.RepeatEvery"/>).</summary>
    Continues,

    /// <summary>Work that had waited that long has begun.</summary>
    Ended,
}

/// <param name="Event">What happened.</param>
/// <param name="Lasted">How long the wait has lasted, or lasted in all when it ended.</param>
/// <param name="Longest">The longest any one piece of work waited in it.</param>
/// <param name="Told">For <see cref="StallEvent.Ended"/>: whether the log already said it began. A wait first seen when it ends (the watching thread was late too) has not been said.</param>
/// <param name="Counts">The pool's counts at the look.</param>
internal readonly record struct Stall(StallEvent Event, TimeSpan Lasted, TimeSpan Longest, bool Told, ThreadPoolCounts Counts);

/// <summary>
/// Decides what the log says about a wait for a thread. A wait shorter than <see cref="LateAfter"/> is the pool doing its work (it adds
/// threads on a half-second rhythm once its minimum is in use) and says nothing. A longer one is said when it is first seen, again every
/// <see cref="RepeatEvery"/> while it goes on, and once more when it ends, so a stall of any length leaves a few lines, not one a second.
/// </summary>
internal sealed class ThreadPoolStallTracker
{
    /// <summary>How long a piece of work may wait for a thread before the wait is worth a line in the log.</summary>
    internal static readonly TimeSpan LateAfter = TimeSpan.FromSeconds(2);

    /// <summary>How often a wait that goes on is said again.</summary>
    internal static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(30);

    private TimeSpan? _began;
    private TimeSpan _lastSaid;
    private TimeSpan _longest;

    /// <param name="now">The time of the look, on any clock that only runs forward.</param>
    /// <param name="delay">How long the probe waited to begin: when it has begun, in all; when it has not, so far.</param>
    /// <param name="begun">Whether the probe has begun.</param>
    /// <param name="counts">The pool's counts.</param>
    internal Stall Observe(TimeSpan now, TimeSpan delay, bool begun, ThreadPoolCounts counts)
    {
        if (delay < LateAfter)
        {
            return Quiet(counts);
        }

        _longest = delay > _longest ? delay : _longest;
        var told = _began is not null;
        _began ??= now - delay;
        var lasted = now - _began.Value;
        if (begun)
        {
            var ended = new Stall(StallEvent.Ended, lasted, _longest, told, counts);
            _began = null;
            _longest = TimeSpan.Zero;
            return ended;
        }

        if (!told)
        {
            _lastSaid = now;
            return new Stall(StallEvent.Began, lasted, _longest, Told: true, counts);
        }

        if (now - _lastSaid < RepeatEvery)
        {
            return Quiet(counts);
        }

        _lastSaid = now;
        return new Stall(StallEvent.Continues, lasted, _longest, Told: true, counts);
    }

    private static Stall Quiet(ThreadPoolCounts counts) => new(StallEvent.None, TimeSpan.Zero, TimeSpan.Zero, Told: false, counts);
}
