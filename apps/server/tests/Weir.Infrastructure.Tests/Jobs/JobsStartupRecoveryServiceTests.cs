using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Workers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.LibraryMode;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// The start-up service that runs crash recovery and the swap sweep before any worker claims a job (FL-20, JOB-001): where it
/// gets the folders to walk, what it does when a step cannot run, and that the worker lanes start whatever happens.
/// </summary>
public sealed class JobsStartupRecoveryServiceTests : IDisposable
{
    private const string PassKind = "processing.test.after_recovery.v1";

    private readonly JobsTestDatabase _db = new();
    private readonly CapturingLogger<JobsStartupRecoveryService> _log = new();
    private readonly SwapScenario _swap = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_sweep_walks_every_library_folder_from_the_settings_and_puts_back_an_original_it_finds_missing()
    {
        await AddLibraryFoldersAsync("/lib", "/other");
        await AddLibraryFoldersAsync("/lib");
        _swap.Files.Delete(SwapScenario.Original);
        _swap.Files.Put(SwapScenario.Backup, "OLD");
        _swap.Files.Put(SwapScenario.Temp, "NEW");
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.Equal(new SwapRecoveryReport(1, 0, 1, 0), service.LastSwapSweepReport);
        Assert.Equal("OLD", _swap.Files.Read(SwapScenario.Original));
        Assert.Equal([SwapScenario.Original], _swap.Files.Paths);
        Assert.Equal(["EnumerateLeftovers(/lib)", "EnumerateLeftovers(/other)"], _swap.Files.Operations.Where(operation => operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_sweep_finds_a_leftover_the_job_rows_do_not_mention_because_it_walks_the_folders()
    {
        await AddLibraryFoldersAsync("/lib");
        _swap.Files.Put(SwapScenario.Backup, "OLD");
        _swap.Files.Write(SwapScenario.Original, "NEW");
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), service.LastSwapSweepReport);
        Assert.Equal("NEW", _swap.Files.Read(SwapScenario.Original));
        Assert.False(_swap.Files.Has(SwapScenario.Backup));
    }

    [Fact]
    public async Task A_swap_recorded_on_a_job_row_is_put_right_even_when_no_library_has_a_folder()
    {
        _swap.Files.Delete(SwapScenario.Original);
        _swap.Files.Put(SwapScenario.Backup, "OLD");
        await _swap.Journal.RecordAsync(new SwapJournalEntry(SwapScenario.JobId, SwapScenario.Original, SwapJournalState.Committing));
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.Equal(new SwapRecoveryReport(0, 0, 1, 0), service.LastSwapSweepReport);
        Assert.Equal("OLD", _swap.Files.Read(SwapScenario.Original));
        Assert.Equal(SwapJournalState.Recovered, _swap.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task Without_library_mode_there_is_no_sweep_and_recovery_still_finishes()
    {
        await AddLibraryFoldersAsync("/lib");
        var service = Service(sweep: null);

        await StartAndWaitAsync(service);

        Assert.Null(service.LastSwapSweepReport);
        Assert.NotNull(service.LastReport);
        Assert.Empty(_swap.Files.Operations);
    }

    [Fact]
    public async Task Start_returns_while_the_sweep_is_still_walking_a_folder()
    {
        await AddLibraryFoldersAsync("/lib");
        using var held = HoldTheSweep();
        var service = Service(_swap.Sweep);

        // Started on a thread of its own: a start that waited for the sweep would never come back to this test.
        await Task.Run(() => service.StartAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
        await held.RunningAsync();

        Assert.False(service.RecoveryCompleted.IsCompleted);
    }

    [Fact]
    public async Task The_gate_stays_shut_until_the_sweep_has_finished()
    {
        await AddLibraryFoldersAsync("/lib");
        using var held = HoldTheSweep();
        var service = Service(_swap.Sweep);

        await Task.Run(() => service.StartAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
        await held.RunningAsync();

        Assert.False(service.RecoveryCompleted.IsCompleted);
        Assert.Null(service.LastSwapSweepReport);

        held.Release();
        await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(service.LastSwapSweepReport);
    }

    [Fact]
    public async Task A_sweep_that_cannot_run_is_a_warning_and_the_gate_still_opens()
    {
        await AddLibraryFoldersAsync("/lib");
        _swap.Files.OnStep = operation =>
        {
            if (operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the share vanished");
            }
        };
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.Null(service.LastSwapSweepReport);
        Assert.True(_log.Logged(LogLevel.Warning, "Library mode's startup sweep could not run; interrupted swaps, if any, are picked up at the next start."));
        Assert.DoesNotContain(_log.Messages, message => message.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Library_folders_that_cannot_be_read_leave_the_sweep_to_the_recorded_swaps_and_say_so()
    {
        _swap.Files.Delete(SwapScenario.Original);
        _swap.Files.Put(SwapScenario.Backup, "OLD");
        await _swap.Journal.RecordAsync(new SwapJournalEntry(SwapScenario.JobId, SwapScenario.Original, SwapJournalState.Committing));
        _db.Execute("ALTER TABLE library_folders RENAME TO library_folders_hidden");
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.True(_log.Logged(LogLevel.Warning, "Could not read library folders for the startup sweep; it will only look at paths recorded on job rows."));
        Assert.Equal("OLD", _swap.Files.Read(SwapScenario.Original));
        Assert.DoesNotContain(_swap.Files.Operations, operation => operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_library_folder_that_breaks_the_folder_rules_is_named_in_a_warning_and_left_working()
    {
        var library = _db.AddLibrary(name: "Movies");
        _db.Execute("UPDATE libraries SET watched_folder = @home WHERE id = @id", ("@home", _db.Home), ("@id", library));
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.True(_log.Logged(
            LogLevel.Warning,
            "Workflow Movies's watched folder no longer meets Weir's folder rules: Weir can't use its own data folder as a watched folder. Choose a different folder. It keeps working until the workflow is next saved."));
        Assert.Equal(_db.Home, (string?)_db.Scalar("SELECT watched_folder FROM libraries WHERE id = @id", ("@id", library)));
        Assert.Equal(1, _log.Messages.Count(message => message.Message.Contains("no longer meets Weir's folder rules", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_library_folder_that_meets_the_folder_rules_draws_no_warning()
    {
        _db.AddLibrary(name: "Movies");
        var service = Service(_swap.Sweep);

        await StartAndWaitAsync(service);

        Assert.DoesNotContain(_log.Messages, message => message.Message.Contains("folder rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_recovery_logs_Critical_and_the_worker_lanes_still_start()
    {
        _db.InsertRawJob("interrupted", "processing.test.interrupted.v1", ProcessingJobStatus.Leased, leaseOwner: "dead-host-1-w0", leaseExpiresAt: "2999-01-01 00:00:00+00:00", attemptCount: 1);
        _db.Execute(
            "CREATE TRIGGER refuse_requeue BEFORE UPDATE ON jobs WHEN OLD.dedupe_key = 'interrupted' " +
            "BEGIN SELECT RAISE(ABORT, 'the database refused the requeue'); END");
        await _db.Store.EnqueueOrGetAsync("after", PassKind);
        var passRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(_swap.Sweep);
        var lanes = WorkerService(service, new DelegateHandler(PassKind, _ => passRan.TrySetResult()));

        await service.StartAsync(CancellationToken.None);
        await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(15));
        await lanes.StartAsync(CancellationToken.None);
        await passRan.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await lanes.StopAsync(CancellationToken.None);

        var critical = Assert.Single(_log.Messages, message => message.Level == LogLevel.Critical);
        Assert.Equal("Weir startup recovery failed; passes and scans will start without it once Weir is restarted.", critical.Message);
        Assert.Null(service.LastReport);
        Assert.Null(service.LastSwapSweepReport);
        Assert.Empty(_swap.Files.Operations);
    }

    [Fact]
    public async Task A_worker_lane_claims_nothing_until_the_sweep_has_finished_and_then_claims_the_job()
    {
        await AddLibraryFoldersAsync("/lib");
        await _db.Store.EnqueueOrGetAsync("queued", PassKind);
        using var held = HoldTheSweep();
        var passRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(_swap.Sweep);
        var lanes = WorkerService(service, new DelegateHandler(PassKind, _ => passRan.TrySetResult()));

        await Task.Run(() => service.StartAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
        await lanes.StartAsync(CancellationToken.None);
        await held.RunningAsync();

        // The lane waits on the gate, which the sweep still holds shut, so the job has not been claimed.
        Assert.False(passRan.Task.IsCompleted);
        Assert.Equal("pending", (string?)_db.Scalar("SELECT status FROM jobs WHERE dedupe_key = 'queued'"));
        Assert.Equal(0, _db.Count("SELECT attempt_count FROM jobs WHERE dedupe_key = 'queued'"));

        held.Release();
        await passRan.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await lanes.StopAsync(CancellationToken.None);
        Assert.NotNull(service.LastSwapSweepReport);
    }

    [Fact]
    public async Task The_upkeep_lane_claims_nothing_until_the_sweep_has_finished_and_then_takes_the_scan()
    {
        await AddLibraryFoldersAsync("/lib");
        await _db.Store.EnqueueOrGetAsync("queued", Weir.Core.LibraryMode.LibraryModeJobKinds.ScanKind);
        using var held = HoldTheSweep();
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(_swap.Sweep);
        var registry = new JobHandlerRegistry([new DelegateHandler(Weir.Core.LibraryMode.LibraryModeJobKinds.ScanKind, _ => scanned.TrySetResult())]);
        var lane = new UpkeepWorkerService(
            new ProcessingJobProcessor(_db.Store, registry, new SqliteActivityWriter(_db.Database), new NoUnhandledJobFailureRecorder(), new NoJobNotifications(), TimeProvider.System, NullLogger<ProcessingJobProcessor>.Instance),
            new WorkerHeartbeats(TimeProvider.System),
            new WorkerLoopTimings { IdleSleep = TimeSpan.FromMilliseconds(50), TickErrorBackoff = TimeSpan.FromMilliseconds(50), LeaseSeconds = 3600 },
            TimeProvider.System,
            NullLogger<UpkeepWorkerService>.Instance,
            registry,
            service);
        _db.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        await Task.Run(() => service.StartAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
        await lane.StartAsync(CancellationToken.None);
        await held.RunningAsync();

        Assert.False(scanned.Task.IsCompleted);
        Assert.Equal("pending", (string?)_db.Scalar("SELECT status FROM jobs WHERE dedupe_key = 'queued'"));

        held.Release();
        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await lane.StopAsync(CancellationToken.None);
    }

    private HeldSweep HoldTheSweep() => new(_swap.Files);

    private JobsStartupRecoveryService Service(SwapRecoverySweep? sweep) =>
        new(_db.Store, Options(), new LibrarySettingsStore(), new LibraryStore(), _db.Clock, _log, sweep);

    private async Task StartAndWaitAsync(JobsStartupRecoveryService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private WeirOptions Options() =>
        WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = _db.Home,
                ["WEIR_PROCESSING_WORKER_COUNT"] = "1",
            },
            OperatingSystem.IsWindows(),
            _db.Home,
            _db.Home));

    private async Task AddLibraryFoldersAsync(params string[] folders)
    {
        var library = _db.AddLibrary(name: "Library " + _db.Count("SELECT COUNT(*) FROM libraries").ToString(CultureInfo.InvariantCulture));
        var uow = await UnitOfWork.OpenAsync(_db.Database, CancellationToken.None);
        await using (uow)
        {
            await new LibrarySettingsStore().SetAsync(uow, library, LibrarySettings.Empty with { Folders = folders });
            await uow.CommitAsync();
        }
    }

    private ProcessingWorkerService WorkerService(JobsStartupRecoveryService recovery, IJobHandler handler)
    {
        var options = Options();
        var registry = new JobHandlerRegistry([handler]);
        var processor = new ProcessingJobProcessor(
            _db.Store,
            registry,
            new SqliteActivityWriter(_db.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            TimeProvider.System,
            NullLogger<ProcessingJobProcessor>.Instance);
        var timings = new WorkerLoopTimings
        {
            IdleSleep = TimeSpan.FromMilliseconds(50),
            TickErrorBackoff = TimeSpan.FromMilliseconds(50),
            ConcurrencyCacheTtl = TimeSpan.FromMilliseconds(100),
            LeaseSeconds = 3600,
        };
        _db.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        return new ProcessingWorkerService(
            processor, _db.Store, new WorkerHeartbeats(TimeProvider.System), options, timings, TimeProvider.System, NullLogger<ProcessingWorkerService>.Instance, registry, recovery);
    }

    /// <summary>Stops the sweep at its first folder walk until it is released, so a test can look at the service while it runs.</summary>
    private sealed class HeldSweep : IDisposable
    {
        private readonly ManualResetEventSlim _running = new();
        private readonly ManualResetEventSlim _released = new();

        public HeldSweep(FakeSwapFileSystem files)
        {
            files.OnStep = operation =>
            {
                if (operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal))
                {
                    _running.Set();
                    _released.Wait();
                }
            };
        }

        public Task RunningAsync() => Eventually.ThatAsync(() => _running.IsSet);

        public void Release() => _released.Set();

        public void Dispose()
        {
            _released.Set();
        }
    }
}
