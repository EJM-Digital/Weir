using System.Diagnostics;

using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The edges of the server host, with real stand-in servers on a fake clock as in ServerHostTests: a watchdog that finds the server
/// running, a restart that gives up, a server that cannot be found, hung or killed, and a change of who may connect that works,
/// goes back, or cannot go back.
/// </summary>
public sealed class ServerHostEdgeTests
{
    private const int TestTimeoutMs = 120_000;
    private const string ExitCode = "WEIR_STANDIN_EXIT_CODE";

    private static Func<TimeSpan, Task> After(Func<Task> condition) => async delay =>
    {
        if (delay == ServerHealth.RetryDelay)
        {
            await condition();
        }
    };

    private static async Task<StartGaveUpException> GiveUpAsync(StartRig rig, Task start, Func<TimeSpan, Task>? whenParked = null)
    {
        try
        {
            await rig.DriveAsync(start, whenParked);
        }
        catch (StartGaveUpException gaveUp)
        {
            return gaveUp;
        }

        throw new InvalidOperationException("The start was expected to give up and did not.");
    }

    private static async Task KillAsync(int pid)
    {
        using var running = Process.GetProcessById(pid);
        running.Kill();
        await running.WaitForExitAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_after_the_watchdog_was_told_to_stop_does_not_start_it_again()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        using var stop = new CancellationTokenSource();
        rig.Host.Watch(() => { }, stop.Token);
        await stop.CancelAsync();
        await StartRig.UntilAsync(() => !rig.Host.IsWatching, "the watchdog to end");

        await rig.Host.MoveToScopeAsync(ListenScope.ThisPcOnly, CancellationToken.None);

        Assert.False(rig.Host.IsWatching);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_start_policy_the_host_is_given_decides_how_many_tries_it_makes()
    {
        using var rig = new StartRig(StartPolicy.Default with { StartLimitBurst = 2 });
        StartRig.Steer(ExitCode, "3");

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None), After(rig.LatestExitedAsync));

        Assert.Equal(2, gaveUp.Attempts);
        Assert.Equal(2, rig.Starts.Count);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_watchdog_that_finds_the_server_running_leaves_it_alone_and_keeps_watching()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        Assert.False(rig.Host.IsWatching);
        rig.Host.Watch(() => throw new Xunit.Sdk.XunitException("The watchdog gave up on a server that is running."), CancellationToken.None);

        while (await rig.Clock.NextDelay() != ServerHost.WatchInterval)
        {
        }
        rig.Clock.Advance(ServerHost.WatchInterval);
        await StartRig.UntilAsync(() => rig.Host.WatchLooks == 1, "the watchdog to look at the server");

        Assert.Single(rig.Starts);
        Assert.True(rig.Host.IsWatching);
        Assert.True(rig.Host.ServerIsRunning);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_a_move_has_already_replaced_is_not_restarted_for_having_exited()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        using var someoneElse = Process.GetCurrentProcess();

        var back = await rig.Host.RestartAfterExitAsync(someoneElse, CancellationToken.None);

        Assert.True(back);
        Assert.Single(rig.Starts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_process_the_host_does_not_hold_is_not_taken_for_its_failed_server()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        using var someoneElse = Process.GetCurrentProcess();

        rig.Host.ForgetFailedProcess(someoneElse);

        Assert.True(rig.Host.ServerIsRunning);
        Assert.False(someoneElse.HasExited);
    }

    [Fact]
    public void A_process_that_was_never_started_has_no_start_time_so_it_began_no_later_than_now()
    {
        using var never = new Process();

        var started = ServerHost.StartTimeUtc(never);

        Assert.InRange(started, DateTime.UtcNow - TimeSpan.FromMinutes(1), DateTime.UtcNow);
    }

    [Fact]
    public void Windows_starting_nothing_is_a_failure_to_start_and_says_which_program()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ServerHost.Started(null));

        Assert.Equal("Failed to start WeirServer.exe", failure.Message);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_running_server_is_known_only_while_its_process_is()
    {
        using var rig = new StartRig();
        Assert.Null(rig.Host.Running);

        await rig.StartReadyAsync();
        var pid = rig.Starts[0].Pid;
        Assert.Equal(pid, rig.Host.Running?.ProcessId);

        await KillAsync(pid);
        Assert.Null(rig.Host.Running);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Restart_Weir_that_cannot_bring_the_server_back_gives_up_and_leaves_Weir_stopped()
    {
        using var rig = new StartRig();
        StartRig.Steer(ExitCode, "3");

        var restart = rig.Host.RestartNowAsync(CancellationToken.None);
        await rig.DriveAsync(restart, After(rig.LatestExitedAsync));

        Assert.False(await restart);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Host.GaveUpAfterAttempts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_program_that_is_not_there_is_a_failed_start_that_says_so()
    {
        using var rig = new StartRig();
        File.Delete(Path.Combine(rig.Server, "WeirServer.exe"));

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        Assert.Contains("could not be started: Bundled server host is missing.", gaveUp.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Starts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_start_with_no_port_and_no_way_to_find_one_is_a_failed_start_that_says_so()
    {
        using var rig = new StartRig(portUnknown: true);

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        Assert.Contains("no port is known", gaveUp.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Starts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_hung_server_that_is_reported_stopped_cleanly_is_taken_as_gone_and_tried_again()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        rig.Host.StopHung = async process =>
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return ServerStopOutcome.StoppedCleanly;
        };

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None), After(rig.LatestReportedAsync));

        Assert.Equal(StartPolicy.Default.StartLimitBurst, gaveUp.Attempts);
        Assert.StartsWith("hung in phase 'opening the database'", gaveUp.Reason, StringComparison.Ordinal);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_exits_after_it_reported_a_phase_names_the_phase_it_was_in()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        var looked = new HashSet<int>();

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None), async delay =>
        {
            if (delay != ServerHealth.RetryDelay)
            {
                return;
            }

            await rig.LatestReportedAsync();
            var pid = rig.Starts[^1].Pid;
            if (!looked.Add(pid))
            {
                // The start has seen the report on the delay before this one, so it has a phase to name when the server ends.
                await KillAsync(pid);
            }
        });

        Assert.Contains("(last phase:", gaveUp.Reason, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Choosing_the_devices_the_server_already_listens_for_changes_nothing()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();

        var change = await rig.Host.MoveToScopeAsync(ListenScope.ThisPcOnly, CancellationToken.None);

        Assert.Equal(ScopeChange.Unchanged, change);
        Assert.Single(rig.Starts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Choosing_other_devices_restarts_the_server_for_them()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();

        var move = rig.Host.MoveToScopeAsync(ListenScope.OtherDevices, CancellationToken.None);
        await rig.DriveAsync(move, After(rig.LatestListeningAsync));

        Assert.Equal(ScopeChange.Applied, await move);
        Assert.Equal(ListenScope.OtherDevices, rig.Host.Scope);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Equal(2, rig.Starts.Count);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_will_not_start_for_other_devices_goes_back_to_this_PC_only()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var before = rig.Starts.Count;
        StartRig.Steer(ExitCode, "3");

        var move = rig.Host.MoveToScopeAsync(ListenScope.OtherDevices, CancellationToken.None);
        await rig.DriveAsync(move, async delay =>
        {
            if (delay != ServerHealth.RetryDelay)
            {
                return;
            }

            if (rig.Starts.Count == before + 1)
            {
                await rig.LatestExitedAsync();
                StartRig.Steer(ExitCode, null);
            }
            else
            {
                await rig.LatestListeningAsync();
            }
        });

        Assert.Equal(ScopeChange.Failed, await move);
        Assert.Equal(ListenScope.ThisPcOnly, rig.Host.Scope);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_for_other_devices_that_cannot_go_back_either_leaves_Weir_stopped()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        StartRig.Steer(ExitCode, "3");

        var move = rig.Host.MoveToScopeAsync(ListenScope.OtherDevices, CancellationToken.None);
        await rig.DriveAsync(move, After(rig.LatestExitedAsync));

        Assert.Equal(ScopeChange.Failed, await move);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_move_whose_hung_server_cannot_be_stopped_leaves_Weir_stopped_and_starts_nothing_beside_it()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        File.WriteAllText(rig.ReadyFile, string.Empty);
        await rig.StartReadyAsync();
        File.Delete(rig.ReadyFile);
        rig.Host.StopHung = _ => Task.FromResult(ServerStopOutcome.KillFailed);

        var move = rig.Host.MoveToScopeAsync(ListenScope.OtherDevices, CancellationToken.None);
        await rig.DriveAsync(move, After(rig.LatestReportedAsync));

        Assert.Equal(ScopeChange.Failed, await move);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Contains("It is still running, so nothing is started beside it.", rig.TrayLog, StringComparison.Ordinal);
    }
}
