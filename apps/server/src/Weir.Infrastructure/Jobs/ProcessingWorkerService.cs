using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Core.Workers;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// The Processing worker lane: <c>WEIR_PROCESSING_WORKER_COUNT</c> slots, of which the saved "Files at once"
/// value decides how many take work. Each slot reports heartbeats for readiness.
/// </summary>
public sealed class ProcessingWorkerService : BackgroundService
{
    public const string HeartbeatModule = "processing";

    private readonly ProcessingJobProcessor _processor;
    private readonly ProcessingJobStore _store;
    private readonly WorkerHeartbeats _heartbeats;
    private readonly WeirOptions _options;
    private readonly WorkerLoopTimings _timings;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessingWorkerService> _logger;
    private readonly WorkerWakeSignals _wakeSignals;
    private readonly JobHandlerRegistry _handlers;
    private readonly JobsStartupRecoveryService _recovery;

    public ProcessingWorkerService(
        ProcessingJobProcessor processor,
        ProcessingJobStore store,
        WorkerHeartbeats heartbeats,
        WeirOptions options,
        WorkerLoopTimings timings,
        TimeProvider time,
        ILogger<ProcessingWorkerService> logger,
        JobHandlerRegistry handlers,
        JobsStartupRecoveryService recovery,
        WorkerWakeSignals? wakeSignals = null)
    {
        _processor = processor;
        _store = store;
        _heartbeats = heartbeats;
        _options = options;
        _timings = timings;
        _time = time;
        _logger = logger;
        _handlers = handlers;
        _recovery = recovery;
        _wakeSignals = wakeSignals ?? new WorkerWakeSignals();
    }

    /// <summary>The <c>lease_owner</c> a slot claims as: <c>{hostname}-{pid}-w{index}</c>.</summary>
    public static string LeaseOwner(int workerIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{System.Net.Dns.GetHostName()}-{Environment.ProcessId}-w{workerIndex}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            // Startup recovery (#718) must finish looking at a library's folders before a pass can touch them.
            await _recovery.RecoveryCompleted.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // The shipped default is 10 slots; the saved files-at-once value gates how many are active (#329, #633).
        _logger.LogDebug(
            "Worker slot cap is {SlotCap}; the saved files-at-once setting gates how many are active.",
            _options.ProcessingWorkerCount);
        // Upkeep kinds (scans, maintenance sweeps) are left for the upkeep lane, which claims them without the
        // files-at-once and per-library limits this lane enforces (#717).
        var filesKinds = WorkLanes.FilesLaneKinds(_handlers);
        var slots = Enumerable.Range(0, _options.ProcessingWorkerCount)
            .Select(index => Task.Run(() => RunSlotAsync(index, filesKinds, stoppingToken), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(slots).ConfigureAwait(false);
    }

    /// <summary>How often a slot repeats the same "could not read the files-at-once setting" warning.</summary>
    internal static readonly TimeSpan SettingsReadFailureLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>Test seam: called each time a slot has started waiting for work, with its wake taken and its poll timer running.</summary>
    internal Action? IdleWaiting { get; init; }

    /// <summary>One slot: repeatedly process jobs until stopping.</summary>
    internal async Task RunSlotAsync(int workerIndex, ClaimableKinds filesKinds, CancellationToken stoppingToken)
    {
        var owner = LeaseOwner(workerIndex);
        _heartbeats.Started(HeartbeatModule, workerIndex);
        var cachedMaxConcurrent = OperatorSettingsRules.MaxFilesAtOnce;
        long cacheExpires = 0;
        var cacheValid = false;
        string? lastReadFailure = null;
        long? lastReadFailureLoggedAt = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _heartbeats.Beat(HeartbeatModule, workerIndex);
                if (!cacheValid || _time.GetTimestamp() >= cacheExpires)
                {
                    try
                    {
                        cachedMaxConcurrent = (int)OperatorSettingsRules.ClampMaxConcurrentFiles(await ReadMaxConcurrentFilesAsync(stoppingToken).ConfigureAwait(false));
                        cacheExpires = _time.GetTimestamp() + (long)(_timings.ConcurrencyCacheTtl.TotalSeconds * _time.TimestampFrequency);
                        cacheValid = true;
                    }
#pragma warning disable CA1031 // The slot keeps its previous value and tries again next pass; a settings read must not stop it.
                    catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        // The read is retried on every pass until it works, so the warning is written only when the
                        // problem changes or once a minute, never once per pass.
                        if (exception.Message != lastReadFailure || lastReadFailureLoggedAt is null ||
                            _time.GetElapsedTime(lastReadFailureLoggedAt.Value) >= SettingsReadFailureLogInterval)
                        {
                            _logger.LogWarning(
                                exception,
                                "Worker slot {WorkerIndex} could not read the files-at-once setting; keeping {FilesAtOnce}.",
                                workerIndex,
                                cachedMaxConcurrent);
                            lastReadFailure = exception.Message;
                            lastReadFailureLoggedAt = _time.GetTimestamp();
                        }
                    }
                }

                // Taken before looking for work, so a job queued while this slot looks still wakes it.
                var wake = _wakeSignals.NextWake(WorkLane.Files, workerIndex);
                if (workerIndex >= cachedMaxConcurrent)
                {
                    await WorkerIdleWait.RunAsync(HeartbeatModule, workerIndex, _heartbeats, wake, _timings, _time, stoppingToken, IdleWaiting).ConfigureAwait(false);
                    continue;
                }

                JobProcessOutcome outcome;
                try
                {
                    outcome = await _processor.ProcessOneAsync(owner, _timings.LeaseSeconds, kinds: filesKinds, cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // The worker survives a crashed tick and tries again.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    _logger.LogError(exception, "Worker tick crashed worker_index={WorkerIndex}", workerIndex);
                    await Task.Delay(_timings.TickErrorBackoff, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (outcome == JobProcessOutcome.Idle)
                {
                    await WorkerIdleWait.RunAsync(HeartbeatModule, workerIndex, _heartbeats, wake, _timings, _time, stoppingToken, IdleWaiting).ConfigureAwait(false);
                }

                // Processed: spin straight on to drain the queue.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _heartbeats.Stopped(HeartbeatModule, workerIndex);
        }
    }

    /// <summary>The saved "Files at once" value; the operator settings row defaults it to 1. A read, so it never takes the write lock.</summary>
    private Task<int> ReadMaxConcurrentFilesAsync(CancellationToken cancellationToken) =>
        _store.ReadAsync(
            (connection, transaction) =>
            {
                var value = ProcessingJobStore.Scalar(connection, transaction, "SELECT max_concurrent_files FROM operator_settings WHERE id = 1");
                return value is null or DBNull ? 1 : (int)Math.Clamp(Convert.ToInt64(value, CultureInfo.InvariantCulture), int.MinValue, int.MaxValue);
            },
            cancellationToken);
}
