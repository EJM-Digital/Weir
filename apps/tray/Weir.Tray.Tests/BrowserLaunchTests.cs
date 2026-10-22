using System.ComponentModel;
using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

public sealed class BrowserLaunchTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Browser_shell_failure_is_non_fatal_and_logged()
    {
        var result = Program.OpenBrowser(
            9347,
            _ => throw new Win32Exception(unchecked((int)0x80004021), "Shell execution unavailable"));

        Assert.False(result);
        var log = LogFile.ReadAllText(Path.Combine(_home.Path, "tray-host.log"));
        Assert.Contains("Could not open Weir in the browser", log);
        Assert.Contains("Shell execution unavailable", log);
    }

    [Fact]
    public void Browser_launch_uses_the_local_server_and_shell_execution()
    {
        ProcessStartInfo? observed = null;

        var result = Program.OpenBrowser(9347, info => observed = info);

        Assert.True(result);
        Assert.NotNull(observed);
        Assert.Equal("http://127.0.0.1:9347/", observed.FileName);
        Assert.True(observed.UseShellExecute);
    }

    [Fact]
    public void Browser_launch_can_open_the_local_update_check_page()
    {
        ProcessStartInfo? observed = null;

        var result = Program.OpenBrowser(
            9347,
            info => observed = info,
            Program.UpdateCheckPath);

        Assert.True(result);
        Assert.NotNull(observed);
        Assert.Equal("http://127.0.0.1:9347/system?tab=about", observed.FileName);
        Assert.True(observed.UseShellExecute);
    }

    [Fact]
    public void An_update_restart_does_not_announce_itself()
    {
        Assert.False(Program.AnnouncesStart(UpdateHandOver.ThenRestart.RestartArguments));
    }

    [Fact]
    public void A_start_a_person_makes_tells_them_Weir_is_running()
    {
        Assert.True(Program.AnnouncesStart([]));
        Assert.True(Program.AnnouncesStart(["--port", "9400"]));
    }
}
