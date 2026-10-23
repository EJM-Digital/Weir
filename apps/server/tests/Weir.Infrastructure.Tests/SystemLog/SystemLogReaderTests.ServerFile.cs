using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Weir.Core.Logs;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.SystemLog;

namespace Weir.Infrastructure.Tests.SystemLog;

public sealed partial class SystemLogReaderTests
{
    [Fact]
    public async Task A_server_line_whose_time_cannot_be_read_is_left_out_of_the_list()
    {
        AddServerLine(minutesAgo: 5, LogLevel.Warning, "weir.processing", "It is slow");
        _logFile.WriteLine(Regex.Replace(
            LogLineFormat.JsonLine(Noon, LogLevel.Warning, "weir.processing", "No time on this one", null, null, null),
            "\"timestamp\": \"[^\"]*\"",
            "\"timestamp\": \"some time ago\""));

        var page = await Read(new SystemLogFilter());

        Assert.Equal("It is slow", Assert.Single(page.Rows).Title);
    }

    [Fact]
    public async Task A_server_log_that_cannot_be_opened_is_said_so_and_the_rest_of_the_list_is_still_given()
    {
        await AddEvent(minutesAgo: 30, "auth.login_succeeded", "Signed in", result: "success");
        AddServerLine(minutesAgo: 10, LogLevel.Warning, "weir.processing", "It is slow");
        var log = new CapturingLogger<SystemLogReader>();
        var reader = new SystemLogReader(_logFile, log);

        using (var unreadable = UnreadablePath.Create(_logFile.Path))
        {
            if (unreadable is null)
            {
                return;
            }

            var page = await _store.WithUnitOfWork(uow => reader.ReadAsync(uow, new SystemLogFilter(), SystemLogOrder.Newest, null, 50), commit: false);

            Assert.Equal("Signed in", Assert.Single(page.Rows).Title);
        }

        Assert.True(log.Logged(LogLevel.Warning, "could not be opened"));
    }

    [Fact]
    public async Task A_server_log_that_was_never_written_adds_nothing_to_the_list()
    {
        await AddEvent(minutesAgo: 30, "auth.login_succeeded", "Signed in", result: "success");
        var reader = new SystemLogReader(new WeirLogFile(_store.Home.Join("never", "weir.log"), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Noon)), new CapturingLogger<SystemLogReader>());
        File.Delete(_store.Home.Join("never", "weir.log"));

        var page = await _store.WithUnitOfWork(uow => reader.ReadAsync(uow, new SystemLogFilter(), SystemLogOrder.Newest, null, 50), commit: false);

        Assert.Equal("Signed in", Assert.Single(page.Rows).Title);
    }
}
