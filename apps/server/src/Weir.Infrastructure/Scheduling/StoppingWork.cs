namespace Weir.Infrastructure.Scheduling;

/// <summary>Work a background service does until the server stops, where the stop itself is not a failure.</summary>
internal static class StoppingWork
{
    /// <summary>
    /// Runs <paramref name="work"/> and returns when it ends. A cancellation raised because <paramref name="stoppingToken"/> was
    /// cancelled is the end of the work, not an error; any other cancellation, and every other failure, is still thrown.
    /// </summary>
    public static async Task RunAsync(Func<Task> work, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}
