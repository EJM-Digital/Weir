using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Weir.Infrastructure.Logging;

/// <summary>
/// Weir's log line formats: JSON lines in <c>weir.log</c> and plain console lines. The formats are
/// fixed so the Logs screen, support bundles, existing log readers and support tooling keep parsing them.
/// </summary>
public static class LogLineFormat
{
    /// <summary>
    /// Reads <c>WEIR_LOG_LEVEL</c>: a known level name in any case, otherwise INFO. <c>NOTSET</c> logs
    /// everything.
    /// </summary>
    public static LogLevel ParseMinimumLevel(string? name) => (name ?? string.Empty).ToUpperInvariant() switch
    {
        "CRITICAL" or "FATAL" => LogLevel.Critical,
        "ERROR" => LogLevel.Error,
        "WARNING" or "WARN" => LogLevel.Warning,
        "DEBUG" => LogLevel.Debug,
        "NOTSET" => LogLevel.Trace,
        _ => LogLevel.Information,
    };

    /// <summary>The level name written in log lines (DEBUG, INFO, WARNING, ERROR, CRITICAL) for a .NET level.</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "NOTSET",
    };

    /// <summary>ISO 8601 UTC with a <c>Z</c> suffix: microseconds when there are any, none on an exact second.</summary>
    public static string IsoTimestamp(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var micro = (utc.Ticks % TimeSpan.TicksPerSecond) / 10;
        return micro == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + micro.ToString("D6", CultureInfo.InvariantCulture) + "Z";
    }

    /// <summary>
    /// One line of <c>weir.log</c>: fixed keys in a fixed order, <c>", "</c> and <c>": "</c> separators, and
    /// every non-ASCII character escaped as <c>\uXXXX</c>. There is no source file and line for a log call,
    /// so <c>source</c> is null.
    /// </summary>
    public static string JsonLine(DateTimeOffset now, LogLevel level, string logger, string message, Exception? exception, string? correlationId, string? jobId)
    {
        var builder = new StringBuilder(256);
        builder.Append("{\"timestamp\": ");
        AppendString(builder, IsoTimestamp(now));
        builder.Append(", \"level\": ");
        AppendString(builder, LevelName(level));
        builder.Append(", \"logger\": ");
        AppendString(builder, logger);
        builder.Append(", \"message\": ");
        AppendString(builder, message);
        builder.Append(", \"source\": null, \"detail\": null, \"correlation_id\": ");
        AppendString(builder, correlationId);
        builder.Append(", \"job_id\": ");
        AppendString(builder, jobId);
        if (exception is not null)
        {
            builder.Append(", \"traceback\": ");
            AppendString(builder, exception.ToString().Trim());
        }

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>The console line: local time (<c>yyyy-MM-dd HH:mm:ss,fff</c>), level, logger name and message, space-separated.</summary>
    public static string ConsoleLine(DateTimeOffset now, LogLevel level, string logger, string message, Exception? exception)
    {
        var local = now.ToLocalTime();
        var line = local.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture) + " " + LevelName(level) + " " + logger + " " + message;
        return exception is null ? line : line + Environment.NewLine + exception.ToString();
    }

    private static void AppendString(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c is < ' ' or > '~')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
