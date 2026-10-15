using System.Globalization;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A report of a finished hand-off that Deluno could not take yet (a 503 from its events route, say) is sent again by the media manager
/// heartbeat, which runs every minute; one Deluno refused for good (a 401 or 403) is not. Deluno is a real HTTP server on the loopback
/// address, the heartbeat is the production task, and the clock is the test's own, so no test waits for a minute to pass.
/// </summary>
public sealed class OwedReportRetryTests
{
    private const string ReadyToImport = "Told % that film.mkv is ready to import";
    private const string CouldNotTell = "Weir could not tell % about film.mkv";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_report_Deluno_answers_503_once_is_sent_again_on_the_next_heartbeat_and_stops_being_owed()
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(DelunoReply.Unavailable());

        await world.FinishHandoffAsync("h1");

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(1, await world.OwedAsync());
        Assert.Contains("which is not answering", await world.ReasonAsync(), StringComparison.Ordinal);

        await world.BeatAfterAsync(Minute);

        Assert.Equal(2, world.Deluno.Reports.Count);
        Assert.Equal(world.Deluno.Reports[0].Body, world.Deluno.Reports[1].Body);
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(1, await world.ActivityAsync(ReadyToImport));
        Assert.Contains("is answering again, and Weir has told it this file is ready", await world.ReasonAsync(), StringComparison.Ordinal);

        await world.BeatAfterAsync(Minute);

        Assert.Equal(2, world.Deluno.Reports.Count);
    }

    [Fact]
    public async Task A_report_that_failed_is_not_sent_again_before_the_wait_is_over()
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(DelunoReply.Unavailable());
        await world.FinishHandoffAsync("h1");

        await world.BeatAfterAsync(TimeSpan.FromSeconds(30));

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(1, await world.OwedAsync());
    }

    [Theory]
    [InlineData("120", 2)]
    [InlineData("300", 5)]
    public async Task A_Retry_After_in_seconds_is_waited_out_before_the_report_is_sent_again(string retryAfter, int minutes)
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(DelunoReply.Unavailable(("Retry-After", retryAfter)));
        await world.FinishHandoffAsync("h1");

        for (var passed = 1; passed < minutes; passed++)
        {
            await world.BeatAfterAsync(Minute);
            Assert.Single(world.Deluno.Reports);
        }

        await world.BeatAfterAsync(Minute);

        Assert.Equal(2, world.Deluno.Reports.Count);
        Assert.Equal(0, await world.OwedAsync());
    }

    [Fact]
    public async Task A_Retry_After_that_is_a_date_is_waited_out_before_the_report_is_sent_again()
    {
        using var world = await OwedReportWorld.StartAsync();
        var until = world.Fixture.Store.Clock.GetUtcNow().AddMinutes(3).ToString("R", CultureInfo.InvariantCulture);
        world.Deluno.Answer(DelunoReply.Unavailable(("Retry-After", until)));
        await world.FinishHandoffAsync("h1");

        await world.BeatAfterAsync(Minute);
        await world.BeatAfterAsync(Minute);
        Assert.Single(world.Deluno.Reports);

        await world.BeatAfterAsync(Minute);

        Assert.Equal(2, world.Deluno.Reports.Count);
    }

    [Fact]
    public async Task A_report_Deluno_keeps_refusing_is_sent_again_after_a_minute_then_two_then_four()
    {
        using var world = await OwedReportWorld.StartAsync();
        for (var refused = 0; refused < 4; refused++)
        {
            world.Deluno.Answer(DelunoReply.Unavailable());
        }

        await world.FinishHandoffAsync("h1");
        var sentAt = new List<int> { 0 };
        for (var minute = 1; minute <= 16; minute++)
        {
            await world.BeatAfterAsync(Minute);
            if (world.Deluno.Reports.Count > sentAt.Count)
            {
                sentAt.Add(minute);
            }
        }

        Assert.Equal([0, 1, 3, 7, 15], sentAt);
        Assert.Equal(0, await world.OwedAsync());
    }

    [Fact]
    public async Task A_report_Deluno_refuses_for_a_week_is_given_up_and_the_person_is_told()
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.AnswerEverythingWith(DelunoReply.Unavailable());

        await world.FinishHandoffAsync("h1");
        var hours = 0;
        while (await world.OwedAsync() > 0 && hours++ < 10 * 24)
        {
            await world.BeatAfterAsync(TimeSpan.FromHours(1));
        }

        Assert.Equal(0, await world.OwedAsync());
        Assert.InRange(hours, 7 * 24, 7 * 24 + 2);
        Assert.Equal(1, await world.ActivityAsync(CouldNotTell));
        Assert.DoesNotContain("not answering", await world.ReasonAsync(), StringComparison.Ordinal);
        var sent = world.Deluno.Reports.Count;

        await world.BeatAfterAsync(TimeSpan.FromHours(1));

        Assert.Equal(sent, world.Deluno.Reports.Count);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task A_report_Deluno_answered_with_a_failure_that_may_pass_stays_owed_and_is_sent_again(int status)
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(DelunoReply.Of(status));

        await world.FinishHandoffAsync("h1");

        Assert.Equal(1, await world.OwedAsync());
        Assert.Equal(0, await world.ActivityAsync(CouldNotTell));

        await world.BeatAfterAsync(Minute);

        Assert.Equal(2, world.Deluno.Reports.Count);
        Assert.Equal(0, await world.OwedAsync());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task A_report_Deluno_refused_for_good_is_not_sent_again_and_the_person_is_told(int status)
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(DelunoReply.Of(status));

        await world.FinishHandoffAsync("h1");

        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(1, await world.ActivityAsync(CouldNotTell));

        await world.BeatAfterAsync(TimeSpan.FromHours(1));
        await world.BeatAfterAsync(TimeSpan.FromDays(1));

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(0, await world.ActivityAsync(ReadyToImport));
    }

    [Fact]
    public async Task A_report_owed_while_Deluno_was_down_is_sent_on_the_first_heartbeat_that_finds_it_answering()
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Stop();

        await world.FinishHandoffAsync("h1");
        await world.BeatAfterAsync(Minute);
        await world.BeatAfterAsync(Minute);

        Assert.Empty(world.Deluno.Reports);
        Assert.Equal(1, await world.OwedAsync());

        world.Deluno.Start();
        await world.BeatAfterAsync(Minute);

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(1, await world.ActivityAsync(ReadyToImport));
    }

    [Fact]
    public async Task A_heartbeat_that_cannot_look_up_what_is_owed_still_saves_every_managers_answer()
    {
        using var world = await OwedReportWorld.StartAsync();
        await world.Fixture.AddConnectionAsync("deluno", world.Deluno.BaseUrl, "k2");
        await world.Fixture.Store.Execute("PRAGMA foreign_keys=OFF; DROP TABLE media_manager_handoffs");

        await world.BeatAsync();

        var connections = await world.Fixture.Db(uow => world.Fixture.ConnectionStore.ListAsync(uow), commit: false);
        Assert.Equal(2, connections.Count);
        Assert.All(connections, connection => Assert.True(connection.LastTestOk));
    }
}
