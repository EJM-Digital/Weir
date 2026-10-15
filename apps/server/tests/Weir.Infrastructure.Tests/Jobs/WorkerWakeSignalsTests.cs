using Microsoft.Extensions.Time.Testing;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Idle worker slots are woken when a job they run is queued, and the poll stays as the fallback (#716, #1098).</summary>
public sealed class WorkerWakeSignalsTests : IDisposable
{
    private const string RemuxPass = "processing.file.remux_pass.v1";

    private readonly JobsTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_wake_taken_before_the_job_was_queued_is_completed_by_it()
    {
        var signals = new WorkerWakeSignals();
        var wake = signals.NextWake(WorkLane.Files, 0);

        signals.Wake(WorkLane.Files);

        Assert.True(wake.IsCompletedSuccessfully);
    }

    [Fact]
    public void Each_slot_waits_on_a_signal_of_its_own()
    {
        var signals = new WorkerWakeSignals();
        var first = signals.NextWake(WorkLane.Files, 0);
        var second = signals.NextWake(WorkLane.Files, 1);

        signals.Wake(WorkLane.Files);
        var afterwards = signals.NextWake(WorkLane.Files, 0);

        Assert.NotSame(first, second);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(afterwards.IsCompleted);
    }

    [Fact]
    public void The_same_slot_number_in_two_lanes_is_two_slots()
    {
        var signals = new WorkerWakeSignals();
        var files = signals.NextWake(WorkLane.Files, 0);
        var upkeep = signals.NextWake(WorkLane.Upkeep, 0);

        signals.Wake(WorkLane.Upkeep);

        Assert.True(upkeep.IsCompletedSuccessfully);
        Assert.False(files.IsCompleted);
    }

    [Fact]
    public void A_wake_with_nobody_waiting_is_not_kept_for_the_next_look()
    {
        var signals = new WorkerWakeSignals();
        signals.Wake(WorkLane.Upkeep);

        var next = signals.NextWake(WorkLane.Upkeep, 0);

        Assert.False(next.IsCompleted);
    }

    [Fact]
    public void A_burst_of_wakes_completes_a_waiting_slot_once_and_leaves_its_next_wait_open()
    {
        var signals = new WorkerWakeSignals();
        var wake = signals.NextWake(WorkLane.Files, 0);

        for (var i = 0; i < 100; i++)
        {
            signals.Wake(WorkLane.Files);
        }

        Assert.True(wake.IsCompletedSuccessfully);
        Assert.False(signals.NextWake(WorkLane.Files, 0).IsCompleted);
    }

    [Theory]
    [InlineData(RemuxPass, WorkLane.Files, WorkLane.Upkeep)]
    [InlineData(ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(LibraryModeJobKinds.ScanKind, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(PeriodicJobKinds.WorkTempStaleSweep, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(PeriodicJobKinds.UnclaimedHandbackCleanup, WorkLane.Upkeep, WorkLane.Files)]
    public void A_job_wakes_the_lane_that_runs_it_and_no_other(string jobKind, WorkLane runs, WorkLane other)
    {
        var signals = new WorkerWakeSignals();
        var runsIt = signals.NextWake(runs, 0);
        var notIt = signals.NextWake(other, 0);

        signals.WakeFor(jobKind);

        Assert.True(runsIt.IsCompletedSuccessfully);
        Assert.False(notIt.IsCompleted);
    }

    [Theory]
    [InlineData(RemuxPass, WorkLane.Files, WorkLane.Upkeep)]
    [InlineData(ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(LibraryModeJobKinds.ScanKind, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(PeriodicJobKinds.WorkTempStaleSweep, WorkLane.Upkeep, WorkLane.Files)]
    [InlineData(PeriodicJobKinds.UnclaimedHandbackCleanup, WorkLane.Upkeep, WorkLane.Files)]
    public async Task Queuing_a_job_wakes_the_slots_of_its_lane_only(string jobKind, WorkLane runs, WorkLane other)
    {
        var signals = new WorkerWakeSignals();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: signals);
        var runsIt = signals.NextWake(runs, 0);
        var notIt = signals.NextWake(other, 0);

        await store.EnqueueOrGetAsync("job:one", jobKind);

        Assert.True(runsIt.IsCompletedSuccessfully);
        Assert.False(notIt.IsCompleted);
    }

    [Fact]
    public async Task Finding_the_job_already_queued_wakes_nobody()
    {
        var signals = new WorkerWakeSignals();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: signals);
        await store.EnqueueOrGetAsync("file:one", RemuxPass);
        var wake = signals.NextWake(WorkLane.Files, 0);

        await store.EnqueueOrGetAsync("file:one", RemuxPass);

        Assert.False(wake.IsCompleted);
    }

    [Fact]
    public async Task Queuing_a_job_that_starts_later_wakes_nobody_until_it_is_due()
    {
        var signals = new WorkerWakeSignals();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: signals);
        var wake = signals.NextWake(WorkLane.Files, 0);

        await store.EnqueueOrGetAsync("file:later", RemuxPass, notBefore: _db.Clock.GetUtcNow().AddMinutes(10));

        Assert.False(wake.IsCompleted);
    }

    [Fact]
    public async Task Queuing_a_job_that_is_already_due_wakes_the_slots()
    {
        var signals = new WorkerWakeSignals();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: signals);
        var wake = signals.NextWake(WorkLane.Files, 0);

        await store.EnqueueOrGetAsync("file:due", RemuxPass, notBefore: _db.Clock.GetUtcNow().AddMinutes(-1));

        Assert.True(wake.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Queuing_the_next_run_of_a_repeating_job_wakes_the_slots()
    {
        var signals = new WorkerWakeSignals();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, wakeSignals: signals);
        var wake = signals.NextWake(WorkLane.Upkeep, 0);

        await store.EnqueueNextRunAsync("sweep", PeriodicJobKinds.WorkTempStaleSweep);

        Assert.True(wake.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_idle_slot_stops_waiting_as_soon_as_it_is_woken()
    {
        var time = new FakeTimeProvider();
        var signals = new WorkerWakeSignals();
        var waiting = WorkerIdleWait.WaitForWorkAsync(signals.NextWake(WorkLane.Upkeep, 0), TimeSpan.FromMinutes(5), time, CancellationToken.None);

        signals.Wake(WorkLane.Upkeep);
        await waiting;

        Assert.Equal(TimeSpan.Zero, time.GetUtcNow() - time.Start);
    }

    [Fact]
    public async Task An_idle_slot_looks_again_when_the_poll_comes_round_without_a_wake()
    {
        var time = new FakeTimeProvider();
        var signals = new WorkerWakeSignals();
        var waiting = WorkerIdleWait.WaitForWorkAsync(signals.NextWake(WorkLane.Files, 0), TimeSpan.FromSeconds(5), time, CancellationToken.None);
        var finishedEarly = waiting.IsCompleted;

        time.Advance(TimeSpan.FromSeconds(5));
        await waiting;

        Assert.False(finishedEarly);
    }
}
