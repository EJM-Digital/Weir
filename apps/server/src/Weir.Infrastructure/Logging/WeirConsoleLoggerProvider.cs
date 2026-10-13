using Microsoft.Extensions.Logging;

namespace Weir.Infrastructure.Logging;

/// <summary>
/// Writes every log entry at or above the configured level to the console in Weir's plain format (local time, level, logger name and
/// message), through <see cref="NonBlockingConsole"/>, so that a log call never waits for a console that is not being read. This
/// replaces the framework's console logger, whose write into a full pipe stopped the server before it had logged anything.
/// </summary>
public sealed class WeirConsoleLoggerProvider : ILoggerProvider
{
    private readonly NonBlockingConsole _console;
    private readonly TimeProvider _time;
    private readonly LogLevel _minimumLevel;

    public WeirConsoleLoggerProvider(NonBlockingConsole console, TimeProvider time, LogLevel minimumLevel)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(this, categoryName);

    public void Dispose()
    {
        // The console is owned by whoever created it.
    }

    private sealed class ConsoleLogger(WeirConsoleLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider._console.WriteLine(LogLineFormat.ConsoleLine(provider._time.GetUtcNow(), logLevel, category, formatter(state, exception), exception));
        }
    }
}
