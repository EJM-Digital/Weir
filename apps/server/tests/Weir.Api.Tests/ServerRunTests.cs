using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Host;

namespace Weir.Api.Tests;

/// <summary>
/// A server that fails to start (its port is held) has already begun its background loops. They must be stopped, and finish, before the
/// services they use are disposed, or a loop in the middle of a run fails on a disposed service and logs an error for a start that
/// was refused with a plain sentence.
/// </summary>
public sealed class ServerRunTests
{
    [Fact]
    public async Task A_start_that_fails_stops_the_background_loops_before_their_services_are_disposed()
    {
        var shared = new Shared();
        using var host = Build(shared, failToStart: true);

        Exception? told = null;

        var failure = await Assert.ThrowsAsync<IOException>(() => ServerRun.RunAsync(host, exception => told = exception));

        Assert.Same(failure, told);
        Assert.Equal("the loop ended before the service was disposed", shared.Outcome);
    }

    [Fact]
    public async Task A_server_runs_until_it_is_asked_to_stop_and_then_ends_its_loops_before_disposing_their_services()
    {
        var shared = new Shared();
        using var host = Build(shared, failToStart: false);

        var run = ServerRun.RunAsync(host);
        await shared.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("the loop ended before the service was disposed", shared.Outcome);
    }

    private static IHost Build(Shared shared, bool failToStart)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(shared);
        builder.Services.AddSingleton<Service>();
        builder.Services.AddHostedService<Loop>();
        if (failToStart)
        {
            builder.Services.AddHostedService<FailsToStart>();
        }

        return builder.Build();
    }

    private sealed class Shared
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Outcome { get; set; }
    }

    private sealed class Service(Shared shared) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            shared.Outcome ??= "the service was disposed while the loop was still running";
        }
    }

    private sealed class Loop(Service service, Shared shared) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            shared.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A run that stops mid-way gives the services a moment to be disposed, as the real loops' own work does.
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(false);
                shared.Outcome ??= service.Disposed ? "the service was disposed while the loop was still running" : "the loop ended before the service was disposed";
            }
        }
    }

    private sealed class FailsToStart(Shared shared) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await shared.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("the port is held");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
