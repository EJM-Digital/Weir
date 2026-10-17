using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Workers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Worker slots, heartbeats, history retention, periodic enqueue and the Activity writer.</summary>
public sealed class JobServicesTests : IDisposable
{
    private readonly JobsTestDatabase _db = new(keepSeedRows: true);
    private readonly JobRowsRetention _retention;

    public JobServicesTests() => _retention = new JobRowsRetention(_db.Store);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_worker_slot_processes_a_job_then_stops_and_reports_stopped()
    {
        await _db.Store.EnqueueOrGetAsync("loop1", "processing.test.loop_ok.v1");
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new WorkerHeartbeats(TimeProvider.System);
        var service = WorkerService([new DelegateHandler("processing.test.loop_ok.v1", context => seen.TrySetResult(context.JobKind))], heartbeats, workerCount: 1);
        _db.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal("processing.test.loop_ok.v1", await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Eventually.ThatAsync(async () => (await _db.Store.GetAsync(1))!.Status == ProcessingJobStatus.Completed);
        Assert.Equal("healthy", heartbeats.Snapshot([new("processing", 1)])[0].Status);
        await service.StopAsync(CancellationToken.None);

        var lane = heartbeats.Snapshot([new("processing", 1)])[0];
        Assert.Equal(("degraded", 1), (lane.Status, lane.StoppedWorkers));
    }

    [Fact]
    public async Task Slots_above_the_saved_files_at_once_value_stay_idle_but_keep_beating()
    {
        _db.Execute("UPDATE operator_settings SET max_concurrent_files = 3");
        var heartbeats = new WorkerHeartbeats(TimeProvider.System);
        var service = WorkerService([], heartbeats, workerCount: 8);

        await service.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => heartbeats.Snapshot([new("processing", 8)])[0].ActiveWorkers == 8);
        var lane = heartbeats.Snapshot([new("processing", 8)])[0];
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("healthy", lane.Status);
    }

    [Fact]
    public async Task Only_slots_below_the_files_at_once_value_take_work()
    {
        _db.Execute("UPDATE operator_settings SET max_concurrent_files = 2");
        for (var i = 0; i < 12; i++)
        {
            await _db.Store.EnqueueOrGetAsync($"j{i}", "processing.test.owner.v1");
        }

        var owners = new System.Collections.Concurrent.ConcurrentBag<string>();
        var service = WorkerService([new DelegateHandler("processing.test.owner.v1", context => owners.Add(context.LeaseOwner))], new WorkerHeartbeats(TimeProvider.System), workerCount: 8);
        _db.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        await service.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => _db.Count("SELECT count(*) FROM jobs WHERE status = 'completed'") == 12);
        await service.StopAsync(CancellationToken.None);

        Assert.Subset(new HashSet<string> { ProcessingWorkerService.LeaseOwner(0), ProcessingWorkerService.LeaseOwner(1) }, owners.ToHashSet());
    }

    [Fact]
    public async Task A_worker_survives_a_crashed_tick()
    {
        // A handler that throws on the claim path's own machinery is covered by the processor; here the
        // database disappears under the worker and comes back.
        await _db.Store.EnqueueOrGetAsync("after", "processing.test.after.v1");
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _db.Execute("ALTER TABLE jobs RENAME TO jobs_hidden");
        var logger = new RecordingLogger<ProcessingWorkerService>();
        var service = WorkerService([new DelegateHandler("processing.test.after.v1", _ => ran.TrySetResult())], new WorkerHeartbeats(TimeProvider.System), workerCount: 1, logger);
        _db.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        await service.StartAsync(CancellationToken.None);
        // Wait for the tick to actually crash against the renamed table, rather than guessing how long
        // that takes: restoring the table too early would let the worker succeed without ever exercising
        // the crash-and-continue path this test exists to prove.
        await Eventually.ThatAsync(() => logger.Errors.Count >= 1);
        _db.Execute("ALTER TABLE jobs_hidden RENAME TO jobs");
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Lease_owners_name_host_process_and_slot()
    {
        Assert.Equal($"{System.Net.Dns.GetHostName()}-{Environment.ProcessId}-w3", ProcessingWorkerService.LeaseOwner(3));
    }

    [Fact]
    public async Task History_retention_prunes_old_terminal_rows_only()
    {
        _db.InsertRawJob("old-done", "processing.a.v1", ProcessingJobStatus.Completed);
        _db.InsertRawJob("old-failed", "processing.a.v1", ProcessingJobStatus.Failed);
        _db.InsertRawJob("old-cancelled", "processing.a.v1", ProcessingJobStatus.Cancelled);
        _db.InsertRawJob("old-finalize", "processing.a.v1", ProcessingJobStatus.HandlerOkFinalizeFailed);
        _db.InsertRawJob("old-pending", "processing.a.v1");
        _db.InsertRawJob("old-leased", "processing.a.v1", ProcessingJobStatus.Leased);
        _db.InsertRawJob("new-done", "processing.a.v1", ProcessingJobStatus.Completed);
        _db.Execute("UPDATE jobs SET updated_at = '2025-01-01 00:00:00' WHERE dedupe_key LIKE 'old-%'");
        _db.Execute("INSERT INTO activity_events (created_at, event_type, module, title) VALUES ('2025-01-01 00:00:00', 'x', 'processing', 'old'), (CURRENT_TIMESTAMP, 'x', 'processing', 'new')");

        var counts = await _retention.RunTickAsync(90, DateTimeOffset.UtcNow);

        Assert.Equal(4, counts.Processing);
        Assert.Equal(1, counts.Activity);
        Assert.Equal(5, counts.Total);
        Assert.Equal(["new-done", "old-leased", "old-pending"], (await _db.Store.ListAsync()).Select(j => j.DedupeKey).Order(StringComparer.Ordinal));
        Assert.Equal(1, _db.Count("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public async Task Retention_removes_more_old_rows_than_one_batch_holds()
    {
        var old = (BatchedDeletes.BatchSize * 2) + 1;
        _db.Execute(
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {old}) " +
            "INSERT INTO activity_events (created_at, event_type, module, title) SELECT '2025-01-01 00:00:00', 'x', 'processing', 'old' FROM n");
        _db.Execute("INSERT INTO activity_events (created_at, event_type, module, title) VALUES (CURRENT_TIMESTAMP, 'x', 'processing', 'new')");

        var counts = await _retention.RunTickAsync(90, DateTimeOffset.UtcNow);

        Assert.Equal(old, counts.Activity);
        Assert.Equal(1, _db.Count("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public async Task Activity_retention_of_zero_keeps_everything()
    {
        _db.Execute("UPDATE suite_settings SET activity_retention_days = 0");
        _db.Execute("INSERT INTO activity_events (created_at, event_type, module, title) VALUES ('2000-01-01 00:00:00', 'x', 'processing', 'old')");

        Assert.Equal(0, (await _retention.RunTickAsync(90, DateTimeOffset.UtcNow)).Activity);
    }

    [Fact]
    public async Task Periodic_enqueue_skips_families_this_server_cannot_run()
    {
        // JobHandlerRegistry.Empty has no handler for the enqueuer's job kind, so
        // PeriodicEnqueueService.ExecuteAsync's own handler check never even starts this family's loop:
        // there is nothing to wait for, on any clock.
        var enqueuer = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromMilliseconds(50), killSwitch: false);
        var service = new PeriodicEnqueueService([enqueuer], JobHandlerRegistry.Empty, TimeProvider.System, NullLogger<PeriodicEnqueueService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Periodic_work_temp_sweep_enqueue_keeps_one_row_per_scope()
    {
        var time = new FakeTimeProvider();
        var clock = new TimerWatchingClock(time);
        var interval = TimeSpan.FromMilliseconds(20);
        var registry = new JobHandlerRegistry([new DelegateHandler(PeriodicJobKinds.WorkTempStaleSweep, _ => { })]);
        IPeriodicEnqueuer[] enqueuers =
        [
            new WorkTempStaleSweepEnqueuer(_db.Store, "movie", interval, killSwitch: false),
            new WorkTempStaleSweepEnqueuer(_db.Store, "TV", interval, killSwitch: false),
        ];
        var service = new PeriodicEnqueueService(enqueuers, registry, clock, NullLogger<PeriodicEnqueueService>.Instance);

        // Each family's loop sets one timer when it has finished a pass and parks on it, so a count of timers says how many passes
        // are done and that every loop is waiting for the clock.
        await service.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => clock.TimersSet == enqueuers.Length);
        Assert.Equal(2, _db.Count("SELECT count(*) FROM jobs"));

        // The dedupe key keeps this to one row per scope; force several more due ticks, each one finished before the next.
        for (var i = 1; i <= 5; i++)
        {
            time.Advance(interval);
            await Eventually.ThatAsync(() => clock.TimersSet == enqueuers.Length * (i + 1));
        }

        await service.StopAsync(CancellationToken.None);

        var rows = (await _db.Store.ListAsync()).OrderBy(j => j.DedupeKey, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [("processing.work_temp_stale_sweep:v1:movie", "{\"media_scope\":\"movie\",\"trigger\":\"scheduled\"}"),
             ("processing.work_temp_stale_sweep:v1:tv", "{\"media_scope\":\"tv\",\"trigger\":\"scheduled\"}")],
            rows.Select(r => (r.DedupeKey, r.PayloadJson!)));
    }

    [Fact]
    public async Task Periodic_families_honour_their_saved_switch_and_the_environment_kill_switch()
    {
        var sweep = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromMinutes(1), killSwitch: false);
        var killed = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromMinutes(1), killSwitch: true);

        Assert.True(await sweep.IsEnabledAsync(CancellationToken.None));
        Assert.False(await killed.IsEnabledAsync(CancellationToken.None));
        _db.Execute("UPDATE operator_settings SET work_temp_stale_sweep_enabled = 0");
        Assert.False(await sweep.IsEnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_family_switched_on_while_weir_runs_starts_without_a_restart()
    {
        _db.Execute("UPDATE operator_settings SET work_temp_stale_sweep_enabled = 0");
        var time = new FakeTimeProvider();
        var recheck = TimeSpan.FromMilliseconds(50);
        var registry = new JobHandlerRegistry([new DelegateHandler(PeriodicJobKinds.WorkTempStaleSweep, _ => { })]);
        var clock = new PeriodicEnqueueClock();
        var sweep = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromHours(1), killSwitch: false);
        var iterationComplete = new SemaphoreSlim(0);
        var service = new PeriodicEnqueueService(
            [sweep], registry, time, NullLogger<PeriodicEnqueueService>.Instance, clock, recheck, () => iterationComplete.Release());

        await service.StartAsync(CancellationToken.None);
        // The loop's first pass, before any Advance below, so a later Advance always lands on the delay it set up
        // from that pass rather than racing its own startup.
        await WaitForIterationAsync(iterationComplete);

        // Force several recheck ticks while switched off. Waiting on the completion signal instead of a fixed
        // real sleep proves each Advance was actually acted on before the next one, on whatever thread-pool
        // schedule the machine running the test happens to give it.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(recheck);
            await WaitForIterationAsync(iterationComplete);
        }

        Assert.Equal(0, _db.Count("SELECT count(*) FROM jobs"));
        Assert.Null(clock.NextRunFor([PeriodicJobKinds.WorkTempStaleSweep]));

        _db.Execute("UPDATE operator_settings SET work_temp_stale_sweep_enabled = 1");
        time.Advance(recheck);
        await WaitForIterationAsync(iterationComplete);

        Assert.Equal(1, _db.Count("SELECT count(*) FROM jobs"));
        await service.StopAsync(CancellationToken.None);

        var next = clock.NextRunFor([PeriodicJobKinds.WorkTempStaleSweep]);
        Assert.NotNull(next);
        Assert.Equal(TimeSpan.FromHours(1), next.Value.Interval);
        Assert.Equal(time.GetUtcNow().AddHours(1), next.Value.NextRunAt);
    }

    [Fact]
    public async Task A_cleanup_family_is_listed_with_its_next_run_while_switched_on_and_leaves_the_list_when_switched_off()
    {
        var time = new FakeTimeProvider();
        var recheck = TimeSpan.FromMilliseconds(50);
        var registry = new JobHandlerRegistry([new DelegateHandler(PeriodicJobKinds.WorkTempStaleSweep, _ => { })]);
        var tasks = new PeriodicTaskRegistry(time);
        var sweep = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromHours(1), killSwitch: false);
        var iterationComplete = new SemaphoreSlim(0);
        var service = new PeriodicEnqueueService(
            [sweep], registry, time, NullLogger<PeriodicEnqueueService>.Instance, new PeriodicEnqueueClock(), recheck, () => iterationComplete.Release(), tasks);

        await service.StartAsync(CancellationToken.None);
        await WaitForIterationAsync(iterationComplete);

        var listed = Assert.Single(tasks.Snapshot());
        Assert.Equal(
            (ScheduledJobTasks.LeftoverFiles.Key, "Clear leftover files", time.GetUtcNow() + TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            (listed.Key, listed.Label, listed.NextRunAt, listed.Interval));

        _db.Execute("UPDATE operator_settings SET work_temp_stale_sweep_enabled = 0");
        time.Advance(recheck);
        await WaitForIterationAsync(iterationComplete);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(tasks.Snapshot());
    }

    /// <summary>
    /// Bounded generously: this is proving the loop's own iteration count, not timing an actual wait, so a
    /// signal that never arrives is a real bug, not a slow machine.
    /// </summary>
    private static async Task WaitForIterationAsync(SemaphoreSlim iterationComplete) =>
        Assert.True(
            await iterationComplete.WaitAsync(TimeSpan.FromSeconds(10)),
            "The periodic loop did not complete an iteration in time.");

    [Fact]
    public async Task A_saved_interval_replaces_the_environments()
    {
        var sweep = new WorkTempStaleSweepEnqueuer(_db.Store, "movie", TimeSpan.FromHours(1), killSwitch: false);
        Assert.Equal(TimeSpan.FromHours(1), await sweep.IntervalAsync(CancellationToken.None));

        _db.Execute("UPDATE operator_settings SET work_temp_stale_sweep_interval_seconds = 900");

        Assert.Equal(TimeSpan.FromMinutes(15), await sweep.IntervalAsync(CancellationToken.None));
    }

    /// <summary>A periodic enqueuer's failed tick is retried after <see cref="PeriodicSchedule.FailureCooldown"/>.</summary>
    [Fact]
    public async Task A_failing_periodic_enqueue_retries_after_its_cooldown()
    {
        var time = new FakeTimeProvider();
        var registry = new JobHandlerRegistry([new DelegateHandler("processing.flaky.v1", _ => { })]);
        var flaky = new FlakyEnqueuer();
        var service = new PeriodicEnqueueService([flaky], registry, time, NullLogger<PeriodicEnqueueService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => flaky.Calls >= 1);

        time.Advance(PeriodicSchedule.FailureCooldown);
        await Eventually.ThatAsync(() => flaky.Calls >= 2);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_activity_writer_fills_the_classified_columns()
    {
        var writer = new SqliteActivityWriter(_db.Database);

        var id = await writer.RecordAsync(new ActivityEventDraft("processing.worker_failure", "processing", "t", "{\"result\":\"failed\",\"library_id\":2,\"relative_media_path\":\"x.mkv\",\"trigger\":\"retry\",\"run_id\":\"a\"}"));

        Assert.True(id > 0);
        Assert.Equal(1, _db.Count(
            "SELECT count(*) FROM activity_events WHERE id = @id AND result = 'failed' AND library_id = 2 AND relative_path = 'x.mkv' AND \"trigger\" = 'retry' AND run_key = 'run:a' AND created_at IS NOT NULL",
            ("@id", id)));
    }

    private ProcessingWorkerService WorkerService(
        IEnumerable<IJobHandler> handlers, WorkerHeartbeats heartbeats, int workerCount, ILogger<ProcessingWorkerService>? logger = null)
    {
        var options = Weir.Core.Configuration.WeirOptionsLoader.Load(new Weir.Core.Configuration.RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = _db.Home,
                ["WEIR_PROCESSING_WORKER_COUNT"] = workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            OperatingSystem.IsWindows(),
            _db.Home,
            _db.Home));
        var timings = new WorkerLoopTimings { IdleSleep = TimeSpan.FromMilliseconds(50), TickErrorBackoff = TimeSpan.FromMilliseconds(50), ConcurrencyCacheTtl = TimeSpan.FromMilliseconds(100), LeaseSeconds = 3600 };
        var registry = new JobHandlerRegistry(handlers);
        var processor = new ProcessingJobProcessor(
            _db.Store,
            registry,
            new SqliteActivityWriter(_db.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            TimeProvider.System,
            NullLogger<ProcessingJobProcessor>.Instance);
        // Never started, so RecoveryCompleted reads as already done and this slot never waits on it.
        var recovery = new JobsStartupRecoveryService(_db.Store, options, new LibrarySettingsStore(), new LibraryStore(), TimeProvider.System, NullLogger<JobsStartupRecoveryService>.Instance);
        return new ProcessingWorkerService(processor, _db.Store, heartbeats, options, timings, TimeProvider.System, logger ?? NullLogger<ProcessingWorkerService>.Instance, registry, recovery);
    }

    private sealed class FlakyEnqueuer : IPeriodicEnqueuer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string Name => "flaky";

        public string JobKind => "processing.flaky.v1";

        public TimeSpan Interval => TimeSpan.FromHours(1);

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task EnqueueOnceAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1 ? throw new InvalidOperationException("first tick fails") : Task.CompletedTask;
    }
}
