using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// #540 item 1: nothing renewed a lease, so a handler running longer than its lease could be claimed by
/// a second worker while the first was still running it (two ffmpeg processes on the same file). A
/// heartbeat now renews the lease roughly every lease/3 while the handler runs. Proven here with a real
/// SQLite file, a genuinely long-running fake handler and a second worker trying to claim in parallel, on
/// a shared <see cref="FakeTimeProvider"/> so the heartbeat's own timer, the claim's lease math and the
/// test's assertions all agree on "now" instead of racing the real clock.
/// </summary>
[Collection(SerialTestGroup.Name)]
public sealed class LeaseRenewalTests : IDisposable
{
    private const string Kind = "processing.test.long_running.v1";
    private readonly JobsTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_job_running_longer_than_its_lease_is_never_claimed_by_another_worker()
    {
        await _db.Store.EnqueueOrGetAsync("long-running", Kind, maxAttempts: 3);

        var intruderRuns = 0;
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningHandler = new DelegateHandler(Kind, async _ =>
        {
            claimed.TrySetResult();
            await release.Task;
        });
        var intruderHandler = new DelegateHandler(Kind, _ => Interlocked.Increment(ref intruderRuns));

        using var heartbeatWaiting = new SemaphoreSlim(0);
        var runningProcessor = Processor(runningHandler, heartbeatWaiting);
        var intruderProcessor = Processor(intruderHandler);

        // A one-second lease; the handler outlives it by design, so only a working renewal keeps it safe.
        const int leaseSeconds = 1;
        var runningTask = runningProcessor.ProcessOneAsync("worker-running", leaseSeconds, _time.GetUtcNow());
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        // Step past the original lease in heartbeat-sized increments (lease/3), so each step fires one
        // renewal, and try to claim with the intruder at the same "now" after each one. Waiting for the
        // heartbeat to be waiting again (rather than sleeping) means this step's renewal has been written
        // and the next tick's timer is set before the intruder reads the lease or the clock moves on. The
        // intruder must only ever see a lease that has been pushed out ahead of "now" - never expired.
        var intruderOutcomes = new List<JobProcessOutcome>();
        for (var step = 0; step < 6; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(leaseSeconds / 3.0));
            await WaitForHeartbeatAsync(heartbeatWaiting);
            intruderOutcomes.Add(await intruderProcessor.ProcessOneAsync("worker-intruder", leaseSeconds, _time.GetUtcNow()));
        }

        release.TrySetResult();
        var outcome = await runningTask;

        Assert.Equal(JobProcessOutcome.Processed, outcome);
        Assert.Equal(0, intruderRuns);
        Assert.All(intruderOutcomes, o => Assert.Equal(JobProcessOutcome.Idle, o));
        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task The_lease_is_visibly_renewed_ahead_of_its_original_expiry()
    {
        await _db.Store.EnqueueOrGetAsync("renewed", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(Kind, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var processor = Processor(handler, heartbeatWaiting);

        const int leaseSeconds = 1;
        var runTask = processor.ProcessOneAsync("worker", leaseSeconds, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);
        var originalExpiry = (await _db.Store.GetAsync(1))!.LeaseExpiresAt;

        // Step forward in heartbeat-sized increments (lease/3), same as the intruder test: renewing the
        // lease only ever succeeds while it hasn't already expired, so a single jump straight past the
        // full lease would (correctly) find nothing left to renew instead of proving the heartbeat works.
        // After each step, wait until the heartbeat is waiting again: its renewal for that step is written
        // by then, and its next timer is set before the clock moves again.
        for (var step = 0; step < 3; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(leaseSeconds / 3.0));
            await WaitForHeartbeatAsync(heartbeatWaiting);
        }

        var renewedExpiry = (await _db.Store.GetAsync(1))!.LeaseExpiresAt;
        Assert.True(renewedExpiry > originalExpiry, $"expected {renewedExpiry} > {originalExpiry}");

        release.TrySetResult();
        Assert.Equal(JobProcessOutcome.Processed, await runTask);
    }

    [Fact]
    public async Task A_renewal_that_meets_a_busy_database_is_tried_again_at_the_next_tick()
    {
        await _db.Store.EnqueueOrGetAsync("busy", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(Kind, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        var attempts = new List<DateTimeOffset>();
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var processor = Processor(handler, heartbeatWaiting, renew: (jobId, owner, expiry) =>
        {
            attempts.Add(expiry);
            return attempts.Count == 1
                ? throw new SqliteException("database is locked", 5)
                : _db.Store.RenewLeaseAsync(jobId, owner, expiry, _time.GetUtcNow());
        });

        const int leaseSeconds = 3;
        var runTask = processor.ProcessOneAsync("worker", leaseSeconds, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        for (var step = 0; step < 3; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(leaseSeconds / 3.0));
            await WaitForHeartbeatAsync(heartbeatWaiting);
        }

        Assert.Equal(3, attempts.Count);
        release.TrySetResult();
        Assert.Equal(JobProcessOutcome.Processed, await runTask);
    }

    [Fact]
    public async Task A_job_whose_lease_runs_out_while_every_renewal_meets_a_busy_database_is_stopped_and_left_for_recovery()
    {
        await _db.Store.EnqueueOrGetAsync("outlived", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UntilCancelledHandler(Kind, started);
        var attempts = 0;
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) =>
        {
            attempts++;
            throw new SqliteException("database is locked", 5);
        });

        const int leaseSeconds = 3;
        var runTask = processor.ProcessOneAsync("worker", leaseSeconds, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        // Two renewals fail inside the lease and the handler keeps running; the lease ends at the third second.
        for (var step = 0; step < 2; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await WaitForHeartbeatAsync(heartbeatWaiting);
        }

        Assert.False(handler.Cancelled);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(handler.Cancelled);
        Assert.True(attempts >= 2);
        Assert.Equal(ProcessingJobStatus.Leased, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task A_renewal_that_fails_for_a_reason_other_than_a_busy_database_does_not_stop_the_job()
    {
        await _db.Store.EnqueueOrGetAsync("broken", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(Kind, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var log = new MessageLog();
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) => throw new SqliteException("disk I/O error", 10), logger: log);

        const int leaseSeconds = 3;
        var runTask = processor.ProcessOneAsync("worker", leaseSeconds, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        // The renewal loop ends on the first failure, as it always has, and the lease runs out without the handler being stopped.
        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.ThatAsync(() => log.Messages.Any(message => message.StartsWith("Lease renewal loop crashed", StringComparison.Ordinal)));
        _time.Advance(TimeSpan.FromSeconds(5));
        release.TrySetResult();

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private const string AsleepWhile = "Weir was not running, or the computer was asleep, while it worked on";

    /// <summary>Weir runs for <paramref name="time"/>, a second at a time, so the pulse has no silence.</summary>
    private void WeirRunsFor(ProcessPulse pulse, TimeSpan time)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += TimeSpan.FromSeconds(1))
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            pulse.Note();
        }
    }

    [Fact]
    public async Task A_job_whose_lease_ran_out_while_the_computer_slept_is_told_as_information_and_left_for_recovery()
    {
        await _db.Store.EnqueueOrGetAsync("slept", Kind, payloadJson: "{\"relative_media_path\":\"Heat (1995)/Heat.mkv\"}", maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UntilCancelledHandler(Kind, started);
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var log = new MessageLog();
        var pulse = new ProcessPulse(_time);
        var renewalNotBack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) => renewalNotBack.Task, logger: log, pulse: pulse);

        var runTask = processor.ProcessOneAsync("worker", 3, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        _time.Advance(TimeSpan.FromHours(7));
        await Eventually.ThatAsync(() => handler.Cancelled);
        renewalNotBack.SetException(new OperationCanceledException());

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(log.Said(LogLevel.Warning));
        Assert.Equal(
            $"{AsleepWhile} Heat.mkv; the job was stopped and will be picked up again. job_id=1",
            Assert.Single(log.Said(LogLevel.Information)));
        Assert.Equal(ProcessingJobStatus.Leased, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task A_job_whose_lease_ran_out_while_weir_ran_is_still_a_warning()
    {
        await _db.Store.EnqueueOrGetAsync("outlived", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UntilCancelledHandler(Kind, started);
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var log = new MessageLog();
        var pulse = new ProcessPulse(_time);
        var renewalNotBack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) => renewalNotBack.Task, logger: log, pulse: pulse);

        var runTask = processor.ProcessOneAsync("worker", 3, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        WeirRunsFor(pulse, TimeSpan.FromSeconds(4));
        await Eventually.ThatAsync(() => handler.Cancelled);
        renewalNotBack.SetException(new OperationCanceledException());

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(log.Said(LogLevel.Information));
        Assert.Contains("ran out before it could be renewed", Assert.Single(log.Said(LogLevel.Warning), message => message.StartsWith("The lease on job", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_renewal_that_finds_the_lease_gone_after_the_computer_slept_is_information()
    {
        await _db.Store.EnqueueOrGetAsync("woke", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(Kind, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var log = new MessageLog();
        var pulse = new ProcessPulse(_time);
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) => Task.FromResult(false), logger: log, pulse: pulse);

        var runTask = processor.ProcessOneAsync("worker", 30, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        _time.Advance(TimeSpan.FromSeconds(10));
        await Eventually.ThatAsync(() => log.Said(LogLevel.Information).Any());
        release.TrySetResult();

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(log.Said(LogLevel.Warning));
        Assert.Equal($"{AsleepWhile} a background job; the job's claim ran out in the meantime. job_id=1", Assert.Single(log.Said(LogLevel.Information)));
    }

    [Fact]
    public async Task A_renewal_that_finds_the_lease_gone_while_weir_ran_is_still_a_warning()
    {
        await _db.Store.EnqueueOrGetAsync("taken", Kind, maxAttempts: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(Kind, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        using var heartbeatWaiting = new SemaphoreSlim(0);
        var log = new MessageLog();
        var pulse = new ProcessPulse(_time);
        var processor = Processor(handler, heartbeatWaiting, renew: (_, _, _) => Task.FromResult(false), logger: log, pulse: pulse);

        var runTask = processor.ProcessOneAsync("worker", 30, _time.GetUtcNow());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHeartbeatAsync(heartbeatWaiting);

        WeirRunsFor(pulse, TimeSpan.FromSeconds(10));
        await Eventually.ThatAsync(() => log.Said(LogLevel.Warning).Any());
        release.TrySetResult();

        Assert.Equal(JobProcessOutcome.Processed, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(log.Said(LogLevel.Information));
        Assert.Contains("no longer held", Assert.Single(log.Said(LogLevel.Warning)));
    }

    private sealed class MessageLog : ILogger<ProcessingJobProcessor>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IEnumerable<string> Messages => _entries.Select(entry => entry.Message);

        public IEnumerable<string> Said(LogLevel level) => _entries.Where(entry => entry.Level == level).Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class UntilCancelledHandler(string jobKind, TaskCompletionSource started) : IJobHandler
    {
        public string JobKind => jobKind;

        public bool Cancelled { get; private set; }

        public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Wait for the heartbeat's next "waiting for my next tick" signal. A missing signal means the heartbeat
    /// stopped renewing (it lost the lease, or crashed), which is exactly what these tests must catch.
    /// </summary>
    private static async Task WaitForHeartbeatAsync(SemaphoreSlim heartbeatWaiting) =>
        Assert.True(
            await heartbeatWaiting.WaitAsync(TimeSpan.FromSeconds(5)),
            "The lease-renewal heartbeat did not renew and wait for its next tick in time.");

    private ProcessingJobProcessor Processor(IJobHandler handler, SemaphoreSlim? heartbeatWaiting = null, Func<long, string, DateTimeOffset, Task<bool>>? renew = null, ILogger<ProcessingJobProcessor>? logger = null, ProcessPulse? pulse = null) =>
        new(
            new ProcessingJobStore(new SqliteDatabase(_db.DbPath, pooling: false), _time),
            new JobHandlerRegistry([handler]),
            new RecordingActivityWriter(),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _time,
            logger ?? NullLogger<ProcessingJobProcessor>.Instance,
            pulse: pulse)
        {
            LeaseRenewalWaiting = heartbeatWaiting is null ? null : () => heartbeatWaiting.Release(),
            RenewOverride = renew,
        };
}
