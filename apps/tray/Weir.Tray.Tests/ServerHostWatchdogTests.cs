using System.Diagnostics;
using System.Globalization;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// How the watchdog ends and how it comes back, and how a move to another port ends: real stand-in servers on a fake clock, as in
/// ServerHostTests. A watchdog that gave up leaves Weir stopped and says so once; a server stopped on purpose is never started again by
/// it; a restart or a move brings it back; and a move saves its port only when the server is on it.
/// </summary>
public sealed class ServerHostWatchdogTests
{
    private const int TestTimeoutMs = 120_000;
    private const string ExitCode = "WEIR_STANDIN_EXIT_CODE";

    private static Func<TimeSpan, Task> AfterRetry(Func<Task> condition) => async delay =>
    {
        if (delay == ServerHealth.RetryDelay)
        {
            await condition();
        }
    };

    private static async Task KillAsync(int pid)
    {
        using var running = Process.GetProcessById(pid);
        running.Kill();
        await running.WaitForExitAsync();
    }

    private static string FileOf(StartRig rig, string name) => Path.Combine(rig.HomePath, name);

    /// <summary>Ends the running server while every start after it ends at once, and drives the clock until the watchdog gives up.</summary>
    private static async Task<int> GiveUpAsync(StartRig rig)
    {
        var gaveUp = 0;
        rig.Host.Watch(() => Interlocked.Increment(ref gaveUp), CancellationToken.None);
        StartRig.Steer(ExitCode, "3");
        await KillAsync(rig.Starts[0].Pid);
        var ended = Task.Run(() => StartRig.UntilAsync(() => Volatile.Read(ref gaveUp) > 0 && rig.Host.Phase == ServerPhase.Stopped && !rig.Host.IsWatching, "the watchdog to give up"));
        await rig.DriveAsync(ended, AfterRetry(rig.LatestExitedAsync));
        return Volatile.Read(ref gaveUp);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_running_server_that_cannot_be_brought_back_ends_the_trying_once_and_leaves_Weir_stopped()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();

        var gaveUp = await GiveUpAsync(rig);

        Assert.Equal(1, gaveUp);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Host.GaveUpAfterAttempts);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Starts.Count);
        Assert.False(rig.Host.IsWatching);
        Assert.Equal(1, CountOf(rig.TrayLog, "Giving up"));
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Starts.Count);
        Assert.Equal(1, gaveUp);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_stopped_on_purpose_is_never_started_again_by_the_watchdog()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var gaveUp = 0;
        rig.Host.Watch(() => Interlocked.Increment(ref gaveUp), CancellationToken.None);
        Assert.True(rig.Host.IsWatching);

        await rig.Host.StopAsync();
        var ended = Task.Run(() => StartRig.UntilAsync(() => !rig.Host.IsWatching, "the watchdog to see the server was stopped on purpose"));
        await rig.DriveAsync(ended);

        Assert.Single(rig.Starts);
        Assert.Equal(0, gaveUp);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(0, rig.Host.GaveUpAfterAttempts);
        Assert.DoesNotContain("Start attempt", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_watchdog_is_started_again_when_a_move_to_another_port_brings_the_server_back()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        Assert.Equal(1, await GiveUpAsync(rig));
        StartRig.Steer(ExitCode, null);

        var move = rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None);
        await rig.DriveAsync(move, AfterRetry(rig.LatestListeningAsync));

        Assert.Equal(PortMove.Moved, await move);
        Assert.True(rig.Host.IsWatching);
        var startsBefore = rig.Starts.Count;
        await KillAsync(rig.Starts[^1].Pid);
        var restarted = Task.Run(() => StartRig.UntilAsync(() => rig.Starts.Count == startsBefore + 1 && rig.Host.Phase == ServerPhase.Running, "the watchdog to start the server again"));
        await rig.DriveAsync(restarted, AfterRetry(rig.LatestListeningAsync));
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_watchdog_is_started_again_by_Restart_Weir_after_it_gave_up()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        Assert.Equal(1, await GiveUpAsync(rig));
        StartRig.Steer(ExitCode, null);

        var restart = rig.Host.RestartNowAsync(CancellationToken.None);
        await rig.DriveAsync(restart, AfterRetry(rig.LatestListeningAsync));

        Assert.True(await restart);
        Assert.True(rig.Host.IsWatching);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_to_a_free_port_saves_it_writes_it_as_the_current_port_and_the_server_is_on_it()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var to = StartRig.AnotherPort();

        var move = rig.Host.MoveToPortAsync(to, CancellationToken.None);
        await rig.DriveAsync(move, AfterRetry(rig.LatestListeningAsync));

        Assert.Equal(PortMove.Moved, await move);
        var port = to.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(to, rig.Host.Port);
        Assert.Equal(port, File.ReadAllText(FileOf(rig, PortChoice.SavedPortFileName)).Trim());
        Assert.Equal(port, File.ReadAllText(FileOf(rig, ServerHost.CurrentPortFileName)).Trim());
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Contains($"Change port: Weir is healthy on http://127.0.0.1:{port}/ and port {port} is saved.", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_the_server_cannot_make_goes_back_to_the_old_port_saves_nothing_and_ends_Running()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var old = rig.Host.Port;
        var before = rig.Starts.Count;
        StartRig.Steer(ExitCode, "3");

        var move = rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None);
        await rig.DriveAsync(move, async delay =>
        {
            if (delay != ServerHealth.RetryDelay)
            {
                return;
            }

            if (rig.Starts.Count == before + 1)
            {
                // The server for the new port has exited; the one that goes back is let start.
                await rig.LatestExitedAsync();
                StartRig.Steer(ExitCode, null);
            }
            else
            {
                await rig.LatestListeningAsync();
            }
        });

        Assert.Equal(PortMove.KeptOldPort, await move);
        Assert.Equal(old, rig.Host.Port);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.False(File.Exists(FileOf(rig, PortChoice.SavedPortFileName)));
        Assert.Contains($"Change port: going back to port {old.ToString(CultureInfo.InvariantCulture)}.", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_that_cannot_go_back_either_leaves_Weir_stopped_and_saves_nothing()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var old = rig.Host.Port;
        StartRig.Steer(ExitCode, "3");

        var move = rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None);
        await rig.DriveAsync(move, AfterRetry(rig.LatestExitedAsync));

        Assert.Equal(PortMove.Stopped, await move);
        Assert.Equal(old, rig.Host.Port);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.False(File.Exists(FileOf(rig, PortChoice.SavedPortFileName)));
        Assert.Equal(ListenScope.ThisPcOnly, rig.Host.Scope);
    }

    private static int CountOf(string text, string wanted)
    {
        var count = 0;
        for (var at = text.IndexOf(wanted, StringComparison.Ordinal); at >= 0; at = text.IndexOf(wanted, at + wanted.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
