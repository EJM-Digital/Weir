using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Weir.Api.Tests;

/// <summary>A logger factory that keeps every message with its category, so a test can assert on what the server wrote.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    // Background timers log through this factory too, from their own threads.
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
