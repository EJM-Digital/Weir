using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// System › About asks the tray for a step by creating a flag file in the runtime home: check now, download now, or restart
/// and apply. The tray acts on each once, starts nothing twice, and writes update-state.json as it goes, which is how the
/// page shows "Checking", "Downloading update" and what came of them.
/// </summary>
public sealed class TrayUpdateRequestsTests : IDisposable
{
    private static readonly TimeSpan FinishCeiling = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<UpdateMenuAction> _changes = Channel.CreateUnbounded<UpdateMenuAction>();
    private readonly ConcurrentQueue<UpdateMode> _announced = new();
    private readonly TaskCompletionSource _appliedNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TrayUpdates? _updates;

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _home.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_request_checks_at_once_and_writes_what_it_found()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);

        RequestFlag("update-check-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.False(File.Exists(FlagPath("update-check-now")));
        Assert.Equal(1, service.Checks);
        Assert.Equal(("idle", false, "9.9.9", null), ReadState());
        Assert.Equal([UpdateMode.NotifyOnly], _announced);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_state_says_checking_while_the_check_runs()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldCheck = release.Task };
        var updates = Build(service, UpdateMode.NotifyOnly);

        RequestFlag("update-check-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());

        Assert.Equal(("checking", false, null, null), ReadState());

        release.SetResult();
        await UntilSettledAsync();
        Assert.Equal(("idle", false, "9.9.9", null), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_that_finds_nothing_leaves_the_state_idle_with_no_version()
    {
        var updates = Build(new FakeUpdateService { FindsUpdate = false }, UpdateMode.NotifyOnly);

        RequestFlag("update-check-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal(("idle", false, null, null), ReadState());
        Assert.Empty(_announced);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_that_fails_writes_the_reason()
    {
        var updates = Build(new FakeUpdateService { CheckFailure = "Weir could not reach GitHub." }, UpdateMode.NotifyOnly);

        RequestFlag("update-check-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal(("failed", false, null, "Weir could not reach GitHub."), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_in_a_mode_that_downloads_goes_on_to_download_and_says_so()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldDownload = release.Task };
        var updates = Build(service, UpdateMode.DownloadOnly);

        RequestFlag("update-check-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());

        Assert.Equal(("downloading", false, "9.9.9", null), ReadState());

        release.SetResult();
        await UntilSettledAsync();
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_request_downloads_a_found_update_even_when_the_mode_only_notifies()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldDownload = release.Task };
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        RequestFlag("update-download-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());
        Assert.Equal(("downloading", false, "9.9.9", null), ReadState());

        release.SetResult();
        await UntilSettledAsync();

        Assert.False(File.Exists(FlagPath("update-download-now")));
        Assert.True(service.IsDownloaded);
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
        Assert.Equal([UpdateMode.NotifyOnly, UpdateMode.DownloadOnly], _announced);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_request_finds_the_update_first_when_the_tray_has_not_looked_yet()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);

        RequestFlag("update-download-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal((1, 1), (service.Checks, service.Downloads));
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_request_with_nothing_newer_says_so()
    {
        var service = new FakeUpdateService { FindsUpdate = false };
        var updates = Build(service, UpdateMode.NotifyOnly);

        RequestFlag("update-download-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal(0, service.Downloads);
        Assert.Equal(("failed", false, null, "There is no newer version of Weir to download."), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_that_fails_writes_the_reason_and_keeps_the_version()
    {
        var service = new FakeUpdateService { DownloadFailure = "Weir could not download the update." };
        var updates = Build(service, UpdateMode.NotifyOnly);

        RequestFlag("update-download-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal(("failed", false, "9.9.9", "Weir could not download the update."), ReadState());
        Assert.False(service.IsDownloaded);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_or_download_asked_for_while_one_is_under_way_is_not_started()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldCheck = release.Task };
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());

        await updates.CheckAsync();
        await updates.DownloadAsync();
        release.SetResult();
        await UntilSettledAsync();

        Assert.Equal((1, 0), (service.Checks, service.Downloads));
    }

    [Fact]
    public async Task A_check_does_not_start_while_an_update_is_downloaded_and_waiting()
    {
        var service = new FakeUpdateService().AlreadyDownloaded();
        var updates = Build(service, UpdateMode.NotifyOnly);

        await updates.CheckAsync();

        Assert.Equal(0, service.Checks);
        Assert.True(service.IsDownloaded);
    }

    [Fact]
    public void An_apply_request_hands_the_downloaded_update_over_to_install()
    {
        var updates = Build(new FakeUpdateService().AlreadyDownloaded(), UpdateMode.DownloadOnly);
        RequestFlag("update-apply-now");

        updates.ActOnRequests();

        Assert.True(_appliedNow.Task.IsCompletedSuccessfully);
        Assert.False(File.Exists(FlagPath("update-apply-now")));
    }

    [Fact]
    public void An_apply_request_before_the_download_is_taken_away_and_answered_with_what_to_do()
    {
        var updates = Build(new FakeUpdateService(), UpdateMode.DownloadOnly);
        RequestFlag("update-apply-now");

        updates.ActOnRequests();

        Assert.False(_appliedNow.Task.IsCompleted);
        Assert.False(File.Exists(FlagPath("update-apply-now")));
        Assert.Equal(("failed", false, null, "Download the update first."), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task An_apply_request_is_not_left_for_the_download_that_finishes_later()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldDownload = release.Task };
        var updates = Build(service, UpdateMode.DownloadOnly);
        RequestFlag("update-download-now");
        updates.ActOnRequests();
        RequestFlag("update-apply-now");
        updates.ActOnRequests();

        release.SetResult();
        await updates.RequestedWork!;
        updates.ActOnRequests();

        Assert.False(_appliedNow.Task.IsCompleted);
        Assert.False(File.Exists(FlagPath("update-apply-now")));
        Assert.True(service.IsDownloaded);
    }

    [Fact(Timeout = 30_000)]
    public async Task An_apply_request_while_a_step_is_under_way_leaves_the_state_saying_so()
    {
        var release = new TaskCompletionSource();
        var updates = Build(new FakeUpdateService { HoldCheck = release.Task }, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());
        File.Delete(FlagPath("update-state.json"));
        RequestFlag("update-apply-now");

        updates.ActOnRequests();

        Assert.Equal(("checking", false, null, null), ReadState());
        release.SetResult();
        await updates.RequestedWork!;
    }

    [Theory]
    [InlineData("update-check-now")]
    [InlineData("update-download-now")]
    [InlineData("update-apply-now")]
    public void A_flag_left_long_ago_is_thrown_away_and_not_acted_on(string flag)
    {
        var service = new FakeUpdateService().AlreadyDownloaded();
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag(flag);
        File.SetLastWriteTimeUtc(FlagPath(flag), _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(2));

        updates.ActOnRequests();

        Assert.False(File.Exists(FlagPath(flag)));
        Assert.Null(updates.RequestedWork);
        Assert.False(_appliedNow.Task.IsCompleted);
        Assert.False(File.Exists(FlagPath("update-state.json")));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_flag_a_moment_old_is_still_a_click()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-download-now");
        File.SetLastWriteTimeUtc(FlagPath("update-download-now"), _clock.GetUtcNow().UtcDateTime - TrayUpdates.RequestMaxAge + TimeSpan.FromSeconds(30));

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(1, service.Downloads);
    }

    [Fact]
    public async Task A_download_does_not_start_while_an_update_is_downloaded_and_waiting()
    {
        var service = new FakeUpdateService().AlreadyDownloaded();
        var updates = Build(service, UpdateMode.NotifyOnly);

        await updates.DownloadAsync();

        Assert.Equal((0, 0), (service.Checks, service.Downloads));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_request_that_cannot_be_taken_up_writes_the_state_as_it_is()
    {
        var release = new TaskCompletionSource();
        var service = new FakeUpdateService { HoldCheck = release.Task };
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");
        updates.ActOnRequests();
        Assert.Equal(UpdateMenuAction.Wait, await NextChangeAsync());
        File.Delete(FlagPath("update-state.json"));

        await updates.CheckAsync(asked: true);
        Assert.Equal(("checking", false, null, null), ReadState());
        File.Delete(FlagPath("update-state.json"));
        await updates.DownloadAsync(asked: true);
        Assert.Equal(("checking", false, null, null), ReadState());

        release.SetResult();
        await updates.RequestedWork!;
    }

    [Fact]
    public async Task A_check_or_download_asked_for_while_an_update_waits_writes_that_it_is_downloaded()
    {
        var updates = Build(new FakeUpdateService(), UpdateMode.DownloadOnly);
        await updates.DownloadAsync();
        File.Delete(FlagPath("update-state.json"));

        await updates.CheckAsync(asked: true);
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
        File.Delete(FlagPath("update-state.json"));
        await updates.DownloadAsync(asked: true);
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
    }

    [Fact]
    public async Task A_periodic_check_that_cannot_run_writes_nothing()
    {
        var updates = Build(new FakeUpdateService(), UpdateMode.DownloadOnly);
        await updates.DownloadAsync();
        File.Delete(FlagPath("update-state.json"));

        await updates.CheckAsync();

        Assert.False(File.Exists(FlagPath("update-state.json")));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_watcher_that_cannot_be_built_does_not_stop_the_start_up_check()
    {
        var service = new FakeUpdateService();
        var notAFolder = FlagPath("not-a-folder");
        File.WriteAllText(notAFolder, string.Empty);
        var updates = Build(service, UpdateMode.NotifyOnly, notAFolder, checkOnStartup: true);

        updates.Start();
        await UntilSettledAsync();

        Assert.Equal(1, service.Checks);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_tray_says_it_is_alive_when_it_starts_and_every_half_minute()
    {
        var updates = Build(new FakeUpdateService(), UpdateMode.NotifyOnly);
        var started = _clock.GetUtcNow();

        updates.Start();
        await _clock.NextDelay();
        Assert.Equal(started, HeartbeatAt());

        _clock.Advance(TrayUpdates.HeartbeatInterval);
        await _clock.NextDelay();
        Assert.Equal(started + TrayUpdates.HeartbeatInterval, HeartbeatAt());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_tray_that_was_not_installed_says_it_is_alive_and_cannot_update()
    {
        var service = new FakeUpdateService { IsInstalled = false };
        var updates = Build(service, UpdateMode.Auto, checkOnStartup: true);
        var started = _clock.GetUtcNow();

        updates.Start();
        await _clock.NextDelay();

        Assert.Equal(started, HeartbeatAt());
        Assert.False(HeartbeatSaysInstalled());
        Assert.Equal(0, service.Checks);
        Assert.False(File.Exists(FlagPath("update-state.json")));
    }

    [Fact(Timeout = 30_000)]
    public async Task An_installed_tray_says_it_was_installed()
    {
        var updates = Build(new FakeUpdateService(), UpdateMode.NotifyOnly);

        updates.Start();
        await _clock.NextDelay();

        Assert.True(HeartbeatSaysInstalled());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_check_asked_for_before_the_tray_started_is_acted_on_at_start()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");

        updates.Start();
        await UntilSettledAsync();

        Assert.False(File.Exists(FlagPath("update-check-now")));
        Assert.Equal(1, service.Checks);
        Assert.Equal(("idle", false, "9.9.9", null), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_asked_for_before_the_tray_started_is_acted_on_at_start()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-download-now");

        updates.Start();
        await UntilSettledAsync();

        Assert.False(File.Exists(FlagPath("update-download-now")));
        Assert.Equal(("downloaded", true, "9.9.9", null), ReadState());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_request_made_while_the_tray_runs_is_acted_on_when_the_watcher_hears_it()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);
        updates.Start();

        RequestFlag("update-check-now");
        await SettleWatcherAsync();
        await UntilSettledAsync();

        Assert.False(File.Exists(FlagPath("update-check-now")));
        Assert.Equal(1, service.Checks);
        Assert.Equal(("idle", false, "9.9.9", null), ReadState());
    }

    private TrayUpdates Build(FakeUpdateService service, UpdateMode mode, string? home = null, bool checkOnStartup = false)
    {
        _updates = new TrayUpdates(
            service,
            home ?? _home.Path,
            new UpdateSettings { Mode = mode, CheckOnStartup = checkOnStartup, CheckIntervalMinutes = 0 },
            new UpdateCallbacks(
                OnUi: action => action(),
                Changed: () => _changes.Writer.TryWrite(_updates!.MenuState().Action),
                Announce: _announced.Enqueue,
                ApplyNow: () => _appliedNow.TrySetResult()),
            _clock,
            _shutdown.Token);
        return _updates;
    }

    [Fact(Timeout = 30_000)]
    public async Task A_request_whose_flag_cannot_be_removed_is_still_acted_on_and_the_log_says_so()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, UpdateMode.NotifyOnly);
        RequestFlag("update-check-now");

        using (new FileStream(FlagPath("update-check-now"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            updates.ActOnRequests();
        }
        await UntilSettledAsync();

        Assert.Equal(1, service.Checks);
        Assert.Contains("Could not delete", LogFile.ReadAllText(FlagPath("tray-host.log")), StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_download_that_finds_nothing_to_download_says_there_is_no_newer_version()
    {
        var updates = Build(new FakeUpdateService { DownloadFindsNothing = true }, UpdateMode.NotifyOnly);

        RequestFlag("update-download-now");
        updates.ActOnRequests();
        await UntilSettledAsync();

        Assert.Equal(("failed", false, "9.9.9", "There is no newer version of Weir to download."), ReadState());
    }

    // The tray tells the menu of every change; the one that finds the tray idle again ends a check or download.
    private async Task<UpdateMenuAction> NextChangeAsync() =>
        await _changes.Reader.ReadAsync().AsTask().WaitAsync(FinishCeiling);

    // The file system tells the watcher about a flag on its own; the settle that follows is on the test's clock, so the test waits
    // for the watcher to start it and then moves the clock on.
    private async Task SettleWatcherAsync()
    {
        while (await _clock.NextDelay() != UpdateRequestWatcher.Settle)
        {
        }

        _clock.Advance(UpdateRequestWatcher.Settle);
    }

    private async Task UntilSettledAsync()
    {
        while (await NextChangeAsync() == UpdateMenuAction.Wait)
        {
        }
    }

    private DateTimeOffset HeartbeatAt() =>
        DateTimeOffset.Parse(JsonNode.Parse(File.ReadAllText(FlagPath("tray-heartbeat.json")))!["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

    private bool HeartbeatSaysInstalled() =>
        JsonNode.Parse(File.ReadAllText(FlagPath("tray-heartbeat.json")))!["installed"]!.GetValue<bool>();

    private string FlagPath(string name) => Path.Combine(_home.Path, name);

    private void RequestFlag(string name) => File.WriteAllText(FlagPath(name), string.Empty);

    private (string? State, bool? Downloaded, string? Version, string? Failure) ReadState()
    {
        var state = JsonNode.Parse(File.ReadAllText(FlagPath("update-state.json")))!;
        return (state["state"]?.GetValue<string>(), state["downloaded"]?.GetValue<bool>(), state["version"]?.GetValue<string>(), state["failure"]?.GetValue<string>());
    }
}
