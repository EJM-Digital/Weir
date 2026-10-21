using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Downloading an update installs nothing. Velopack's installer stops a Weir that is still running a minute after it
/// starts, so an install is started only when the tray is about to end (#857). The one exception is Automatic mode: a
/// downloaded update is handed over for installing once Weir has been idle for a while, and never in the other modes
/// (#875).
/// </summary>
public sealed class TrayUpdatesTests : IDisposable
{
    private static readonly TimeSpan FinishCeiling = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StandInServers _servers = new();
    private readonly TaskCompletionSource _appliedNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _servers.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _home.Dispose();
    }

    [Theory]
    [InlineData((int)UpdateMode.Auto)]
    [InlineData((int)UpdateMode.DownloadOnly)]
    public async Task A_downloaded_update_is_not_installed_by_the_download(int mode)
    {
        var service = new FakeUpdateService();

        await CheckUntilAnnouncedAsync(service, (UpdateMode)mode);

        Assert.True(service.IsDownloaded);
        Assert.Empty(service.Applied);
        Assert.False(_appliedNow.Task.IsCompleted);
    }

    [Fact]
    public async Task A_found_update_is_not_downloaded_when_the_mode_only_notifies()
    {
        var service = new FakeUpdateService();

        await CheckUntilAnnouncedAsync(service, UpdateMode.NotifyOnly);

        Assert.False(service.IsDownloaded);
        Assert.Empty(service.Applied);
    }

    [Fact]
    public async Task The_server_is_told_an_update_is_downloaded_and_waiting()
    {
        await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);

        var state = File.ReadAllText(Path.Combine(_home.Path, "update-state.json"));
        Assert.Contains("\"downloaded\": true", state, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"9.9.9\"", state, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_forgets_a_download_made_by_the_run_before_it()
    {
        var statePath = Path.Combine(_home.Path, "update-state.json");
        File.WriteAllText(statePath, """{"downloaded": true, "version": "9.9.9"}""");
        var updates = new TrayUpdates(
            new FakeUpdateService(),
            _home.Path,
            new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = 0 },
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _ => { }, ApplyNow: () => { }),
            _clock,
            _shutdown.Token);

        updates.Start();

        var state = File.ReadAllText(statePath);
        Assert.Contains("\"downloaded\": false", state, StringComparison.Ordinal);
        Assert.Contains("\"version\": null", state, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_downloaded_update_waits_for_Weir_to_be_idle_in_Automatic_mode()
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);

        Assert.NotNull(updates.IdleWatch);
        Assert.False(updates.IdleWatch.IsCompleted);
    }

    [Fact]
    public async Task Asked_twice_to_wait_for_Weir_to_be_idle_it_waits_once()
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);
        var first = updates.IdleWatch;

        updates.InstallWhenIdle();

        Assert.Same(first, updates.IdleWatch);
        var started = File.ReadAllLines(Path.Combine(_home.Path, "tray-host.log")).Count(line => line.Contains("installing once Weir has been idle", StringComparison.Ordinal));
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task Checks_go_on_at_the_interval_the_person_chose()
    {
        var service = new FakeUpdateService();
        var interval = TimeSpan.FromMinutes(60);
        var updates = new TrayUpdates(
            service,
            _home.Path,
            new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = 60, Mode = UpdateMode.NotifyOnly },
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _ => { }, ApplyNow: () => { }),
            _clock,
            _shutdown.Token);

        updates.Start();
        while (await _clock.NextDelay() != interval)
        {
        }
        _clock.Advance(interval);

        Assert.True(SpinWait.SpinUntil(() => service.Checks > 0, FinishCeiling));
    }

    [Theory]
    [InlineData((int)UpdateMode.DownloadOnly)]
    [InlineData((int)UpdateMode.NotifyOnly)]
    public async Task An_update_never_waits_to_install_itself_in_the_other_modes(int mode)
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), (UpdateMode)mode);

        Assert.Null(updates.IdleWatch);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_update_is_handed_over_for_installing_once_Weir_has_been_idle()
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);

        await RunIdleUntilTheWaitEndsAsync(updates, ServerSays.Idle);

        Assert.True(_appliedNow.Task.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_update_is_not_handed_over_while_Weir_stays_busy()
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);

        for (var polls = 0; polls < (int)(2 * IdleInstall.IdlePeriod / IdleInstall.PollInterval); polls++)
        {
            Assert.NotSame(updates.IdleWatch, await Task.WhenAny(updates.IdleWatch!, _clock.NextDelay()));
            AdvanceOneStep(ServerSays.Busy);
        }

        Assert.False(_appliedNow.Task.IsCompleted);
        Assert.False(updates.IdleWatch!.IsCompleted);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_update_is_not_handed_over_when_the_mode_was_changed_while_it_waited()
    {
        var updates = await CheckUntilAnnouncedAsync(new FakeUpdateService(), UpdateMode.Auto);
        new UpdateSettings { Mode = UpdateMode.DownloadOnly }.Save(_home.Path);

        await RunIdleUntilTheWaitEndsAsync(updates, ServerSays.Idle);

        Assert.False(_appliedNow.Task.IsCompleted);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_update_installed_when_idle_stops_the_server_cleanly_and_then_restarts_Weir()
    {
        var server = await _servers.StartAsync(Path.Combine(_home.Path, "server"));
        var service = new FakeUpdateService();
        var atInstall = new TaskCompletionSource<(bool HasExited, int? ExitCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnApply = _ => atInstall.TrySetResult((server.HasExited, server.HasExited ? server.ExitCode : null));
        var shutdown = new TrayShutdown(() => ServerProcessStop.StopAsync(server), service);
        var updates = await CheckUntilAnnouncedAsync(
            service,
            UpdateMode.Auto,
            applyNow: () => BackgroundWork.Observe("Apply update", shutdown.RestartToUpdateAsync()));

        await RunIdleUntilTheWaitEndsAsync(updates, ServerSays.Idle);

        Assert.Equal((HasExited: true, ExitCode: 0), await atInstall.Task.WaitAsync(FinishCeiling));
        Assert.Equal([FakeUpdateService.AppliedAndRestarted], service.Applied);
    }

    // Runs a check on the thread pool, as the tray does, and returns once the person has been told about the outcome.
    private async Task<TrayUpdates> CheckUntilAnnouncedAsync(FakeUpdateService service, UpdateMode mode, Action? applyNow = null)
    {
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new TrayUpdates(
            service,
            _home.Path,
            new UpdateSettings { Mode = mode },
            new UpdateCallbacks(
                OnUi: action => action(),
                Changed: () => { },
                Announce: _ => announced.TrySetResult(),
                ApplyNow: applyNow ?? (() => _appliedNow.TrySetResult())),
            _clock,
            _shutdown.Token);

        _ = updates.CheckInBackground();

        await announced.Task.WaitAsync(FinishCeiling);
        return updates;
    }

    // Answers each poll with what the server would say, until the wait for idle has finished one way or the other.
    private async Task RunIdleUntilTheWaitEndsAsync(TrayUpdates updates, Action<string, DateTimeOffset> serverSays)
    {
        var watch = updates.IdleWatch!;
        serverSays(_home.Path, _clock.GetUtcNow());
        while (await Task.WhenAny(watch, _clock.NextDelay()) != watch)
        {
            AdvanceOneStep(serverSays);
        }
        await watch;
    }

    // The answer is written for the moment the clock is about to reach, so it is in place when the wait wakes.
    private void AdvanceOneStep(Action<string, DateTimeOffset> serverSays)
    {
        serverSays(_home.Path, _clock.GetUtcNow() + IdleInstall.PollInterval);
        _clock.Advance(IdleInstall.PollInterval);
    }
}
