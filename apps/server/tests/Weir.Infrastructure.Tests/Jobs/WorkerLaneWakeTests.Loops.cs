using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Core.Workers;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

public sealed partial class WorkerLaneWakeTests
{
    private static readonly TimeSpan IdleSleep = new WorkerLoopTimings().IdleSleep;

    [Fact]
    public async Task The_upkeep_lane_takes_a_scan_and_leaves_file_work_for_the_files_lane()
    {
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new IdleSlots(WorkLanes.UpkeepSlots);
        var upkeep = UpkeepLane(
            [new DelegateHandler(LibraryModeJobKinds.ScanKind, _ => scanned.TrySetResult()), new DelegateHandler(FilesKind, _ => { })],
            idle.Waiting);
        _db.InsertRawJob("file:first", FilesKind);
        _db.InsertRawJob("scan:second", LibraryModeJobKinds.ScanKind);
        await upkeep.StartAsync(CancellationToken.None);
        try
        {
            await scanned.Task.WaitAsync(Ceiling);
            await Eventually.ThatAsync(() => _db.Count("SELECT count(*) FROM jobs WHERE job_kind = @kind AND status = 'completed'", ("@kind", LibraryModeJobKinds.ScanKind)) == 1);

            Assert.Equal("pending", _db.Scalar("SELECT status FROM jobs WHERE job_kind = @kind", ("@kind", FilesKind)));
            Assert.Equal(0, _db.Count("SELECT attempt_count FROM jobs WHERE job_kind = @kind", ("@kind", FilesKind)));
        }
        finally
        {
            await upkeep.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_upkeep_slots_report_under_their_own_name_while_they_run_and_stopped_once_the_lane_stops()
    {
        var idle = new IdleSlots(WorkLanes.UpkeepSlots);
        var heartbeats = new WorkerHeartbeats(_db.Clock);
        var upkeep = UpkeepLane([new DelegateHandler(LibraryModeJobKinds.ScanKind, _ => { })], idle.Waiting, heartbeats);
        KeyValuePair<string, int>[] expected = [new(WorkLanes.UpkeepHeartbeatModule, WorkLanes.UpkeepSlots)];

        await upkeep.StartAsync(CancellationToken.None);
        await idle.AllWaiting.WaitAsync(Ceiling);
        var running = Assert.Single(heartbeats.Snapshot(expected));
        await upkeep.StopAsync(CancellationToken.None);
        var stopped = Assert.Single(heartbeats.Snapshot(expected));

        Assert.Equal(("upkeep", "healthy", WorkLanes.UpkeepSlots, 0), (running.Module, running.Status, running.ActiveWorkers, running.StoppedWorkers));
        Assert.Equal(("degraded", 0, WorkLanes.UpkeepSlots), (stopped.Status, stopped.ActiveWorkers, stopped.StoppedWorkers));
    }

    [Fact]
    public async Task The_upkeep_lane_survives_a_crashed_tick_says_so_and_carries_on_when_the_database_is_back()
    {
        var logger = new RecordingLogger<UpkeepWorkerService>();
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upkeep = UpkeepLane([new DelegateHandler(LibraryModeJobKinds.ScanKind, _ => scanned.TrySetResult())], () => { }, logger: logger);
        _db.InsertRawJob("scan:after", LibraryModeJobKinds.ScanKind);
        _db.Execute("ALTER TABLE jobs RENAME TO jobs_hidden");
        await upkeep.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => logger.Errors.Count >= WorkLanes.UpkeepSlots);
            _db.Execute("ALTER TABLE jobs_hidden RENAME TO jobs");

            await Eventually.ThatAdvancingAsync(_db.Clock, new WorkerLoopTimings().TickErrorBackoff, () => scanned.Task.IsCompleted);
        }
        finally
        {
            await upkeep.StopAsync(CancellationToken.None);
        }

        Assert.All(logger.Errors, message => Assert.StartsWith("Upkeep worker tick crashed", message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_files_at_once_value_saved_while_weir_runs_takes_effect_once_the_slots_read_it_again()
    {
        _db.Execute("UPDATE operator_settings SET max_concurrent_files = 1");
        var owners = new ConcurrentDictionary<string, bool>();
        var allThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new IdleSlots(3);
        var files = FilesLane(
            [new DelegateHandler(FilesKind, async context =>
            {
                owners[context.LeaseOwner] = true;
                if (owners.Count == 3)
                {
                    allThree.TrySetResult();
                }

                await allThree.Task;
            })],
            idle.Waiting,
            workerCount: 3);
        await files.StartAsync(CancellationToken.None);
        try
        {
            await idle.AllWaiting.WaitAsync(Ceiling);

            _db.Execute("UPDATE operator_settings SET max_concurrent_files = 3");
            for (var job = 0; job < 3; job++)
            {
                await _store.EnqueueOrGetAsync($"file:{job}", FilesKind);
            }

            // Each job holds its slot until all three are at work, so only a slot that has read the new value can finish this.
            await Eventually.ThatAdvancingAsync(_db.Clock, IdleSleep, () => allThree.Task.IsCompleted);
        }
        finally
        {
            await files.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, owners.Count);
    }

    [Fact]
    public async Task A_files_at_once_value_that_cannot_be_read_is_warned_about_once_a_minute_not_on_every_pass()
    {
        var logger = new RecordingLogger<ProcessingWorkerService>();
        var files = FilesLane([new DelegateHandler(FilesKind, _ => { })], () => { }, logger: logger);
        _db.Execute("UPDATE operator_settings SET max_concurrent_files = 'not a number'");
        await files.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => logger.At(LogLevel.Warning).Count == 1);

            // Claiming a job reads the same value, so each pass ends in a crashed tick: five of those are five passes, a second apart.
            await Eventually.ThatAdvancingAsync(_db.Clock, new WorkerLoopTimings().TickErrorBackoff, () => logger.Errors.Count >= 5);
            Assert.Single(logger.At(LogLevel.Warning));

            _db.Clock.Advance(ProcessingWorkerService.SettingsReadFailureLogInterval);
            await Eventually.ThatAdvancingAsync(_db.Clock, new WorkerLoopTimings().TickErrorBackoff, () => logger.At(LogLevel.Warning).Count == 2);
        }
        finally
        {
            await files.StopAsync(CancellationToken.None);
        }

        Assert.All(logger.At(LogLevel.Warning), message => Assert.Contains("could not read the files-at-once setting", message, StringComparison.Ordinal));
    }
}
