using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Jobs;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A worker step when recording its own outcome fails: each failure is logged, the step ends, and the worker carries on.</summary>
public sealed class ProcessingJobProcessorFaultTests : IDisposable
{
    private const string Kind = "processing.test.ok.v1";

    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;

    private readonly JobsTestDatabase _db = new();
    private readonly ListLogger<ProcessingJobProcessor> _log = new();

    public void Dispose() => _db.Dispose();

    private ProcessingJobProcessor Processor(
        Action<JobWorkContext> handler,
        Func<long, string, DateTimeOffset, Task<bool>>? complete = null,
        IUnhandledJobFailureRecorder? recorder = null) =>
        new(
            _db.Store,
            new JobHandlerRegistry([new DelegateHandler(Kind, handler)]),
            new SqliteActivityWriter(_db.Database),
            recorder ?? new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _db.Clock,
            _log)
        {
            CompleteOverride = complete,
        };

    private void LoseTheJobsTable() => _db.Execute("ALTER TABLE jobs RENAME TO jobs_unavailable");

    [Fact]
    public async Task A_job_that_cannot_be_recorded_as_finished_and_is_no_longer_ours_is_warned_about_not_failed_again()
    {
        await _db.Store.EnqueueOrGetAsync("finished-elsewhere", Kind, maxAttempts: 3);
        var processor = Processor(_ => { }, complete: async (id, owner, now) =>
        {
            Assert.True(await _db.Store.CompleteClaimedAsync(id, owner, now));
            return false;
        });

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Terminalization recovery did not apply", StringComparison.Ordinal));
        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task A_job_whose_completion_and_recovery_both_fail_is_logged_and_the_step_still_ends()
    {
        await _db.Store.EnqueueOrGetAsync("both-fail", Kind, maxAttempts: 3);
        var processor = Processor(_ => { }, complete: (_, _, _) =>
        {
            LoseTheJobsTable();
            throw new InvalidOperationException("the queue is gone");
        });

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("fail_leased_processing_job_after_complete_failure failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_job_that_cannot_be_marked_failed_is_logged_and_the_step_still_ends()
    {
        await _db.Store.EnqueueOrGetAsync("cannot-fail", Kind, maxAttempts: 3);
        var processor = Processor(_ =>
        {
            LoseTheJobsTable();
            throw new InvalidOperationException("the handler failed");
        });

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("fail_claimed_processing_job failed after handler error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_whose_diagnostics_cannot_be_kept_is_still_marked_failed()
    {
        await _db.Store.EnqueueOrGetAsync("no-diagnostics", Kind, maxAttempts: 1);
        var processor = Processor(_ => throw new InvalidOperationException("the handler failed"), recorder: new ThrowingRecorder());

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Failure diagnostics could not be persisted", StringComparison.Ordinal));
        Assert.Equal(ProcessingJobStatus.Failed, (await _db.Store.GetAsync(1))!.Status);
    }

    [Theory]
    [InlineData("""{"run_id":"abc"}""", true)]
    [InlineData("""{"run_id":"   "}""", false)]
    [InlineData("""{"run_id":42}""", true)]
    [InlineData("""{"run_id":4.5}""", false)]
    [InlineData("""{"run_id":true}""", false)]
    [InlineData("""{"run_id":null}""", false)]
    [InlineData("""{"other":1}""", false)]
    public void A_run_is_named_in_the_activity_only_when_its_id_is_a_word_or_a_whole_number(string payload, bool named)
    {
        var detail = new WireObject();

        ProcessingJobProcessor.AddProvenance(detail, JsonDocument.Parse(payload).RootElement);

        Assert.Equal(named, detail.Get("run_id") is not null);
    }

    [Fact]
    public void A_job_with_no_payload_names_no_trigger_or_run()
    {
        var detail = new WireObject();

        ProcessingJobProcessor.AddProvenance(detail, null);

        Assert.Null(detail.Get("run_id"));
        Assert.Null(detail.Get("trigger"));
    }

    private sealed class ThrowingRecorder : IUnhandledJobFailureRecorder
    {
        public Task<bool?> RecordAsync(UnhandledJobFailure failure, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the file state could not be written");
    }
}
