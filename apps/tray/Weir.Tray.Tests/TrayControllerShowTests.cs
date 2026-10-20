using Xunit;

namespace Weir.Tray.Tests;

/// <summary>What the icon, the hover text and the menu say, and when they are drawn again.</summary>
public sealed class TrayControllerShowTests : IDisposable
{
    private static readonly TrayStatus Well = new(Paused: false, PausedUntil: null, ManagersUnreachable: [], FoldersUnreachable: [], ServerOk: true);

    private readonly List<TrayRig> _rigs = [];

    public void Dispose() => _rigs.ForEach(rig => rig.Dispose());

    private TrayRig Rig(FakeUpdateService? updates = null)
    {
        var rig = new TrayRig(updates: updates);
        _rigs.Add(rig);
        return rig;
    }

    private static TrayStatusReading Reading(TrayStatus status, DateTime writtenAt) => new(status, writtenAt, Failed: false);

    // A server that started a minute ago and is answering.
    private static void Run(TrayRig rig)
    {
        rig.Server.StartedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        rig.Server.SetPhase(ServerPhase.Running);
        rig.Shell.Drain();
    }

    [Fact]
    public void Nothing_is_drawn_before_the_icon_exists()
    {
        var rig = Rig();

        rig.Controller.Render();
        rig.Controller.ShowNotUpdated("There is no room.");

        Assert.Empty(rig.Shell.Icons);
        Assert.Empty(rig.Shell.HoverTexts);
        Assert.Empty(rig.Shell.Menus);
        Assert.Empty(rig.Shell.Balloons);
    }

    [Fact]
    public void A_running_server_that_has_not_yet_said_how_it_is_still_blinks()
    {
        var rig = Rig();
        rig.Controller.Begin();

        Run(rig);

        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.None), rig.Shell.LastIcon);
        Assert.True(rig.Shell.Blinking);
    }

    [Fact]
    public void A_status_the_running_server_wrote_turns_the_dot_green_and_stops_the_blink()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);

        rig.Controller.ShowServerStatus(Reading(Well, DateTime.UtcNow));

        Assert.Equal(new TrayIconKey(TrayDot.Green, TrayMark.None), rig.Shell.LastIcon);
        Assert.False(rig.Shell.Blinking);
        Assert.Equal(new TrayState(ServerPhase.Running, Well, null, 9347).HoverText, rig.Shell.LastHoverText);
    }

    [Fact]
    public void Something_that_does_not_answer_turns_the_dot_amber_and_the_hover_text_names_it()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);
        var status = Well with { ManagersUnreachable = ["Deluno"] };

        rig.Controller.ShowServerStatus(Reading(status, DateTime.UtcNow));

        Assert.Equal(new TrayIconKey(TrayDot.Amber, TrayMark.None), rig.Shell.LastIcon);
        Assert.Contains("Deluno", rig.Shell.LastHoverText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pause_shows_its_mark_and_the_menu_offers_to_resume()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);

        rig.Controller.ShowServerStatus(Reading(Well with { Paused = true }, DateTime.UtcNow));

        Assert.Equal(new TrayIconKey(TrayDot.Green, TrayMark.Paused), rig.Shell.LastIcon);
        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.Pause, Text: "Resume processing" });
    }

    [Fact]
    public void A_status_the_server_wrote_before_it_started_is_not_shown()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);

        rig.Controller.ShowServerStatus(Reading(Well, DateTime.UtcNow.AddHours(-1)));

        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.None), rig.Shell.LastIcon);
    }

    [Fact]
    public void A_server_that_is_starting_again_forgets_what_the_one_before_it_said()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);
        rig.Controller.ShowServerStatus(Reading(Well, DateTime.UtcNow));

        rig.Server.SetPhase(ServerPhase.Starting);
        rig.Shell.Drain();

        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.None), rig.Shell.LastIcon);
        Assert.True(rig.Shell.Blinking);
    }

    [Fact]
    public void A_server_that_stopped_for_good_is_red()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);

        rig.Server.SetPhase(ServerPhase.Stopped);
        rig.Shell.Drain();

        Assert.Equal(new TrayIconKey(TrayDot.Red, TrayMark.None), rig.Shell.LastIcon);
        Assert.False(rig.Shell.Blinking);
    }

    [Fact]
    public void The_reason_a_server_gave_up_is_in_the_hover_text()
    {
        var rig = Rig();
        rig.Server.GaveUpError = new StartupError("Port 9347 is in use", string.Empty);
        rig.Controller.Begin();

        rig.Server.SetPhase(ServerPhase.Stopped);
        rig.Shell.Drain();

        Assert.Contains("Port 9347 is in use", rig.Shell.LastHoverText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_servers_own_reason_wins_over_the_reason_the_supervision_gave_up()
    {
        var rig = Rig();
        rig.Server.StartupError = new StartupError("Out of space", "There is no room.");
        rig.Server.GaveUpError = new StartupError("Port 9347 is in use", string.Empty);

        Assert.Equal(rig.Server.StartupError, rig.Controller.State().StartupError);
    }

    [Fact]
    public void A_status_that_never_comes_turns_the_dot_amber_once_the_wait_is_over()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Run(rig);

        rig.Clock.Advance(TrayStatusDeadline.Wait);
        rig.Shell.Handlers!.BlinkTick();

        Assert.Equal(new TrayIconKey(TrayDot.Amber, TrayMark.None), rig.Shell.LastIcon);
        Assert.False(rig.Shell.Blinking);
    }

    [Fact]
    public void The_blink_swaps_between_the_lit_and_the_unlit_icon_and_leaves_the_menu_alone()
    {
        var rig = Rig();
        rig.Controller.Begin();
        var menus = rig.Shell.Menus.Count;
        var icons = rig.Shell.Icons.Count;

        rig.Shell.Handlers!.BlinkTick();
        rig.Shell.Handlers.BlinkTick();

        Assert.Equal(
            [new TrayIconKey(null, TrayMark.None), new TrayIconKey(TrayDot.Starting, TrayMark.None)],
            rig.Shell.Icons.Skip(icons));
        Assert.Equal(menus, rig.Shell.Menus.Count);
    }

    [Fact]
    public void A_blink_that_finds_the_dot_known_draws_everything_again()
    {
        var rig = Rig();
        rig.Controller.Begin();
        rig.Server.SetPhase(ServerPhase.Stopped);
        var menus = rig.Shell.Menus.Count;

        rig.Shell.Handlers!.BlinkTick();

        Assert.Equal(menus + 1, rig.Shell.Menus.Count);
        Assert.Equal(new TrayIconKey(TrayDot.Red, TrayMark.None), rig.Shell.LastIcon);
    }

    [Fact]
    public void The_menu_is_drawn_again_as_it_opens()
    {
        var rig = Rig();
        rig.Controller.Begin();
        var menus = rig.Shell.Menus.Count;

        rig.Shell.Handlers!.MenuOpening();

        Assert.Equal(menus + 1, rig.Shell.Menus.Count);
    }

    [Fact]
    public void A_downloaded_update_shows_its_mark_and_the_menu_offers_to_restart()
    {
        var rig = Rig(new FakeUpdateService().AlreadyDownloaded());
        rig.Controller.Begin();

        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.UpdateReady), rig.Shell.LastIcon);
        Assert.Contains(rig.Shell.LastMenu, entry => entry.Item == TrayMenuItem.Update && entry.Text.StartsWith("Restart to update", StringComparison.Ordinal));
    }

    [Fact]
    public void The_menu_says_whether_Weir_starts_with_Windows()
    {
        var rig = Rig();
        rig.Controller.Begin();
        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.StartWithWindows, Checked: false });

        rig.Startup.Enable();
        rig.Shell.Handlers!.MenuOpening();

        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.StartWithWindows, Checked: true });
    }

    [Fact]
    public void The_notice_of_an_update_that_installs_by_itself_says_when()
    {
        var rig = Rig(new FakeUpdateService().AlreadyDownloaded());
        rig.Controller.Begin();

        rig.Controller.ShowUpdateNotice(UpdateMode.Auto);

        var balloon = rig.Shell.LastBalloon;
        Assert.Equal(("Weir Update Ready", ToolTipIcon.Info, 10_000), (balloon.Title, balloon.Icon, balloon.TimeoutMs));
        Assert.Equal(
            $"Version 9.9.9 has been downloaded. Weir installs it and restarts by itself once it has been idle for {IdleInstall.IdlePeriodText}, or when Weir next quits or starts.",
            balloon.Text);
        rig.Shell.Handlers!.BalloonClicked();
        Assert.Empty(rig.Updates.Applied);
    }

    [Fact]
    public async Task The_notice_of_an_update_that_waits_to_be_installed_offers_to_install_it_on_a_click()
    {
        var rig = Rig(new FakeUpdateService().AlreadyDownloaded());
        rig.Controller.Begin();

        rig.Controller.ShowUpdateNotice(UpdateMode.DownloadOnly);
        var balloon = rig.Shell.LastBalloon;
        rig.Shell.Handlers!.BalloonClicked();
        await SettleAsync(() => rig.Updates.Applied.Count > 0);

        Assert.Equal(
            ("Weir Update Ready", "Version 9.9.9 has been downloaded. Click here to restart and install it, or it installs the next time Weir quits or starts.", ToolTipIcon.Info),
            (balloon.Title, balloon.Text, balloon.Icon));
        Assert.Equal([FakeUpdateService.AppliedAndRestarted], rig.Updates.Applied);
        Assert.Contains("stop", rig.Server.Calls);
    }

    [Fact]
    public void The_notice_of_an_update_that_is_only_available_points_to_the_page_that_installs_it()
    {
        var rig = Rig();
        rig.Controller.Begin();

        rig.Controller.ShowUpdateNotice(UpdateMode.NotifyOnly);
        var balloon = rig.Shell.LastBalloon;
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(
            ("Weir Update", "Version new version is available. Open System › About in Weir to update.", ToolTipIcon.Info),
            (balloon.Title, balloon.Text, balloon.Icon));
        Assert.Equal([(9347, Program.UpdateCheckPath)], rig.Shell.BrowserOpens);
    }

    [Fact]
    public void The_notice_of_an_update_names_the_version_that_was_found()
    {
        var rig = Rig(new FakeUpdateService { FindsUpdate = true }.AlreadyDownloaded());
        rig.Controller.Begin();

        rig.Controller.ShowUpdateNotice(UpdateMode.NotifyOnly);

        Assert.Contains("Version 9.9.9 is available", rig.Shell.LastBalloon.Text, StringComparison.Ordinal);
    }

    // Waits for background work the controller starts and does not return a task for.
    internal static async Task SettleAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The background work did not finish.");
            await Task.Delay(10);
        }
    }
}
