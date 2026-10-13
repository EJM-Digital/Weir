using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// An update installs itself only after the server has been idle for the whole period without a break (#875). Time is
/// fake and the server's answers are files written at chosen moments, so every case is exact.
/// </summary>
public sealed class IdleInstallTests : IDisposable
{
    private static readonly TimeSpan Step = IdleInstall.PollInterval;

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly IdleInstall _install;

    public IdleInstallTests()
    {
        _install = new IdleInstall(_home.Path, _clock);
    }

    public void Dispose() => _home.Dispose();

    [Fact]
    public void An_update_does_not_install_while_the_server_is_busy()
    {
        for (var elapsed = TimeSpan.Zero; elapsed <= TimeSpan.FromHours(2); elapsed += Step)
        {
            Assert.False(PollAfter(Step, ServerSays.Busy));
        }
    }

    [Fact]
    public void An_update_installs_once_the_server_has_been_idle_for_the_whole_period()
    {
        Assert.False(PollAfter(TimeSpan.Zero, ServerSays.Idle));
        Assert.False(PollAfter(IdleInstall.IdlePeriod - Step, ServerSays.Idle));
        Assert.True(PollAfter(Step, ServerSays.Idle));
    }

    [Fact]
    public void Work_starting_during_the_wait_starts_the_period_again()
    {
        Assert.False(PollAfter(TimeSpan.Zero, ServerSays.Idle));
        Assert.False(PollAfter(IdleInstall.IdlePeriod - Step, ServerSays.Idle));
        Assert.False(PollAfter(Step, ServerSays.Busy));

        Assert.False(PollAfter(Step, ServerSays.Idle));
        Assert.False(PollAfter(IdleInstall.IdlePeriod - Step, ServerSays.Idle));
        Assert.True(PollAfter(Step, ServerSays.Idle));
    }

    [Fact]
    public void A_server_that_stops_answering_is_not_idle()
    {
        Assert.False(PollAfter(TimeSpan.Zero, ServerSays.Idle));

        // The last answer stays in the file, growing older, as it does when the server has stopped or hung.
        for (var elapsed = Step; elapsed <= TimeSpan.FromHours(1); elapsed += Step)
        {
            _clock.Advance(Step);
            Assert.False(_install.Poll());
        }
    }

    [Fact]
    public void A_server_that_answers_again_after_a_silence_starts_the_period_from_then()
    {
        Assert.False(PollAfter(TimeSpan.Zero, ServerSays.Idle));
        _clock.Advance(WorkStateFile.MaxAge + Step);
        Assert.False(_install.Poll());

        Assert.False(PollAfter(Step, ServerSays.Idle));
        Assert.False(PollAfter(IdleInstall.IdlePeriod - Step, ServerSays.Idle));
        Assert.True(PollAfter(Step, ServerSays.Idle));
    }

    [Fact(Timeout = 10_000)]
    public async Task Waiting_returns_once_the_server_has_been_idle_for_the_whole_period()
    {
        ServerSays.Idle(_home.Path, _clock.GetUtcNow());
        var waiting = _install.WaitUntilIdleAsync(CancellationToken.None);
        var polls = 0;

        while (await Task.WhenAny(waiting, _clock.NextDelay()) != waiting)
        {
            polls++;
            AdvanceOneStep(ServerSays.Idle);
        }

        await waiting;
        Assert.Equal((int)(IdleInstall.IdlePeriod / Step), polls);
    }

    [Fact(Timeout = 10_000)]
    public async Task Waiting_keeps_waiting_through_busy_answers()
    {
        ServerSays.Idle(_home.Path, _clock.GetUtcNow());
        var waiting = _install.WaitUntilIdleAsync(CancellationToken.None);

        for (var polls = 0; polls < (int)(2 * IdleInstall.IdlePeriod / Step); polls++)
        {
            Assert.NotSame(waiting, await Task.WhenAny(waiting, _clock.NextDelay()));
            AdvanceOneStep(ServerSays.Busy);
        }

        Assert.False(waiting.IsCompleted);
    }

    [Fact(Timeout = 10_000)]
    public async Task Waiting_ends_when_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        ServerSays.Busy(_home.Path, _clock.GetUtcNow());
        var waiting = _install.WaitUntilIdleAsync(cancel.Token);
        await _clock.NextDelay();

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private bool PollAfter(TimeSpan wait, Action<string, DateTimeOffset> serverSays)
    {
        _clock.Advance(wait);
        serverSays(_home.Path, _clock.GetUtcNow());
        return _install.Poll();
    }

    // The answer is written for the moment the clock is about to reach, so it is in place when the wait wakes.
    private void AdvanceOneStep(Action<string, DateTimeOffset> serverSays)
    {
        serverSays(_home.Path, _clock.GetUtcNow() + Step);
        _clock.Advance(Step);
    }

    [Fact]
    public void An_answer_that_cannot_be_read_just_now_neither_counts_as_quiet_nor_starts_the_period_again()
    {
        Assert.False(PollAfter(TimeSpan.Zero, ServerSays.Idle));
        Assert.False(PollAfter(IdleInstall.IdlePeriod - Step, ServerSays.Idle));

        // The server is renaming a new answer over the old one: Windows refuses the open for as long as this lasts.
        _clock.Advance(Step);
        using (new FileStream(Path.Combine(_home.Path, WorkStateFile.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_install.Poll());
        }

        Assert.True(PollAfter(TimeSpan.Zero, ServerSays.Idle));
    }
}
