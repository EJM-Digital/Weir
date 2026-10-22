using Weir.Core.Json;
using Weir.Core.Logs;

namespace Weir.Core.Tests.Logs;

/// <summary>Reading the log for the Logs screen: which lines are kept, how each is described, and what the filters leave out.</summary>
public sealed class SuiteLogFilterTests
{
    private static string Line(string level, string message, string logger = "weir.test", string? traceback = null, string? detail = null, string? source = null, string timestamp = "2026-10-01T10:00:00Z") =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp,
            ["level"] = level,
            ["logger"] = logger,
            ["message"] = message,
            ["traceback"] = traceback,
            ["detail"] = detail,
            ["source"] = source,
        });

    private static SuiteLogsResult Read(SuiteLogFilter filter, params string[] lines)
    {
        foreach (var line in lines)
        {
            filter.Add(line);
        }

        return filter.Result();
    }

    [Fact]
    public void A_level_filter_keeps_only_that_level_and_still_counts_every_line()
    {
        var result = Read(
            new SuiteLogFilter(" error ", null, null, 50),
            Line("ERROR", "broke"),
            Line("WARNING", "careful"),
            Line("INFO", "fine"),
            Line("CRITICAL", "worse"));

        Assert.Equal(["broke"], result.Items.Select(i => i.Message));
        Assert.Equal((4L, 2L, 1L, 1L), (result.Total, result.Errors, result.Warnings, result.Information));
    }

    [Fact]
    public void An_exception_filter_keeps_only_lines_with_a_traceback_or_only_those_without()
    {
        string[] lines = [Line("ERROR", "with", traceback: "at Foo()"), Line("ERROR", "without")];

        Assert.Equal(["with"], Read(new SuiteLogFilter(null, null, true, 50), lines).Items.Select(i => i.Message));
        Assert.Equal(["without"], Read(new SuiteLogFilter(null, null, false, 50), lines).Items.Select(i => i.Message));
        Assert.Equal(["without", "with"], Read(new SuiteLogFilter(null, null, null, 50), lines).Items.Select(i => i.Message));
    }

    [Theory]
    [InlineData("NEEDLE")]
    [InlineData("in the detail")]
    [InlineData("at Needle.Run")]
    [InlineData("the-source")]
    [InlineData("corr-7")]
    [InlineData("job-9")]
    [InlineData("platform.activity")]
    public void A_search_looks_through_every_part_of_a_line(string search)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = "2026-10-01T10:00:00Z",
            ["level"] = "ERROR",
            ["logger"] = "weir.platform.activity",
            ["message"] = "needle",
            ["detail"] = "in the detail",
            ["traceback"] = "at Needle.Run",
            ["source"] = "the-source",
            ["correlation_id"] = "corr-7",
            ["job_id"] = "job-9",
        });

        var result = Read(new SuiteLogFilter(null, search, null, 50), line, Line("ERROR", "unrelated"));

        Assert.Equal(["needle"], result.Items.Select(i => i.Message));
    }

    [Fact]
    public void The_newest_lines_are_kept_when_there_are_more_than_the_limit()
    {
        var lines = Enumerable.Range(1, 4).Select(i => Line("ERROR", $"line {i}")).ToArray();

        Assert.Equal(["line 4", "line 3"], Read(new SuiteLogFilter(null, null, null, 2), lines).Items.Select(i => i.Message));
        Assert.Equal(["line 4"], Read(new SuiteLogFilter(null, null, null, 0), lines).Items.Select(i => i.Message));
        Assert.Equal(4, Read(new SuiteLogFilter(null, null, null, 1000), lines).Items.Count);
    }

    [Fact]
    public void Information_from_a_logger_that_is_not_Weirs_is_left_out_of_the_counts_too()
    {
        var result = Read(
            new SuiteLogFilter(null, null, null, 50),
            Line("INFO", "framework chatter", logger: "Microsoft.Hosting"),
            Line("WARNING", "framework warning", logger: "Microsoft.Hosting"),
            Line("INFO", "ours"));

        Assert.Equal(["ours", "framework warning"], result.Items.Select(i => i.Message));
        Assert.Equal((2L, 1L), (result.Total, result.Warnings));
        Assert.True(SuiteLogFilter.IsLowValueNoise("INFO", "Microsoft.Hosting"));
        Assert.False(SuiteLogFilter.IsLowValueNoise("ERROR", "Microsoft.Hosting"));
        Assert.False(SuiteLogFilter.IsLowValueNoise("INFO", "weir.api"));
    }

    [Theory]
    [InlineData("{\"timestamp\": \"2026-10-01T10:00:00Z\", \"message\": \"m\"}", "INFO", "weir")]
    [InlineData("{\"timestamp\": \"2026-10-01T10:00:00Z\", \"message\": \"m\", \"level\": \"  \", \"logger\": \" \"}", "INFO", "weir")]
    [InlineData("{\"timestamp\": \"2026-10-01T10:00:00Z\", \"message\": \"m\", \"level\": \"warning\", \"logger\": \"x\"}", "WARNING", "x")]
    public void A_line_with_no_level_or_logger_gets_the_usual_ones(string raw, string level, string logger)
    {
        var entry = SuiteLogFilter.Parse(raw)!;

        Assert.Equal((level, logger), (entry.Level, entry.Logger));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"message\": \"no time\"}")]
    [InlineData("{\"timestamp\": \"2026-10-01T10:00:00Z\"}")]
    [InlineData("{\"timestamp\": \"2026-10-01T10:00:00Z\", \"message\": \"  \"}")]
    public void A_line_that_is_not_a_log_entry_is_ignored(string raw)
    {
        Assert.Null(SuiteLogFilter.Parse(raw));
        Assert.Equal(0, Read(new SuiteLogFilter(null, null, null, 5), raw).Total);
    }

    [Theory]
    [InlineData("weir.processing.pass", null, "Processing")]
    [InlineData("x", "weir.processing", "Processing")]
    [InlineData("weir.platform.auth", null, "Authentication")]
    [InlineData("weir.platform.activity", null, "Activity")]
    [InlineData("weir.other", null, "System")]
    public void A_line_is_filed_under_the_part_of_Weir_that_wrote_it(string logger, string? source, string component)
    {
        var entry = SuiteLogFilter.Parse(Line("ERROR", "m", logger: logger, source: source))!;

        Assert.Equal(component, entry.Component);
    }

    [Fact]
    public void Blank_optional_parts_of_a_line_are_dropped()
    {
        var entry = SuiteLogFilter.Parse(Line("ERROR", "m", detail: "  ", traceback: "", source: " src "))!;

        Assert.Null(entry.Detail);
        Assert.Null(entry.Traceback);
        Assert.Equal("src", entry.Source);
    }

    [Fact]
    public void A_line_shows_its_timestamp_in_the_form_the_API_uses_or_not_at_all()
    {
        var shown = SuiteLogFilter.ToOut(SuiteLogFilter.Parse(Line("ERROR", "m", timestamp: "2026-10-01T10:00:00Z"))!);
        var broken = SuiteLogFilter.ToOut(SuiteLogFilter.Parse(Line("ERROR", "m", timestamp: "yesterday"))!);

        Assert.Equal("2026-10-01T10:00:00Z", ((WireString)shown!["timestamp"]).Value);
        Assert.Null(broken);
    }
}
