using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Logging;

namespace Weir.Infrastructure.Tests.Logging;

/// <summary>The lines the System screen's log shows live: each warning, error and information line of Weir's own reaches the open streams, and nothing quieter or from elsewhere does.</summary>
public sealed class LogAlertsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly WeirLogFile _file;
    private readonly LogAlerts _alerts = new();
    private readonly WeirLogFileLoggerProvider _provider;

    public LogAlertsTests()
    {
        _file = new WeirLogFile(_temp.Join("weir.log"), TimeProvider.System);
        _provider = new WeirLogFileLoggerProvider(_file, TimeProvider.System, LogLevel.Debug, _alerts);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _file.Dispose();
        _temp.Dispose();
    }

    private static async Task<LogAlert> NextAsync(BroadcastSubscription<LogAlert> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var alert in subscription.ReadAllAsync(timeout.Token))
        {
            return alert;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    [Theory]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public async Task A_warning_or_worse_reaches_the_open_streams_with_its_level_and_message(LogLevel level, string name)
    {
        using var subscription = _alerts.Subscribe();

        _provider.CreateLogger("weir.test").Log(level, "The disk is nearly full on {Drive}.", "D:");

        var alert = await NextAsync(subscription);
        Assert.Equal((name, "The disk is nearly full on D:."), (alert.Level, alert.Message));
    }

    [Fact]
    public async Task Information_from_weirs_own_loggers_reaches_the_open_streams_too()
    {
        using var subscription = _alerts.Subscribe();

        _provider.CreateLogger("weir.test").LogInformation("Scanned {Library}.", "Movies");

        var alert = await NextAsync(subscription);
        Assert.Equal(("INFO", "Scanned Movies."), (alert.Level, alert.Message));
    }

    [Fact]
    public async Task Information_from_other_loggers_and_debug_lines_are_written_to_the_file_but_never_published()
    {
        using var subscription = _alerts.Subscribe();
        _provider.CreateLogger("Some.Library.Chatter").LogInformation("Nothing to see.");
        var logger = _provider.CreateLogger("weir.test");

        logger.LogDebug("Nor here.");
        logger.LogWarning("This one needs a look.");

        Assert.Equal("This one needs a look.", (await NextAsync(subscription)).Message);
        var lines = new List<string>();
        _file.ReadLines(lines.Add);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public async Task A_line_with_nothing_but_blanks_is_written_but_never_published()
    {
        using var subscription = _alerts.Subscribe();
        var logger = _provider.CreateLogger("weir.test");

        logger.LogWarning("   ");
        logger.LogWarning("Something to read.");

        Assert.Equal("Something to read.", (await NextAsync(subscription)).Message);
    }

    [Fact]
    public void No_level_at_all_is_never_enabled()
    {
        Assert.False(_provider.CreateLogger("weir.test").IsEnabled(LogLevel.None));
    }

    [Fact]
    public async Task A_line_below_the_minimum_level_is_neither_written_nor_published()
    {
        using var quiet = new WeirLogFileLoggerProvider(_file, TimeProvider.System, LogLevel.Error, _alerts);
        using var subscription = _alerts.Subscribe();
        var logger = quiet.CreateLogger("weir.test");

        logger.LogWarning("Too quiet for this install.");
        logger.LogError("Loud enough.");

        Assert.Equal("Loud enough.", (await NextAsync(subscription)).Message);
    }
}
