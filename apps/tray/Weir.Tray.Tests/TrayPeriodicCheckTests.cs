using System.Threading.Channels;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray looks for an update every check_interval_minutes (1 to 10080 in the settings file), counted on the tray's clock, and
/// does not while update checking is off. The clock is the fake one, so no test waits for real time.
/// </summary>
public sealed class TrayPeriodicCheckTests : IDisposable
{
    private const int IntervalMinutes = 90;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(IntervalMinutes);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<DateTimeOffset> _checks = Channel.CreateUnbounded<DateTimeOffset>();
    private readonly Channel<bool> _choicesRead = Channel.CreateUnbounded<bool>();

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _home.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(IntervalMinutes)]
    [InlineData(10080)]
    public async Task The_next_check_comes_one_interval_after_the_last_and_not_before(int minutes)
    {
        var interval = TimeSpan.FromMinutes(minutes);
        var started = _clock.GetUtcNow();
        await StartAsync(minutes, readChoice: null);

        _clock.Advance(interval - TimeSpan.FromSeconds(1));
        Assert.False(_checks.Reader.TryRead(out _));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var first = await NextCheckAsync();
        _clock.Advance(interval);
        var second = await NextCheckAsync();

        Assert.Equal(started + interval, first);
        Assert.Equal(started + (2 * interval), second);
    }

    [Fact]
    public async Task No_check_is_made_while_update_checking_is_off_and_the_next_tick_checks_once_it_is_on_again()
    {
        var started = _clock.GetUtcNow();
        var checking = false;
        await StartAsync(IntervalMinutes, readChoice: () => new UpdateSettings { CheckForUpdates = checking });

        _clock.Advance(Interval);
        await _choicesRead.Reader.ReadAsync().AsTask().WaitAsync(Ceiling);
        checking = true;
        _clock.Advance(Interval);
        var first = await NextCheckAsync();

        Assert.Equal(started + (2 * Interval), first);
    }

    private async Task StartAsync(int minutes, Func<UpdateSettings>? readChoice)
    {
        var settings = new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = minutes };
        var service = new FakeUpdateService { FindsUpdate = false };
        service.OnCheck = () => _checks.Writer.TryWrite(_clock.GetUtcNow());
        var updates = new TrayUpdates(
            service,
            _home.Path,
            settings,
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _ => { }, ApplyNow: () => { }),
            _clock,
            _shutdown.Token,
            readChoice is null ? null : () =>
            {
                var choice = readChoice();
                _choicesRead.Writer.TryWrite(true);
                return choice;
            });

        updates.Start();

        await WaitForTimerAsync(TimeSpan.FromMinutes(minutes));
    }

    // The periodic loop runs on the thread pool, so time is moved only once its timer exists.
    private async Task WaitForTimerAsync(TimeSpan period)
    {
        while (await _clock.NextDelay().WaitAsync(Ceiling) != period)
        {
        }
    }

    private async Task<DateTimeOffset> NextCheckAsync() => await _checks.Reader.ReadAsync().AsTask().WaitAsync(Ceiling);
}
