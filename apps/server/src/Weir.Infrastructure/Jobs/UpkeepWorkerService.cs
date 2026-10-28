using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Core.Workers;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// The Upkeep worker lane (#717): <see cref="WorkLanes.UpkeepSlots"/> slots that run scans and maintenance
/// sweeps, on their own admission. A pass never waits behind a scan and a scan never waits behind a pass:
/// upkeep is exempt from "Files at once" and each library's pass limit, and blocked only from running twice
/// against the same library at once (<see cref="WorkAdmission.UpkeepBlockedLibraryIds"/>).
/// </summary>
public sealed class UpkeepWorkerService : BackgroundService
{
    private readonly ProcessingJobProcessor _processor;
    private readonly WorkerHeartbeats _heartbeats;
    private readonly WorkerLoopTimings _timings;
    private readonly TimeProvider _time;
    private readonly ILogger<UpkeepWorkerService> _logger;
    private readonly JobHandlerRegistry _handlers;
    private readonly JobsStartupRecoveryService _recovery;
    private readonly WorkerWakeSignals _wakeSignals;

    public UpkeepWorkerService(
        ProcessingJobProcessor processor,
        WorkerHeartbeats heartbeats,
        WorkerLoopTimings timings,
        TimeProvider time,
        ILogger<UpkeepWorkerService> logger,
        JobHandlerRegistry handlers,
        JobsStartupRecoveryService recovery,
        WorkerWakeSignals? wakeSignals = null)
    {
        _processor = processor;
        _heartbeats = heartbeats;
        _timings = timings;
        _time = time;
        _logger = logger;
        _handlers = handlers;
        _recovery = recovery;
        _wakeSignals = wakeSignals ?? new WorkerWakeSignals();
    }

    /// <summary>The <c>lease_owner</c> a slot claims as: <c>{hostname}-{pid}-u{index}</c>, apart from the files lane's <c>w</c>.</summary>
    public static string LeaseOwner(int workerIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{System.Net.Dns.GetHostName()}-{Environment.ProcessId}-u{workerIndex}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            // Startup recovery (#718) must finish looking at a library's folders before a scan can touch them.
            WaitingForRecovery?.Invoke();
            await _recovery.RecoveryCompleted.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var upkeepKinds = WorkLanes.UpkeepLaneKinds(_handlers);
        var slots = Enumerable.Range(0, WorkLanes.UpkeepSlots)
            .Select(index => Task.Run(() => RunSlotAsync(index, upkeepKinds, stoppingToken), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(slots).ConfigureAwait(false);
    }

    /// <summary>Test seam: called once the lane has started and is about to wait for startup recovery to finish.</summary>
    internal Action? WaitingForRecovery { get; init; }

    /// <summary>Test seam: called each time a slot has started waiting for work, with its wake taken and its poll timer running.</summary>
    internal Action? IdleWaiting { get; init; }

    /// <summary>One slot: repeatedly claim and run an upkeep job until stopping. Unlike the files lane, no setting gates how many slots take work.</summary>
    internal async Task RunSlotAsync(int workerIndex, ClaimableKinds upkeepKinds, CancellationToken stoppingToken)
    {
        var owner = LeaseOwner(workerIndex);
        _heartbeats.Started(WorkLanes.UpkeepHeartbeatModule, workerIndex);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _heartbeats.Beat(WorkLanes.UpkeepHeartbeatModule, workerIndex);
                // Taken before looking for work, so a job queued while this slot looks still wakes it.
                var wake = _wakeSignals.NextWake(WorkLane.Upkeep, workerIndex);
                JobProcessOutcome outcome;
                try
                {
                    outcome = await _processor.ProcessOneAsync(
                        owner,
                        _timings.LeaseSeconds,
                        lane: WorkLane.Upkeep,
                        kinds: upkeepKinds,
                        cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // The slot survives a crashed tick and tries again.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    _logger.LogError(exception, "Upkeep worker tick crashed worker_index={WorkerIndex}", workerIndex);
                    await Task.Delay(_timings.TickErrorBackoff, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (outcome == JobProcessOutcome.Idle)
                {
                    await WorkerIdleWait.RunAsync(WorkLanes.UpkeepHeartbeatModule, workerIndex, _heartbeats, wake, _timings, _time, stoppingToken, IdleWaiting).ConfigureAwait(false);
                }

                // Processed: spin straight on to drain the queue.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _heartbeats.Stopped(WorkLanes.UpkeepHeartbeatModule, workerIndex);
        }
    }
}
