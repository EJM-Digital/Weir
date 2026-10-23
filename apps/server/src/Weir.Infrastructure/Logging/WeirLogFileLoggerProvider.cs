using Microsoft.Extensions.Logging;
using Weir.Core.Logs;

namespace Weir.Infrastructure.Logging;

/// <summary>Writes every log entry at or above the configured level to <see cref="WeirLogFile"/>.</summary>
public sealed class WeirLogFileLoggerProvider : ILoggerProvider
{
    private readonly WeirLogFile _file;
    private readonly TimeProvider _time;
    private readonly LogLevel _minimumLevel;
    private readonly LogAlerts? _alerts;

    public WeirLogFileLoggerProvider(WeirLogFile file, TimeProvider time, LogLevel minimumLevel, LogAlerts? alerts = null)
    {
        _file = file;
        _time = time;
        _minimumLevel = minimumLevel;
        _alerts = alerts;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // The file is owned by whoever created it.
    }

    private sealed class FileLogger(WeirLogFileLoggerProvider provider, string category) : ILogger
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

            var now = provider._time.GetUtcNow();
            var message = formatter(state, exception);
            provider._file.WriteLine(LogLineFormat.JsonLine(now, logLevel, category, message, exception, LogContext.RequestId, LogContext.JobId));
            if (provider._alerts is { } alerts && logLevel >= LogLevel.Information)
            {
                var alertText = message.Trim();
                var levelName = LogLineFormat.LevelName(logLevel);
                if (alertText.Length > 0 && !SuiteLogFilter.IsLowValueNoise(levelName, category))
                {
                    alerts.Publish(new LogAlert(now, levelName, alertText));
                }
            }
        }
    }
}
