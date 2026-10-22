using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Host;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests.Startup;

/// <summary>Two servers asked to listen on the same stop event: only the one that made it acts on it.</summary>
public sealed class StopRequestListenerTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [WindowsFact("The tray's stop request is a Windows named event.")]
    [SupportedOSPlatform("windows")]
    public async Task A_listener_for_an_event_someone_else_made_says_so_and_never_stops_the_server()
    {
        var eventName = StopRequest.EventName(Environment.ProcessId) + "-" + Guid.NewGuid().ToString("N");
        var first = new Lifetime();
        var second = new Lifetime();
        var secondLog = new MessageLog<StopRequestListener>();
        using var owner = new StopRequestListener(first, new MessageLog<StopRequestListener>(), eventName);
        using var late = new StopRequestListener(second, secondLog, eventName);
        await owner.StartAsync(CancellationToken.None);
        await late.StartAsync(CancellationToken.None);

        using (var stopEvent = EventWaitHandle.OpenExisting(eventName))
        {
            stopEvent.Set();
        }

        await first.Stopped.Task.WaitAsync(Ceiling);
        Assert.Contains($"The stop event {eventName} already exists, so Weir cannot be asked to stop through it.", secondLog.Messages);
        Assert.False(second.Stopped.Task.IsCompleted);
        await owner.StopAsync(CancellationToken.None);
        await late.StopAsync(CancellationToken.None);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped.TrySetResult();
    }

    private sealed class MessageLog<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
