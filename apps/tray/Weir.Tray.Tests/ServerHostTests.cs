using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray's one supervisor for every start of the server, with real stand-in servers on a fake clock: a server that exits at
/// once, one that hangs in a phase, one that is slow but keeps reporting (which must not be killed), the start limit, backoff and
/// the Restart Weir budget. The tests pass the fake delays themselves, so no assertion is about how long anything took.
/// </summary>
public sealed class ServerHostTests
{
    private const int TestTimeoutMs = 120_000;
    private static readonly StartPolicy Policy = StartPolicy.Default;

    // Passes each short poll only once the newest stand-in has done what the test is about: a real process takes a moment to exit or
    // to listen, and the fake clock would otherwise run through the whole window before it had.
    private static Func<TimeSpan, Task> After(Func<Task> condition) => async delay =>
    {
        if (delay == ServerHealth.RetryDelay)
        {
            await condition();
        }
    };

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_exits_at_once_is_tried_again_after_a_growing_wait_until_the_start_limit_and_then_the_tray_gives_up()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));

        Assert.Equal(Policy.StartLimitBurst, gaveUp.Attempts);
        Assert.StartsWith("exited with code 3", gaveUp.Reason, StringComparison.Ordinal);
        // The waits between the five attempts, with the jitter's random number at 0: half of 2, 4, 8 and 16 seconds.
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            rig.Delays.Where(delay => delay != ServerHealth.RetryDelay));
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(Policy.StartLimitBurst, rig.Host.GaveUpAfterAttempts);
        Assert.False(rig.Host.ServerIsRunning);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Every_attempt_is_logged_with_its_number_the_reason_and_the_next_delay_and_stays_in_both_logs()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");

        var start = rig.Host.StartAsync(CancellationToken.None);
        await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));

        foreach (var log in new[] { rig.TrayLog, rig.ServerLog })
        {
            Assert.Contains("Start attempt 1 failed: exited with code 3", log, StringComparison.Ordinal);
            Assert.Contains("Trying again in 1 s (1 of 5 failures allowed within 15 min)", log, StringComparison.Ordinal);
            Assert.Contains("Start attempt 2 failed: exited with code 3", log, StringComparison.Ordinal);
            Assert.Contains("Trying again in 2 s (2 of 5 failures allowed within 15 min)", log, StringComparison.Ordinal);
            Assert.Contains("Trying again in 4 s (3 of 5 failures allowed within 15 min)", log, StringComparison.Ordinal);
            Assert.Contains("Trying again in 8 s (4 of 5 failures allowed within 15 min)", log, StringComparison.Ordinal);
            Assert.Contains("Start attempt 5 failed: exited with code 3", log, StringComparison.Ordinal);
            Assert.Contains("Giving up (5 of 5 failures allowed within 15 min); Weir is not running. Restart Weir tries again.", log, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Each_retry_tells_the_new_server_which_attempt_it_is_and_why_the_earlier_ones_failed()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");

        var start = rig.Host.StartAsync(CancellationToken.None);
        await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));

        var starts = rig.Starts;
        Assert.Equal(["", "2", "3", "4", "5"], starts.Select(s => s.Attempt));
        Assert.Equal(string.Empty, starts[0].Failures);
        Assert.StartsWith("attempt 1: exited with code 3", starts[1].Failures, StringComparison.Ordinal);
        Assert.Contains("attempt 4: exited with code 3", starts[4].Failures, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_start_that_only_worked_on_a_retry_is_logged_as_failed_attempts_and_still_ends_ready()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "7");
        StartRig.Steer("WEIR_STANDIN_SUCCEED_AT_ATTEMPT", "3");

        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, After(async () =>
        {
            await rig.LatestStartedAsync(1);
            await StartRig.UntilAsync(() => !StartRig.IsRunning(rig.Starts[^1].Pid) || rig.Starts[^1].Attempt == "3", "the stand-in to exit or the third attempt to start");
            if (rig.Starts[^1].Attempt == "3")
            {
                await rig.LatestListeningAsync();
            }
        }));

        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.True(rig.Host.ServerIsRunning);
        foreach (var log in new[] { rig.TrayLog, rig.ServerLog })
        {
            Assert.Contains("Start attempt 1 failed: exited with code 7", log, StringComparison.Ordinal);
            Assert.Contains("Start attempt 2 failed: exited with code 7", log, StringComparison.Ordinal);
            Assert.Contains("Weir started on attempt 3, after 2 failed attempts: attempt 1: exited with code 7", log, StringComparison.Ordinal);
        }

        // The words the suite's tray-log check looks for are there, so a start that needed a retry is never read as a clean one.
        Assert.Matches("(?i)(failed)", rig.TrayLog);
        Assert.Equal("3", rig.Starts[^1].Attempt);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_hung_in_a_phase_is_stopped_with_the_phase_named_and_tried_again_until_the_limit()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestReportedAsync));

        Assert.Equal(Policy.StartLimitBurst, gaveUp.Attempts);
        Assert.Equal("hung in phase 'opening the database' for 30 s", gaveUp.Reason);
        Assert.Contains("Start attempt 1 failed: hung in phase 'opening the database' for 30 s. Trying again in 1 s", rig.TrayLog, StringComparison.Ordinal);
        // Each stuck process was looked at before it was stopped, and every one is gone.
        Assert.Equal(Policy.StartLimitBurst, CountOf(rig.TrayLog, "did not answer: it had run for"));
        Assert.Contains("loaded", rig.TrayLog, StringComparison.Ordinal);
        await rig.AllExitedAsync();
        Assert.False(rig.Host.ServerIsRunning);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_hung_before_it_reported_anything_is_stopped_and_said_to_have_been()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestListeningAsync));

        Assert.Equal("hung before it reported anything for 30 s", gaveUp.Reason);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Another_server_answering_ready_on_the_port_is_not_taken_for_the_one_the_tray_started()
    {
        using var rig = new StartRig();
        using var stranger = new ReadyAnswerer(rig.Host.Port);
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestListeningAsync));

        // The stranger said 200 again and again, and the server the tray started never said it was listening.
        Assert.True(stranger.Answers > 0);
        Assert.Equal("hung before it reported anything for 30 s", gaveUp.Reason);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.False(rig.Host.ServerIsRunning);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_progress_file_of_an_earlier_run_does_not_pass_for_progress_of_this_one()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, async delay =>
        {
            if (delay == ServerHealth.RetryDelay)
            {
                await rig.LatestListeningAsync();
                // A record of a server that has since ended, with a process id this one does not have, appears on every look.
                rig.WriteProgress(pid: 1, phase: "ready");
            }
        });

        Assert.Equal("hung before it reported anything for 30 s", gaveUp.Reason);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_start_clears_the_file_an_earlier_server_left_before_the_new_server_runs()
    {
        using var rig = new StartRig();
        rig.WriteProgress(pid: 1, phase: "ready");

        await rig.StartReadyAsync();

        // The stand-in writes no progress of its own, so a file that is still there would be the earlier server's.
        Assert.Null(StartProgressFile.Read(rig.HomePath));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_slow_start_that_keeps_reporting_progress_is_never_killed_however_long_it_takes()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "migrating");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        var step = 0;
        var polls = 0;
        var began = rig.Clock.GetUtcNow();

        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, async delay =>
        {
            Assert.Equal(ServerHealth.RetryDelay, delay);
            await rig.LatestListeningAsync();
            // Every fourth poll (a second of fake time) the migration moves on; after five windows' worth it is done.
            if (++polls % 4 == 0)
            {
                rig.WriteProgress(rig.Starts[^1].Pid, "migrating", ++step, 1_000);
            }

            if (polls == (int)(Policy.StallWindow * 5 / ServerHealth.RetryDelay))
            {
                File.WriteAllText(rig.ReadyFile, "ready");
            }
        });

        Assert.True(rig.Clock.GetUtcNow() - began > Policy.StallWindow * 4, "The start was not slower than four stall windows");
        Assert.Single(rig.Starts);
        Assert.True(rig.Host.ServerIsRunning);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.DoesNotContain("failed", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_tray_asks_every_server_it_starts_for_its_progress_on_standard_output()
    {
        using var rig = new StartRig();

        await rig.StartReadyAsync();

        Assert.Equal("1", Assert.Single(rig.Starts).Pipe);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_slow_start_that_reports_only_on_the_pipe_is_never_killed_although_no_progress_file_is_ever_written()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_SAY_FILE", rig.SayFile);
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        var polls = 0;
        var step = 0;
        var began = rig.Clock.GetUtcNow();

        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, async delay =>
        {
            await rig.LatestListeningAsync();
            // Every twentieth poll (five seconds of fake time) the migration reports a step; after five windows' worth it is done.
            if (++polls % 20 == 0)
            {
                rig.Say(rig.Starts[^1].Pid, "migrating", ++step, 1_000);
                await StartRig.UntilAsync(() => rig.ServerLog.Contains($"(step {step} of 1000)", StringComparison.Ordinal), "the line to reach the tray through the pipe");
            }

            if (polls == (int)(Policy.StallWindow * 5 / delay))
            {
                File.WriteAllText(rig.ReadyFile, "ready");
            }
        });

        Assert.False(File.Exists(Path.Combine(rig.HomePath, StartProgressFile.FileName)));
        Assert.True(rig.Clock.GetUtcNow() - began > Policy.StallWindow * 4);
        Assert.Single(rig.Starts);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.DoesNotContain("failed", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_start_that_reported_on_the_pipe_and_then_went_quiet_is_hung_in_the_phase_it_last_reported()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_SAY_FILE", rig.SayFile);
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        var said = 0;

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, async delay =>
        {
            if (delay != ServerHealth.RetryDelay)
            {
                return;
            }

            await rig.LatestListeningAsync();
            if (said < rig.Starts.Count)
            {
                rig.Say(rig.Starts[^1].Pid, "saving_copy");
                await StartRig.UntilAsync(() => rig.ServerLog.Contains($"[pid {rig.Starts[^1].Pid} progress] saving a copy of the data", StringComparison.Ordinal), "the line to reach the tray");
                said = rig.Starts.Count;
            }
        });

        Assert.Equal("hung in phase 'saving a copy of the data' for 30 s", gaveUp.Reason);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_asks_for_longer_silence_than_the_stall_window_is_given_it()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "saving_copy");
        StartRig.Steer("WEIR_STANDIN_EXPECT_S", "300");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        var polls = 0;
        var began = rig.Clock.GetUtcNow();

        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, async delay =>
        {
            await rig.LatestListeningAsync();
            // It says nothing more for 250 seconds, eight times the stall window, then is ready.
            if (++polls == (int)(TimeSpan.FromSeconds(250) / delay))
            {
                File.WriteAllText(rig.ReadyFile, "ready");
            }
        });

        Assert.True(rig.Clock.GetUtcNow() - began >= TimeSpan.FromSeconds(250));
        Assert.Single(rig.Starts);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_process_the_tray_did_not_start_is_never_stopped_with_the_ones_it_did()
    {
        using var rig = new StartRig();
        var bystanderFolder = Path.Combine(rig.HomePath, "bystander");
        var bystanderExe = StandInServers.Place(bystanderFolder, "WeirServer.exe");
        var bystanderStart = new ProcessStartInfo(bystanderExe) { UseShellExecute = false, CreateNoWindow = true };
        bystanderStart.Environment["WEIR_HOME"] = bystanderFolder;
        bystanderStart.ArgumentList.Add("no-stop-event");
        using var bystander = Process.Start(bystanderStart)!;
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        try
        {
            var start = rig.Host.StartAsync(CancellationToken.None);
            await RunToEndAsync(rig, start, After(rig.LatestReportedAsync));

            await rig.AllExitedAsync();
            Assert.False(bystander.HasExited, "A server the tray did not start was stopped");
        }
        finally
        {
            bystander.Kill(entireProcessTree: true);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_ran_and_then_exits_is_started_again_through_the_same_supervision()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var gaveUp = false;
        rig.Host.Watch(() => gaveUp = true, CancellationToken.None);
        var first = rig.Starts[0].Pid;
        using (var running = Process.GetProcessById(first))
        {
            running.Kill();
            await running.WaitForExitAsync();
        }

        var restarted = Task.Run(() => StartRig.UntilAsync(() => rig.Starts.Count == 2 && rig.Host.ServerIsRunning && rig.Host.Phase == ServerPhase.Running, "the second server to be running"));
        await rig.DriveAsync(restarted, After(async () =>
        {
            await rig.LatestStartedAsync(2);
            await rig.LatestListeningAsync();
        }));

        Assert.False(gaveUp);
        Assert.Contains("Start attempt 1 failed: exited with code", rig.TrayLog, StringComparison.Ordinal);
        Assert.Equal("2", rig.Starts[1].Attempt);
        Assert.StartsWith("attempt 1: exited with code", rig.Starts[1].Failures, StringComparison.Ordinal);
        Assert.Contains("Weir started on attempt 2, after 1 failed attempt", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Restart_Weir_after_the_tray_gave_up_gets_the_whole_budget_of_attempts_again()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");
        var start = rig.Host.StartAsync(CancellationToken.None);
        await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", null);

        var restart = rig.Host.RestartNowAsync(CancellationToken.None);
        await rig.DriveAsync(restart, After(rig.LatestListeningAsync));

        Assert.True(await restart);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Equal(0, rig.Host.GaveUpAfterAttempts);
        // Attempt 1 again, so nothing is passed to the server about earlier failures.
        Assert.Equal(string.Empty, rig.Starts[^1].Attempt);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_said_why_it_cannot_start_is_not_tried_again()
    {
        const string Headline = "Couldn't save a copy of its data before updating";
        const string Sentence = "Weir couldn't save a copy of its data before updating, so it didn't change anything: the drive is full.";
        using var rig = new StartRig();
        File.WriteAllText(Path.Combine(rig.Server, "fail-start.txt"), $"{Headline}\n{Sentence}");

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));

        Assert.Equal(1, gaveUp.Attempts);
        Assert.Single(File.ReadAllLines(Path.Combine(rig.Server, "starts.txt")));
        Assert.Equal(new StartupError(Headline, Sentence), rig.Host.StartupError);
        Assert.Contains("The server said why it cannot start, so it is not tried again", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_started_for_a_port_change_is_told_nothing_about_the_failures_before_it()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "7");
        StartRig.Steer("WEIR_STANDIN_SUCCEED_AT_ATTEMPT", "3");
        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, After(async () =>
        {
            await rig.LatestStartedAsync(1);
            await StartRig.UntilAsync(() => !StartRig.IsRunning(rig.Starts[^1].Pid) || rig.Starts[^1].Attempt == "3", "the stand-in to exit or the third attempt to start");
            if (rig.Starts[^1].Attempt == "3")
            {
                await rig.LatestListeningAsync();
            }
        }));
        Assert.Equal("3", rig.Starts[^1].Attempt);
        var startsBefore = rig.Starts.Count;
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", null);

        var move = rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None);
        await rig.DriveAsync(move, After(rig.LatestListeningAsync));

        Assert.Equal(PortMove.Moved, await move);
        var moved = rig.Starts[startsBefore];
        Assert.Equal(string.Empty, moved.Attempt);
        Assert.Equal(string.Empty, moved.Failures);
        // The move is a start of its own: no "started on attempt" line and no failures held over for the next crash to report.
        Assert.Equal(1, CountOf(rig.TrayLog, "Weir started on attempt"));
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_crash_after_a_port_change_is_counted_from_the_change_and_not_from_the_failures_before_it()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "7");
        StartRig.Steer("WEIR_STANDIN_SUCCEED_AT_ATTEMPT", "3");
        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, After(async () =>
        {
            await rig.LatestStartedAsync(1);
            await StartRig.UntilAsync(() => !StartRig.IsRunning(rig.Starts[^1].Pid) || rig.Starts[^1].Attempt == "3", "the stand-in to exit or the third attempt to start");
            if (rig.Starts[^1].Attempt == "3")
            {
                await rig.LatestListeningAsync();
            }
        }));
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", null);
        var move = rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None);
        await rig.DriveAsync(move, After(rig.LatestListeningAsync));
        Assert.Equal(PortMove.Moved, await move);
        var gaveUp = false;
        rig.Host.Watch(() => gaveUp = true, CancellationToken.None);
        var startsBefore = rig.Starts.Count;
        using (var running = Process.GetProcessById(rig.Starts[^1].Pid))
        {
            running.Kill();
            await running.WaitForExitAsync();
        }

        var restarted = Task.Run(() => StartRig.UntilAsync(() => rig.Starts.Count == startsBefore + 1 && rig.Host.Phase == ServerPhase.Running, "the server to be started again"));
        await rig.DriveAsync(restarted, After(async () =>
        {
            await rig.LatestStartedAsync(startsBefore + 1);
            await rig.LatestListeningAsync();
        }));

        Assert.False(gaveUp);
        var again = rig.Starts[startsBefore];
        Assert.Equal("2", again.Attempt);
        Assert.StartsWith("attempt 1: exited with code", again.Failures, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt 2", again.Failures, StringComparison.Ordinal);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hung_server_that_is_still_running_after_the_stop_is_not_started_beside_and_the_tray_says_so(bool killFailed)
    {
        var reported = killFailed ? ServerStopOutcome.KillFailed : ServerStopOutcome.Killed;
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        // The stop reports what the theory says and leaves the process running: a kill that did not take, or one that was not waited for.
        rig.Host.StopHung = _ => Task.FromResult(reported);

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestReportedAsync));

        Assert.Equal(1, gaveUp.Attempts);
        Assert.Contains("could not be stopped", gaveUp.Reason, StringComparison.Ordinal);
        Assert.Single(rig.Starts);
        Assert.Single(rig.Running());
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Contains($"End process {rig.Starts[0].Pid}", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains("Not trying again: another server would find this one still holding the port and the database", rig.TrayLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Trying again in", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_process_a_quit_has_already_stopped_and_disposed_is_gone_and_is_not_disposed_a_second_time()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_PHASE", "opening_database");
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);
        // The quit gets to the process first: it is killed and disposed while the attempt is still stopping it.
        rig.Host.StopHung = async process =>
        {
            var outcome = await ServerProcessStop.StopAsync(process, ServerStopTimeouts.Orphan);
            process.Dispose();
            return outcome;
        };

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestReportedAsync));

        Assert.Equal(StartPolicyBurst, gaveUp.Attempts);
        Assert.StartsWith("hung in phase 'opening the database'", gaveUp.Reason, StringComparison.Ordinal);
        await rig.AllExitedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task What_the_server_said_about_why_it_cannot_start_is_read_once_and_what_is_logged_is_what_is_done()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");
        var reads = 0;
        var said = new StartupError("Weir's settings are not valid", "WEIR_PORT must be a number.");
        // Nothing the first time (so the start is tried again), the note the second: a host that read more than once for a failure would
        // log "Trying again" and then give up on the same failure.
        rig.Host.ServerSaidOverride = () => ++reads == 1 ? null : said;

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await RunToEndAsync(rig, start, After(rig.LatestExitedAsync));

        Assert.Equal(2, gaveUp.Attempts);
        Assert.Equal(2, reads);
        Assert.Equal(2, rig.Starts.Count);
        Assert.Equal(1, CountOf(rig.TrayLog, "Trying again in"));
        Assert.Equal(1, CountOf(rig.TrayLog, "so it is not tried again: WEIR_PORT must be a number."));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_icon_says_stopping_while_the_server_finishes_and_stopped_after()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();
        var phases = new List<ServerPhase>();
        rig.Host.PhaseChanged += () => phases.Add(rig.Host.Phase);

        await rig.Host.StopAsync();

        Assert.Equal([ServerPhase.Stopping, ServerPhase.Stopped], phases);
        Assert.False(rig.Host.ServerIsRunning);
    }

    private static async Task<StartGaveUpException> RunToEndAsync(StartRig rig, Task start, Func<TimeSpan, Task> whenParked)
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

    // Something else that holds the server port and answers GET /ready with 200, as another running Weir would.
    private sealed class ReadyAnswerer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private int _answers;

        internal ReadyAnswerer(int port)
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(AnswerAsync);
        }

        internal int Answers => Volatile.Read(ref _answers);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AnswerAsync()
        {
            const string Body = "{\"ready\":true}";
            var response = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}");
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    _ = await stream.ReadAsync(new byte[4096], _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                    Interlocked.Increment(ref _answers);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException or IOException)
            {
                // Stopped, or the asker went away.
            }
        }
    }

    private static int StartPolicyBurst => Policy.StartLimitBurst;

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
