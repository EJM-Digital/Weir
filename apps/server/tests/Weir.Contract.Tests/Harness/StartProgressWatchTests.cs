namespace Weir.Contract.Tests.Harness;

/// <summary>A server that is still reporting is given the time it needs, and one that has gone quiet is given up on.</summary>
[ContractArea("harness")]
public sealed class StartProgressWatchTests
{
    private const int Pid = 4242;

    private readonly ManualClock _clock = new();

    [Fact]
    public void A_server_that_has_reported_nothing_is_hung_only_once_the_first_report_is_late()
    {
        var watch = new StartProgressWatch(Pid, _clock);

        _clock.Advance(StartProgressWatch.Silence);
        Assert.False(watch.IsHung(out _));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watch.IsHung(out var silentFor));
        Assert.Equal(StartProgressWatch.Silence + TimeSpan.FromSeconds(1), silentFor);
        Assert.Null(watch.Phase);
    }

    [Fact]
    public void A_start_that_keeps_reporting_is_never_hung_however_long_it_takes()
    {
        var watch = new StartProgressWatch(Pid, _clock);

        for (var step = 1; step <= 20; step++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            watch.Observe(Record("migrating", at: step));
            Assert.False(watch.IsHung(out _));
        }

        Assert.Equal("migrating", watch.Phase);
    }

    [Fact]
    public void A_record_that_has_not_changed_is_not_progress_however_often_it_is_read()
    {
        var watch = new StartProgressWatch(Pid, _clock);
        watch.Observe(Record("opening_database", at: 1));

        for (var look = 0; look < 40; look++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            watch.Observe(Record("opening_database", at: 1));
        }

        Assert.True(watch.IsHung(out var silentFor));
        Assert.Equal(TimeSpan.FromSeconds(40), silentFor);
    }

    [Fact]
    public void A_record_of_another_server_is_not_this_servers_progress()
    {
        var watch = new StartProgressWatch(Pid, _clock);
        _clock.Advance(TimeSpan.FromSeconds(20));

        watch.Observe(Record("ready", at: 1, pid: Pid + 1));
        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.True(watch.IsHung(out _));
        Assert.Null(watch.Phase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"pid\": 4242, \"phase\": \"migra")]
    [InlineData("not a record")]
    public void A_record_that_cannot_be_read_is_no_news(string? record)
    {
        var watch = new StartProgressWatch(Pid, _clock);
        _clock.Advance(TimeSpan.FromSeconds(31));

        watch.Observe(record);

        Assert.True(watch.IsHung(out _));
    }

    [Fact]
    public void A_server_that_asks_for_patience_is_given_it_up_to_the_ceiling()
    {
        var watch = new StartProgressWatch(Pid, _clock);
        watch.Observe(Record("saving_copy", at: 1, patienceSeconds: 468));

        _clock.Advance(TimeSpan.FromSeconds(468));
        Assert.False(watch.IsHung(out _));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watch.IsHung(out _));

        watch.Observe(Record("saving_copy", at: 2, patienceSeconds: 86_400));
        _clock.Advance(StartProgressWatch.PatienceCeiling);
        Assert.False(watch.IsHung(out _));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watch.IsHung(out _));
    }

    [Fact]
    public void A_later_record_that_asks_for_nothing_puts_the_ordinary_silence_back()
    {
        var watch = new StartProgressWatch(Pid, _clock);
        watch.Observe(Record("saving_copy", at: 1, patienceSeconds: 468));
        watch.Observe(Record("migrating", at: 2));

        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.True(watch.IsHung(out _));
    }

    private static string Record(string phase, int at, int pid = Pid, int patienceSeconds = 0) =>
        $$"""{ "pid": {{pid}}, "phase": "{{phase}}", "step": 0, "steps": 0, "expect_within_s": {{patienceSeconds}}, "at": "2026-10-15T00:00:{{at:00}}Z" }""";

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
