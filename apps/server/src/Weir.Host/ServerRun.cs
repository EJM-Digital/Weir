namespace Weir.Host;

/// <summary>Starts the server and runs it until it is asked to stop.</summary>
internal static class ServerRun
{
    /// <summary>
    /// Runs <paramref name="host"/> to the end of its life. A start that fails (its port is held) has already begun the background loops
    /// that start before the listener, so they are stopped, and finish, before the host is disposed; otherwise a loop in the middle of a
    /// run fails on a service that has just been disposed. <paramref name="onStartFailed"/> is told of the failure first, while the log is still
    /// open (stopping closes it). The start's own exception is thrown.
    /// </summary>
    internal static async Task RunAsync(IHost host, Action<Exception>? onStartFailed = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                onStartFailed?.Invoke(exception);
                await host.StopAsync().ConfigureAwait(false);
                throw;
            }

            await host.WaitForShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            if (host is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
    }
}
