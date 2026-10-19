using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// While update checking is off the tray looks at nothing: not at start, not on the hour, not when a button in System or the menu
/// asks. The choice is read from the file each time, so switching it in System takes hold at once.
/// </summary>
public sealed class TrayUpdatesCheckingOffTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _redrawn;

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _home.Dispose();
    }

    private TrayUpdates Build(FakeUpdateService service, UpdateSettings settings, Func<UpdateSettings>? read = null) =>
        new(
            service,
            _home.Path,
            settings,
            new UpdateCallbacks(OnUi: action => action(), Changed: () => Interlocked.Increment(ref _redrawn), Announce: _ => { }, ApplyNow: () => { }),
            _clock,
            _shutdown.Token,
            read);

    private string StateText() => File.ReadAllText(Path.Combine(_home.Path, "update-state.json"));

    [Fact]
    public async Task Nothing_is_looked_for_or_downloaded_whoever_asks()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.Auto, CheckForUpdates = false });

        await updates.CheckAsync();
        await updates.DownloadAsync();
        await updates.CheckAsync(asked: true);
        await updates.DownloadAsync(asked: true);

        Assert.Equal(0, service.Checks);
        Assert.Equal(0, service.Downloads);
        Assert.False(service.HasPendingUpdate);
    }

    [Fact]
    public async Task A_check_asked_for_in_System_is_answered_with_the_state_as_it_is_and_nothing_is_looked_for()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { CheckForUpdates = false });
        File.WriteAllText(Path.Combine(_home.Path, "update-check-now"), string.Empty);

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(0, service.Checks);
        Assert.False(File.Exists(Path.Combine(_home.Path, "update-check-now")));
        Assert.DoesNotContain("checking", StateText(), StringComparison.Ordinal);
        Assert.Contains("Update checking is off, so nothing was looked for.", File.ReadAllText(Path.Combine(_home.Path, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_asked_for_in_System_is_answered_and_nothing_is_downloaded()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { CheckForUpdates = false });
        File.WriteAllText(Path.Combine(_home.Path, "update-download-now"), string.Empty);

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(0, service.Checks);
        Assert.Equal(0, service.Downloads);
        Assert.DoesNotContain("downloading", StateText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_menu_says_checking_is_off_and_opens_the_page_where_it_is_switched_on()
    {
        var updates = Build(new FakeUpdateService(), new UpdateSettings { CheckForUpdates = false });
        await updates.CheckAsync();

        Assert.Equal(new UpdateMenuState("Update checking is off", true, UpdateMenuAction.OpenUpdatePage), updates.MenuState());
    }

    [Fact]
    public async Task The_choice_is_read_again_before_every_check_so_a_change_in_System_takes_hold_at_once()
    {
        var service = new FakeUpdateService();
        var checking = true;
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.NotifyOnly }, () => new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckForUpdates = checking });

        await updates.CheckAsync();
        checking = false;
        await updates.CheckAsync();
        await updates.CheckAsync(asked: true);
        checking = true;
        await updates.CheckAsync();

        Assert.Equal(2, service.Checks);
    }

    [Fact]
    public async Task The_menu_is_drawn_again_when_the_choice_changes_and_not_when_it_stays()
    {
        var checking = true;
        var updates = Build(new FakeUpdateService(), new UpdateSettings { Mode = UpdateMode.NotifyOnly }, () => new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckForUpdates = checking });
        await updates.CheckAsync();
        var drawnForChecks = Volatile.Read(ref _redrawn);

        checking = false;
        await updates.CheckAsync();
        var afterOff = Volatile.Read(ref _redrawn);
        await updates.CheckAsync();

        Assert.Equal(drawnForChecks + 1, afterOff);
        Assert.Equal(afterOff, Volatile.Read(ref _redrawn));
        Assert.Equal(UpdateMenuAction.OpenUpdatePage, updates.MenuState().Action);

        checking = true;
        await updates.CheckAsync();
        Assert.Equal(UpdateMenuAction.Download, updates.MenuState().Action);
    }

    [Fact]
    public async Task An_update_downloaded_before_checking_was_switched_off_can_still_be_installed()
    {
        var service = new FakeUpdateService().AlreadyDownloaded();
        var updates = Build(service, new UpdateSettings { CheckForUpdates = false });

        await updates.CheckAsync();

        Assert.True(updates.IsDownloaded);
        Assert.Equal(UpdateMenuAction.Restart, updates.MenuState().Action);
    }
}
