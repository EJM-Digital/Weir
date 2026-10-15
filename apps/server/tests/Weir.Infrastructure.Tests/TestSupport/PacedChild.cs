using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Processes;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// A stand-in child whose pace, and the clock the runner's idle limit reads, both belong to the test. The child prints its next line
/// only when the test has seen the last one (<see cref="Pace"/>), and the test moves the clock by hand (<see cref="Tick"/>), so how
/// long a busy machine takes to start the child, run its timers or schedule the reader decides nothing: the limit is measured in
/// the lines the test has seen, not in seconds that other work can eat. The pulse the runner counts awake time by notes the clock at
/// every step of <see cref="Tick"/>, and not at all during <see cref="Sleep"/>.
/// </summary>
internal sealed class PacedChild : IDisposable
{
    private readonly TempDirectory _acks = new();

    public PacedChild()
    {
        Pulse = new ProcessPulse(Time);
        Runner = new ProcessRunner(time: Time, pulse: Pulse);
    }

    /// <summary>The clock the runner's idle limit reads.</summary>
    public FakeTimeProvider Time { get; } = new();

    public ProcessPulse Pulse { get; }

    public ProcessRunner Runner { get; }

    /// <summary>The pause argument that makes the stand-in wait for <see cref="Tick"/> between lines.</summary>
    public string Pace => "ack:" + _acks.Path;

    /// <summary>Lets <paramref name="seen"/> lines' worth of time pass and tells the child that many have been seen, so it prints the next.</summary>
    public void Tick(TimeSpan elapsed, int seen)
    {
        Pass(elapsed);
        File.WriteAllBytes(_acks.Join(seen.ToString(System.Globalization.CultureInfo.InvariantCulture)), []);
    }

    /// <summary>Lets <paramref name="elapsed"/> of awake time pass, a second at a time, without telling the child anything: it prints nothing more.</summary>
    public void Pass(TimeSpan elapsed)
    {
        for (var left = elapsed; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(1))
        {
            Time.Advance(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            Pulse.Note();
        }
    }

    /// <summary>The computer sleeps for <paramref name="time"/>: the clock moves and nothing in the process runs until it wakes and the pulse notes the time.</summary>
    public void Sleep(TimeSpan time)
    {
        Time.Advance(time);
        Pulse.Note();
    }

    public void Dispose()
    {
        Pulse.Dispose();
        _acks.Dispose();
    }
}
