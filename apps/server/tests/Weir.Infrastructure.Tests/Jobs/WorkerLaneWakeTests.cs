using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Workers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// A job queued on an idle Weir is claimed by its lane's worker at once, without the poll interval passing (#1098). The clock
/// never moves in these tests, so a claim can only have come from the wake. The loops themselves are in
/// <c>WorkerLaneWakeTests.Loops.cs</c>: which jobs a lane takes, what it reports, and how it carries on after a failure.
/// </summary>
public sealed partial class WorkerLaneWakeTests : IDisposable
{
    private const string FilesKind = "processing.test.files_lane.v1";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly JobsTestDatabase _db = new(keepSeedRows: true);
    private readonly WorkerWakeSignals _signals = new();
    private readonly ProcessingJobStore _store;

    public WorkerLaneWakeTests() => _store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: _signals);

    public void Dispose() => _db.Dispose();

    public static TheoryData<WorkLane, string> JobsByLane => new()
    {
        { WorkLane.Files, FilesKind },
        { WorkLane.Upkeep, ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch },
        { WorkLane.Upkeep, LibraryModeJobKinds.ScanKind },
        { WorkLane.Upkeep, PeriodicJobKinds.WorkTempStaleSweep },
        { WorkLane.Upkeep, PeriodicJobKinds.UnclaimedHandbackCleanup },
    };

    [Theory]
    [MemberData(nameof(JobsByLane))]
    public async Task A_job_queued_on_an_idle_lane_is_claimed_without_the_poll_interval_passing(WorkLane lane, string jobKind)
    {
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new IdleSlots(lane == WorkLane.Files ? 1 : WorkLanes.UpkeepSlots);
        var service = Lane(lane, jobKind, claimed, idle.Waiting);
        var queuedAt = _db.Clock.GetUtcNow();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await idle.AllWaiting.WaitAsync(Ceiling);

            await _store.EnqueueOrGetAsync("job:one", jobKind);
            await claimed.Task.WaitAsync(Ceiling);

            Assert.Equal(queuedAt, _db.Clock.GetUtcNow());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(JobsByLane))]
    public async Task A_job_that_became_claimable_without_being_queued_is_found_when_the_poll_comes_round(WorkLane lane, string jobKind)
    {
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new IdleSlots(lane == WorkLane.Files ? 1 : WorkLanes.UpkeepSlots);
        var service = Lane(lane, jobKind, claimed, idle.Waiting);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await idle.AllWaiting.WaitAsync(Ceiling);

            // A row put straight into the table: nothing signals the lane, as when a retry's wait ends.
            _db.InsertRawJob("job:raw", jobKind);
            _db.Clock.Advance(new WorkerLoopTimings().IdleSleep);

            await claimed.Task.WaitAsync(Ceiling);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(JobsByLane))]
    public async Task A_job_held_back_by_a_pause_is_claimed_when_processing_is_resumed_without_the_poll_interval_passing(WorkLane lane, string jobKind)
    {
        _db.Pause(scanWhilePaused: false);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new IdleSlots(lane == WorkLane.Files ? 1 : WorkLanes.UpkeepSlots);
        var service = Lane(lane, jobKind, claimed, idle.Waiting);
        var pausedAt = _db.Clock.GetUtcNow();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await idle.AllWaiting.WaitAsync(Ceiling);
            // A row put straight into the table: nothing signals the lane, so only the resume can bring the slots back.
            _db.InsertRawJob("job:held", jobKind);

            var pause = new SuitePauseService(new SuiteSettingsStore(new AuthStore()), new ActivityStore(), new DataChangePublisher(), _signals);
            var uow = await UnitOfWork.OpenAsync(_db.Database);
            await using (uow)
            {
                await pause.ChangeAsync(uow, paused: false, minutes: null, keepEnd: false, scanWhilePaused: true, Timestamp.UtcNow(_db.Clock), "alice");
                await uow.CommitAsync();
            }

            await claimed.Task.WaitAsync(Ceiling);

            Assert.Equal(pausedAt, _db.Clock.GetUtcNow());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_scan_queued_while_both_lanes_are_idle_is_taken_by_an_upkeep_slot()
    {
        var scannedBy = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filesIdle = new IdleSlots(1);
        var upkeepIdle = new IdleSlots(WorkLanes.UpkeepSlots);
        var scan = ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch;
        var handlers = new IJobHandler[] { new DelegateHandler(scan, context => scannedBy.TrySetResult(context.LeaseOwner)), new DelegateHandler(FilesKind, _ => { }) };
        var files = FilesLane(handlers, filesIdle.Waiting);
        var upkeep = UpkeepLane(handlers, upkeepIdle.Waiting);
        await files.StartAsync(CancellationToken.None);
        await upkeep.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(filesIdle.AllWaiting, upkeepIdle.AllWaiting).WaitAsync(Ceiling);

            await _store.EnqueueOrGetAsync("scan:one", scan);
            var owner = await scannedBy.Task.WaitAsync(Ceiling);

            Assert.Matches("-u[0-9]+$", owner);
        }
        finally
        {
            await files.StopAsync(CancellationToken.None);
            await upkeep.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Completes once every slot of a lane has looked for work, found none and started waiting, so a wake cannot be missed by one still on its way.</summary>
    private sealed class IdleSlots(int slots)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = slots;

        public Task AllWaiting => _all.Task;

        public void Waiting()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _all.TrySetResult();
            }
        }
    }

    private IHostedService Lane(WorkLane lane, string jobKind, TaskCompletionSource claimed, Action idleWaiting)
    {
        IJobHandler[] handlers = [new DelegateHandler(jobKind, _ => claimed.TrySetResult())];
        return lane == WorkLane.Files ? FilesLane(handlers, idleWaiting) : UpkeepLane(handlers, idleWaiting);
    }

    private ProcessingWorkerService FilesLane(IJobHandler[] handlers, Action idleWaiting, int workerCount = 1, ILogger<ProcessingWorkerService>? logger = null)
    {
        var options = Options(workerCount);
        var registry = new JobHandlerRegistry(handlers);
        return new ProcessingWorkerService(
            Processor(registry),
            _store,
            new WorkerHeartbeats(_db.Clock),
            options,
            Timings(),
            _db.Clock,
            logger ?? NullLogger<ProcessingWorkerService>.Instance,
            registry,
            Recovery(options),
            _signals)
        {
            IdleWaiting = idleWaiting,
        };
    }

    private UpkeepWorkerService UpkeepLane(IJobHandler[] handlers, Action idleWaiting, WorkerHeartbeats? heartbeats = null, ILogger<UpkeepWorkerService>? logger = null)
    {
        var registry = new JobHandlerRegistry(handlers);
        return new UpkeepWorkerService(
            Processor(registry),
            heartbeats ?? new WorkerHeartbeats(_db.Clock),
            Timings(),
            _db.Clock,
            logger ?? NullLogger<UpkeepWorkerService>.Instance,
            registry,
            Recovery(Options(workerCount: 1)),
            _signals)
        {
            IdleWaiting = idleWaiting,
        };
    }

    private ProcessingJobProcessor Processor(JobHandlerRegistry registry) =>
        new(
            _store,
            registry,
            new SqliteActivityWriter(_db.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _db.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);

    private static WorkerLoopTimings Timings() => new() { LeaseSeconds = 3600 };

    /// <summary>Never started, so its recovery reads as already done and no slot waits on it.</summary>
    private JobsStartupRecoveryService Recovery(WeirOptions options) =>
        new(_store, options, new LibrarySettingsStore(), new LibraryStore(), _db.Clock, NullLogger<JobsStartupRecoveryService>.Instance);

    private WeirOptions Options(int workerCount) =>
        WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = _db.Home,
                ["WEIR_PROCESSING_WORKER_COUNT"] = workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            OperatingSystem.IsWindows(),
            _db.Home,
            _db.Home));
}
