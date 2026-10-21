namespace Weir.Tray.Tests;

/// <summary>
/// Tells a test that a fake has been changed by work the controller started and does not return a task for. A fake calls
/// <see cref="Raise"/> after each change; the test waits on the change itself, so nothing waits for the clock.
/// </summary>
internal sealed class TestActivity
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private TaskCompletionSource _next = NewSignal();

    internal void Raise() => Interlocked.Exchange(ref _next, NewSignal()).SetResult();

    /// <summary>Returns once <paramref name="done"/> holds. The guard only ends a test whose work never happens.</summary>
    internal async Task UntilAsync(Func<bool> done)
    {
        while (true)
        {
            var next = Volatile.Read(ref _next).Task;
            if (done())
            {
                return;
            }

            try
            {
                await next.WaitAsync(HangGuard);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The background work did not finish.");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
