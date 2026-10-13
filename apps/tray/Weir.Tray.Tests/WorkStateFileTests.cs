using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray trusts the server's work-state.json only when it is readable and fresh. Anything else reads as "unknown",
/// which never lets an update install (#875).
/// </summary>
public sealed class WorkStateFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _home = TempDirectory.Create();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void A_fresh_answer_of_idle_reads_as_idle()
    {
        ServerSays.Idle(_home.Path, Now);

        Assert.Equal(ServerWork.Idle, WorkStateFile.Read(_home.Path, Now));
    }

    [Fact]
    public void A_fresh_answer_of_busy_reads_as_busy()
    {
        ServerSays.Busy(_home.Path, Now);

        Assert.Equal(ServerWork.Busy, WorkStateFile.Read(_home.Path, Now));
    }

    [Fact]
    public void No_answer_reads_as_unknown()
    {
        Assert.Equal(ServerWork.Unknown, WorkStateFile.Read(_home.Path, Now));
    }

    [Fact]
    public void An_answer_older_than_the_limit_reads_as_unknown()
    {
        ServerSays.Idle(_home.Path, Now);

        Assert.Equal(ServerWork.Idle, WorkStateFile.Read(_home.Path, Now + WorkStateFile.MaxAge));
        Assert.Equal(ServerWork.Unknown, WorkStateFile.Read(_home.Path, Now + WorkStateFile.MaxAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_answer_from_far_in_the_future_reads_as_unknown()
    {
        ServerSays.Idle(_home.Path, Now + WorkStateFile.MaxAge + TimeSpan.FromSeconds(1));

        Assert.Equal(ServerWork.Unknown, WorkStateFile.Read(_home.Path, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"busy\":fal")]
    [InlineData("[]")]
    [InlineData("{\"busy\":false}")]
    [InlineData("{\"checkedAt\":\"2026-09-30T12:00:00Z\"}")]
    [InlineData("{\"busy\":\"no\",\"checkedAt\":\"2026-09-30T12:00:00Z\"}")]
    public void An_answer_that_cannot_be_understood_reads_as_unknown(string contents)
    {
        File.WriteAllText(Path.Combine(_home.Path, WorkStateFile.FileName), contents);

        Assert.Equal(ServerWork.Unknown, WorkStateFile.Read(_home.Path, Now));
    }

    [Fact]
    public void An_answer_that_stays_refused_reads_as_not_read_which_says_nothing()
    {
        ServerSays.Idle(_home.Path, Now);
        using var held = new FileStream(Path.Combine(_home.Path, WorkStateFile.FileName), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(ServerWork.NotRead, WorkStateFile.Read(_home.Path, Now));
    }

    [Fact]
    public void The_countdown_keeps_its_place_through_an_answer_that_was_not_read()
    {
        var countdown = new IdleCountdown(TimeSpan.FromMinutes(5));
        Assert.False(countdown.Observe(ServerWork.Idle, Now));

        Assert.False(countdown.Observe(ServerWork.NotRead, Now + TimeSpan.FromMinutes(3)));
        Assert.True(countdown.Observe(ServerWork.Idle, Now + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_countdown_starts_again_through_an_answer_that_is_unknown()
    {
        var countdown = new IdleCountdown(TimeSpan.FromMinutes(5));
        Assert.False(countdown.Observe(ServerWork.Idle, Now));

        Assert.False(countdown.Observe(ServerWork.Unknown, Now + TimeSpan.FromMinutes(3)));
        Assert.False(countdown.Observe(ServerWork.Idle, Now + TimeSpan.FromMinutes(5)));
    }
}
