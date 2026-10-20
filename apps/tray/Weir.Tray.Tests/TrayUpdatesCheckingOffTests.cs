using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// While update checking is off, Weir's own checks and downloads stop: the start-up check, the hourly check and the download after
/// a check. A check or download a person asks for, in System or from the menu, is never refused. The choice is read from the file
/// each time, so switching it in System takes hold at once.
/// </summary>
public sealed class TrayUpdatesCheckingOffTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<UpdateMode> _announced = [];

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
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _announced.Add, ApplyNow: () => { }),
            _clock,
            _shutdown.Token,
            read);

    private string StateText() => File.ReadAllText(Path.Combine(_home.Path, "update-state.json"));

    [Fact]
    public async Task Weirs_own_check_and_download_look_at_nothing()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.Auto, CheckForUpdates = false });

        await updates.CheckAsync();
        await updates.DownloadAsync();

        Assert.Equal(0, service.Checks);
        Assert.Equal(0, service.Downloads);
        Assert.False(service.HasPendingUpdate);
    }

    [Fact]
    public async Task A_check_asked_for_in_System_goes_ahead_and_the_update_it_finds_is_not_downloaded_by_itself()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.Auto, CheckForUpdates = false });
        File.WriteAllText(Path.Combine(_home.Path, "update-check-now"), string.Empty);

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(1, service.Checks);
        Assert.Equal(0, service.Downloads);
        Assert.False(File.Exists(Path.Combine(_home.Path, "update-check-now")));
        Assert.Contains("\"9.9.9\"", StateText(), StringComparison.Ordinal);
        Assert.Equal([UpdateMode.NotifyOnly], _announced);
    }

    [Fact]
    public async Task A_download_asked_for_in_System_goes_ahead_and_finds_the_update_first()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.Auto, CheckForUpdates = false });
        File.WriteAllText(Path.Combine(_home.Path, "update-download-now"), string.Empty);

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(1, service.Checks);
        Assert.Equal(1, service.Downloads);
        Assert.True(updates.IsDownloaded);
    }

    [Fact]
    public async Task A_check_chosen_from_the_menu_goes_ahead()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { CheckForUpdates = false });

        await updates.CheckInBackground(asked: true);

        Assert.Equal(1, service.Checks);
        Assert.Equal(UpdateMenuAction.Download, updates.MenuState().Action);
    }

    [Fact]
    public async Task A_download_chosen_from_the_menu_goes_ahead()
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { CheckForUpdates = false });
        await updates.CheckInBackground(asked: true);

        await updates.DownloadInBackground(asked: true);

        Assert.Equal(1, service.Downloads);
        Assert.Equal(UpdateMenuAction.Restart, updates.MenuState().Action);
    }

    [Fact]
    public async Task The_menu_item_keeps_offering_a_check_while_checking_is_off()
    {
        var updates = Build(new FakeUpdateService(), new UpdateSettings { CheckForUpdates = false });

        await updates.CheckAsync();

        Assert.Equal(new UpdateMenuState("Check for updates", true, UpdateMenuAction.Check), updates.MenuState());
    }

    [Fact]
    public async Task The_choice_is_read_again_before_every_automatic_check_so_a_change_in_System_takes_hold_at_once()
    {
        var service = new FakeUpdateService();
        var checking = true;
        var updates = Build(service, new UpdateSettings { Mode = UpdateMode.NotifyOnly }, () => new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckForUpdates = checking });

        await updates.CheckAsync();
        checking = false;
        await updates.CheckAsync();
        checking = true;
        await updates.CheckAsync();

        Assert.Equal(2, service.Checks);
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
