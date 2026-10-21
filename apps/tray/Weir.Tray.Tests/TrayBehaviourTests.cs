using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>Clicks, balloons and the folders the menu opens.</summary>
public sealed class TrayBehaviourTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void A_click_and_the_double_click_that_follows_it_open_Weir_once()
    {
        var clock = new FakeTimeProvider();
        var debounce = new Debounce(TimeSpan.FromMilliseconds(1250), clock);

        Assert.True(debounce.Allow());
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(debounce.Allow());
    }

    [Fact]
    public void A_later_click_opens_Weir_again()
    {
        var clock = new FakeTimeProvider();
        var debounce = new Debounce(TimeSpan.FromMilliseconds(1250), clock);

        Assert.True(debounce.Allow());
        clock.Advance(TimeSpan.FromMilliseconds(1250));

        Assert.True(debounce.Allow());
    }

    [Fact]
    public void Information_stays_ten_seconds_and_a_problem_thirty()
    {
        Assert.Equal(10_000, TrayBalloons.TimeoutMs(ToolTipIcon.Info));
        Assert.Equal(10_000, TrayBalloons.TimeoutMs(ToolTipIcon.None));
        Assert.Equal(30_000, TrayBalloons.TimeoutMs(ToolTipIcon.Warning));
        Assert.Equal(30_000, TrayBalloons.TimeoutMs(ToolTipIcon.Error));
    }

    [Fact]
    public void The_balloon_when_the_server_gave_up_offers_the_restart_in_plain_words()
    {
        Assert.Equal("Weir stopped and couldn't start again by itself. Click to restart it.", TrayBalloons.StoppedText);
    }

    [Fact]
    public void A_second_launch_is_told_where_Weir_is()
    {
        Assert.Equal("Weir is already running at http://localhost:9400. Click to open it.", TrayBalloons.AlreadyRunningText(9400));
    }

    [Fact]
    public void A_moved_port_offers_the_new_address_to_open()
    {
        Assert.Equal("Weir is now at http://localhost:9400. Click to open it.", TrayBalloons.PortMovedText(9400));
    }

    [Fact]
    public void The_logs_folder_is_logs_in_the_data_folder()
    {
        Assert.Equal(@"C:\ProgramData\Weir\logs", RuntimeFolders.Logs(@"C:\ProgramData\Weir", _ => null));
        Assert.Equal(@"C:\ProgramData\Weir\logs", RuntimeFolders.Logs(@"C:\ProgramData\Weir", _ => "  "));
    }

    [Fact]
    public void A_log_folder_the_operator_chose_is_the_one_opened()
    {
        Assert.Equal(@"D:\weir-logs", RuntimeFolders.Logs(@"C:\ProgramData\Weir", name => name == "WEIR_LOG_DIR" ? @"D:\weir-logs" : null));
        Assert.Equal(@"C:\ProgramData\Weir\server-logs", RuntimeFolders.Logs(@"C:\ProgramData\Weir", _ => "server-logs"));
    }

    [Fact]
    public void The_version_has_no_build_metadata()
    {
        Assert.Equal("1.0.0-rc.10", AppVersion.Without("1.0.0-rc.10+4f2a9c1d"));
        Assert.Equal("1.0.0", AppVersion.Without("1.0.0"));
        Assert.Equal("unknown", AppVersion.Without(null));
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    [Fact]
    public void A_start_a_person_made_offers_the_address_to_open_and_opens_nothing()
    {
        Assert.Equal("Weir is running at http://localhost:9347. Click to open it.", TrayBalloons.RunningText(9347));
    }

    [Fact]
    public void Showing_a_file_asks_Explorer_to_select_it()
    {
        var log = Path.Combine(_home.Path, "tray-host.log");
        File.WriteAllText(log, "x");
        ProcessStartInfo? started = null;

        RuntimeFolders.Reveal(log, info => started = info, _ => { });

        Assert.NotNull(started);
        Assert.Equal("explorer.exe", started.FileName);
        Assert.Equal($"/select,\"{log}\"", started.Arguments);
    }

    [Fact]
    public void A_file_that_is_not_there_opens_its_folder_instead_of_selecting_it()
    {
        ProcessStartInfo? started = null;
        var missing = Path.Combine(_home.Path, "tray-host.log");

        RuntimeFolders.Reveal(missing, info => started = info, _ => { });

        Assert.NotNull(started);
        Assert.Equal(_home.Path, started.FileName);
        Assert.Empty(started.Arguments);
    }

    [Fact]
    public void The_fallback_icon_is_a_copy_so_disposing_it_leaves_the_systems_own_icon_alone()
    {
        var fallback = Program.FallbackIcon();

        Assert.NotSame(SystemIcons.Application, fallback);
        Assert.NotSame(fallback, Program.FallbackIcon());
        fallback.Dispose();
        Assert.True(SystemIcons.Application.Width > 0);
    }

    [Fact]
    public void The_installed_tray_is_the_running_program_or_the_install_root_s_own()
    {
        Assert.Equal("C:/Weir/Weir.exe", Program.InstalledTray("C:/Weir/Weir.exe"));
        Assert.Equal(Path.Combine(InstallProcesses.Root(), "Weir.exe"), Program.InstalledTray(null));
    }
}
