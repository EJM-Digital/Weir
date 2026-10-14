using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>The job store: enqueue dedupe, atomic claim, lease, complete, fail.</summary>
public sealed class ProcessingJobStoreTests : IDisposable
{
    private const string Kind = "processing.test.harness.v1";
    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;
    private readonly JobsTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Enqueue_duplicate_dedupe_key_returns_same_row()
    {
        var a = await _db.Store.EnqueueOrGetAsync("k1", Kind);
        var b = await _db.Store.EnqueueOrGetAsync("k1", Kind);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_job_that_runs_again_queues_a_new_run_once_the_last_one_has_finished()
    {
        var first = await _db.Store.EnqueueNextRunAsync("again", Kind);
        Assert.Equal(first.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!.Id);
        Assert.True(await _db.Store.CompleteClaimedAsync(first.Id, "w", T0));

        var second = await _db.Store.EnqueueNextRunAsync("again", Kind);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ProcessingJobStatus.Pending, second.Status);
        Assert.Equal("again", second.DedupeKey);
        Assert.Equal($"again:ran:{first.Id}", _db.Scalar($"SELECT dedupe_key FROM jobs WHERE id = {first.Id}"));
        Assert.Equal(ProcessingJobStatus.Completed, _db.Scalar($"SELECT status FROM jobs WHERE id = {first.Id}"));
    }

    [Fact]
    public async Task A_job_that_runs_again_queues_a_new_run_after_a_failed_one_too()
    {
        var first = await _db.Store.EnqueueNextRunAsync("again", Kind);
        _db.Execute("UPDATE jobs SET status = @status WHERE id = @id", ("@status", ProcessingJobStatus.Failed), ("@id", first.Id));

        var second = await _db.Store.EnqueueNextRunAsync("again", Kind);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_job_that_runs_again_never_has_two_runs_waiting_or_running()
    {
        var waiting = await _db.Store.EnqueueNextRunAsync("again", Kind);
        Assert.Equal(waiting.Id, (await _db.Store.EnqueueNextRunAsync("again", Kind)).Id);

        await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0);
        Assert.Equal(waiting.Id, (await _db.Store.EnqueueNextRunAsync("again", Kind)).Id);

        Assert.Equal(1, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Enqueue_clamps_attempts_and_cost_and_writes_a_fresh_pending_row()
    {
        var job = await _db.Store.EnqueueOrGetAsync("shape", Kind, "{\"a\":1}", maxAttempts: 0, runnerCost: -4, priority: 7);

        Assert.Equal(ProcessingJobStatus.Pending, job.Status);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(0, job.RunnerCost);
        Assert.Equal(7, job.Priority);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.NotBefore);
        Assert.Equal("{\"a\":1}", job.PayloadJson);
    }

    [Fact]
    public async Task A_job_queued_to_start_later_is_not_claimed_before_then()
    {
        var job = await _db.Store.EnqueueOrGetAsync("later", Kind, notBefore: T0.AddSeconds(45));

        Assert.Equal(T0.AddSeconds(45), job.NotBefore);
        Assert.Equal("2026-04-10 12:00:45.000000", _db.Scalar("SELECT not_before FROM jobs"));
        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(44)));
        Assert.Equal(job.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(45)))!.Id);
    }

    [Fact]
    public async Task Concurrent_enqueue_same_dedupe_key_single_row()
    {
        using var barrier = new Barrier(8);
        var ids = await OwnThreads.RunAllAsync(8, async _ =>
        {
            barrier.SignalAndWait();
            return (await _db.Store.EnqueueOrGetAsync("race", Kind)).Id;
        });

        Assert.Single(ids.Distinct());
        Assert.Equal(1, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Enqueue_refuses_retired_and_unprefixed_kinds()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Store.EnqueueOrGetAsync("t", "pruner.candidate_removal.preview.v1"));
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Store.EnqueueOrGetAsync("u", "bare.kind"));
        Assert.Equal(0, _db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Second_claim_does_not_return_same_job_when_first_holds_lease()
    {
        await _db.Store.EnqueueOrGetAsync("solo", Kind);

        Assert.NotNull(await _db.Store.ClaimNextAsync("w1", T0.AddHours(1), T0));
        Assert.Null(await _db.Store.ClaimNextAsync("w2", T0.AddHours(1), T0));
    }

    [Fact]
    public async Task Expired_lease_can_be_reclaimed()
    {
        await _db.Store.EnqueueOrGetAsync("reclaim", Kind);
        var first = await _db.Store.ClaimNextAsync("w1", T0.AddSeconds(30), T0);
        Assert.NotNull(first);
        Assert.Equal(1, first.AttemptCount);

        var second = await _db.Store.ClaimNextAsync("w2", T0.AddHours(1), T0.AddMinutes(5));

        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("w2", second.LeaseOwner);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task Claim_writes_the_stored_timestamp_text_formats()
    {
        await _db.Store.EnqueueOrGetAsync("text", Kind);
        await _db.Store.ClaimNextAsync("w", T0.AddHours(1).AddTicks(1_234_567), T0);

        Assert.Equal("2026-04-10 13:00:00.123456+00:00", _db.Scalar("SELECT lease_expires_at FROM jobs"));
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$", (string)_db.Scalar("SELECT updated_at FROM jobs")!);
    }

    [Fact]
    public async Task Complete_only_succeeds_for_owning_lease()
    {
        await _db.Store.EnqueueOrGetAsync("done", Kind);
        var job = (await _db.Store.ClaimNextAsync("good", T0.AddHours(1), T0))!;

        Assert.False(await _db.Store.CompleteClaimedAsync(job.Id, "evil", T0));
        Assert.True(await _db.Store.CompleteClaimedAsync(job.Id, "good", T0));

        var row = (await _db.Store.GetAsync(job.Id))!;
        Assert.Equal(ProcessingJobStatus.Completed, row.Status);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Complete_fails_when_lease_expired_or_row_not_leased()
    {
        await _db.Store.EnqueueOrGetAsync("late", Kind);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddSeconds(10), T0))!;

        Assert.False(await _db.Store.CompleteClaimedAsync(job.Id, "w", T0.AddMinutes(30)));
        Assert.False(await _db.Store.CompleteClaimedAsync(9999, "w", T0));
        Assert.True(await _db.Store.CompleteClaimedAsync(job.Id, "w", T0.AddSeconds(10)));
        Assert.False(await _db.Store.CompleteClaimedAsync(job.Id, "w", T0));
    }

    [Fact]
    public async Task Fail_requeues_until_max_attempts_then_failed()
    {
        await _db.Store.EnqueueOrGetAsync("retry", Kind, maxAttempts: 2);
        long id = 0;
        for (var round = 0; round < 2; round++)
        {
            // Advance the clock enough to clear the not_before backoff of the previous failure.
            var now = T0.AddSeconds(round * 60);
            var job = await _db.Store.ClaimNextAsync("w", T0.AddHours(1), now);
            Assert.NotNull(job);
            Assert.Equal(round + 1, job.AttemptCount);
            id = job.Id;
            Assert.True(await _db.Store.FailClaimedAsync(id, "w", $"e{round}", now));
        }

        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0));
        var row = (await _db.Store.GetAsync(id))!;
        Assert.Equal(ProcessingJobStatus.Failed, row.Status);
        Assert.Equal("e1", row.LastError);
        Assert.Null(row.NotBefore);
    }

    [Fact]
    public async Task A_failed_attempt_waits_out_its_backoff_before_it_can_be_claimed_again()
    {
        await _db.Store.EnqueueOrGetAsync("backoff", Kind, maxAttempts: 5);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!;
        Assert.True(await _db.Store.FailClaimedAsync(job.Id, "w", "boom", T0));

        Assert.Equal("2026-04-10 12:00:30.000000", _db.Scalar("SELECT not_before FROM jobs"));
        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(29)));
        Assert.NotNull(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(30).AddTicks(10)));
        Assert.True(await _db.Store.FailClaimedAsync(job.Id, "w", "boom", T0.AddSeconds(31)));
        Assert.Equal("2026-04-10 12:01:31.000000", _db.Scalar("SELECT not_before FROM jobs"));
    }

    [Theory]
    [InlineData(0, true)] // now has zero microseconds: the sqlite3 adapter omits the fraction entirely.
    [InlineData(1, true)] // now has a fraction: the old text comparison already got this one right.
    public async Task A_job_is_claimable_at_exactly_its_not_before_second_in_either_now_format(int extraTicks, bool expectClaimable)
    {
        // #540 item 2: not_before is always written with an explicit fraction and no offset
        // ("...30.000000"), while `now` goes through the sqlite3 adapter, which omits a zero fraction
        // and adds an offset ("...30+00:00"). A text comparison sorts '+' before '.', so the exact
        // not_before second was missed whenever `now`'s microseconds happened to be zero. Comparing by
        // julianday() instead reads both shapes as the same instant.
        await _db.Store.EnqueueOrGetAsync("exact", Kind, maxAttempts: 5);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddSeconds(30), T0))!;
        Assert.True(await _db.Store.FailClaimedAsync(job.Id, "w", "boom", T0));
        Assert.Equal("2026-04-10 12:00:30.000000", _db.Scalar("SELECT not_before FROM jobs"));

        var now = T0.AddSeconds(30).AddTicks(extraTicks);
        var claimed = await _db.Store.ClaimNextAsync("w2", T0.AddHours(1), now);
        Assert.Equal(expectClaimable, claimed is not null);
    }

    [Fact]
    public async Task A_legacy_six_digit_not_before_row_still_compares_correctly()
    {
        // A raw row in the legacy timestamp shape earlier releases wrote (no offset, always six fraction
        // digits) — not a row this store itself produced — claimable at its exact second.
        _db.InsertRawJob("legacy-written", Kind, maxAttempts: 5);
        _db.Execute("UPDATE jobs SET not_before = '2026-04-10 12:00:30.000000' WHERE dedupe_key = 'legacy-written'");

        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(29)));
        Assert.NotNull(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0.AddSeconds(30)));
    }

    [Fact]
    public async Task Fail_refuses_a_wrong_owner_or_expired_lease()
    {
        await _db.Store.EnqueueOrGetAsync("guard", Kind);
        var job = (await _db.Store.ClaimNextAsync("good", T0.AddSeconds(5), T0))!;

        Assert.False(await _db.Store.FailClaimedAsync(job.Id, "evil", "x", T0));
        Assert.False(await _db.Store.FailClaimedAsync(job.Id, "good", "x", T0.AddSeconds(6)));
        Assert.Equal(ProcessingJobStatus.Leased, (await _db.Store.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Renew_lease_extends_expiry_only_for_the_owning_unexpired_lease()
    {
        // #540 item 1: the store-level half of lease renewal (the processor's heartbeat calls this).
        await _db.Store.EnqueueOrGetAsync("renew", Kind);
        var job = (await _db.Store.ClaimNextAsync("good", T0.AddSeconds(30), T0))!;

        Assert.True(await _db.Store.RenewLeaseAsync(job.Id, "good", T0.AddSeconds(60), T0.AddSeconds(10)));
        Assert.Equal(T0.AddSeconds(60), (await _db.Store.GetAsync(job.Id))!.LeaseExpiresAt);

        // Wrong owner, and a lease that has already lapsed: neither renews.
        Assert.False(await _db.Store.RenewLeaseAsync(job.Id, "evil", T0.AddSeconds(90), T0.AddSeconds(20)));
        Assert.False(await _db.Store.RenewLeaseAsync(job.Id, "good", T0.AddSeconds(200), T0.AddSeconds(61)));
        Assert.Equal(T0.AddSeconds(60), (await _db.Store.GetAsync(job.Id))!.LeaseExpiresAt);

        // A completed row holds no lease to renew.
        Assert.True(await _db.Store.CompleteClaimedAsync(job.Id, "good", T0.AddSeconds(15)));
        Assert.False(await _db.Store.RenewLeaseAsync(job.Id, "good", T0.AddSeconds(400), T0.AddSeconds(16)));
    }

    [Fact]
    public async Task Fail_leased_after_complete_failure_sets_handler_ok_finalize_failed()
    {
        await _db.Store.EnqueueOrGetAsync("term-fail", Kind);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!;
        Assert.Equal(1, job.AttemptCount);

        Assert.True(await _db.Store.FailLeasedAfterCompleteFailureAsync(job.Id, "w", "processing_terminalization_failure: synthetic", T0));

        var row = (await _db.Store.GetAsync(job.Id))!;
        Assert.Equal(ProcessingJobStatus.HandlerOkFinalizeFailed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.LeaseOwner);
        Assert.Contains("synthetic", row.LastError, StringComparison.Ordinal);
        Assert.Null(await _db.Store.ClaimNextAsync("w2", T0.AddHours(1), T0));
    }

    [Fact]
    public async Task Handler_ok_finalize_failed_row_is_not_claimable()
    {
        await _db.Store.EnqueueOrGetAsync("finalize-stuck", Kind);
        _db.Execute("UPDATE jobs SET status = 'handler_ok_finalize_failed', lease_owner = NULL, lease_expires_at = NULL WHERE id = 1");

        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0));
    }

    [Fact]
    public async Task Fail_leased_after_complete_failure_rejects_wrong_owner_and_bounds_the_error()
    {
        await _db.Store.EnqueueOrGetAsync("term-owner", Kind);
        var job = (await _db.Store.ClaimNextAsync("good", T0.AddHours(1), T0))!;

        Assert.False(await _db.Store.FailLeasedAfterCompleteFailureAsync(job.Id, "evil", "x", T0));
        Assert.True(await _db.Store.FailLeasedAfterCompleteFailureAsync(job.Id, "good", new string('e', 12_000), T0));
        Assert.Equal(10_000, (await _db.Store.GetAsync(job.Id))!.LastError!.Length);
    }

    [Fact]
    public async Task Recover_handler_ok_finalize_failed_to_completed()
    {
        await _db.Store.EnqueueOrGetAsync("recover-ok", Kind);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!;
        await _db.Store.FailLeasedAfterCompleteFailureAsync(job.Id, "w", "processing_terminalization_failure: synthetic", T0);

        Assert.Equal(JobActionOutcome.Ok, await _db.Store.RecoverHandlerOkFinalizeFailedToCompletedAsync(job.Id, "tester", T0));

        var row = (await _db.Store.GetAsync(job.Id))!;
        Assert.Equal(ProcessingJobStatus.Completed, row.Status);
        Assert.Equal(
            "processing_terminalization_failure: synthetic\n--- manual_recover_finalize_failure: marked completed at 2026-04-10T12:00:00Z by tester " +
            "(handler was not re-run; row was handler_ok_finalize_failed).",
            row.LastError);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task Recover_finalize_rejects_wrong_status_and_missing_job()
    {
        await _db.Store.EnqueueOrGetAsync("recover-wrong", Kind);
        var job = (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!;

        Assert.Equal(JobActionOutcome.WrongStatus, await _db.Store.RecoverHandlerOkFinalizeFailedToCompletedAsync(job.Id, "x", T0));
        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.RecoverHandlerOkFinalizeFailedToCompletedAsync(99999, "x"));
    }

    [Fact]
    public async Task Two_workers_claim_different_jobs()
    {
        await _db.Store.EnqueueOrGetAsync("a", Kind);
        await _db.Store.EnqueueOrGetAsync("b", Kind);
        using var barrier = new Barrier(2);

        var claims = await OwnThreads.RunAllAsync(2, async index =>
        {
            barrier.SignalAndWait();
            return (await _db.Store.ClaimNextAsync($"t{index}", T0.AddHours(1), T0))?.Id;
        });

        Assert.Equal(2, claims.Where(id => id is not null).Distinct().Count());
    }

    [Fact]
    public async Task Cancel_pending_tombstones_dedupe_so_fresh_enqueue_reuses_key()
    {
        var job = await _db.Store.EnqueueOrGetAsync("reuse-me", Kind);

        Assert.Equal(JobActionOutcome.Ok, await _db.Store.CancelPendingAsync(job.Id));

        var old = (await _db.Store.GetAsync(job.Id))!;
        Assert.Equal(ProcessingJobStatus.Cancelled, old.Status);
        Assert.Equal($"reuse-me:cancelled:{job.Id}", old.DedupeKey);
        Assert.Equal("Cancelled by operator before a worker claimed this job.", old.LastError);
        var fresh = await _db.Store.EnqueueOrGetAsync("reuse-me", Kind);
        Assert.NotEqual(job.Id, fresh.Id);
        Assert.Equal("reuse-me", fresh.DedupeKey);
        Assert.Equal(ProcessingJobStatus.Pending, fresh.Status);
        Assert.Equal(JobActionOutcome.WrongStatus, await _db.Store.CancelPendingAsync(job.Id));
        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.CancelPendingAsync(12345));
    }

    [Fact]
    public async Task Move_to_top_raises_a_pending_job_above_everything_waiting()
    {
        var first = await _db.Store.EnqueueOrGetAsync("p1", Kind, priority: 3);
        var second = await _db.Store.EnqueueOrGetAsync("p2", Kind);
        var third = await _db.Store.EnqueueOrGetAsync("p3", Kind);

        Assert.Equal(JobActionOutcome.Ok, await _db.Store.MoveToTopAsync(third.Id));
        Assert.Equal(JobActionOutcome.Ok, await _db.Store.MoveToTopAsync(second.Id));

        Assert.Equal(second.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!.Id);
        Assert.Equal(third.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!.Id);
        Assert.Equal(first.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!.Id);
        Assert.Equal(JobActionOutcome.WrongStatus, await _db.Store.MoveToTopAsync(first.Id));
        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.MoveToTopAsync(404));
    }

    [Fact]
    public async Task Claim_order_is_priority_then_id()
    {
        await _db.Store.EnqueueOrGetAsync("low", Kind);
        var high = await _db.Store.EnqueueOrGetAsync("high", Kind, priority: 5);

        Assert.Equal(high.Id, (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0))!.Id);
    }

    [Fact]
    public async Task Claimable_kinds_leave_unhandled_live_kinds_pending_but_take_refused_kinds()
    {
        _db.InsertRawJob("unported", "processing.file.remux_pass.v1");
        _db.InsertRawJob("retired", "trimmer.legacy.v1");
        _db.InsertRawJob("unprefixed", "legacy.unprefixed");
        _db.InsertRawJob("upper", "Processing.case.v1");
        _db.InsertRawJob("handled", "processing.handled.v1");
        var kinds = new ClaimableKinds(["processing.handled.v1"], IncludeRefusedKinds: true);

        var claimed = new List<string>();
        while (await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0, kinds: kinds) is { } job)
        {
            claimed.Add(job.JobKind);
        }

        Assert.Equal(["trimmer.legacy.v1", "legacy.unprefixed", "Processing.case.v1", "processing.handled.v1"], claimed);
        Assert.Equal(ProcessingJobStatus.Pending, (string?)_db.Scalar("SELECT status FROM jobs WHERE dedupe_key = 'unported'"));
        Assert.Null(await _db.Store.ClaimNextAsync("w", T0.AddHours(1), T0, kinds: new ClaimableKinds([], IncludeRefusedKinds: false)));
    }

    [Fact]
    public async Task Metrics_see_claims_completions_failures_and_queue_depth()
    {
        var metrics = new RecordingMetrics();
        var store = new ProcessingJobStore(_db.Database, _db.Clock, metrics);
        await store.EnqueueOrGetAsync("m1", Kind, maxAttempts: 1);
        await store.EnqueueOrGetAsync("m2", Kind);
        var a = (await store.ClaimNextAsync("w", T0.AddHours(1), T0))!;
        await store.CompleteClaimedAsync(a.Id, "w", T0);
        var b = (await store.ClaimNextAsync("w", T0.AddHours(1), T0))!;
        await store.FailClaimedAsync(b.Id, "w", "x", T0);

        Assert.Equal(["started", "completed", "started"], metrics.Events);
        Assert.Equal([1, 2, 2, 1, 1, 1], metrics.Depths);
    }

    private sealed class RecordingMetrics : IJobQueueMetrics
    {
        public List<string> Events { get; } = [];

        public List<int> Depths { get; } = [];

        public void RecordJobEvent(string moduleName, string jobEvent) => Events.Add(jobEvent);

        public void SetQueueDepth(string moduleName, int depth) => Depths.Add(depth);
    }
}
