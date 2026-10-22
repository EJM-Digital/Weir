using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The check Weir makes by itself every CheckIntervalMinutes: it follows the tray's clock, so it runs when that much time has
/// passed and not before, and it keeps running for as long as the tray does.
/// </summary>
public sealed class TrayPeriodicCheckTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(45);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _announced = new(0);

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _announced.Dispose();
        _home.Dispose();
    }

    private TrayUpdates Started(FakeUpdateService service, UpdateSettings settings)
    {
        var updates = new TrayUpdates(
            service,
            _home.Path,
            settings,
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _ => _announced.Release(), ApplyNow: () => { }),
            _clock,
            _shutdown.Token);
        updates.Start();
        return updates;
    }

    // The loop is parked on the clock once it has asked for its wait; the heartbeat and the request watcher ask for theirs too.
    private async Task WaitUntilParkedForAsync(TimeSpan wait)
    {
        while (await _clock.NextDelay().WaitAsync(Ceiling) != wait)
        {
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_check_is_made_each_time_the_interval_passes_and_not_before()
    {
        var service = new FakeUpdateService();
        _ = Started(service, new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckOnStartup = false, CheckIntervalMinutes = (int)Interval.TotalMinutes });
        await WaitUntilParkedForAsync(Interval);

        _clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(0, service.Checks);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _announced.WaitAsync(Ceiling);
        Assert.Equal(1, service.Checks);

        _clock.Advance(Interval);
        await _announced.WaitAsync(Ceiling);
        Assert.Equal(2, service.Checks);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(10080)]
    public async Task The_next_check_comes_one_interval_after_the_last_for_every_interval_the_settings_allow(int minutes)
    {
        var interval = TimeSpan.FromMinutes(minutes);
        var service = new FakeUpdateService();
        _ = Started(service, new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckOnStartup = false, CheckIntervalMinutes = minutes });
        await WaitUntilParkedForAsync(interval);

        _clock.Advance(interval - TimeSpan.FromSeconds(1));
        Assert.Equal(0, service.Checks);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _announced.WaitAsync(Ceiling);
        Assert.Equal(1, service.Checks);

        _clock.Advance(interval);
        await _announced.WaitAsync(Ceiling);
        Assert.Equal(2, service.Checks);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_check_made_by_the_interval_downloads_what_it_finds_when_the_mode_downloads()
    {
        var service = new FakeUpdateService();
        _ = Started(service, new UpdateSettings { Mode = UpdateMode.DownloadOnly, CheckOnStartup = false, CheckIntervalMinutes = (int)Interval.TotalMinutes });
        await WaitUntilParkedForAsync(Interval);

        _clock.Advance(Interval);
        await _announced.WaitAsync(Ceiling);

        Assert.Equal(1, service.Checks);
        Assert.Equal(1, service.Downloads);
        Assert.True(service.IsDownloaded);
    }
}
