using System.Text.Json.Nodes;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Before the tray applies an update it has the still-running server save a copy of Weir's data, through
/// update-backup-request.json and update-backup-result.json (#951). An update whose copy could not be saved is not applied, and
/// the version that is running keeps running. Time is fake and the server's answers are files written at chosen moments.
/// </summary>
public sealed class UpdateBackupTests : IDisposable
{
    private const string Target = "1.0.0-rc.13";
    private const string DiskFull = "Drive C: needs about 480 MB free to hold the copy and has 120 MB. Free up space there, then try again.";

    private static readonly DateTime ServerStarted = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly DelayWatchingTimeProvider _clock = new();
    private readonly StandInServers _servers = new();
    private RunningServer? _server = new(4242, ServerStarted);

    public void Dispose()
    {
        _servers.Dispose();
        _home.Dispose();
    }

    private string RequestPath => Path.Combine(_home.Path, UpdateBackupRequest.RequestFileName);

    private string ResultPath => Path.Combine(_home.Path, UpdateBackupRequest.ResultFileName);

    private string ReadyPath => Path.Combine(_home.Path, UpdateBackupReady.FileName);

    private UpdateBackupRequest Request() => new(_home.Path, _clock, () => _server);

    private string AskedId() => (string)JsonNode.Parse(File.ReadAllText(RequestPath))!["id"]!;

    private void ServerWrites(string id, string state, string? reason = null) =>
        File.WriteAllText(
            ResultPath,
            new JsonObject { ["id"] = id, ["state"] = state, ["reason"] = reason }.ToJsonString());

    // What a server with the backup watcher writes when it starts listening.
    private void ServerIsReady(int pid, DateTime startedUtc) =>
        File.WriteAllText(
            ReadyPath,
            new JsonObject { ["pid"] = pid, ["started_at"] = startedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString());

    // The server is ready to hear the request, and the tray has asked and is parked on its first look at the answer.
    private async Task<(Task<UpdateBackupAnswer> Answer, string Id)> AskAsync()
    {
        ServerIsReady(_server!.Value.ProcessId, _server.Value.StartedUtc);
        var answer = Request().AskAsync(Target, CancellationToken.None);
        await _clock.NextDelay();
        return (answer, AskedId());
    }

    private async Task StepAsync(Task<UpdateBackupAnswer> answer)
    {
        _clock.Advance(UpdateBackupRequest.PollInterval);
        if (!answer.IsCompleted)
        {
            await Task.WhenAny(answer, _clock.NextDelay());
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task The_request_names_the_version_the_update_is_to_and_when_it_was_made()
    {
        var (answer, id) = await AskAsync();

        var request = JsonNode.Parse(File.ReadAllText(RequestPath))!;
        Assert.Equal(Target, (string?)request["target_version"]);
        Assert.Equal(_clock.GetUtcNow(), DateTimeOffset.Parse((string)request["requested_at"]!, System.Globalization.CultureInfo.InvariantCulture));
        ServerWrites(id, "saved");
        await StepAsync(answer);
        Assert.Equal(UpdateBackupOutcome.Saved, (await answer).Outcome);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_says_it_is_saving_and_then_that_it_saved_is_waited_for()
    {
        var (answer, id) = await AskAsync();

        ServerWrites(id, "started");
        await StepAsync(answer);
        Assert.False(answer.IsCompleted);
        ServerWrites(id, "saved");
        await StepAsync(answer);

        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null), await answer);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_could_not_save_the_copy_says_why_in_its_own_words()
    {
        var (answer, id) = await AskAsync();

        ServerWrites(id, "started");
        await StepAsync(answer);
        ServerWrites(id, "failed", DiskFull);
        await StepAsync(answer);

        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull), await answer);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_busy_server_that_answers_after_the_first_wait_has_still_saved_the_copy()
    {
        var (answer, id) = await AskAsync();

        _clock.Advance(UpdateBackupRequest.HearingTime);
        await _clock.NextDelay();
        Assert.False(answer.IsCompleted);
        ServerWrites(id, "started");
        await StepAsync(answer);
        ServerWrites(id, "saved");
        await StepAsync(answer);

        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null), await answer);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_missed_the_request_is_asked_again_on_a_doubling_schedule_with_the_same_id_and_a_fresh_time()
    {
        var (answer, id) = await AskAsync();
        var asks = new List<(string Id, DateTimeOffset At)> { ReadAsk() };

        for (var ask = 0; ask < UpdateBackupRequest.Asks - 1; ask++)
        {
            // The server missed it: the file is as the tray left it, so a new one is the only way it is heard.
            File.Delete(RequestPath);
            _clock.Advance(UpdateBackupRequest.HearingWindow(ask));
            await _clock.NextDelay();
            Assert.False(answer.IsCompleted);
            asks.Add(ReadAsk());
        }

        Assert.All(asks, made => Assert.Equal(id, made.Id));
        Assert.Equal(asks.OrderBy(made => made.At), asks);
        Assert.Equal(asks.Count, asks.Select(made => made.At).Distinct().Count());

        ServerWrites(id, "saved");
        await StepAsync(answer);
        Assert.Equal(UpdateBackupOutcome.Saved, (await answer).Outcome);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_stays_unheard_gets_the_whole_ceiling_and_the_person_is_told_once_that_Weir_is_busy()
    {
        var told = new List<string>();
        ServerIsReady(_server!.Value.ProcessId, _server.Value.StartedUtc);
        var answer = new UpdateBackupRequest(_home.Path, _clock, () => _server, told.Add).AskAsync(Target, CancellationToken.None);
        await _clock.NextDelay();

        var waited = TimeSpan.Zero;
        for (var ask = 0; ask < UpdateBackupRequest.Asks - 1; ask++)
        {
            var window = UpdateBackupRequest.HearingWindow(ask);
            _clock.Advance(window);
            waited += window;
            await _clock.NextDelay();
            Assert.False(answer.IsCompleted);
        }

        Assert.Single(told);
        Assert.Contains("Weir is busy", told[0], StringComparison.Ordinal);
        _clock.Advance(UpdateBackupRequest.HearingWindow(UpdateBackupRequest.Asks - 1));

        Assert.Equal(UpdateBackupOutcome.Failed, (await answer).Outcome);
        Assert.Equal(UpdateBackupRequest.HearingCeiling, waited + UpdateBackupRequest.HearingWindow(UpdateBackupRequest.Asks - 1));
        Assert.False(File.Exists(RequestPath));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_busy_server_that_then_says_it_cannot_make_the_copy_ends_the_wait_with_its_reason()
    {
        var (answer, id) = await AskAsync();

        _clock.Advance(UpdateBackupRequest.HearingTime);
        await _clock.NextDelay();
        ServerWrites(id, "failed", DiskFull);
        await StepAsync(answer);

        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull), await answer);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_has_heard_is_not_asked_again_however_long_it_takes_to_save()
    {
        var (answer, id) = await AskAsync();

        ServerWrites(id, "started");
        await StepAsync(answer);
        File.Delete(RequestPath);
        _clock.Advance(UpdateBackupRequest.HearingCeiling);
        await _clock.NextDelay();

        Assert.False(answer.IsCompleted);
        Assert.False(File.Exists(RequestPath));
        ServerWrites(id, "saved");
        await StepAsync(answer);
        Assert.Equal(UpdateBackupOutcome.Saved, (await answer).Outcome);
    }

    // The clock is moved one wait at a time: the tray looks at it once a poll, and asks again at the end of each wait.
    private async Task RunOutTheHearingAsync()
    {
        for (var ask = 0; ask < UpdateBackupRequest.Asks - 1; ask++)
        {
            _clock.Advance(UpdateBackupRequest.HearingWindow(ask));
            await _clock.NextDelay();
        }

        _clock.Advance(UpdateBackupRequest.HearingWindow(UpdateBackupRequest.Asks - 1));
    }

    private (string Id, DateTimeOffset At) ReadAsk()
    {
        var request = JsonNode.Parse(File.ReadAllText(RequestPath))!;
        return ((string)request["id"]!, DateTimeOffset.Parse((string)request["requested_at"]!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_said_it_can_answer_and_does_not_has_failed_and_the_request_is_taken_back()
    {
        var (answer, _) = await AskAsync();

        await RunOutTheHearingAsync();

        var result = await answer;
        Assert.Equal(UpdateBackupOutcome.Failed, result.Outcome);
        Assert.Contains("too busy to answer the request", result.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(RequestPath));
    }

    [Fact]
    public async Task A_server_with_no_marker_is_an_older_build_and_is_not_waited_for()
    {
        var answer = Request().AskAsync(Target, CancellationToken.None);

        // Completed with no look at the clock: the menu does not wait for a server that cannot answer.
        Assert.True(answer.IsCompletedSuccessfully);
        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.NotAnswered, null), await answer);
        Assert.False(File.Exists(RequestPath));
    }

    [Fact]
    public async Task A_marker_left_by_another_server_is_not_this_ones()
    {
        ServerIsReady(_server!.Value.ProcessId + 1, ServerStarted);
        Assert.Equal(UpdateBackupOutcome.NotAnswered, (await Request().AskAsync(Target, CancellationToken.None)).Outcome);

        // The same id, reused by a process that started later.
        ServerIsReady(_server.Value.ProcessId, ServerStarted - TimeSpan.FromHours(1));
        Assert.Equal(UpdateBackupOutcome.NotAnswered, (await Request().AskAsync(Target, CancellationToken.None)).Outcome);
        Assert.False(File.Exists(RequestPath));
    }

    [Fact]
    public async Task With_no_server_running_there_is_no_one_to_ask()
    {
        ServerIsReady(4242, ServerStarted);
        _server = null;

        Assert.Equal(UpdateBackupOutcome.NotAnswered, (await Request().AskAsync(Target, CancellationToken.None)).Outcome);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_answer_to_an_earlier_request_is_not_taken_for_this_ones()
    {
        var (answer, _) = await AskAsync();

        ServerWrites("an-earlier-request", "saved");
        await RunOutTheHearingAsync();

        Assert.Equal(UpdateBackupOutcome.Failed, (await answer).Outcome);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_server_that_stops_while_it_is_saving_is_a_failed_copy()
    {
        var (answer, id) = await AskAsync();

        ServerWrites(id, "started");
        _server = null;
        await StepAsync(answer);

        var result = await answer;
        Assert.Equal(UpdateBackupOutcome.Failed, result.Outcome);
        Assert.Contains("Weir stopped while it was saving the copy", result.Reason, StringComparison.Ordinal);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_copy_that_never_finishes_is_a_failed_copy()
    {
        var (answer, id) = await AskAsync();

        ServerWrites(id, "started");
        await StepAsync(answer);
        _clock.Advance(UpdateBackupRequest.LongestSave);

        var result = await answer;
        Assert.Equal(UpdateBackupOutcome.Failed, result.Outcome);
        Assert.Contains("taking too long", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_server_running_there_is_nothing_to_ask_and_the_update_may_go_ahead()
    {
        var asked = false;
        var hook = Hook(
            (_, _) =>
            {
                asked = true;
                return Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null));
            },
            running: false);

        Assert.True(await hook.SaveBeforeApplyAsync(Target));
        Assert.False(asked);
    }

    [Fact]
    public async Task A_saved_copy_lets_the_update_go_ahead_and_forgets_an_earlier_failure()
    {
        File.WriteAllText(Path.Combine(_home.Path, "update-backup-failed"), Target);
        var hook = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null)));

        Assert.True(await hook.SaveBeforeApplyAsync(Target));
        Assert.False(UpdateBackupHook.FailedFor(_home.Path, Target));
    }

    [Fact]
    public async Task A_server_too_old_to_answer_leaves_the_copy_to_the_server_that_starts_after_the_update()
    {
        var hook = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.NotAnswered, null)));

        Assert.True(await hook.SaveBeforeApplyAsync(Target));
    }

    [Fact]
    public async Task A_copy_that_could_not_be_saved_stops_the_update_and_says_why()
    {
        var told = new List<string>();
        var hook = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull)), told.Add);

        Assert.False(await hook.SaveBeforeApplyAsync(Target));
        Assert.Equal([DiskFull], told);
        Assert.True(UpdateBackupHook.FailedFor(_home.Path, Target));
        Assert.False(UpdateBackupHook.FailedFor(_home.Path, "1.0.0-rc.14"));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NullReferenceException))]
    public async Task Whatever_goes_wrong_in_asking_is_a_failed_copy_not_a_crash(Type thrown)
    {
        var told = new List<string>();
        var hook = Hook((_, _) => throw (Exception)Activator.CreateInstance(thrown, "raw text")!, told.Add);

        Assert.False(await hook.SaveBeforeApplyAsync(Target));
        Assert.Single(told);
        Assert.StartsWith("Weir couldn't ask itself to save the copy", told[0], StringComparison.Ordinal);
        Assert.DoesNotContain("raw text", told[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_that_cannot_be_shown_still_leaves_the_update_not_applied()
    {
        var hook = Hook(
            (_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull)),
            _ => throw new InvalidOperationException("the icon is gone"));

        Assert.False(await hook.SaveBeforeApplyAsync(Target));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_restart_to_update_whose_copy_failed_applies_nothing_and_the_old_version_keeps_running()
    {
        var server = await _servers.StartAsync(ServerFolder());
        var update = new FakeUpdateService().AlreadyDownloaded();
        var told = new List<string>();
        var backup = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull)), told.Add);

        await new TrayShutdown(() => ServerProcessStop.StopAsync(server), update, backup).RestartToUpdateAsync();

        Assert.Empty(update.Applied);
        Assert.False(server.HasExited);
        Assert.Equal([DiskFull], told);
    }

    [Fact(Timeout = 60_000)]
    public async Task Quitting_whose_copy_failed_stops_Weir_but_leaves_the_update_waiting()
    {
        var server = await _servers.StartAsync(ServerFolder());
        var update = new FakeUpdateService().AlreadyDownloaded();
        var backup = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull)));

        await new TrayShutdown(() => ServerProcessStop.StopAsync(server), update, backup).QuitAsync();

        Assert.Empty(update.Applied);
        Assert.True(server.HasExited);
    }

    [Fact(Timeout = 60_000)]
    public async Task Quitting_still_stops_the_server_when_asking_for_the_copy_throws()
    {
        var server = await _servers.StartAsync(ServerFolder());
        var update = new FakeUpdateService().AlreadyDownloaded();

        await new TrayShutdown(() => ServerProcessStop.StopAsync(server), update, new ThrowingBackup()).QuitAsync();

        Assert.Empty(update.Applied);
        Assert.True(server.HasExited);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_copy_is_asked_for_while_the_server_still_runs_and_before_the_update_is_applied()
    {
        var server = await _servers.StartAsync(ServerFolder());
        var update = new FakeUpdateService().AlreadyDownloaded();
        var order = new List<string>();
        var serverRanWhenAsked = false;
        string? versionAsked = null;
        update.OnApply = call => order.Add(call);
        var backup = Hook(
            (version, _) =>
            {
                serverRanWhenAsked = !server.HasExited;
                versionAsked = version;
                order.Add("copy saved");
                return Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null));
            });

        await new TrayShutdown(() => ServerProcessStop.StopAsync(server), update, backup).RestartToUpdateAsync();

        Assert.True(serverRanWhenAsked);
        Assert.Equal(update.PendingVersion, versionAsked);
        Assert.Equal(["copy saved", FakeUpdateService.AppliedAndRestarted], order);
        Assert.True(server.HasExited);
    }

    [Fact(Timeout = 60_000)]
    public async Task Nothing_is_asked_for_when_no_update_is_downloaded()
    {
        var server = await _servers.StartAsync(ServerFolder());
        var asked = false;
        var backup = Hook((_, _) =>
        {
            asked = true;
            return Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null));
        });

        await new TrayShutdown(() => ServerProcessStop.StopAsync(server), new FakeUpdateService(), backup).QuitAsync();

        Assert.False(asked);
    }

    [Fact(Timeout = 10_000)]
    public async Task Asks_that_overlap_make_one_attempt_and_the_others_return_at_once()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var stopped = 0;
        var update = new FakeUpdateService().AlreadyDownloaded();
        var shutdown = new TrayShutdown(
            () =>
            {
                Interlocked.Increment(ref stopped);
                return Task.CompletedTask;
            },
            update,
            new GatedBackup(() =>
            {
                Interlocked.Increment(ref asked);
                return gate.Task;
            }));

        var first = shutdown.RestartToUpdateAsync();
        // Idle install, a balloon click, the apply-now flag and Quit, all while the first waits for its copy.
        var others = new[] { await shutdown.RestartToUpdateAsync(), await shutdown.RestartToUpdateAsync(), await shutdown.QuitAsync() };

        Assert.Equal([false, false, false], others);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, asked);

        gate.SetResult(true);
        Assert.True(await first);

        Assert.Equal(1, asked);
        Assert.Equal(1, stopped);
        Assert.Equal([FakeUpdateService.AppliedAndRestarted], update.Applied);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_new_attempt_may_begin_once_the_one_before_it_has_ended_without_applying()
    {
        var asked = 0;
        var update = new FakeUpdateService().AlreadyDownloaded();
        var shutdown = new TrayShutdown(
            () => Task.CompletedTask,
            update,
            new GatedBackup(() =>
            {
                asked++;
                return Task.FromResult(false);
            }));

        Assert.True(await shutdown.RestartToUpdateAsync());
        Assert.True(await shutdown.RestartToUpdateAsync());

        Assert.Equal(2, asked);
        Assert.Empty(update.Applied);
    }

    [Fact(Timeout = 30_000)]
    public async Task Every_apply_now_flag_is_an_attempt_even_after_one_that_did_not_apply()
    {
        var service = new FakeUpdateService().AlreadyDownloaded();
        var attempts = new SemaphoreSlim(0);
        var updates = new TrayUpdates(
            service,
            _home.Path,
            new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = 0 },
            new UpdateCallbacks(OnUi: action => action(), Changed: () => { }, Announce: _ => { }, ApplyNow: () => attempts.Release()),
            _clock,
            CancellationToken.None);
        var flag = Path.Combine(_home.Path, "update-apply-now");
        updates.Start();
        await _clock.NextDelay();

        await FlagAndWaitAsync(flag, 1, attempts);
        await FlagAndWaitAsync(flag, 2, attempts);
    }

    // The file system tells the watcher about the flag on its own thread, and the settle that follows runs on the test's clock, so
    // the clock is moved on each time the watcher starts one, until the flag has been acted on.
    private async Task FlagAndWaitAsync(string flag, int nth, SemaphoreSlim attempts)
    {
        File.WriteAllText(flag, "apply");
        File.SetLastWriteTimeUtc(flag, new DateTime(2026, 10, 10, 8, 0, nth, DateTimeKind.Utc));

        var acted = attempts.WaitAsync(TimeSpan.FromSeconds(10));
        while (await Task.WhenAny(acted, _clock.DelayWaitingAsync()) != acted)
        {
            if (_clock.TryTakeDelay(out var delay) && delay == UpdateRequestWatcher.Settle)
            {
                _clock.Advance(UpdateRequestWatcher.Settle);
            }
        }

        Assert.True(await acted, $"The apply-now flag number {nth} was not acted on.");
        await Eventually(() => !File.Exists(flag));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Yield();
            ceiling.Token.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task The_message_of_a_quit_that_could_not_apply_the_update_is_still_there_at_the_next_start()
    {
        var failing = Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull)));
        await failing.SaveBeforeApplyAsync(Target);

        // A tray starting again, and again, running the version the update was meant to replace.
        Assert.Equal(DiskFull, UpdateBackupHook.PendingReason(_home.Path, "1.0.0-rc.12"));
        Assert.Equal(DiskFull, UpdateBackupHook.PendingReason(_home.Path, "1.0.0-rc.12"));

        var held = JsonNode.Parse(File.ReadAllText(Path.Combine(_home.Path, "update-not-applied.json")))!;
        Assert.Equal(DiskFull, (string?)held["reason"]);
        Assert.Equal(Target, (string?)held["version"]);
    }

    [Fact]
    public async Task The_message_goes_when_a_copy_is_saved()
    {
        await Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull))).SaveBeforeApplyAsync(Target);

        await Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null))).SaveBeforeApplyAsync(Target);

        Assert.Null(UpdateBackupHook.PendingReason(_home.Path, "1.0.0-rc.12"));
        Assert.False(File.Exists(Path.Combine(_home.Path, "update-not-applied.json")));
    }

    [Fact]
    public async Task The_message_goes_when_the_update_it_is_about_has_been_installed_some_other_way()
    {
        await Hook((_, _) => Task.FromResult(new UpdateBackupAnswer(UpdateBackupOutcome.Failed, DiskFull))).SaveBeforeApplyAsync(Target);

        Assert.Null(UpdateBackupHook.PendingReason(_home.Path, Target));
        Assert.False(File.Exists(Path.Combine(_home.Path, "update-not-applied.json")));
        Assert.False(UpdateBackupHook.FailedFor(_home.Path, Target));
    }

    [Fact]
    public void An_update_whose_copy_failed_is_not_applied_at_start_up_where_no_server_runs_to_ask()
    {
        File.WriteAllText(Path.Combine(_home.Path, "update-backup-failed"), "3.3.0");
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };

        var handedOver = UpdateOnStart.TryApply(service, UpdateMode.Auto, _home.Path, () => { });

        Assert.False(handedOver);
        Assert.Empty(service.Applied);
    }

    [Fact]
    public void A_different_update_left_waiting_is_applied_at_start_up_as_before()
    {
        File.WriteAllText(Path.Combine(_home.Path, "update-backup-failed"), "3.2.0");
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };

        Assert.True(UpdateOnStart.TryApply(service, UpdateMode.Auto, _home.Path, () => { }));
        Assert.Equal([FakeUpdateService.AppliedLeftWaiting], service.Applied);
    }

    [Fact]
    public void A_server_left_running_is_asked_for_the_copy_before_it_is_stopped_and_the_update_applied()
    {
        var order = new List<string>();
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };
        service.OnApply = call => order.Add(call);
        var backup = new GatedBackup(() =>
        {
            order.Add("copy asked");
            return Task.FromResult(true);
        });

        var handedOver = UpdateOnStart.TryApply(service, UpdateMode.Auto, _home.Path, () => order.Add("orphan stopped"), backup, () => true);

        Assert.True(handedOver);
        Assert.Equal(["copy asked", "orphan stopped", FakeUpdateService.AppliedLeftWaiting], order);
    }

    [Fact]
    public void A_server_left_running_that_cannot_save_the_copy_keeps_the_update_waiting_and_is_not_stopped()
    {
        var stopped = false;
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };

        var handedOver = UpdateOnStart.TryApply(
            service, UpdateMode.Auto, _home.Path, () => stopped = true, new GatedBackup(() => Task.FromResult(false)), () => true);

        Assert.False(handedOver);
        Assert.Empty(service.Applied);
        Assert.False(stopped);
        // Not used up: the next start asks again.
        Assert.False(File.Exists(Path.Combine(_home.Path, UpdateOnStart.AttemptFileName)));
    }

    [Fact]
    public void A_server_left_running_is_asked_again_even_when_an_earlier_copy_failed()
    {
        File.WriteAllText(Path.Combine(_home.Path, "update-backup-failed"), "3.3.0");
        var asked = false;
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };

        var handedOver = UpdateOnStart.TryApply(
            service,
            UpdateMode.Auto,
            _home.Path,
            () => { },
            new GatedBackup(() =>
            {
                asked = true;
                return Task.FromResult(true);
            }),
            () => true);

        Assert.True(asked);
        Assert.True(handedOver);
    }

    [Fact]
    public void With_no_server_left_running_the_start_up_install_asks_nothing()
    {
        var asked = false;
        var service = new FakeUpdateService { LeftWaitingVersion = "3.3.0" };

        var handedOver = UpdateOnStart.TryApply(
            service,
            UpdateMode.Auto,
            _home.Path,
            () => { },
            new GatedBackup(() =>
            {
                asked = true;
                return Task.FromResult(false);
            }),
            () => false);

        Assert.True(handedOver);
        Assert.False(asked);
    }

    private UpdateBackupHook Hook(
        Func<string, CancellationToken, Task<UpdateBackupAnswer>> ask, Action<string>? onNotUpdated = null, bool running = true) =>
        new(ask, _home.Path, () => running, onNotUpdated ?? (_ => { }));

    private string ServerFolder() => Path.Combine(_home.Path, "server-" + Guid.NewGuid().ToString("n"));

    private sealed class GatedBackup(Func<Task<bool>> save) : IUpdateBackup
    {
        public Task<bool> SaveBeforeApplyAsync(string? targetVersion) => save();
    }

    private sealed class ThrowingBackup : IUpdateBackup
    {
        public Task<bool> SaveBeforeApplyAsync(string? targetVersion) => throw new InvalidOperationException("raw text");
    }

    [Fact(Timeout = 10_000)]
    public async Task A_ready_marker_that_cannot_be_read_just_now_is_not_a_server_without_one()
    {
        ServerIsReady(_server!.Value.ProcessId, _server.Value.StartedUtc);
        using var held = new FileStream(ReadyPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var answer = await Request().AskAsync(Target, CancellationToken.None);

        Assert.Equal(UpdateBackupOutcome.Failed, answer.Outcome);
        Assert.False(File.Exists(RequestPath));
    }

    [Fact]
    public void A_failed_copy_marker_that_cannot_be_read_just_now_leaves_the_update_waiting()
    {
        File.WriteAllText(Path.Combine(_home.Path, "update-backup-failed"), "2.0.0");
        using var held = new FileStream(Path.Combine(_home.Path, "update-backup-failed"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(UpdateBackupHook.FailedFor(_home.Path, "3.0.0"));
    }

    [Fact]
    public void A_failed_copy_marker_that_is_not_there_does_not()
    {
        Assert.False(UpdateBackupHook.FailedFor(_home.Path, "3.0.0"));
    }

    [Fact]
    public void A_held_back_reason_that_cannot_be_read_just_now_is_neither_shown_as_missing_nor_removed()
    {
        var notApplied = Path.Combine(_home.Path, "update-not-applied.json");
        File.WriteAllText(notApplied, "{\"version\":\"2.0.0\",\"reason\":\"No room.\"}");
        using (new FileStream(notApplied, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(UpdateBackupHook.PendingReason(_home.Path, "1.0.0"));
        }

        Assert.Equal("No room.", UpdateBackupHook.PendingReason(_home.Path, "1.0.0"));
    }
}
