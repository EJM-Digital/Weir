using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Logging;

namespace Weir.Infrastructure.Tests.Logging;

/// <summary>Weir's JSON log lines escape every character a JSON reader would otherwise mis-read, and name the levels the Logs screen knows.</summary>
public sealed class LogLineFormatEscapingTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("a\tb", "a\\tb")]
    [InlineData("a\bb", "a\\bb")]
    [InlineData("a\fb", "a\\fb")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\rb", "a\\rb")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("aéb", "a\\u00e9b")]
    public void A_message_is_escaped_the_way_JSON_requires(string message, string escaped)
    {
        var line = LogLineFormat.JsonLine(Now, LogLevel.Information, "weir", message, null, null, null);

        Assert.Contains("\"message\": \"" + escaped + "\"", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("FATAL", LogLevel.Critical)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("NOTSET", LogLevel.Trace)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("nonsense", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    public void The_minimum_level_is_read_from_its_name_in_any_case(string? name, LogLevel expected)
    {
        Assert.Equal(expected, LogLineFormat.ParseMinimumLevel(name));
    }

    [Fact]
    public void The_console_line_carries_the_failure_under_the_message_when_there_is_one()
    {
        var plain = LogLineFormat.ConsoleLine(Now, LogLevel.Error, "weir", "It broke", null);
        var failed = LogLineFormat.ConsoleLine(Now, LogLevel.Error, "weir", "It broke", new InvalidOperationException("why"));

        Assert.EndsWith("ERROR weir It broke", plain, StringComparison.Ordinal);
        Assert.Contains("It broke" + Environment.NewLine + "System.InvalidOperationException: why", failed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_with_no_name_in_the_log_is_written_as_not_set()
    {
        Assert.Equal("NOTSET", LogLineFormat.LevelName(LogLevel.None));
    }
}
