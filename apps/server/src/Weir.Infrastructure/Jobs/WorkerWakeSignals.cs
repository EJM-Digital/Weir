using Weir.Core.Jobs;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Tells idle worker slots that a job they run was queued, so it is picked up at once rather than at the next poll
/// (#716, #1098). Each slot waits on a signal of its own and still decides for itself whether to claim: this only wakes,
/// so nothing waits in line behind it, and the poll stays as the fallback for work that becomes due without being queued
/// (a retry's wait ending, processing being resumed).
/// </summary>
/// <remarks>
/// A job wakes only the slots of the lane that runs it, so a queued scan never rouses the file slots and a queued file
/// pass never rouses the upkeep slots. A signal is spent by the wake that completes it and a slot takes a fresh one only
/// when it next looks for work, so a wake is never remembered beyond that look and an idle slot cannot spin: it returns to
/// waiting after one look unless another job was queued during it.
/// </remarks>
public sealed class WorkerWakeSignals
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(WorkLane Lane, int Slot), TaskCompletionSource> _waiting = [];

    /// <summary>
    /// Completes at the next <see cref="Wake"/> of <paramref name="lane"/>. A slot takes it before it looks for work, so a
    /// job queued while it is looking still wakes it.
    /// </summary>
    public Task NextWake(WorkLane lane, int slot)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue((lane, slot), out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[(lane, slot)] = signal;
            }

            return signal.Task;
        }
    }

    /// <summary>Wakes every slot of <paramref name="lane"/> that is waiting for work.</summary>
    public void Wake(WorkLane lane)
    {
        List<TaskCompletionSource> woken = [];
        lock (_lock)
        {
            foreach (var key in _waiting.Keys.Where(key => key.Lane == lane).ToArray())
            {
                woken.Add(_waiting[key]);
                _waiting.Remove(key);
            }
        }

        foreach (var signal in woken)
        {
            signal.TrySetResult();
        }
    }

    /// <summary>Wakes the idle slots of both lanes, for a change that lets work start that was held back (processing resumed).</summary>
    public void WakeEveryLane()
    {
        Wake(WorkLane.Files);
        Wake(WorkLane.Upkeep);
    }

    /// <summary>Wakes the slots of the lane that runs <paramref name="jobKind"/>.</summary>
    public void WakeFor(string jobKind) => Wake(WorkLanes.LaneOf(jobKind));
}
