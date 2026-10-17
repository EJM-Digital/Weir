namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// A clock on which every wait is over at once: a timer fires as soon as it is made, so code that waits between polls runs through its
/// polls without the test waiting for the wall clock or racing a fake one. The time it reports does not move.
/// </summary>
internal sealed class ElapsedAtOnce : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Fired();
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return timer;
    }

    private sealed class Fired : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
