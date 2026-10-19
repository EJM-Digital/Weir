using System.Diagnostics;
using System.Text.RegularExpressions;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// tray-host.log is the tray's one record of what it did. The scenarios that judge a run read it line by line and take the time from the start
/// of each line, so its format is a promise; and a log that cannot be written must not stop the tray.
/// </summary>
public sealed partial class TrayLogTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    private string LogPath => Path.Combine(_home.Path, "tray-host.log");

    // The pattern scripts/scenarios-ci/tray-log.mjs strips from the front of each line, and the VM suite's log check reads the time with.
    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] (.*)$")]
    private static partial Regex Line();

    [Fact]
    public void A_line_is_a_time_in_brackets_and_the_message()
    {
        var before = DateTime.Now.AddSeconds(-1);

        TrayLog.Write("Weir is healthy on http://127.0.0.1:9347/");

        var line = Assert.Single(File.ReadAllLines(LogPath));
        var match = Line().Match(line);
        Assert.True(match.Success, line);
        Assert.Equal("Weir is healthy on http://127.0.0.1:9347/", match.Groups[1].Value);
        var written = DateTime.ParseExact(line[1..20], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(written, before, DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public void The_log_grows_by_a_line_at_a_time_and_earlier_lines_are_never_rewritten()
    {
        TrayLog.Write("first");
        var afterFirst = File.ReadAllText(LogPath);

        TrayLog.Write("second");
        TrayLog.Write("third");

        var text = File.ReadAllText(LogPath);
        Assert.StartsWith(afterFirst, text, StringComparison.Ordinal);
        Assert.Equal(["first", "second", "third"], File.ReadAllLines(LogPath).Select(line => Line().Match(line).Groups[1].Value));
    }

    [Fact]
    public void A_message_of_several_lines_is_one_write_after_which_the_next_line_starts_clean()
    {
        TrayLog.Write("Background work stopped after an error:\nSystem.InvalidOperationException: boom");
        TrayLog.Write("next");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.Matches(Line(), lines[0]);
        Assert.Equal("System.InvalidOperationException: boom", lines[1]);
        Assert.Matches(Line(), lines[2]);
    }

    [Fact]
    public void The_home_is_looked_for_at_every_write_so_a_moved_home_gets_the_next_line()
    {
        using var other = TempDirectory.AsWeirHome();

        TrayLog.Write("in the other home");

        Assert.Equal("in the other home", Line().Match(Assert.Single(File.ReadAllLines(Path.Combine(other.Path, "tray-host.log")))).Groups[1].Value);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task Writers_on_several_threads_never_share_a_line()
    {
        var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var line = 0; line < 50; line++)
            {
                TrayLog.Write($"writer {writer} line {line}");
            }
        }));

        await Task.WhenAll(writers);

        var messages = File.ReadAllLines(LogPath).Select(line => Line().Match(line)).ToList();
        Assert.All(messages, match => Assert.True(match.Success, match.Value));
        var expected = Enumerable.Range(0, 8).SelectMany(writer => Enumerable.Range(0, 50).Select(line => $"writer {writer} line {line}"));
        Assert.Equal(expected.Order(StringComparer.Ordinal), messages.Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_log_that_cannot_be_written_goes_to_the_trace_and_the_tray_carries_on()
    {
        // The home is a file, so no folder can be made in it.
        var notAFolder = Path.Combine(_home.Path, "a-file");
        File.WriteAllText(notAFolder, "not a folder");
        Environment.SetEnvironmentVariable(Program.RuntimeHomeVariable, Path.Combine(notAFolder, "home"));
        using var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        Trace.Listeners.Add(listener);
        try
        {
            TrayLog.Write("a line nowhere can keep");
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Environment.SetEnvironmentVariable(Program.RuntimeHomeVariable, _home.Path);
        }

        Assert.Contains("Weir tray could not write tray-host.log", trace.ToString(), StringComparison.Ordinal);
        Assert.Contains("a line nowhere can keep", trace.ToString(), StringComparison.Ordinal);
    }
}
