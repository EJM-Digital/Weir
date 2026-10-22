using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData((int)UpdateMode.Auto)]
    [InlineData((int)UpdateMode.DownloadOnly)]
    [InlineData((int)UpdateMode.NotifyOnly)]
    public async Task A_check_chosen_from_the_menu_announces_the_update_it_found_whatever_the_mode_and_downloads_nothing(int mode)
    {
        var service = new FakeUpdateService();
        var updates = Build(service, new UpdateSettings { Mode = (UpdateMode)mode, CheckForUpdates = false });

        await updates.CheckInBackground(asked: true);

        Assert.Equal([UpdateMode.NotifyOnly], _announced);
        Assert.Equal(0, service.Downloads);
        Assert.Equal(UpdateMenuAction.Download, updates.MenuState().Action);
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

    private JsonNode State() => JsonNode.Parse(StateText())!;

    [Fact]
    public async Task A_check_asked_for_in_System_that_finds_nothing_newer_says_when_it_was_made()
    {
        var updates = Build(new FakeUpdateService { FindsUpdate = false }, new UpdateSettings { CheckForUpdates = false });
        File.WriteAllText(Path.Combine(_home.Path, "update-check-now"), string.Empty);

        updates.ActOnRequests();
        await updates.RequestedWork!;

        Assert.Equal(("idle", null), (State()["state"]!.GetValue<string>(), State()["version"]?.GetValue<string>()));
        Assert.Equal(_clock.GetUtcNow(), State()["checkedAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_check_that_finds_an_update_names_it_and_says_when_it_was_made()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero));
        var updates = Build(new FakeUpdateService(), new UpdateSettings { CheckForUpdates = false });

        await updates.CheckInBackground(asked: true);

        Assert.Equal("9.9.9", State()["version"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), State()["checkedAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_check_that_failed_does_not_claim_to_have_looked()
    {
        var updates = Build(new FakeUpdateService { CheckFailure = "Weir could not reach GitHub." }, new UpdateSettings { CheckForUpdates = false });

        await updates.CheckInBackground(asked: true);

        Assert.Equal("failed", State()["state"]!.GetValue<string>());
        Assert.Null(State()["checkedAt"]);
    }

    [Fact]
    public async Task The_time_of_the_last_check_stays_in_the_file_when_the_update_it_found_is_downloaded()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero));
        var updates = Build(new FakeUpdateService(), new UpdateSettings { CheckForUpdates = false });
        await updates.CheckInBackground(asked: true);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await updates.DownloadInBackground(asked: true);

        Assert.Equal("downloaded", State()["state"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), State()["checkedAt"]!.GetValue<DateTimeOffset>());
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
