using Xunit;

namespace Weir.Tray.Tests;

/// <summary>How the tray starts the server and what it says while and after: the icon, the balloons, the exit code of an unattended start.</summary>
public sealed class TrayControllerStartTests : IDisposable
{
    private static readonly TrayState StartingState = new(ServerPhase.Starting, null, null, 9347);

    private readonly List<TrayRig> _rigs = [];

    public void Dispose() => _rigs.ForEach(rig => rig.Dispose());

    private TrayRig Rig(TrayStart? start = null, Func<PortChoice.Resolution>? resolvePort = null)
    {
        var rig = new TrayRig(start, resolvePort: resolvePort);
        _rigs.Add(rig);
        return rig;
    }

    [Fact]
    public void Beginning_prepares_the_server_environment_and_saves_the_port_it_will_use()
    {
        var rig = Rig();

        rig.Controller.Begin();

        Assert.Equal(["prepare", "write-port-file"], rig.Server.Calls);
    }

    [Fact]
    public void Beginning_without_a_known_port_saves_none()
    {
        var rig = Rig();
        rig.Server.Port = 0;

        rig.Controller.Begin();

        Assert.Equal(["prepare"], rig.Server.Calls);
    }

    [Fact]
    public void The_icon_is_up_and_blinking_with_a_menu_before_the_server_has_answered()
    {
        var rig = Rig();

        rig.Controller.Begin();

        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.None), rig.Shell.LastIcon);
        Assert.Equal(StartingState.HoverText, rig.Shell.LastHoverText);
        Assert.True(rig.Shell.Blinking);
        Assert.Contains(rig.Shell.LastMenu, entry => entry.Item == TrayMenuItem.Quit);
        Assert.NotNull(rig.Shell.Handlers);
    }

    [Fact]
    public void The_server_is_started_inside_the_message_loop_not_while_beginning()
    {
        var rig = Rig();

        rig.Controller.Begin();
        Assert.DoesNotContain("start", rig.Server.Calls);

        rig.Shell.Drain();
        Assert.Contains("start", rig.Server.Calls);
    }

    [Fact]
    public async Task A_server_that_is_ready_is_announced_and_a_click_on_the_notice_opens_Weir()
    {
        var rig = Rig();
        rig.Controller.Begin();

        await rig.Controller.StartAsync();
        rig.Server.SetPhase(ServerPhase.Running);
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(("Weir", TrayBalloons.RunningText(9347), ToolTipIcon.Info, 10_000), rig.Shell.Balloons.Single());
        Assert.Equal([(9347, "/")], rig.Shell.BrowserOpens);
        Assert.Contains("watch", rig.Server.Calls);
    }

    [Fact]
    public async Task A_notice_is_clicked_once_only()
    {
        var rig = Rig();
        rig.Controller.Begin();
        await rig.Controller.StartAsync();
        rig.Server.SetPhase(ServerPhase.Running);

        rig.Shell.Handlers!.BalloonClicked();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Shell.Handlers.BalloonClicked();

        Assert.Single(rig.Shell.BrowserOpens);
    }

    [Fact]
    public async Task A_start_that_is_not_to_be_announced_says_nothing_and_still_watches_the_server()
    {
        var rig = Rig(new TrayStart(AnnounceOnReady: false, Interactive: true));
        rig.Controller.Begin();

        await rig.Controller.StartAsync();

        Assert.Empty(rig.Shell.Balloons);
        Assert.Contains("watch", rig.Server.Calls);
    }

    [Fact]
    public async Task A_start_with_nobody_at_the_desktop_that_cannot_get_the_server_up_exits_with_code_1()
    {
        var rig = Rig(new TrayStart(AnnounceOnReady: false, Interactive: false));
        rig.Server.OnStart = () => throw new TimeoutException("The server did not answer.");
        rig.Controller.Begin();

        await rig.Controller.StartAsync();

        Assert.Equal(1, rig.Controller.ExitCode);
        Assert.Equal(1, rig.Shell.ThreadExits);
        Assert.Empty(rig.Shell.Balloons);
        Assert.DoesNotContain("watch", rig.Server.Calls);
    }

    [Fact]
    public async Task A_start_that_fails_with_a_person_at_the_desktop_says_so_and_a_click_tries_again()
    {
        var rig = Rig();
        rig.Server.OnStart = () => throw new InvalidOperationException("Gave up.");
        rig.Controller.Begin();

        await rig.Controller.StartAsync();
        var balloon = rig.Shell.Balloons.Single();
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(("Weir", TrayBalloons.CouldNotStartText, ToolTipIcon.Error, 30_000), balloon);
        Assert.Equal(0, rig.Controller.ExitCode);
        Assert.Equal(0, rig.Shell.ThreadExits);
        Assert.Contains("restart", rig.Server.Calls);
        Assert.Contains("watch", rig.Server.Calls);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    public async Task A_server_program_that_cannot_be_found_or_run_is_a_failed_start_too(Type failure)
    {
        var rig = Rig(new TrayStart(AnnounceOnReady: true, Interactive: false));
        rig.Server.OnStart = () => throw (Exception)Activator.CreateInstance(failure)!;
        rig.Controller.Begin();

        await rig.Controller.StartAsync();

        Assert.Equal(1, rig.Controller.ExitCode);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_failed_start_is_not_swallowed()
    {
        var rig = Rig();
        rig.Server.OnStart = () => throw new ArgumentException("A bug.");
        rig.Controller.Begin();

        await Assert.ThrowsAsync<ArgumentException>(rig.Controller.StartAsync);
    }

    [Fact]
    public void The_notice_for_a_start_that_failed_gives_the_servers_own_reason_first()
    {
        var rig = Rig();
        rig.Server.StartupError = new StartupError("Out of space", "There is no room to save a copy of the data.");
        rig.Server.NoPortChosen = true;
        rig.Server.GaveUpPortInUse = 9347;
        rig.Server.GaveUpAfterAttempts = 4;

        Assert.Equal(TrayBalloons.CouldNotStartBecause(rig.Server.StartupError), rig.Controller.CouldNotStartText());
    }

    [Fact]
    public void The_notice_for_a_start_that_failed_names_a_port_window_that_was_closed()
    {
        var rig = Rig();
        rig.Server.NoPortChosen = true;
        rig.Server.GaveUpPortInUse = 9347;

        Assert.Equal(TrayBalloons.NoPortChosenText, rig.Controller.CouldNotStartText());
    }

    [Fact]
    public void The_notice_for_a_start_that_failed_names_the_port_another_program_holds()
    {
        var rig = Rig();
        rig.Server.GaveUpPortInUse = 9347;
        rig.Server.GaveUpAfterAttempts = 4;

        Assert.Equal(TrayBalloons.PortInUseText(9347, 4), rig.Controller.CouldNotStartText());
    }

    [Fact]
    public void The_notice_for_a_start_that_failed_counts_the_tries()
    {
        var rig = Rig();
        rig.Server.GaveUpAfterAttempts = 3;

        Assert.Equal(TrayBalloons.GaveUpText(3), rig.Controller.CouldNotStartText());
    }

    [Fact]
    public void The_notice_for_a_start_that_failed_for_no_known_reason_is_the_general_one()
    {
        Assert.Equal(TrayBalloons.CouldNotStartText, Rig().Controller.CouldNotStartText());
    }

    [Fact]
    public void The_notice_for_a_server_that_stopped_gives_the_servers_own_reason_first()
    {
        var rig = Rig();
        rig.Server.StartupError = new StartupError("Out of space", "There is no room.");
        rig.Server.GaveUpPortInUse = 9347;

        Assert.Equal(TrayBalloons.StoppedBecause(rig.Server.StartupError), rig.Controller.StoppedText());
    }

    [Fact]
    public void The_notice_for_a_server_that_stopped_names_the_port_another_program_holds()
    {
        var rig = Rig();
        rig.Server.GaveUpPortInUse = 9347;
        rig.Server.GaveUpAfterAttempts = 4;

        Assert.Equal(TrayBalloons.StoppedPortInUseText(9347, 4), rig.Controller.StoppedText());
    }

    [Fact]
    public void The_notice_for_a_server_that_stopped_counts_the_tries()
    {
        var rig = Rig();
        rig.Server.GaveUpAfterAttempts = 2;

        Assert.Equal(TrayBalloons.StoppedAfterTriesText(2), rig.Controller.StoppedText());
    }

    [Fact]
    public void The_notice_for_a_server_that_stopped_for_no_known_reason_is_the_general_one()
    {
        Assert.Equal(TrayBalloons.StoppedText, Rig().Controller.StoppedText());
    }

    [Fact]
    public async Task A_server_the_watchdog_gives_up_on_is_reported_and_a_click_restarts_it()
    {
        var rig = Rig();
        rig.Controller.Begin();
        rig.Shell.Drain();
        rig.Shell.Balloons.Clear();

        rig.Server.GaveUp!();
        rig.Shell.Drain();
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(("Weir", TrayBalloons.StoppedText, ToolTipIcon.Error, 30_000), rig.Shell.Balloons.Single());
        Assert.Contains("restart", rig.Server.Calls);
    }

    [Fact]
    public async Task Watching_begins_once_however_many_times_the_server_comes_up()
    {
        var rig = Rig();
        rig.Controller.Begin();
        await rig.Controller.StartAsync();

        await rig.Controller.RestartAsync();
        await rig.Controller.RestartAsync();

        Assert.Single(rig.Server.Calls, "watch");
        Assert.Equal(2, rig.Server.Calls.Count(call => call == "restart"));
    }

    [Fact]
    public async Task A_restart_that_fails_says_why_and_a_click_tries_again()
    {
        var rig = Rig();
        rig.Server.OnRestart = _ => Task.FromResult(false);
        rig.Server.GaveUpAfterAttempts = 2;
        rig.Controller.Begin();

        await rig.Controller.RestartAsync();
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(("Weir", TrayBalloons.GaveUpText(2), ToolTipIcon.Error, 30_000), rig.Shell.Balloons[0]);
        Assert.Equal(2, rig.Server.Calls.Count(call => call == "restart"));
        Assert.DoesNotContain("watch", rig.Server.Calls);
    }

    [Fact]
    public async Task A_restart_that_is_stopped_because_Weir_is_quitting_says_nothing()
    {
        var rig = Rig();
        rig.Server.OnRestart = _ => throw new OperationCanceledException();
        rig.Controller.Begin();

        await rig.Controller.RestartAsync();

        Assert.Empty(rig.Shell.Balloons);
        Assert.DoesNotContain("watch", rig.Server.Calls);
    }

    [Fact]
    public async Task An_update_that_was_held_back_for_want_of_a_copy_of_the_data_is_said_so_once_the_icon_is_up()
    {
        var rig = Rig();
        File.WriteAllText(
            Path.Combine(rig.Home, "update-not-applied.json"),
            """{ "version": "0.0.1", "reason": "There is no room." }""");
        rig.Controller.Begin();

        await rig.Controller.StartAsync();
        rig.Server.SetPhase(ServerPhase.Running);
        var held = rig.Shell.Balloons[^1];
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(
            ("Weir Update", "Weir didn't update because it couldn't save a copy of its data: There is no room.", ToolTipIcon.Warning, 30_000),
            held);
        Assert.Equal([(9347, Program.UpdateCheckPath)], rig.Shell.BrowserOpens);
    }

    [Fact]
    public async Task A_port_that_was_not_known_is_looked_for_again_on_the_ui_thread_with_each_start()
    {
        var found = new PortChoice.Resolution(9400, Busy: false);
        var rig = Rig(resolvePort: () => found);

        var asked = rig.Server.ResolvePort!(CancellationToken.None);
        Assert.False(asked.IsCompleted);
        rig.Shell.Drain();

        Assert.Equal(found, await asked);
    }

    [Fact]
    public void A_failure_looking_for_the_port_is_told_to_the_start_that_waits()
    {
        var rig = Rig(resolvePort: () => throw new IOException("port.txt"));

        var asked = rig.Server.ResolvePort!(CancellationToken.None);
        rig.Shell.Drain();

        Assert.Equal("port.txt", Assert.IsType<IOException>(asked.Exception!.InnerException).Message);
    }

    [Fact]
    public void A_port_that_was_known_is_not_looked_for()
    {
        Assert.Null(Rig().Server.ResolvePort);
    }

    [Fact]
    public void A_start_the_person_made_asks_about_an_entry_nobody_chose_once_the_icon_is_up()
    {
        var rig = Rig();
        rig.Startup.Enable();
        rig.Shell.StartWithWindowsAnswer = true;

        rig.Controller.Begin();
        Assert.Equal(0, rig.Shell.StartWithWindowsAsked);
        rig.Shell.Drain();

        Assert.Equal(1, rig.Shell.StartWithWindowsAsked);
        Assert.True(rig.Startup.IsAnswered);
        Assert.True(rig.Startup.IsEnabled);
        Assert.Contains(rig.Shell.LastMenu, entry => entry is { Item: TrayMenuItem.StartWithWindows, Checked: true });
    }

    [Fact]
    public void A_start_nobody_asked_for_leaves_the_entry_alone()
    {
        var rig = Rig(new TrayStart(AnnounceOnReady: false, Interactive: true));
        rig.Startup.Enable();

        rig.Controller.Begin();
        rig.Shell.Drain();

        Assert.Equal(0, rig.Shell.StartWithWindowsAsked);
        Assert.False(rig.Startup.IsAnswered);
    }

    [Fact]
    public void A_second_launch_while_Weir_runs_tells_the_person_it_is_already_running_and_a_click_opens_it()
    {
        var rig = Rig();
        rig.Controller.Begin();
        rig.Server.SetPhase(ServerPhase.Running);
        rig.Shell.Drain();

        rig.Controller.ShowAlreadyRunning();
        rig.Shell.Handlers!.BalloonClicked();

        Assert.Equal(("Weir", TrayBalloons.AlreadyRunningText(9347), ToolTipIcon.Info, 10_000), rig.Shell.LastBalloon);
        Assert.Equal([(9347, "/")], rig.Shell.BrowserOpens);
    }
}
