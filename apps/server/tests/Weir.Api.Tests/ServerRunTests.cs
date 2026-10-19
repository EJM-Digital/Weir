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
        Assert.Equal(Ordered, shared.Outcome);
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

        Assert.Equal(Ordered, shared.Outcome);
    }

    private static IHost Build(Shared shared, bool failToStart)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(shared);
        builder.Services.AddSingleton<Service>();
        builder.Services.AddHostedService<Loop>();
        builder.Services.AddHostedService<ReleasesLoopWhenStopped>();
        if (failToStart)
        {
            builder.Services.AddHostedService<FailsToStart>();
        }

        return builder.Build();
    }

    private const string Ordered = "the loop ended before the service was disposed";

    private const string Unordered = "the service was disposed while the loop was still running";

    private sealed class Shared
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>What a loop that is in the middle of a run waits for before it finishes.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool LoopFinished { get; set; }

        public string? Outcome { get; set; }
    }

    /// <summary>A service the loop uses, which records whether the loop had finished when it was disposed.</summary>
    private sealed class Service(Shared shared) : IDisposable
    {
        public void Dispose()
        {
            shared.Outcome ??= shared.LoopFinished ? Ordered : Unordered;
            shared.Release.TrySetResult();
        }
    }

    /// <summary>A loop in the middle of a run, which only a stop of the host lets finish.</summary>
    private sealed class Loop(Service service, Shared shared) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = service;
            shared.Started.TrySetResult();
            await shared.Release.Task.ConfigureAwait(false);
            shared.LoopFinished = true;
        }
    }

    /// <summary>Stopped before the loop (hosts stop services in reverse order), it lets the loop finish: only a host that is stopped does.</summary>
    private sealed class ReleasesLoopWhenStopped(Shared shared) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            shared.Release.TrySetResult();
            return Task.CompletedTask;
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
