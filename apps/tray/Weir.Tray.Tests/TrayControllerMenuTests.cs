using System.Runtime.InteropServices;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>What each click on the icon and on each item of its menu does.</summary>
public sealed class TrayControllerMenuTests : IDisposable
{
    private readonly List<TrayRig> _rigs = [];

    public void Dispose() => _rigs.ForEach(rig => rig.Dispose());

    private TrayRig RunningRig(FakeUpdateService? updates = null)
    {
        var rig = new TrayRig(updates: updates);
        _rigs.Add(rig);
        rig.Controller.Begin();
        rig.Server.StartedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        rig.Server.SetPhase(ServerPhase.Running);
        rig.Shell.Drain();
        rig.Shell.Balloons.Clear();
        return rig;
    }

    [Fact]
    public void Clicking_the_icon_with_the_left_button_opens_Weir()
    {
        var rig = RunningRig();

        rig.Shell.Handlers!.IconClicked();

        Assert.Equal([(9347, "/")], rig.Shell.BrowserOpens);
    }

    [Fact]
    public void A_double_click_right_after_a_click_opens_no_second_window_but_a_later_one_does()
    {
        var rig = RunningRig();
        rig.Shell.Handlers!.IconClicked();

        rig.Clock.Advance(TimeSpan.FromMilliseconds(200));
        rig.Shell.Handlers.IconDoubleClicked();
        Assert.Single(rig.Shell.BrowserOpens);

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Shell.Handlers.IconDoubleClicked();
        Assert.Equal(2, rig.Shell.BrowserOpens.Count);
    }

    [Fact]
    public void Open_Weir_while_the_server_is_still_starting_says_so()
    {
        var rig = new TrayRig();
        _rigs.Add(rig);
        rig.Controller.Begin();

        rig.Click(TrayMenuItem.Open);

        Assert.Empty(rig.Shell.BrowserOpens);
        Assert.Equal(("Weir", TrayBalloons.StillStartingText, ToolTipIcon.Info, 10_000), rig.Shell.LastBalloon);
    }

    [Fact]
    public void Pause_asks_the_server_to_pause_and_a_second_click_asks_it_to_resume()
    {
        var rig = RunningRig();
        var request = Path.Combine(rig.Home, PauseRequestFile.FileName);

        rig.Click(TrayMenuItem.Pause);
        Assert.Contains("\"paused\":true", File.ReadAllText(request), StringComparison.OrdinalIgnoreCase);

        rig.Controller.ShowServerStatus(new TrayStatusReading(
            new TrayStatus(Paused: true, PausedUntil: null, ManagersUnreachable: [], FoldersUnreachable: [], ServerOk: true),
            DateTime.UtcNow,
            Failed: false));
        rig.Click(TrayMenuItem.Pause);
        Assert.Contains("\"paused\":false", File.ReadAllText(request), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pause_that_cannot_write_its_request_tells_the_person_and_a_click_shows_the_log()
    {
        var rig = RunningRig();
        Directory.CreateDirectory(Path.Combine(rig.Home, PauseRequestFile.FileName));

        rig.Click(TrayMenuItem.Pause);
        rig.Shell.Handlers!.BalloonClicked();

        var balloon = rig.Shell.LastBalloon;
        Assert.Equal(("Weir", "Weir couldn't pause processing. See tray-host.log in the data folder.", ToolTipIcon.Warning), (balloon.Title, balloon.Text, balloon.Icon));
        Assert.Equal([Path.Combine(rig.Home, TrayLog.FileName)], rig.Shell.Revealed);
    }

    [Fact]
    public void Restart_Weir_restarts_the_server()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.Restart);

        Assert.Contains("restart", rig.Server.Calls);
    }

    [Fact]
    public void Copy_address_puts_the_address_on_this_pc_on_the_clipboard()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.CopyAddress);

        Assert.Equal([TrayState.AddressFor(9347)], rig.Shell.Clipboard);
    }

    [Fact]
    public void Copy_address_for_other_devices_names_this_pc()
    {
        var rig = RunningRig();
        rig.Server.Scope = ListenScope.OtherDevices;

        rig.Click(TrayMenuItem.CopyAddress);

        Assert.Equal([$"http://{Environment.MachineName}:9347"], rig.Shell.Clipboard);
    }

    [Theory]
    [InlineData(typeof(ExternalException))]
    [InlineData(typeof(ThreadStateException))]
    public void A_clipboard_that_is_held_by_another_program_is_logged_and_nothing_more(Type failure)
    {
        var rig = RunningRig();
        rig.Shell.ClipboardFailure = (Exception)Activator.CreateInstance(failure)!;

        rig.Click(TrayMenuItem.CopyAddress);

        Assert.Empty(rig.Shell.Clipboard);
    }

    [Fact]
    public void A_clipboard_that_fails_some_other_way_is_not_swallowed()
    {
        var rig = RunningRig();
        rig.Shell.ClipboardFailure = new InvalidOperationException("A bug.");

        Assert.Throws<InvalidOperationException>(() => rig.Click(TrayMenuItem.CopyAddress));
    }

    [Fact]
    public async Task Allow_other_devices_restarts_the_server_for_the_network_and_says_so()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.AllowOtherDevices);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);

        Assert.Contains($"move-to-scope-{ListenScope.OtherDevices}", rig.Server.Calls);
        Assert.Equal(ListenScope.OtherDevices, rig.Server.Scope);
    }

    [Fact]
    public async Task Only_this_PC_restarts_the_server_for_this_pc_alone_and_says_so()
    {
        var rig = RunningRig();
        rig.Server.Scope = ListenScope.OtherDevices;

        rig.Click(TrayMenuItem.OnlyThisPc);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);

        Assert.Equal(ListenScope.ThisPcOnly, rig.Server.Scope);
    }

    [Fact]
    public async Task A_notice_that_points_to_the_log_shows_the_log_when_it_is_clicked()
    {
        var rig = RunningRig();
        rig.Firewall.Allows = false;
        rig.Firewall.Answer = Weir.Tray.Firewall.FirewallElevation.Outcome.Failed;

        rig.Click(TrayMenuItem.AllowOtherDevices);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(ToolTipIcon.Warning, rig.Shell.LastBalloon.Icon);
        Assert.Equal([Path.Combine(rig.Home, TrayLog.FileName)], rig.Shell.Revealed);
    }

    [Fact]
    public async Task Change_port_moves_the_server_and_a_click_on_the_notice_opens_Weir_there()
    {
        var rig = RunningRig();
        rig.Shell.PortAnswer = 9400;
        rig.Server.OnMove = to =>
        {
            rig.Server.Port = to;
            return Task.FromResult(PortMove.Moved);
        };

        rig.Click(TrayMenuItem.ChangePort);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(PortPromptReason.Change, rig.Shell.PortQuestions.Single().Reason);
        Assert.Equal(TrayBalloons.PortMovedText(9400), rig.Shell.Balloons.Single().Text);
        Assert.Equal([(9400, "/")], rig.Shell.BrowserOpens);
    }

    [Fact]
    public async Task Change_port_that_fails_shows_the_log_when_the_notice_is_clicked()
    {
        var rig = RunningRig();
        rig.Shell.PortAnswer = 9400;
        rig.Server.OnMove = _ => Task.FromResult(PortMove.KeptOldPort);

        rig.Click(TrayMenuItem.ChangePort);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal([Path.Combine(rig.Home, TrayLog.FileName)], rig.Shell.Revealed);
    }

    [Fact]
    public async Task The_menu_shows_the_port_a_move_is_under_way_to()
    {
        var rig = RunningRig();
        rig.Shell.PortAnswer = 9400;
        var release = new TaskCompletionSource<PortMove>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Server.OnMove = _ => release.Task;

        rig.Click(TrayMenuItem.ChangePort);
        var during = rig.Shell.LastMenu.Single(entry => entry.Item == TrayMenuItem.ChangePort).Text;
        release.SetResult(PortMove.Moved);
        await TrayControllerShowTests.SettleAsync(() => rig.Shell.Balloons.Count > 0);

        Assert.Contains("9400", during, StringComparison.Ordinal);
        Assert.DoesNotContain("9400", rig.Shell.LastMenu.Single(entry => entry.Item == TrayMenuItem.ChangePort).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_data_folder_and_Open_logs_folder_open_those_folders()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.OpenDataFolder);
        rig.Click(TrayMenuItem.OpenLogsFolder);
        rig.Click(TrayMenuItem.ReportProblem);

        var logs = RuntimeFolders.Logs(rig.Home, Environment.GetEnvironmentVariable);
        Assert.Equal([(rig.Home, "data folder"), (logs, "logs folder"), (logs, "logs folder")], rig.Shell.Folders);
    }

    [Fact]
    public void Start_with_Windows_flips_the_choice_and_the_menu_shows_it_at_once()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.StartWithWindows);
        Assert.True(rig.Startup.IsEnabled);
        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.StartWithWindows, Checked: true });

        rig.Click(TrayMenuItem.StartWithWindows);
        Assert.False(rig.Startup.IsEnabled);
        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.StartWithWindows, Checked: false });
    }

    [Fact]
    public void Check_for_updates_in_a_copy_that_cannot_update_itself_opens_the_page_that_does()
    {
        var rig = RunningRig();

        rig.Click(TrayMenuItem.Update);

        Assert.Equal([(9347, Program.UpdateCheckPath)], rig.Shell.BrowserOpens);
    }

    [Fact]
    public async Task Check_for_updates_looks_for_one_then_the_menu_offers_to_download_it_and_then_to_restart()
    {
        var rig = RunningRig(new FakeUpdateService());

        rig.Click(TrayMenuItem.Update);
        await TrayControllerShowTests.SettleAsync(() => rig.Updates.Checks == 1 && rig.Updates.HasPendingUpdate);
        rig.Shell.Handlers!.MenuOpening();
        Assert.Contains(rig.Shell.LastMenu, entry => entry.Item == TrayMenuItem.Update && entry.Text == "Download update (v9.9.9)");

        rig.Click(TrayMenuItem.Update);
        await TrayControllerShowTests.SettleAsync(() => rig.Updates.IsDownloaded);
        rig.Shell.Handlers.MenuOpening();
        Assert.Contains(rig.Shell.LastMenu, entry => entry.Item == TrayMenuItem.Update && entry.Text == "Restart to update (v9.9.9)");

        rig.Click(TrayMenuItem.Update);
        await TrayControllerShowTests.SettleAsync(() => rig.Updates.Applied.Count > 0);
        Assert.Equal([FakeUpdateService.AppliedAndRestarted], rig.Updates.Applied);
        Assert.Equal(1, rig.Updates.Downloads);
        Assert.True(rig.Shell.Hidden);
    }

    [Fact]
    public async Task While_an_update_is_checked_the_update_item_does_nothing()
    {
        var hold = new TaskCompletionSource();
        var rig = RunningRig(new FakeUpdateService { HoldCheck = hold.Task });
        rig.Click(TrayMenuItem.Update);
        await TrayControllerShowTests.SettleAsync(() => rig.Updates.Checks == 1);

        rig.Click(TrayMenuItem.Update);
        hold.SetResult();

        Assert.Equal(1, rig.Updates.Checks);
        Assert.Empty(rig.Shell.BrowserOpens);
    }

    [Fact]
    public async Task Quit_stops_the_server_hides_the_icon_and_ends_the_application()
    {
        var rig = RunningRig();

        await rig.Controller.QuitAsync();

        Assert.Contains("stop", rig.Server.Calls);
        Assert.True(rig.Shell.Hidden);
        Assert.Equal(1, rig.Shell.Exits);
        Assert.True(rig.Shutdown.IsCancellationRequested);
    }

    [Fact]
    public async Task Quit_installs_a_downloaded_update_after_the_server_has_stopped()
    {
        var rig = RunningRig(new FakeUpdateService().AlreadyDownloaded());
        rig.Updates.OnApply = _ => Assert.Contains("stop", rig.Server.Calls);

        await rig.Controller.QuitAsync();

        Assert.Equal([FakeUpdateService.AppliedAndExited], rig.Updates.Applied);
    }

    [Fact]
    public async Task Quit_while_an_update_is_being_applied_does_nothing_more()
    {
        var rig = RunningRig(new FakeUpdateService().AlreadyDownloaded());
        var stopping = new TaskCompletionSource();
        rig.Server.OnStop = () => stopping.Task;
        var update = rig.Controller.ApplyUpdateAndRestartAsync();

        await rig.Controller.QuitAsync();
        stopping.SetResult();
        await update;

        Assert.Equal(0, rig.Shell.Exits);
        Assert.Equal([FakeUpdateService.AppliedAndRestarted], rig.Updates.Applied);
    }

    [Fact]
    public async Task Asking_to_install_while_an_install_is_under_way_says_so()
    {
        var rig = RunningRig(new FakeUpdateService().AlreadyDownloaded());
        var stopping = new TaskCompletionSource();
        rig.Server.OnStop = () => stopping.Task;
        var first = rig.Controller.ApplyUpdateAndRestartAsync();

        await rig.Controller.ApplyUpdateAndRestartAsync();
        stopping.SetResult();
        await first;

        Assert.Equal(("Weir Update", TrayBalloons.UpdateUnderWayText, ToolTipIcon.Info, 10_000), rig.Shell.LastBalloon);
    }

    [Fact]
    public async Task Asking_to_install_with_nothing_downloaded_does_nothing()
    {
        var rig = RunningRig();

        await rig.Controller.ApplyUpdateAndRestartAsync();

        Assert.Empty(rig.Updates.Applied);
        Assert.DoesNotContain("stop", rig.Server.Calls);
    }
}
