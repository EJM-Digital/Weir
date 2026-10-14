using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.LibraryMode;

public sealed class ProcessingJobSwapJournalTests : IDisposable
{
    private readonly JobsTestDatabase _db = new();
    private readonly ProcessingJobSwapJournal _journal;

    public ProcessingJobSwapJournalTests()
    {
        _journal = new ProcessingJobSwapJournal(_db.Database);
    }

    public void Dispose() => _db.Dispose();

    private long Job(string dedupe, string? payload, string status = "leased")
    {
        _db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($key, 'processing.library.clean.v1', $payload, $status)",
            ("$key", dedupe),
            ("$payload", payload),
            ("$status", status));
        return (long)_db.Scalar("SELECT id FROM jobs WHERE dedupe_key = $key", ("$key", dedupe))!;
    }

    private string? PayloadOf(long id) => _db.Scalar("SELECT payload_json FROM jobs WHERE id = $id", ("$id", id)) as string;

    private (string State, string OriginalPath, string TempPath, string BackupPath, bool Committed)? SwapRowOf(long id)
    {
        var row = _db.QueryRow(
            "SELECT state, original_path, temp_path, backup_path, committed FROM library_swaps WHERE job_id = $id", ("$id", id));
        return row is null
            ? null
            : ((string)row[0]!, (string)row[1]!, (string)row[2]!, (string)row[3]!, Convert.ToInt64(row[4]) != 0);
    }

    [Fact]
    public async Task Each_state_is_recorded_in_the_swap_table_and_the_jobs_own_payload_is_never_touched()
    {
        const string originalPayload = "{\"library_id\": 3, \"trigger\": \"library\"}";
        var id = Job("a", originalPayload);

        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committing));
        var committing = SwapRowOf(id);
        Assert.Equal(("committing", "/lib/a.mkv", "/lib/a.weir-tmp.mkv", "/lib/a.weir-bak.mkv", false), committing);
        Assert.Equal(originalPayload, PayloadOf(id));

        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committed));
        var committed = SwapRowOf(id);
        Assert.Equal(("committed", "/lib/a.mkv", "/lib/a.weir-tmp.mkv", "/lib/a.weir-bak.mkv", true), committed);
        Assert.Equal(originalPayload, PayloadOf(id));
    }

    [Fact]
    public async Task A_job_without_a_payload_still_gets_a_swap_row()
    {
        var id = Job("a", null);

        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Writing));

        Assert.Equal(("writing", "/lib/a.mkv", "/lib/a.weir-tmp.mkv", "/lib/a.weir-bak.mkv", false), SwapRowOf(id));
        Assert.Null(PayloadOf(id));
    }

    [Fact]
    public async Task Committed_stays_true_once_set_even_through_a_later_recovery()
    {
        var id = Job("a", null);
        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committed));
        Assert.True(SwapRowOf(id)!.Value.Committed);

        // A crash after commit but before "finished" is recovered rather than rolled back; committed must
        // not be forgotten just because the recorded state moved past Committed.
        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Recovered));
        Assert.True(SwapRowOf(id)!.Value.Committed);
    }

    [Fact]
    public async Task Unfinished_swaps_are_listed_in_job_order_and_everything_else_is_ignored()
    {
        var writing = Job("w", null);
        var committing = Job("c", null, "failed");
        var committed = Job("d", null, "completed");
        var finished = Job("f", null);
        var rolledBack = Job("r", null);
        var recovered = Job("v", null);
        Job("plain", "{\"relative_media_path\": \"a.mkv\"}");

        await _journal.RecordAsync(new SwapJournalEntry(writing, "/lib/w.mkv", SwapJournalState.Writing));
        await _journal.RecordAsync(new SwapJournalEntry(committing, "/lib/c.mkv", SwapJournalState.Committing));
        await _journal.RecordAsync(new SwapJournalEntry(committed, "/lib/d.mkv", SwapJournalState.Committed));
        await _journal.RecordAsync(new SwapJournalEntry(finished, "/lib/f.mkv", SwapJournalState.Finished));
        await _journal.RecordAsync(new SwapJournalEntry(rolledBack, "/lib/r.mkv", SwapJournalState.RolledBack));
        await _journal.RecordAsync(new SwapJournalEntry(recovered, "/lib/v.mkv", SwapJournalState.Recovered));

        var unfinished = await _journal.ListUnfinishedAsync();

        Assert.Equal(
            [
                new SwapJournalEntry(writing, "/lib/w.mkv", SwapJournalState.Writing),
                new SwapJournalEntry(committing, "/lib/c.mkv", SwapJournalState.Committing),
                new SwapJournalEntry(committed, "/lib/d.mkv", SwapJournalState.Committed),
            ],
            unfinished);
    }

    [Fact]
    public async Task A_kept_original_path_is_recorded_and_survives_a_later_write_that_does_not_know_it()
    {
        var id = Job("a", null);

        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committing, "/lib/.weir-originals/a.mkv"));
        Assert.Equal(
            "/lib/.weir-originals/a.mkv",
            (await _journal.ListUnfinishedAsync()).Single(entry => entry.JobId == id).KeptOriginalPath);

        // The Committed write for a job whose keep path was already decided always passes it again; this proves a
        // write that (hypothetically) did not know it yet could not erase it.
        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committed));
        Assert.Equal(
            "/lib/.weir-originals/a.mkv",
            (await _journal.ListUnfinishedAsync()).Single(entry => entry.JobId == id).KeptOriginalPath);
    }

    [Fact]
    public async Task Only_swaps_waiting_on_a_kept_original_conflict_are_listed_as_needing_a_person()
    {
        var conflicted = Job("a", null);
        var other = Job("b", null);
        var committing = Job("c", null);
        await _journal.RecordAsync(new SwapJournalEntry(conflicted, "/lib/a.mkv", SwapJournalState.KeepConflict));
        await _journal.RecordAsync(new SwapJournalEntry(other, "/lib/b.mkv", SwapJournalState.Recovered));
        await _journal.RecordAsync(new SwapJournalEntry(committing, "/lib/c.mkv", SwapJournalState.Committing));

        Assert.Equal(["/lib/a.mkv"], await _journal.ListKeepConflictPathsAsync());
        Assert.DoesNotContain(await _journal.ListUnfinishedAsync(), entry => entry.JobId == conflicted);
    }

    [Fact]
    public async Task A_swap_with_the_setting_off_records_no_kept_original_path()
    {
        var id = Job("a", null);

        await _journal.RecordAsync(new SwapJournalEntry(id, "/lib/a.mkv", SwapJournalState.Committing));

        Assert.Null((await _journal.ListUnfinishedAsync()).Single(entry => entry.JobId == id).KeptOriginalPath);
    }

    [Fact]
    public async Task A_missing_job_row_is_an_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _journal.RecordAsync(new SwapJournalEntry(999, "/lib/a.mkv", SwapJournalState.Writing)));
        Assert.Equal("Job 999 does not exist, so its swap could not be recorded.", error.Message);
    }
}
