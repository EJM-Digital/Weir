using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Tests.Scheduling;

/// <summary>What the registry says about each task: when it is next due, how its last run went, and who is told when that changes.</summary>
public sealed class PeriodicTaskRegistryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly PeriodicTaskRegistry _registry;

    public PeriodicTaskRegistryTests() => _registry = new PeriodicTaskRegistry(_time);

    [Fact]
    public void A_planned_task_is_listed_with_when_it_is_next_due_and_nothing_yet_about_its_last_run()
    {
        _registry.Plan("trim-log", "Trim the log", Noon.AddHours(1), TimeSpan.FromHours(1));

        var task = Assert.Single(_registry.Snapshot());

        Assert.Equal(new PeriodicTaskStatus("trim-log", "Trim the log", false, null, null, null, Noon.AddHours(1), TimeSpan.FromHours(1)), task);
    }

    [Fact]
    public void A_run_that_works_is_listed_as_running_and_then_as_finished_just_now()
    {
        _registry.Plan("trim-log", "Trim the log", Noon, TimeSpan.FromHours(1));

        _registry.Begin("trim-log");
        Assert.True(Assert.Single(_registry.Snapshot()).Running);

        _time.Advance(TimeSpan.FromSeconds(3));
        _registry.End("trim-log", ok: true, error: null, nextRunAt: Noon.AddHours(1));

        var task = Assert.Single(_registry.Snapshot());
        Assert.Equal((false, Noon.AddSeconds(3), true, null, Noon.AddHours(1)), (task.Running, task.LastRunAt, task.LastOk, task.LastError, task.NextRunAt));
    }

    [Fact]
    public void A_failed_run_keeps_its_reason_until_a_run_works()
    {
        _registry.Plan("trim-log", "Trim the log", Noon, TimeSpan.FromHours(1));
        _registry.Begin("trim-log");
        _registry.End("trim-log", ok: false, error: "Weir could not use a file or folder it needed.");

        var failed = Assert.Single(_registry.Snapshot());
        Assert.Equal((false, "Weir could not use a file or folder it needed."), (failed.LastOk, failed.LastError));

        _registry.Begin("trim-log");
        _registry.End("trim-log", ok: true, error: null);

        var worked = Assert.Single(_registry.Snapshot());
        Assert.Equal((true, null), (worked.LastOk, worked.LastError));
    }

    [Fact]
    public void A_task_whose_runs_overlap_stays_running_until_the_last_one_ends()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));
        _registry.Begin("scan-1");
        _registry.Begin("scan-1");

        _registry.End("scan-1", ok: true, error: null);
        Assert.True(Assert.Single(_registry.Snapshot()).Running);

        _registry.End("scan-1", ok: true, error: null);
        Assert.False(Assert.Single(_registry.Snapshot()).Running);
    }

    [Fact]
    public void A_run_reported_for_a_task_nobody_planned_adds_nothing()
    {
        _registry.Begin("scan-9");
        _registry.End("scan-9", ok: false, error: "No.");

        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Planning_a_task_again_moves_its_next_time_and_label_and_keeps_its_history()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon.AddMinutes(5), TimeSpan.FromMinutes(5));
        _registry.Begin("scan-1");
        _registry.End("scan-1", ok: true, error: null);

        _registry.Plan("scan-1", "Scan Films", Noon.AddMinutes(10), TimeSpan.FromMinutes(5));

        var task = Assert.Single(_registry.Snapshot());
        Assert.Equal(("Scan Films", Noon.AddMinutes(10), true), (task.Label, task.NextRunAt, task.LastOk));
    }

    [Fact]
    public void A_removed_task_leaves_the_list()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));

        _registry.Remove("scan-1");

        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void The_list_is_in_label_order()
    {
        _registry.Plan("b", "Trim the log", Noon, null);
        _registry.Plan("a", "Scan Movies", Noon, null);

        Assert.Equal(["Scan Movies", "Trim the log"], _registry.Snapshot().Select(task => task.Label));
    }

    [Fact]
    public async Task A_task_coming_a_run_starting_and_a_run_ending_are_each_announced()
    {
        using var listener = _registry.SubscribeToChanges();

        _registry.Plan("trim-log", "Trim the log", Noon, null);
        var coming = await NextAnnouncementAsync(listener);
        _registry.Begin("trim-log");
        var starting = await NextAnnouncementAsync(listener);
        _registry.End("trim-log", ok: true, error: null);
        var ending = await NextAnnouncementAsync(listener);

        Assert.True(coming < starting && starting < ending);
    }

    [Fact]
    public async Task Moving_the_next_time_is_announced_so_a_countdown_never_goes_stale()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));
        using var listener = _registry.SubscribeToChanges();

        _registry.Plan("scan-1", "Scan Movies", Noon.AddMinutes(1), TimeSpan.FromMinutes(5));

        // Announcements are numbered from one: the task coming was the first, so the move is the second.
        Assert.Equal(2, await NextAnnouncementAsync(listener));
    }

    [Fact]
    public async Task Planning_a_task_for_the_time_it_already_has_announces_nothing()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));
        using var listener = _registry.SubscribeToChanges();
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));
        _registry.Begin("scan-1");

        Assert.Equal(2, await NextAnnouncementAsync(listener));
    }

    private static async Task<long> NextAnnouncementAsync(BroadcastSubscription<long> listener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var version in listener.ReadAllAsync(timeout.Token))
        {
            return version;
        }

        throw new InvalidOperationException("The listener ended.");
    }

    [Fact]
    public void A_task_whose_interval_appears_or_goes_is_planned_again_with_the_new_one()
    {
        _registry.Plan("scan-1", "Scan Movies", Noon, null);
        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(_registry.Snapshot()).Interval);

        _registry.Plan("scan-1", "Scan Movies", Noon, TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(_registry.Snapshot()).Interval);

        _registry.Plan("scan-1", "Scan Movies", Noon, null);
        _registry.Plan("scan-1", "Scan Movies", Noon, null);
        Assert.Null(Assert.Single(_registry.Snapshot()).Interval);
    }
}
