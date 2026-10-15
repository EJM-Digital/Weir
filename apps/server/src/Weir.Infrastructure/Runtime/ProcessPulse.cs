using Microsoft.Extensions.Hosting;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Knows when the whole of Weir was unable to run: the computer asleep or hibernating, a virtual machine saved or paused, a
/// container frozen, or the process stopped. Whatever work is timed by the clock (a scan due every few minutes) is then late
/// for a reason nobody can do anything about, and the log should not call that a fault.
/// </summary>
/// <remarks>
/// <para>
/// A thread of its own notes the time once a second. A gap of <see cref="AwayAfter"/> or more between two notes means nothing in
/// the process ran during it. Every thread stops together when the machine sleeps or the process is frozen, whereas a scheduler
/// that falls behind while Weir is running leaves the thread's notes unbroken. The thread is not one of the pool's, so a starved
/// pool (which <see cref="ThreadPoolWatch"/> reports on its own) is not mistaken for a sleep.
/// </para>
/// <para>
/// The gap is measured on the wall clock, because that is the clock the scheduled work is timed by and the one that keeps
/// counting while the machine sleeps. A monotonic clock cannot stand in for it: Windows' (<c>Stopwatch</c>, which reads
/// <c>QueryPerformanceCounter</c>) counts time asleep, and <c>Environment.TickCount64</c> did on Windows up to .NET 10, while Linux's
/// (<c>CLOCK_MONOTONIC</c>) does not count a suspended host, and keeps running for a container that is only frozen. A power event
/// would miss a container and a saved virtual machine.
/// </para>
/// </remarks>
public sealed class ProcessPulse : BackgroundService
{
    /// <summary>How often the thread notes the time.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>The shortest silence that counts as the process having been away: long enough that a busy machine does not look asleep.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(10);

    /// <summary>How many past absences are kept. The oldest are dropped; work that is late by longer than they reach back was not away.</summary>
    private const int Remembered = 32;

    private readonly TimeProvider _time;
    private readonly TimeSpan _every;
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset From, DateTimeOffset To)> _away = [];
    private DateTimeOffset _lastNoted;
    private long _notes;

    public ProcessPulse(TimeProvider time)
        : this(time, Every)
    {
    }

    // A test beats the pulse quickly when it runs the service for real.
    internal ProcessPulse(TimeProvider time, TimeSpan every)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _every = every;
        _lastNoted = time.GetUtcNow();
    }

    /// <summary>How many times the time has been noted.</summary>
    internal long Notes => Interlocked.Read(ref _notes);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BlockingWork.RunAsync(() =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Note();
                stoppingToken.WaitHandle.WaitOne(_every);
            }
        });

    /// <summary>
    /// The time from <paramref name="from"/> to <paramref name="to"/> in which nothing in the process ran, and how long the silences
    /// that reach into it lasted in all. A silence that has not yet been noted (the thread has not woken since the computer did)
    /// counts as well.
    /// </summary>
    public Absence AwayWithin(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            var within = TimeSpan.Zero;
            var lasted = TimeSpan.Zero;
            var silences = new List<(DateTimeOffset From, DateTimeOffset To)>(_away);
            var now = _time.GetUtcNow();
            if (now - _lastNoted >= AwayAfter)
            {
                silences.Add((_lastNoted, now));
            }

            foreach (var (start, end) in silences)
            {
                var overlap = Overlap(from, to, start, end);
                if (overlap > TimeSpan.Zero)
                {
                    within += overlap;
                    lasted += end - start;
                }
            }

            return new Absence(within, lasted);
        }
    }

    /// <summary>One look at the clock: notes a silence if the last look was long ago.</summary>
    internal void Note()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (now - _lastNoted >= AwayAfter)
            {
                _away.Add((_lastNoted, now));
                if (_away.Count > Remembered)
                {
                    _away.RemoveAt(0);
                }
            }

            _lastNoted = now;
        }

        Interlocked.Increment(ref _notes);
    }

    private static TimeSpan Overlap(DateTimeOffset from, DateTimeOffset to, DateTimeOffset start, DateTimeOffset end)
    {
        var begin = from > start ? from : start;
        var finish = to < end ? to : end;
        return finish > begin ? finish - begin : TimeSpan.Zero;
    }
}

/// <summary>When the process could not run: how much of a stretch of time it covers, and how long the silences that reach into it lasted.</summary>
/// <param name="Within">The part of the stretch in which nothing ran.</param>
/// <param name="Lasted">The whole length of every silence that overlaps the stretch.</param>
public readonly record struct Absence(TimeSpan Within, TimeSpan Lasted)
{
    /// <summary>No silence.</summary>
    public static Absence None => new(TimeSpan.Zero, TimeSpan.Zero);
}
