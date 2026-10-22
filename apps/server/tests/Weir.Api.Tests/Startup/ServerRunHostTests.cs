using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weir.Host;

namespace Weir.Api.Tests.Startup;

/// <summary>Running a host that can only be disposed synchronously, and one that fails to start with nobody to tell.</summary>
public sealed class ServerRunHostTests
{
    [Fact]
    public async Task A_host_that_can_only_be_disposed_synchronously_is_disposed_when_it_ends()
    {
        var host = new PlainHost();

        await ServerRun.RunAsync(host);

        Assert.True(host.Started);
        Assert.True(host.Disposed);
        Assert.True(host.Stopped);
    }

    [Fact]
    public async Task A_start_that_fails_with_nobody_to_tell_is_stopped_and_disposed_and_its_error_is_thrown()
    {
        var failure = new IOException("the port is held");
        var host = new PlainHost { StartFailure = failure };

        var thrown = await Assert.ThrowsAsync<IOException>(() => ServerRun.RunAsync(host));

        Assert.Same(failure, thrown);
        Assert.True(host.Stopped);
        Assert.True(host.Disposed);
    }

    /// <summary>A host that is already asked to stop, and that offers no asynchronous disposal.</summary>
    private sealed class PlainHost : IHost
    {
        public PlainHost()
        {
            Services = new ServiceCollection().AddSingleton<IHostApplicationLifetime>(new StoppedLifetime()).BuildServiceProvider();
        }

        public Exception? StartFailure { get; init; }

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public IServiceProvider Services { get; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class StoppedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);

        public CancellationToken ApplicationStopping => new(canceled: true);

        public CancellationToken ApplicationStopped => new(canceled: true);

        public void StopApplication()
        {
        }
    }
}
