using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The edges of the small runtime helpers: a deadline that is finished with, and sizes of files that are not there.</summary>
public sealed class SmallRuntimeGapsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_deadline_that_is_disposed_is_not_restarted_and_does_not_cancel_when_its_timer_was_already_queued()
    {
        var deadline = new AwakeDeadline(TimeSpan.FromSeconds(10), _time, pulse: null);
        var token = deadline.Token;
        deadline.Dispose();
        deadline.Dispose();

        deadline.Restart();
        _time.Advance(TimeSpan.FromMinutes(1));
        deadline.OnTimer(null);

        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void A_deadline_without_a_limit_never_cancels()
    {
        using var deadline = new AwakeDeadline(limit: null, _time, pulse: null);

        deadline.Restart();
        _time.Advance(TimeSpan.FromDays(1));
        deadline.OnTimer(null);

        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_database_that_is_not_there_asks_for_the_base_patience()
    {
        using var folder = new TempDirectory();

        Assert.Equal(StartupPatience.Base, StartupPatience.ForDatabase(folder.Join("missing.db")));
    }

    [Fact]
    public void A_file_deleted_after_it_was_listed_counts_for_nothing_in_the_data_footprint()
    {
        using var folder = new TempDirectory();

        Assert.Equal(0, DataFootprint.LengthOrZero(new FileInfo(folder.Join("gone.bin"))));
    }
}
