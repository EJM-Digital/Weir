using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The edges of how a hand-off's live status is worked out: which files and jobs belong to it, which of them speak for it, when it
/// counts as changed, and what the ledger row keeps.
/// </summary>
public sealed class HandoffStatusLiveStateTests : IDisposable
{
    private const string Folder = "Example.Movie.2024.1080p";
    private const string Main = Folder + "/Example.Movie.2024.1080p.mkv";
    private const string Extra = Folder + "/Example.Movie.2024.1080p.extra.mkv";
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
        await ReloadAsync();
        await _fixture.Store.Execute($"UPDATE files SET updated_at = '2026-01-15 10:00:00' WHERE library_id = {_libraryId}");
    }

    private async Task ReloadAsync() => _row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release")))!;

    private Task<HandoffStatus> StatusAsync(HandoffLedgerRow? row = null) => _fixture.Db(uow => _fixture.Ledger.CurrentStatusAsync(uow, row ?? _row));

    private Task<int> AddTargetsAsync(params string[] paths) => _fixture.Db(async uow =>
    {
        await _fixture.Targets.AddAsync(uow, _row.Id, paths);
        return 0;
    });

    private Task RemoveTheJobsAsync() => _fixture.Store.Execute("DELETE FROM jobs");

    private Task ForgetTheTargetsAsync() => _fixture.Store.Execute("DELETE FROM media_manager_handoff_targets");

    private Task AddJobAsync(string kind, string status, string path, string updatedAt = "2026-01-15 09:00:00", string? notBefore = null)
    {
        var key = kind == Remux ? $"{IntakeRules.RemuxDedupeKey("deluno", "release")}:{Guid.NewGuid():N}" : $"{kind}:{_libraryId}:{path}:{Guid.NewGuid():N}";
        var before = notBefore is null ? "NULL" : $"'{notBefore}'";
        return _fixture.Store.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, not_before, created_at, updated_at) VALUES " +
            $"('{key}', '{kind}', '{{\"relative_media_path\":\"{path}\"}}', '{status}', {before}, '{updatedAt}', '{updatedAt}')");
    }

    private Task AddFileAsync(string path, string status, string reason = "", string updatedAt = "2026-01-15 10:00:00", long size = 0) =>
        _fixture.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, updated_at) VALUES " +
            $"({_libraryId}, '{path}', '{status}', '{reason}', {size}, '{updatedAt}')");

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

    private static HandoffLedgerRow RowReceived(DateTimeOffset? at) =>
        new(1, "deluno", "release", 1, Main, "queued", null, null, null, ReceivedAt: at);

    // ---- which files and jobs belong to the hand-off ----

    [Fact]
    public async Task A_sibling_folder_whose_name_only_begins_like_the_hand_offs_path_is_not_one_of_its_files()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await AddFileAsync(Folder + "/Part2.mkv", "unprocessed");
        await AddFileAsync(Folder + "-extras/Bonus.mkv", "unprocessed");

        var files = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, _row));

        Assert.Equal([Main, Folder + "/Part2.mkv"], files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_hand_off_that_records_its_files_covers_only_those_even_when_the_list_has_other_rows_under_the_path()
    {
        await ReceiveAsync();
        await AddFileAsync(Extra, "unprocessed");

        var covered = await _fixture.Db(uow => HandoffLedgerStore.CoveredPathsAsync(uow, _row));

        Assert.Equal([Main], covered);
    }

    [Fact]
    public async Task A_hand_off_that_records_a_file_with_no_row_in_the_list_still_covers_it()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await AddTargetsAsync([Folder + "/Taken.off.the.list.mkv"]);

        var covered = await _fixture.Db(uow => HandoffLedgerStore.CoveredPathsAsync(uow, _row));

        Assert.Equal([Folder + "/Taken.off.the.list.mkv"], covered);
    }

    [Fact]
    public async Task Paths_that_differ_only_in_case_are_one_file_where_the_system_ignores_case_and_two_where_it_does_not()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"DELETE FROM files WHERE library_id = {_libraryId}");
        await AddFileAsync(Folder + "/Case.mkv", "unprocessed");
        await AddFileAsync(Folder + "/CASE.mkv", "unprocessed");

        var covered = await _fixture.Db(uow => HandoffLedgerStore.CoveredPathsAsync(uow, _row));

        // The hand-off's own path, and the file or files under it.
        Assert.Equal(OperatingSystem.IsWindows() ? 2 : 3, covered.Count);
    }

    [Fact]
    public async Task A_file_row_in_another_case_than_the_hand_offs_path_belongs_to_it_only_where_the_system_ignores_case()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"DELETE FROM files WHERE library_id = {_libraryId}");
        await AddFileAsync(Folder.ToUpperInvariant() + "/Shouting.mkv", "unprocessed");

        var files = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, _row));

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, files.Count);
    }

    [Fact]
    public async Task A_recorded_file_matches_its_row_in_another_case_only_where_the_system_ignores_case()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"DELETE FROM files WHERE library_id = {_libraryId}");
        await AddTargetsAsync([Folder + "/Quiet.mkv"]);
        await AddFileAsync(Folder + "/QUIET.mkv", "unprocessed");

        var files = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, _row));

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, files.Count);
    }

    [Fact]
    public async Task Only_the_jobs_keyed_to_the_hand_off_or_to_its_files_are_its_jobs()
    {
        await ReceiveAsync();
        var baseKey = IntakeRules.RemuxDedupeKey("deluno", "release");
        var mine = new[] { baseKey + ":one", baseKey, $"{PassThrough}:{_libraryId}:{Main}:fingerprint", $"{Reject}:{_libraryId}:{Main}" };
        var notMine = new[] { "unrelated", baseKey + "2:one", $"{PassThrough}:{_libraryId}:{Main}x", $"{Reject}:{_libraryId}:{Main}x:one", $"{PassThrough}:{_libraryId + 1}:{Main}" };
        foreach (var key in mine.Concat(notMine))
        {
            await _fixture.Store.Execute($"INSERT INTO jobs (dedupe_key, job_kind, status) VALUES ('{key}', '{Remux}', 'pending')");
        }

        var jobs = await _fixture.Db(uow => HandoffLedgerStore.JobsForAsync(uow, _row));

        var keys = jobs.Select(job => job.DedupeKey).ToHashSet(StringComparer.Ordinal);
        Assert.All(mine, key => Assert.Contains(key, keys));
        Assert.All(notMine, key => Assert.DoesNotContain(key, keys));
    }

    // ---- which jobs speak for the hand-off ----

    [Fact]
    public async Task A_remux_pass_that_is_working_still_counts_after_the_hand_off_was_recorded_as_ended()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await AddJobAsync(Remux, "leased", Main, updatedAt: "2026-01-15 09:00:00");

        var status = await StatusAsync(_row with { State = HandoffLedgerRules.Completed, LastChangedAt = At(9, 30) });

        Assert.Equal(HandoffLedgerRules.Working, status.State);
    }

    [Fact]
    public async Task A_pass_through_that_is_working_still_counts_while_the_hand_off_has_not_ended()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await AddJobAsync(PassThrough, "leased", Main, updatedAt: "2026-01-15 09:00:00");

        var status = await StatusAsync(_row with { LastChangedAt = At(9, 30) });

        Assert.Equal(HandoffLedgerRules.Working, status.State);
    }

    [Theory]
    [InlineData("processing_failed")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task A_result_recorded_for_a_file_before_this_hand_off_arrived_is_not_this_hand_offs(string status)
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = '{status}', updated_at = '2025-12-01 10:00:00' WHERE relative_path = '{Main}'");

        var answer = await StatusAsync();

        Assert.Equal(_row.State, answer.State);
    }

    [Fact]
    public async Task A_file_the_workflow_left_alone_before_this_hand_off_arrived_is_not_this_hand_offs_either()
    {
        await LeaveTheSmallerFileAloneAsync("skipped", "Too small.", extraUpdatedAt: "2025-12-01 10:00:00");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
        Assert.Null(status.Message);
    }

    [Fact]
    public void A_record_exactly_a_minute_older_than_the_hand_off_is_still_this_hand_offs()
    {
        var received = At(10);

        Assert.False(HandoffLedgerStore.IsFromEarlierHandoff(RowReceived(received), received - TimeSpan.FromMinutes(1)));
        Assert.True(HandoffLedgerStore.IsFromEarlierHandoff(RowReceived(received), received - TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1)));
        Assert.False(HandoffLedgerStore.IsFromEarlierHandoff(RowReceived(null), At(1)));
        Assert.False(HandoffLedgerStore.IsFromEarlierHandoff(RowReceived(received), null));
    }

    // ---- when the hand-off counts as changed ----

    [Fact]
    public async Task A_change_is_dated_by_the_newest_work_whether_that_is_a_job_or_a_file()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET status = 'leased', updated_at = '2026-01-15 09:45:00'");
        await _fixture.Store.Execute($"UPDATE files SET updated_at = '2026-01-15 09:30:00' WHERE relative_path = '{Main}'");
        var jobNewer = await StatusAsync(_row with { LastChangedAt = At(9) });

        await _fixture.Store.Execute("UPDATE jobs SET updated_at = '2026-01-15 09:30:00'");
        await _fixture.Store.Execute($"UPDATE files SET updated_at = '2026-01-15 09:45:00' WHERE relative_path = '{Main}'");
        var fileNewer = await StatusAsync(_row with { LastChangedAt = At(9) });

        Assert.Equal(At(9, 45), jobNewer.LastChangedAt);
        Assert.Equal(At(9, 45), fileNewer.LastChangedAt);
    }

    [Fact]
    public async Task Work_that_changed_nothing_since_the_ledger_was_last_written_is_dated_by_the_moment_it_was_asked_about()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET status = 'leased', updated_at = '2026-01-15 09:00:00'");
        await _fixture.Store.Execute($"UPDATE files SET updated_at = '2026-01-15 09:00:00' WHERE relative_path = '{Main}'");

        var status = await StatusAsync(_row with { LastChangedAt = At(9) });

        Assert.Equal(HandoffLedgerRules.Working, status.State);
        Assert.Equal(_fixture.Store.Clock.GetUtcNow(), status.LastChangedAt);
    }

    [Fact]
    public async Task A_hand_off_that_is_working_has_no_queue_position_and_no_time_to_wait_for()
    {
        await ReceiveAsync();
        await AddJobAsync(Remux, "pending", Main, notBefore: "2026-01-15 14:00:00");
        await AddJobAsync(Remux, "leased", Main);

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Working, status.State);
        Assert.Null(status.QueuePosition);
        Assert.Null(status.ScheduledFor);
    }

    [Fact]
    public async Task A_hand_off_that_is_queued_has_a_queue_position_and_no_time_to_wait_for()
    {
        await ReceiveAsync();
        await AddJobAsync(Remux, "pending", Main, notBefore: "2026-01-15 14:00:00");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Queued, status.State);
        Assert.NotNull(status.QueuePosition);
        Assert.Null(status.ScheduledFor);
    }

    // ---- what the ledger row keeps ----

    [Fact]
    public async Task A_new_state_is_written_to_the_ledger_with_its_date_and_the_reason_a_file_gave()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'out_of_schedule', status_reason = 'Waiting for the schedule.', updated_at = '2026-01-15 09:30:00' WHERE relative_path = '{Main}'");

        var status = await StatusAsync(_row with { LastChangedAt = At(9) });
        await ReloadAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, _row.State);
        Assert.Equal(At(9, 30), _row.LastChangedAt);
        Assert.Equal("Waiting for the schedule.", _row.Message);
        Assert.Equal("Waiting for the schedule.", status.Message);
    }

    [Fact]
    public async Task A_hand_off_that_is_over_keeps_the_ledger_message_it_had_and_answers_with_the_reason_it_just_found()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET message = 'Stored.'");
        await ReloadAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'processed', status_reason = 'Live.' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();
        await ReloadAsync();

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
        Assert.Equal("Live.", status.Message);
        Assert.Equal("Stored.", _row.Message);
    }

    [Fact]
    public async Task A_new_reason_replaces_the_one_the_ledger_has()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'out_of_schedule', status_reason = 'First.' WHERE relative_path = '{Main}'");
        await StatusAsync();
        await ReloadAsync();
        await _fixture.Store.Execute($"UPDATE files SET status_reason = 'Second.' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();
        await ReloadAsync();

        Assert.Equal("Second.", status.Message);
        Assert.Equal("Second.", _row.Message);
    }

    [Fact]
    public async Task Newer_work_moves_the_ledger_date_of_a_hand_off_whose_state_did_not_change()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute("UPDATE jobs SET updated_at = '2026-01-15 10:30:00'");
        await _fixture.Store.Execute("UPDATE files SET updated_at = '2026-01-15 10:20:00'");

        await StatusAsync();
        await ReloadAsync();

        Assert.Equal(HandoffLedgerRules.Queued, _row.State);
        Assert.Equal(At(10, 30), _row.LastChangedAt);
    }

    // ---- the reason shown ----

    [Fact]
    public async Task A_file_with_a_pass_on_it_that_has_no_reason_of_its_own_adds_no_blank_reason()
    {
        await ReceiveAsync();
        await _fixture.Store.Execute($"UPDATE files SET status_reason = '' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();
        await ReloadAsync();

        Assert.Null(status.Message);
        Assert.Null(_row.Message);
    }

    [Fact]
    public async Task Of_two_files_with_a_pass_on_them_the_first_reason_found_is_the_one_given()
    {
        await ReceiveAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"DELETE FROM files WHERE relative_path = '{Main}'");
        await AddFileAsync(Folder + "/Zebra.mkv", "unprocessed", "Zebra.");
        await AddFileAsync(Main, "unprocessed", "Main.");
        await AddJobAsync(Remux, "pending", Folder + "/Zebra.mkv");
        var listed = await _fixture.Db(uow => HandoffLedgerStore.FileRowsAsync(uow, _row));
        var reasons = new Dictionary<string, string> { [Main] = "Main.", [Folder + "/Zebra.mkv"] = "Zebra." };

        var status = await StatusAsync();

        // The files are read in the order the list gives them; the first one's reason is kept.
        Assert.Equal(reasons[listed[0].RelativePath], status.Message);
    }

    [Fact]
    public async Task A_file_with_no_reason_does_not_blank_the_message_the_ledger_has()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET message = 'Earlier.'");
        await ReloadAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'out_of_schedule', status_reason = '' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Scheduled, status.State);
        Assert.Equal("Earlier.", status.Message);
    }

    [Fact]
    public void A_hand_off_that_reported_an_empty_list_of_files_handed_back_none()
    {
        var empty = RowReceived(null) with { OutputFiles = [], OutputPath = null };
        var one = RowReceived(null) with { OutputFiles = ["/out/a.mkv"] };

        Assert.False(HandoffLedgerStore.HandedBackFile(empty));
        Assert.True(HandoffLedgerStore.HandedBackFile(one));
    }

    // ---- a file the workflow's own rules left alone ----

    private async Task LeaveTheSmallerFileAloneAsync(string extraFileStatus, string reason = "", string extraUpdatedAt = "2026-01-15 10:00:00", string extraPath = Extra)
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await AddTargetsAsync([extraPath]);
        await _fixture.Store.Execute($"UPDATE files SET status = 'processed', size_bytes = 1000 WHERE relative_path = '{Main}'");
        await AddFileAsync(Extra, extraFileStatus, reason, extraUpdatedAt, size: 100);
        await _fixture.Store.Execute($"UPDATE media_manager_handoff_targets SET result = 'completed' WHERE relative_path = '{Main}'");
        await _fixture.Store.Execute($"UPDATE media_manager_handoff_targets SET result = 'skipped' WHERE relative_path = '{extraPath}'");
    }

    [Fact]
    public async Task A_smaller_file_the_workflow_left_alone_does_not_fail_a_hand_off_that_delivered_the_film()
    {
        await LeaveTheSmallerFileAloneAsync("skipped");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
    }

    [Fact]
    public async Task A_file_with_another_status_than_skipped_is_not_left_alone_whatever_its_target_says()
    {
        await LeaveTheSmallerFileAloneAsync("rejected");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Rejected, status.State);
    }

    [Fact]
    public async Task A_skipped_file_with_no_recorded_target_fails_the_hand_off_instead_of_breaking_the_answer()
    {
        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'skipped' WHERE relative_path = '{Main}'");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Failed, status.State);
    }

    [Fact]
    public async Task A_skipped_file_is_matched_to_its_own_target_where_paths_that_differ_only_in_case_are_different_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await ReceiveAsync();
        await RemoveTheJobsAsync();
        await ForgetTheTargetsAsync();
        await _fixture.Store.Execute($"UPDATE files SET status = 'processed', size_bytes = 1000 WHERE relative_path = '{Main}'");
        await AddFileAsync(Folder + "/A.mkv", "processed", size: 1000);
        await AddFileAsync(Folder + "/a.mkv", "skipped", size: 100);
        await AddTargetsAsync(Main, Folder + "/A.mkv", Folder + "/a.mkv");
        await _fixture.Store.Execute("UPDATE media_manager_handoff_targets SET result = 'completed'");
        await _fixture.Store.Execute($"UPDATE media_manager_handoff_targets SET result = 'skipped' WHERE relative_path = '{Folder}/a.mkv'");

        var status = await StatusAsync();

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
    }
}
