namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Weir's heartbeat against the answers Deluno recorded to a report of a finished hand-off (<c>fixtures/recorded/deluno-side/1.0.0-rc.30.34</c>):
/// the 503 it answered once, the 401 and the 403 it answered for good. The heartbeat is the production task on a fake clock, so the minute
/// the retry waits is the test's to pass. The report Weir sent is held to the recording by <c>DelunoReportsTests</c> in the contract suite.
/// </summary>
public sealed class RecordedReportRetryTests
{
    private const string Recordings = "1.0.0-rc.30.34";
    private const string ReadyToImport = "Told % that film.mkv is ready to import";
    private const string CouldNotTell = "Weir could not tell % about film.mkv";

    [Fact]
    public async Task A_report_Deluno_recorded_answering_503_is_sent_again_on_the_next_heartbeat_and_Deluno_then_accepts_it()
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(RecordedDelunoReply.Of(Recordings, "events-completed-answered-503-not-repeated"));
        world.Deluno.Answer(RecordedDelunoReply.Of(Recordings, "events-completed"));

        await world.FinishHandoffAsync("h1");

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(1, await world.OwedAsync());

        await world.BeatAfterAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(2, world.Deluno.Reports.Count);
        Assert.Equal(world.Deluno.Reports[0].Body, world.Deluno.Reports[1].Body);
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(1, await world.ActivityAsync(ReadyToImport));
        Assert.Equal(0, await world.ActivityAsync(CouldNotTell));
    }

    [Theory]
    [InlineData("events-answered-401-not-repeated")]
    [InlineData("events-answered-403-not-repeated")]
    public async Task A_report_Deluno_recorded_answering_401_or_403_is_not_sent_again_however_long_Weir_waits(string state)
    {
        using var world = await OwedReportWorld.StartAsync();
        world.Deluno.Answer(RecordedDelunoReply.Of(Recordings, state));

        await world.FinishHandoffAsync("h1");
        await world.BeatAfterAsync(TimeSpan.FromMinutes(1));
        await world.BeatAfterAsync(TimeSpan.FromHours(1));
        await world.BeatAfterAsync(TimeSpan.FromDays(1));

        Assert.Single(world.Deluno.Reports);
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(1, await world.ActivityAsync(CouldNotTell));
        Assert.Equal(0, await world.ActivityAsync(ReadyToImport));
    }
}
