using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// What a hand-off's status says for each kind of job and file row that can sit behind it: waiting for a time, failing,
/// left over from an earlier hand-off of the same path, or described by a payload Weir cannot read.
/// </summary>
public sealed class HandoffStatusStatesTests : IDisposable
{
    private const string Folder = "Example.Movie.2024.1080p";
    private const string Main = Folder + "/Example.Movie.2024.1080p.mkv";
    private const string Remux = "processing.file.remux_pass.v1";
    private const string PassThrough = "processing.file.pass_through.v1";
    private const string Reject = "processing.file.reject.v1";

    private readonly MediaManagerFixture _fixture = new();
    private long _libraryId;
    private HandoffLedgerRow _row = null!;

    public void Dispose() => _fixture.Dispose();

    private async Task ReceiveAsync()
    {
        var watched = _fixture.Store.Home.Join("movies");
        Directory.CreateDirectory(Path.Join(watched, Folder));
        await File.WriteAllTextAsync(Path.Join(watched, Main), "main");
        _libraryId = await _fixture.LibraryAsync("movie", watched);
        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = "handoff",
            MediaScope = "movie",
            FilePath = Path.Join(watched, Folder),
            HandoffId = "release",
            LibraryId = "lib-1",
        }));
        _row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release")))!;
    }

    private Task<HandoffStatus> StatusAsync() => _fixture.Db(uow => _fixture.Ledger.CurrentStatusAsync(uow, _row));

    private Task RemoveTheQueuedPassAsync() => _fixture.Store.Execute("DELETE FROM jobs");

    private Task AddJobAsync(string kind, string status, string? notBefore = null, string? lastError = null, string createdAt = "2026-01-15 10:00:00", string? payload = null)
    {
        var key = kind == Remux ? $"{Remux}:deluno:handoff:release:{Guid.NewGuid():N}" : $"{kind}:{_libraryId}:{Main}:{Guid.NewGuid():N}";
        var json = payload ?? $"{{\"relative_media_path\":\"{Main}\"}}";
        return _fixture.Store.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, last_error, not_before, created_at, updated_at) VALUES " +
            $"('{key}', '{kind}', '{json.Replace("'", "''", StringComparison.Ordinal)}', '{status}', {Quote(lastError)}, {Quote(notBefore)}, '{createdAt}', '{createdAt}')");
    }

    private static string Quote(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pass_throughs_and_rejections_waiting_for_a_time_are_scheduled_for_the_earliest_of_them()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await AddJobAsync(PassThrough, "pending", notBefore: "2026-01-15 12:00:00");
        await AddJobAsync(Reject, "pending", notBefore: "2026-01-15 11:00:00");
        await AddJobAsync(Reject, "pending", notBefore: "2026-01-15 13:00:00");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, status.State);
        Assert.Equal(At(11), status.ScheduledFor);
        Assert.Null(status.QueuePosition);
    }

    [Fact]
    public async Task A_pass_through_whose_time_has_come_is_scheduled_with_no_time_to_wait_for()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await AddJobAsync(PassThrough, "pending", notBefore: "2026-01-15 09:00:00");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, status.State);
        Assert.Null(status.ScheduledFor);
    }

    [Fact]
    public async Task Passes_waiting_for_a_time_are_scheduled_for_the_earliest_of_them()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET not_before = '2026-01-15 12:00:00'");
        await AddJobAsync(Remux, "pending", notBefore: "2026-01-15 11:00:00");
        await AddJobAsync(Remux, "pending", notBefore: "2026-01-15 14:00:00");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, status.State);
        Assert.Equal(At(11), status.ScheduledFor);
    }

    [Fact]
    public async Task A_pass_ahead_of_another_is_queued_behind_the_work_before_it()
    {
        await ReceiveAsync();
        await AddJobAsync(Remux, "pending");
        await _fixture.Store.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, priority) VALUES ('other', 'processing.file.remux_pass.v1', 'pending', 5)");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Queued, status.State);
        Assert.Equal(2, status.QueuePosition);
    }

    [Fact]
    public async Task A_pass_being_worked_on_makes_the_hand_off_working()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET status = 'leased'");

        Assert.Equal(HandoffLedgerRules.Working, (await StatusAsync()).State);
    }

    [Fact]
    public async Task A_pass_through_that_ran_out_of_attempts_is_failed_with_the_reason_it_recorded()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await AddJobAsync(PassThrough, "failed", lastError: "The output folder is read-only.");
        await AddJobAsync(Reject, "failed", lastError: "A second reason.");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Failed, status.State);
        Assert.Equal("The output folder is read-only.", status.Message);
    }

    [Fact]
    public async Task A_failed_rejection_with_no_reason_recorded_says_so_in_plain_words()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await AddJobAsync(Reject, "failed", lastError: "");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Failed, status.State);
        Assert.Equal("Weir could not hand this file back to your media manager.", status.Message);
    }

    [Fact]
    public async Task A_failed_pass_through_left_by_an_earlier_hand_off_of_the_same_file_does_not_fail_this_one()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await AddJobAsync(PassThrough, "failed", lastError: "Old trouble.", createdAt: "2025-12-01 10:00:00");

        var status = await StatusAsync();

        Assert.NotEqual(HandoffLedgerRules.Failed, status.State);
        Assert.NotEqual("Old trouble.", status.Message);
    }

    [Fact]
    public async Task A_failure_recorded_for_the_file_before_this_hand_off_arrived_is_not_this_hand_offs()
    {
        await ReceiveAsync();
        await RemoveTheQueuedPassAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'skipped', status_reason = 'Too small.', updated_at = '2025-12-01 10:00:00' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();

        Assert.Equal(_row.State, status.State);
        Assert.NotEqual("Too small.", status.Message);
    }

    [Fact]
    public async Task A_file_retrying_later_schedules_the_hand_off_for_the_earlier_of_its_retry_and_a_job_s_time()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET not_before = '2026-01-15 12:00:00', payload_json = '{}'");
        await _fixture.Store.Execute(
            $"UPDATE files SET status = 'processing_failed', status_reason = 'Disk full.', next_retry_at = '2026-01-15 11:00:00' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, status.State);
        Assert.Equal(At(11), status.ScheduledFor);
        Assert.Equal("Disk full.", status.Message);
    }

    [Fact]
    public async Task A_later_retry_does_not_move_a_nearer_time_a_job_is_waiting_for()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET not_before = '2026-01-15 11:00:00', payload_json = '{}'");
        await _fixture.Store.Execute(
            $"UPDATE files SET status = 'processing_failed', next_retry_at = '2026-01-15 15:00:00' WHERE relative_path = '{Main}'");

        Assert.Equal(At(11), (await StatusAsync()).ScheduledFor);
    }

    [Fact]
    public async Task The_reason_a_file_is_waiting_is_kept_while_a_job_for_it_is_still_to_run()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute($"UPDATE files SET status_reason = 'Waiting for the schedule.' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();

        Assert.Equal("Waiting for the schedule.", status.Message);
    }

    [Fact]
    public async Task A_job_whose_payload_Weir_cannot_parse_or_that_names_no_file_does_not_stop_the_status_being_worked_out()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute($"UPDATE jobs SET payload_json = '{{\"count\":{new string('1', WireJsonParser.MaxIntegerDigits + 1)}}}'");
        await AddJobAsync(Remux, "pending", payload: "{}");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Queued, status.State);
    }

    [Fact]
    public async Task A_job_whose_payload_is_not_an_object_is_refused_loudly_rather_than_guessed_at()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET payload_json = '[1,2]'");

        await Assert.ThrowsAsync<InvalidOperationException>(StatusAsync);
    }

    [Fact]
    public async Task A_cancelled_hand_off_keeps_the_state_it_was_cancelled_in()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute($"UPDATE media_manager_handoffs SET state = '{HandoffLedgerRules.Cancelled}'");
        _row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release")))!;

        Assert.Equal(HandoffLedgerRules.Cancelled, (await StatusAsync()).State);
    }

    [Fact]
    public async Task A_hand_off_in_no_library_covers_no_files_and_has_no_jobs_of_its_own_to_wait_for()
    {
        await ReceiveAsync();
        var noLibrary = _row with { LibraryId = null };

        var files = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, noLibrary));

        Assert.Empty(files);
    }

    [Fact]
    public async Task A_hand_off_that_recorded_no_files_of_its_own_covers_everything_listed_under_its_path()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("DELETE FROM media_manager_handoff_targets");

        var covered = await _fixture.Db(uow => HandoffLedgerStore.CoveredPathsAsync(uow, _row));
        var files = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, _row));

        Assert.Contains(Main, covered);
        Assert.Equal([Main], files.Select(file => file.RelativePath));
    }

    [Fact]
    public async Task A_hand_off_reports_the_file_it_named_when_the_list_of_files_was_not_kept()
    {
        await ReceiveAsync();

        var named = _row with { OutputFiles = null, OutputPath = "/media/movies/film.mkv" };
        var none = _row with { OutputFiles = null, OutputPath = null };

        Assert.True(HandoffLedgerStore.HandedBackFile(named));
        Assert.False(HandoffLedgerStore.HandedBackFile(none));
        Assert.Throws<ArgumentNullException>(() => HandoffLedgerStore.HandedBackFile(null!));
    }
}
