using System.Security.AccessControl;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>Starting the tray, from its arguments to the tray itself, with every part that reaches Windows a recorder.</summary>
public sealed class TrayStartupTests : IDisposable
{
    private readonly StartupRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_firewall_command_is_run_alone_and_reports_to_the_console_and_the_log()
    {
        var code = _rig.Run("--allow-lan");

        Assert.Equal(42, code);
        Assert.Equal(["firewall-command"], _rig.Calls);
        Assert.Contains("The firewall command ran.", _rig.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_the_version_runs_the_install_hooks_and_starts_nothing()
    {
        Assert.Equal(0, _rig.Run("--version"));

        Assert.Equal(["installer-hooks"], _rig.Calls);
    }

    [Fact]
    public void A_start_after_Setup_steps_aside_when_Setup_has_started_a_tray_itself()
    {
        _rig.Effects = _rig.Effects with { AnotherTrayRuns = () => true };

        Assert.Equal(0, _rig.Run(RestartAfterSetup.Argument));

        Assert.Equal(["installer-hooks"], _rig.Calls);
        Assert.Contains("Start after Setup: Weir is already running", _rig.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_after_Setup_goes_on_when_no_tray_is_running()
    {
        Assert.Equal(7, _rig.Run(RestartAfterSetup.Argument, "--silent"));

        Assert.Single(_rig.Launches);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_second_start_tells_the_running_tray_unless_it_was_silent(bool silent, bool announced)
    {
        using var running = new Mutex(false, _rig.MutexName);

        var code = silent ? _rig.Run("--silent", "--port", "9400") : _rig.Run();

        Assert.Equal(0, code);
        Assert.Equal(announced, _rig.Calls.Contains("second-launch"));
        Assert.Empty(_rig.Launches);
        Assert.Contains("an existing Weir tray instance is already running", _rig.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_settles_everything_before_the_tray_runs_and_passes_it_what_it_settled()
    {
        _rig.Interactive = true;

        var code = _rig.Run();

        Assert.Equal(7, code);
        Assert.Equal(["installer-hooks", "error-log", "stdio", "windows-forms", "update-service", "orphans"], _rig.Calls);
        var launch = Assert.Single(_rig.Launches);
        Assert.Equal(PortChoice.DefaultPort, launch.Port);
        Assert.Equal(ListenScope.ThisPcOnly, launch.ListenScope);
        Assert.Equal(new TrayStart(AnnounceOnReady: true, Interactive: true), launch.Start);
        Assert.Same(_rig.Updates, launch.Updates);
        Assert.Null(launch.ResolvePort);
        Assert.Equal((_rig.Home, ListenScope.ThisPcOnly, true), Assert.Single(_rig.Widened));
        Assert.Equal(PortChoice.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(_rig.Home, PortChoice.SavedPortFileName)).Trim());
    }

    [Fact]
    public void A_tray_that_is_running_leaves_its_mark_until_the_process_ends_in_order()
    {
        _rig.Run();

        Assert.True(_rig.Mark.TakeLeftBy(TrayRun.ThisProcess().Executable, _ => false));
        _rig.Mark.Set(TrayRun.ThisProcess());
        var atExit = Assert.Single(_rig.AtExit);

        atExit();

        Assert.False(_rig.Mark.TakeLeftBy(TrayRun.ThisProcess().Executable, _ => false));
    }

    [Fact]
    public void A_start_nobody_asked_to_see_announces_nothing()
    {
        _rig.Interactive = true;

        _rig.Run("--no-browser");

        var launch = Assert.Single(_rig.Launches);
        Assert.False(launch.Start.AnnounceOnReady);
    }

    [Fact]
    public void An_update_left_waiting_is_installed_before_a_port_is_asked_for()
    {
        _rig.Updates = new FakeUpdateService { LeftWaitingVersion = "9.9.9" };

        var code = _rig.Run();

        Assert.Equal(0, code);
        Assert.Equal([FakeUpdateService.AppliedLeftWaiting], _rig.Updates.Applied);
        Assert.Empty(_rig.Launches);
    }

    [Fact]
    public void A_start_that_cannot_find_a_port_does_not_run_the_tray()
    {
        foreach (var port in Enumerable.Range(PortChoice.DefaultPort, 200))
        {
            _rig.PortsInUse.Add(port);
        }

        Assert.Equal(1, _rig.Run("--silent"));

        Assert.Empty(_rig.Launches);
    }

    [Fact]
    public void A_port_file_that_cannot_be_read_just_now_is_looked_at_again_by_the_tray()
    {
        File.WriteAllText(Path.Combine(_rig.Home, PortChoice.SavedPortFileName), "9444");
        using var held = new FileStream(Path.Combine(_rig.Home, PortChoice.SavedPortFileName), FileMode.Open, FileAccess.Read, FileShare.None);

        _rig.Run();

        var launch = Assert.Single(_rig.Launches);
        Assert.Equal(0, launch.Port);
        var again = Assert.IsType<Func<PortChoice.Resolution>>(launch.ResolvePort);
        Assert.Contains("could not be read just now", _rig.Log, StringComparison.Ordinal);
        held.Dispose();
        Assert.Equal(9444, again().Port);
    }

    [Fact]
    public void A_person_at_the_desktop_chooses_the_port_when_the_default_is_taken()
    {
        _rig.Interactive = true;
        _rig.PortsInUse.Add(PortChoice.DefaultPort);
        _rig.ChosenPort = 9123;

        _rig.Run();

        Assert.Equal(PortPromptReason.FirstRun, Assert.Single(_rig.Prompts).Reason);
        Assert.Equal(9123, Assert.Single(_rig.Launches).Port);
    }

    [Fact]
    public void A_start_that_throws_is_logged_and_ends_with_an_error_code()
    {
        _rig.Effects = _rig.Effects with { InitializeApplication = () => throw new InvalidOperationException("Windows Forms refused.") };

        Assert.Equal(1, _rig.Run());

        Assert.Contains("Fatal startup error", _rig.Log, StringComparison.Ordinal);
        Assert.Contains("Windows Forms refused.", _rig.Log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void A_data_folder_another_account_owns_stops_the_start_and_tells_the_person_at_the_desktop(bool interactive, int errors)
    {
        _rig.Interactive = interactive;
        _rig.Effects = _rig.Effects with { SecureRuntimeHome = home => throw new RuntimeHomeOwnedByAnotherAccountException(home, "SOMEONE") };

        Assert.Equal(1, _rig.Run());

        Assert.Equal(errors, _rig.Errors.Count);
        Assert.Contains("Not starting", _rig.Log, StringComparison.Ordinal);
        Assert.Empty(_rig.Launches);
    }

    [Theory]
    [MemberData(nameof(RestrictionFailures))]
    public void A_data_folder_that_cannot_be_restricted_does_not_stop_the_start(Exception failure)
    {
        _rig.Effects = _rig.Effects with { SecureRuntimeHome = _ => throw failure };

        Assert.Equal(7, _rig.Run());

        Assert.Contains("keeps its current access list", _rig.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_folder_that_was_tightened_is_said_so_in_the_log()
    {
        _rig.Effects = _rig.Effects with { SecureRuntimeHome = _ => true };

        _rig.Run();

        Assert.Contains("Restricted ", _rig.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_of_a_firewall_command_goes_to_the_console_too()
    {
        TrayStartup.LogToConsoleAndFile("A message for the person who started it.");

        Assert.Contains("A message for the person who started it.", _rig.Log, StringComparison.Ordinal);
    }

    public static TheoryData<Exception> RestrictionFailures() =>
        new()
        {
            new UnauthorizedAccessException("denied"),
            new IOException("in use"),
            new PrivilegeNotHeldException("SeRestorePrivilege"),
        };
}
