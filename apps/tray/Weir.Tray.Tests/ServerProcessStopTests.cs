using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Stopping the bundled server asks it to stop and waits for it to exit on its own; only a server that does not, or
/// cannot be asked, is killed (#833). The processes are real (the stand-in server), not fakes of Process.
/// </summary>
public sealed class ServerProcessStopTests : IDisposable
{
    private static readonly ServerStopTimeouts ShortTimeouts = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly StandInServers _servers = new();

    public void Dispose()
    {
        _servers.Dispose();
        _home.Dispose();
    }

    [Fact]
    public async Task A_server_asked_to_stop_exits_on_its_own_without_being_killed()
    {
        var server = await _servers.StartAsync(ServerFolder());

        var outcome = await ServerProcessStop.StopAsync(server);

        Assert.Equal(ServerStopOutcome.StoppedCleanly, outcome);
        Assert.Equal(0, server.ExitCode);
    }

    [Fact]
    public async Task A_clean_stop_is_logged_with_how_long_it_took()
    {
        var server = await _servers.StartAsync(ServerFolder());

        await ServerProcessStop.StopAsync(server);

        Assert.Matches($@"pid={server.Id} stopped cleanly in \d+\.\d s", TrayLogText());
    }

    [Fact]
    public async Task A_server_that_does_not_stop_when_asked_is_killed_after_the_timeout()
    {
        var server = await _servers.StartAsync(ServerFolder(), "ignore-stop-requests");

        var outcome = await ServerProcessStop.StopAsync(server, ShortTimeouts);

        Assert.Equal(ServerStopOutcome.Killed, outcome);
        Assert.True(server.HasExited);
        Assert.Contains($"pid={server.Id} did not stop in 0.5 s; killing it", TrayLogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_cannot_be_asked_to_stop_is_killed_at_once()
    {
        var server = await _servers.StartAsync(ServerFolder(), "no-stop-event");

        var outcome = await ServerProcessStop.StopAsync(server);

        Assert.Equal(ServerStopOutcome.Killed, outcome);
        Assert.Contains($"pid={server.Id} cannot be asked to stop", TrayLogText(), StringComparison.Ordinal);
        Assert.DoesNotContain($"pid={server.Id} did not stop", TrayLogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_cannot_be_killed_is_reported_as_one_the_tray_could_not_stop()
    {
        var server = await _servers.StartAsync(ServerFolder(), "no-stop-event");

        var outcome = await ServerProcessStop.StopAsync(server, ShortTimeouts, _ => throw new System.ComponentModel.Win32Exception(5, "Access is denied"));

        Assert.Equal(ServerStopOutcome.KillFailed, outcome);
        Assert.Contains($"Could not kill the server process pid={server.Id}: Access is denied", TrayLogText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Quit_waits_longer_than_the_servers_host_allows_its_workers_and_the_installers_hooks_less_than_Velopack_does()
    {
        // The server's host gives its workers 30 seconds to finish when asked to stop; Velopack ends an install or uninstall hook
        // after 30 seconds, and a person is waiting for the tray at start-up.
        var host = TimeSpan.FromSeconds(30);
        var velopackHook = TimeSpan.FromSeconds(30);

        Assert.True(ServerStopTimeouts.Default.Graceful > host);
        Assert.True(ServerStopTimeouts.Hook.WorstCase <= velopackHook - TimeSpan.FromSeconds(10), ServerStopTimeouts.Hook.WorstCase.ToString());
        Assert.True(ServerStopTimeouts.Orphan.WorstCase <= TimeSpan.FromSeconds(15), ServerStopTimeouts.Orphan.WorstCase.ToString());
    }

    [Fact]
    public async Task A_server_that_has_already_exited_needs_no_stopping()
    {
        var server = await _servers.StartAsync(ServerFolder());
        await ServerProcessStop.StopAsync(server);

        var outcome = await ServerProcessStop.StopAsync(server);

        Assert.Equal(ServerStopOutcome.AlreadyExited, outcome);
    }

    [Fact]
    public void The_stop_event_is_named_after_the_process_id_in_the_users_session()
    {
        Assert.Equal(@"Local\Weir-Stop-4242", ServerStopRequest.EventName(4242));
    }

    private string ServerFolder() => Path.Combine(_home.Path, "server-" + Guid.NewGuid().ToString("n"));

    private string TrayLogText() => File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));
}
