using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Core.Jobs;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>The pause is in Activity whichever way it changes: someone pausing or resuming, or a timed pause running out.</summary>
public sealed class SuitePauseServiceTests : IDisposable
{
    private const string Entries =
        "SELECT group_concat(title || ': ' || detail, ' | ') FROM " +
        "(SELECT title, detail FROM activity_events WHERE event_type LIKE 'system.processing_%' ORDER BY id)";

    private readonly StoreFixture _store = new();
    private readonly DataChangePublisher _changes = new();
    private readonly WorkerWakeSignals _workers = new();
    private readonly SuitePauseService _pause;

    public SuitePauseServiceTests() =>
        _pause = new SuitePauseService(new SuiteSettingsStore(new AuthStore()), new ActivityStore(), _changes, _workers);

    public void Dispose() => _store.Dispose();

    private Timestamp Now => Timestamp.UtcNow(_store.Clock);

    private Task<PauseOutcome> ChangeAsync(bool paused, long? minutes = null, bool keepLooking = true, string by = "alice") =>
        InUnitOfWorkAsync(uow => _pause.ChangeAsync(uow, paused, minutes, keepEnd: false, keepLooking, Now, by));

    private Task<PauseOutcome> CurrentAsync() => InUnitOfWorkAsync(uow => _pause.CurrentAsync(uow, Now));

    private async Task<PauseOutcome> InUnitOfWorkAsync(Func<UnitOfWork, Task<PauseState>> work)
    {
        var uow = await UnitOfWork.OpenAsync(_store.Database);
        await using (uow)
        {
            var state = await work(uow);
            await uow.CommitAsync();
            return new PauseOutcome(state, await EntriesAsync());
        }
    }

    private async Task<string[]> EntriesAsync()
    {
        using var connection = _store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Entries;
        return (await command.ExecuteScalarAsync() as string)?.Split(" | ") ?? [];
    }

    [Fact]
    public async Task Resuming_processing_wakes_the_idle_slots_of_both_lanes_once_it_is_committed()
    {
        await ChangeAsync(paused: true);
        var files = _workers.NextWake(WorkLane.Files, 0);
        var upkeep = _workers.NextWake(WorkLane.Upkeep, 0);
        var uow = await UnitOfWork.OpenAsync(_store.Database);
        await using (uow)
        {
            await _pause.ChangeAsync(uow, paused: false, minutes: null, keepEnd: false, scanWhilePaused: true, Now, "alice");
            var wokenBeforeCommit = files.IsCompleted || upkeep.IsCompleted;

            await uow.CommitAsync();

            Assert.False(wokenBeforeCommit);
        }

        Assert.True(files.IsCompletedSuccessfully);
        Assert.True(upkeep.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Pausing_or_changing_a_pause_that_is_still_on_wakes_nobody()
    {
        var files = _workers.NextWake(WorkLane.Files, 0);
        var upkeep = _workers.NextWake(WorkLane.Upkeep, 0);

        await ChangeAsync(paused: true, minutes: 30);
        await ChangeAsync(paused: true, minutes: 60, keepLooking: false);

        Assert.False(files.IsCompleted);
        Assert.False(upkeep.IsCompleted);
    }

    [Fact]
    public async Task Resuming_when_nothing_was_paused_wakes_nobody()
    {
        var files = _workers.NextWake(WorkLane.Files, 0);

        await ChangeAsync(paused: false);

        Assert.False(files.IsCompleted);
    }

    [Fact]
    public async Task A_timed_pause_that_runs_out_is_left_to_the_poll()
    {
        await ChangeAsync(paused: true, minutes: 30);
        var files = _workers.NextWake(WorkLane.Files, 0);
        _store.Clock.Advance(TimeSpan.FromMinutes(31));

        await CurrentAsync();

        Assert.False(files.IsCompleted);
    }

    [Fact]
    public async Task A_timed_pause_that_runs_out_is_lifted_and_recorded_once()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromMinutes(31));

        var lifted = await CurrentAsync();
        var again = await CurrentAsync();

        Assert.False(lifted.State.Paused);
        Assert.Equal(2, lifted.Entries.Length);
        Assert.Equal("Processing resumed: The pause ran out at 2026-01-15 10:30 UTC, so processing was resumed.", lifted.Entries[1]);
        Assert.Equal(lifted.Entries, again.Entries);
        Assert.Equal(0, await _store.Scalar("SELECT processing_paused FROM suite_settings WHERE id = 1"));
    }

    [Fact]
    public async Task The_expiry_task_records_a_pause_that_ran_out_with_nobody_looking()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromHours(1));

        await new SuitePauseExpiryTask(_store.Database, _pause, _store.Clock).RunOnceAsync(CancellationToken.None);

        var entries = await EntriesAsync();
        Assert.Equal(2, entries.Length);
        Assert.StartsWith("Processing resumed: The pause ran out", entries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pausing_again_after_a_pause_ran_out_records_both_in_order()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromMinutes(45));

        var repaused = await ChangeAsync(paused: true, minutes: 60, by: "bob");

        Assert.True(repaused.State.Paused);
        Assert.Equal(3, repaused.Entries.Length);
        Assert.StartsWith("Processing paused: alice paused processing until 2026-01-15 10:30 UTC.", repaused.Entries[0], StringComparison.Ordinal);
        Assert.StartsWith("Processing resumed: The pause ran out", repaused.Entries[1], StringComparison.Ordinal);
        Assert.StartsWith("Processing paused: bob paused processing until 2026-01-15 11:45 UTC.", repaused.Entries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_how_long_a_pause_lasts_is_recorded_and_saving_it_unchanged_is_not()
    {
        var first = await ChangeAsync(paused: true, minutes: 30);
        var same = await ChangeAsync(paused: true, minutes: 30);
        var longer = await ChangeAsync(paused: true, minutes: 120);

        Assert.Single(first.Entries);
        Assert.Single(same.Entries);
        Assert.Equal(2, longer.Entries.Length);
        Assert.StartsWith("Processing paused: alice paused processing until 2026-01-15 12:00 UTC.", longer.Entries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resuming_a_running_weir_records_nothing()
    {
        var resumed = await ChangeAsync(paused: false);

        Assert.Empty(resumed.Entries);
    }

    [Fact]
    public async Task A_change_that_says_nothing_about_the_length_keeps_the_end_of_a_running_pause()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromMinutes(10));

        var kept = await InUnitOfWorkAsync(uow => _pause.ChangeAsync(uow, true, null, keepEnd: true, false, Now, "alice"));

        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), kept.State.PausedUntil!.Value.AsUtc);
        Assert.False(kept.State.ScanWhilePaused);
        Assert.EndsWith("Weir does not look for new files either.", kept.Entries[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pause_that_is_not_running_yet_lasts_until_resumed_when_no_length_is_given()
    {
        var started = await InUnitOfWorkAsync(uow => _pause.ChangeAsync(uow, true, null, keepEnd: true, true, Now, "alice"));

        Assert.Null(started.State.PausedUntil);
    }

    [Fact]
    public async Task Pausing_and_resuming_each_announce_the_pause_once_it_commits()
    {
        using var heard = _changes.Subscribe();

        await ChangeAsync(paused: true);
        await ChangeAsync(paused: false);

        Assert.Equal([DataTopics.Pause, DataTopics.Pause], await NextAsync(heard, 2));
    }

    [Fact]
    public async Task A_pause_that_runs_out_is_announced_by_whatever_lifts_it()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromHours(1));
        using var heard = _changes.Subscribe();

        await new SuitePauseExpiryTask(_store.Database, _pause, _store.Clock).RunOnceAsync(CancellationToken.None);

        Assert.Equal([DataTopics.Pause], await NextAsync(heard, 1));
    }

    [Fact]
    public async Task Lifting_a_lapsed_pause_and_pausing_again_in_one_change_announces_once()
    {
        await ChangeAsync(paused: true, minutes: 30);
        _store.Clock.Advance(TimeSpan.FromMinutes(45));
        using var heard = _changes.Subscribe();

        await ChangeAsync(paused: true, minutes: 60);
        _changes.Publish("sentinel");

        Assert.Equal([DataTopics.Pause, "sentinel"], await NextAsync(heard, 2));
    }

    [Fact]
    public async Task A_change_that_is_not_committed_announces_nothing()
    {
        using var heard = _changes.Subscribe();
        var uow = await UnitOfWork.OpenAsync(_store.Database);
        await using (uow)
        {
            await _pause.ChangeAsync(uow, true, null, keepEnd: false, true, Now, "alice");
        }

        _changes.Publish("sentinel");

        Assert.Equal(["sentinel"], await NextAsync(heard, 1));
    }

    private static async Task<string[]> NextAsync(BroadcastSubscription<string> heard, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var topics = new List<string>();
        await foreach (var topic in heard.ReadAllAsync(timeout.Token))
        {
            topics.Add(topic);
            if (topics.Count == count)
            {
                break;
            }
        }

        return [.. topics];
    }

    private sealed record PauseOutcome(PauseState State, string[] Entries);
}
